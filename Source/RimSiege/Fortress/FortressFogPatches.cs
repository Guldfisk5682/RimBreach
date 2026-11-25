using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimSiege.Raids.Encirclement;
using Verse;

namespace RimSiege.Fortress
{
    [HarmonyPatch]
    internal static class FortressUnfogPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(FogGrid), nameof(FogGrid.Unfog));
            // Unfog can also reveal the rest of a multi-cell building through this worker.
            yield return AccessTools.Method(typeof(FogGrid), "UnfogWorker");
        }

        internal static bool Conceals(FogGrid grid, IntVec3 cell) =>
            FortressFog.TryGet(grid, out FortressFog fortress) && fortress.Conceals(cell) ||
            EncirclementFog.TryGet(grid, out EncirclementFog camp) && camp.Conceals(cell);

        private static bool Prefix(FogGrid __instance, IntVec3 c) => !Conceals(__instance, c);
    }

    [HarmonyPatch(typeof(FloodFillerFog), nameof(FloodFillerFog.FloodUnfog))]
    internal static class FortressFloodFogPatch
    {
        private static bool Prefix(IntVec3 root, Map map) =>
            !FortressUnfogPatch.Conceals(map.fogGrid, root);

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                yield return instruction;
                if (instruction.opcode == OpCodes.Newobj &&
                    instruction.operand is ConstructorInfo constructor &&
                    constructor.DeclaringType == typeof(Predicate<IntVec3>))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_1);
                    yield return CodeInstruction.Call(typeof(FortressFloodFogPatch), nameof(LimitFlood));
                }
            }
        }

        private static Predicate<IntVec3> LimitFlood(Predicate<IntVec3> pass, Map map)
        {
            bool fortressActive = FortressFog.TryGet(map.fogGrid, out FortressFog fortress);
            bool campActive = EncirclementFog.TryGet(map.fogGrid, out EncirclementFog camp);
            if (!fortressActive && !campActive) return pass;
            return cell => fortress?.Conceals(cell) != true && camp?.Conceals(cell) != true && pass(cell);
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.Position), MethodType.Setter)]
    internal static class FortressBreachMovePatch
    {
        private static void Postfix(Thing __instance)
        {
            if (__instance is not Pawn pawn) return;
            FortressFog.PawnArrived(pawn);
            EncirclementFog.PawnArrived(pawn);
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SpawnSetup))]
    internal static class FortressBreachSpawnPatch
    {
        private static void Postfix(Pawn __instance, bool respawningAfterLoad)
        {
            if (respawningAfterLoad) return;
            FortressFog.PawnArrived(__instance);
            EncirclementFog.PawnArrived(__instance);
        }
    }
}
