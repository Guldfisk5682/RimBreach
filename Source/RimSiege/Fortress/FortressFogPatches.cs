using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
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

        private static bool Prefix(FogGrid __instance, IntVec3 c) =>
            !FortressFog.TryGet(__instance, out FortressFog fog) || !fog.Conceals(c);
    }

    [HarmonyPatch(typeof(FloodFillerFog), nameof(FloodFillerFog.FloodUnfog))]
    internal static class FortressFloodFogPatch
    {
        private static bool Prefix(IntVec3 root, Map map) =>
            !FortressFog.TryGet(map.fogGrid, out FortressFog fog) || !fog.Conceals(root);

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

        private static Predicate<IntVec3> LimitFlood(Predicate<IntVec3> pass, Map map) =>
            FortressFog.TryGet(map.fogGrid, out FortressFog fog)
                ? cell => !fog.Conceals(cell) && pass(cell)
                : pass;
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.Position), MethodType.Setter)]
    internal static class FortressBreachMovePatch
    {
        private static void Postfix(Thing __instance)
        {
            if (__instance is Pawn pawn) FortressFog.PawnArrived(pawn);
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SpawnSetup))]
    internal static class FortressBreachSpawnPatch
    {
        private static void Postfix(Pawn __instance, bool respawningAfterLoad)
        {
            if (!respawningAfterLoad) FortressFog.PawnArrived(__instance);
        }
    }
}
