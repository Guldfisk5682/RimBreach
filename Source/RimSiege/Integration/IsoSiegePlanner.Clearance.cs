using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ProjectZ;
using ProjectZ.Patches;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimSiege.Integration
{
    internal sealed partial class IsoSiegePlanner
    {
        private readonly Func<Thing, bool> canClearTop;
        private bool readingTopComponents;
        private static readonly List<ClimbPlanner.Site> NoSites = new();

        internal bool HasSiegeLadderOrClearance(Faction faction)
        {
            if (StairGrid.For(Map) is not { } grid) return false;
            foreach (VLink link in grid.links)
                if (link.thing is Building_SiegeLadder ladder && (faction == null || ladder.Faction == faction) &&
                    (link.topBlocker
                        ? ladder.Spawned && canClearTop(ladder.top.GetEdifice(Map))
                        : StairGrid.UsableIn(link, Map)))
                    return true;
            return false;
        }

        internal bool HasClearance
        {
            get
            {
                foreach (VLink link in StairGrid.For(Map).links)
                    if (link.thing is Building_SiegeLadder ladder && ladder.topBlocker &&
                        canClearTop(ladder.top.GetEdifice(Map))) return true;
                return false;
            }
        }

        internal bool HasUnclaimedClearance(Pawn pawn, IReadOnlyCollection<Pawn> members)
        {
            if (StairGrid.For(Map) is not { } grid) return false;
            foreach (VLink link in grid.links)
            {
                if (link.thing is not Building_SiegeLadder ladder || !ladder.topBlocker ||
                    !canClearTop(ladder.top.GetEdifice(Map))) continue;
                if (!LadderPerch.Busy(ladder, pawn) && !ClearanceClaimed(ladder, pawn, members))
                    return true;
            }
            return false;
        }

        private bool ClearanceClaimed(Building_SiegeLadder ladder, Pawn self, IReadOnlyCollection<Pawn> members)
        {
            if (members == null) return false;
            foreach (Pawn member in members)
            {
                if (member == null || member == self || !member.Spawned || member.Dead || member.Downed ||
                    member.Map != Map) continue;
                if (member.CurJobDef == PZVJobDefOf.PZ_LadderPerch && member.CurJob?.targetA.Thing == ladder)
                    return true;
                JobQueue queue = member.jobs?.jobQueue;
                if (queue == null) continue;
                foreach (QueuedJob queued in queue)
                    if (queued.job.def == PZVJobDefOf.PZ_LadderPerch && queued.job.targetA.Thing == ladder)
                        return true;
            }
            return false;
        }

        [HarmonyPatch(typeof(RaidVertical), nameof(RaidVertical.ClearableTop))]
        private static class ClearanceTargets
        {
            private static void Postfix(IntVec3 c, Map map, ref bool __result)
            {
                if (__result && Active != null && Active.Map == map)
                    __result = Active.canClearTop(c.GetEdifice(map));
            }
        }

        [HarmonyPatch(typeof(ClimbPlanner), "RemovalSitesFrom")]
        private static class NoCeilingExcavation
        {
            private static bool Prefix(List<ClimbPlanner.Site> into)
            {
                if (Active == null) return true;
                into.Clear();
                return false;
            }
        }

        [HarmonyPatch(typeof(ClimbPlanner), "SitesWithTopIn")]
        private static class ExistingRoutesOnly
        {
            private static bool Prefix(ref List<ClimbPlanner.Site> __result)
            {
                if (Active == null || Active.allowNewLadders) return true;
                __result = NoSites;
                return false;
            }

            private static void Postfix(ref List<ClimbPlanner.Site> __result)
            {
                if (Active?.availableLanding is { } available)
                    __result = __result.FindAll(site => !site.existing && available(site.top));
            }
        }

        [HarmonyPatch(typeof(ClimbPlanner), "BlockerSitesWithTopIn")]
        private static class ExistingBlockedRoutesOnly
        {
            private static void Postfix(List<ClimbPlanner.Site> into)
            {
                if (Active != null && !Active.allowNewLadders) into.RemoveAll(site => !site.existing);
                if (Active?.availableLanding is { } available)
                    into.RemoveAll(site => site.existing || !available(site.top));
            }
        }

        [HarmonyPatch(typeof(ClimbPlanner), nameof(ClimbPlanner.HopOk))]
        private static class ExistingHopOnly
        {
            private static bool Prefix(ClimbPlanner.Hop h, Map map, ref bool __result)
            {
                if (Active?.availableLanding is { } available && (h.existing || !available(h.top)))
                {
                    __result = false;
                    return false;
                }
                if (Active == null || Active.allowNewLadders ||
                    h.kind == 1 && ClimbPlanner.LadderAt(h.foot, h.top, map) != null) return true;
                // A cached existing route must not become a free replacement
                // ladder after its original ladder is destroyed.
                __result = false;
                return false;
            }
        }

        // A blocked landing belongs to the walkable interior beside it for
        // site enumeration only. It never joins movement components through
        // a wall, and the native planner still charges its clearing cost.
        [HarmonyPatch]
        private static class TopComponentScope
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(ClimbPlanner), "RimsOf");
                yield return AccessTools.Method(typeof(ClimbPlanner), "BlockerSitesWithTopIn");
            }

            private static void Prefix(RouteGraph g, out (IsoSiegePlanner Planner, bool Previous) __state)
            {
                IsoSiegePlanner planner = Active != null && Active.Graph == g ? Active : null;
                __state = (planner, planner?.readingTopComponents ?? false);
                if (planner != null) planner.readingTopComponents = true;
            }

            private static void Finalizer((IsoSiegePlanner Planner, bool Previous) __state)
            {
                if (__state.Planner != null) __state.Planner.readingTopComponents = __state.Previous;
            }
        }

        [HarmonyPatch(typeof(RouteGraph), nameof(RouteGraph.DigPartAt))]
        private static class LandingComponent
        {
            private static void Postfix(RouteGraph __instance, IntVec3 c, int key, ref int __result)
            {
                IsoSiegePlanner planner = Active;
                if (__result > 0 || planner == null || __instance != planner.Graph || !planner.readingTopComponents || !c.InBounds(planner.Map) ||
                    !planner.canClearTop(c.GetEdifice(planner.Map))) return;
                foreach (IntVec3 offset in GenAdj.CardinalDirections)
                {
                    IntVec3 neighbour = c + offset;
                    if (!neighbour.InBounds(planner.Map) || !ProjectZ.ProjectZ.SameBand(c, neighbour, planner.Map) ||
                        !neighbour.WalkableBy(planner.Map, planner.Traveller)) continue;
                    int part = __instance.DigPartAt(neighbour, key);
                    if (part > 0) { __result = part; return; }
                }
            }
        }
    }
}
