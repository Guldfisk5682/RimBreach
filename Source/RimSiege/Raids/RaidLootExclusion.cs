using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimSiege.Raids.Artillery;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimSiege.Raids
{
    /// <summary>
    /// Keep raid equipment out of the native valuation and steal jobs.
    /// </summary>
    internal static class RaidLootExclusion
    {
        private static readonly List<Func<Thing, Pawn, bool>> Rules = new()
        {
            (thing, _) => thing is LadderKit { RaidId: not 0 },
            CampSupplyOwnership.IsOwnSupply
        };

        internal static void Exclude(Func<Thing, Pawn, bool> rule)
        {
            if (rule != null && !Rules.Contains(rule)) Rules.Add(rule);
        }

        private static bool IsExcluded(Thing thing, Pawn thief)
        {
            if (thing == null) return false;
            for (int i = 0; i < Rules.Count; i++)
                if (Rules[i](thing, thief)) return true;
            return false;
        }

        private static Thing FindStealTarget(IntVec3 root, Map map, ThingRequest thingReq, PathEndMode peMode,
            TraverseParms traverseParams, float maxDistance, Predicate<Thing> validator, Func<Thing, float> priorityGetter,
            int minRegions, int maxRegions, bool lookInHaulSources, Pawn thief)
        {
            Predicate<Thing> wrapped = t => (validator == null || validator(t)) && !IsExcluded(t, thief);
            return GenClosest.ClosestThing_Regionwise_ReachablePrioritized(root, map, thingReq, peMode, traverseParams,
                maxDistance, wrapped, priorityGetter, minRegions, maxRegions, lookInHaulSources);
        }

        [HarmonyPatch(typeof(StealAIUtility), nameof(StealAIUtility.TryFindBestItemToSteal))]
        private static class StealSearchPatch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                MethodInfo target = AccessTools.Method(typeof(GenClosest), nameof(GenClosest.ClosestThing_Regionwise_ReachablePrioritized));
                MethodInfo replacement = AccessTools.Method(typeof(RaidLootExclusion), nameof(FindStealTarget));
                foreach (CodeInstruction code in instructions)
                {
                    if (code.Calls(target))
                    {
                        yield return new CodeInstruction(OpCodes.Ldarg_S, (byte)4).MoveLabelsFrom(code).MoveBlocksFrom(code);
                        code.operand = replacement;
                    }
                    yield return code;
                }
            }
        }
    }
}
