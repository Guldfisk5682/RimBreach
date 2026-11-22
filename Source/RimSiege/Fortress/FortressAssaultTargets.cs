using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimSiege.Integration;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Fortress
{
    internal static class FortressAssaultTargets
    {
        [HarmonyPatch]
        private static class SpecialistDestination
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(LordToil_AssaultColonyBreaching),
                    nameof(LordToil_AssaultColonyBreaching.UpdateAllDuties));
                yield return AccessTools.Method(typeof(LordToil_AssaultColonySappers),
                    nameof(LordToil_AssaultColonySappers.UpdateAllDuties));
            }

            private static IntVec3 Destination(IntVec3 entry, Map map, LordToil toil)
            {
                IntVec3 goal = FortressSite.SupportSite(toil.lord)?.AssaultGoal ?? IntVec3.Invalid;
                return goal.InBounds(map) ? goal : GenAI.RandomRaidDest(entry, map);
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                MethodInfo original = AccessTools.Method(typeof(GenAI), nameof(GenAI.RandomRaidDest));
                MethodInfo replacement = AccessTools.Method(typeof(SpecialistDestination), nameof(Destination));
                foreach (CodeInstruction instruction in instructions)
                {
                    if (instruction.Calls(original))
                    {
                        yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                        instruction.operand = replacement;
                    }
                    yield return instruction;
                }
            }
        }

        [HarmonyPatch(typeof(LordToil_AssaultColonyBreaching), nameof(LordToil_AssaultColonyBreaching.UpdateCurrentBreachTarget))]
        private static class NativeBreachSearch
        {
            private static void Prefix(LordToil_AssaultColonyBreaching __instance, out Faction __state) =>
                __state = VanillaBreachTargets.Enter(FortressSite.SupportSite(__instance.lord)?.Faction);

            private static void Finalizer(Faction __state) => VanillaBreachTargets.Restore(__state);
        }

        [HarmonyPatch(typeof(JobGiver_AITrashBuildingsDistant), "TryGiveJob")]
        private static class NativeAssaultBuildings
        {
            private static void Prefix(Pawn pawn, out Faction __state) =>
                __state = VanillaBreachTargets.Enter(FortressSite.SupportSite(pawn.GetLord())?.Faction);

            private static void Finalizer(Faction __state) => VanillaBreachTargets.Restore(__state);

            private static List<Building> Buildings(ListerBuildings lister, Pawn pawn) =>
                FortressSite.SupportSite(pawn.GetLord()) is { } site
                    ? site.AssaultBuildings(lister) : lister.allBuildingsColonist;

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                FieldInfo colonist = AccessTools.Field(typeof(ListerBuildings), nameof(ListerBuildings.allBuildingsColonist));
                MethodInfo replacement = AccessTools.Method(typeof(NativeAssaultBuildings), nameof(Buildings));
                foreach (CodeInstruction instruction in instructions)
                {
                    if (instruction.LoadsField(colonist))
                    {
                        yield return new CodeInstruction(OpCodes.Ldarg_1).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = replacement;
                    }
                    yield return instruction;
                }
            }
        }
    }
}
