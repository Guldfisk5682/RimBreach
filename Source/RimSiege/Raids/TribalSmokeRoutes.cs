using System.Collections.Generic;
using System.Linq;
using RimSiege.Fortress;
using RimSiege.Integration;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Raids
{
    internal static class TribalSmokeRoutes
    {
        internal sealed class PlannedRoute
        {
            internal List<IntVec3> Nodes;
            internal IntVec3 Goal;
            internal IntVec3 BlockerCell;
        }

        internal static PlannedRoute Plan(Lord lord, Pawn anchor)
        {
            Map map = lord.Map;
            FortressSite site = FortressSite.SupportSite(lord);
            Faction defender = site?.Faction ?? Faction.OfPlayer;
            IntVec3 entry = anchor.Position;
            TraverseParms traverse = TraverseParms.For(anchor);
            IntVec3 goal = IntVec3.Invalid;
            if (lord.CurLordToil is LordToil_AssaultColonyBreaching breaching)
            {
                goal = breaching.Data.breachDest;
                RouteSegmentSnapshot native = VanillaBreachRoutes.Snapshot(breaching.Data.breachingGrid);
                if (native != null && BuildRouteFromNodes(entry, native.Nodes, goal, traverse, map, defender) is { } route)
                    return route;
            }
            else if (lord.CurLordToil is LordToil_AssaultColonySappers)
            {
                foreach (Pawn pawn in lord.ownedPawns)
                {
                    if (pawn.mindState.duty?.def != DutyDefOf.Sapper) continue;
                    goal = pawn.mindState.duty.focus.Cell;
                    if (goal.IsValid) break;
                }
            }

            if (!goal.IsValid) goal = site?.AssaultGoal ?? GenAI.RandomRaidDest(entry, map);
            return BuildCandidateRoute(entry, IsoFloor.OnFloorOf(goal, entry, map), traverse, map, defender);
        }

        internal static PlannedRoute BuildCandidateRoute(IntVec3 entry, IntVec3 candidate,
            TraverseParms traverse, Map map, Faction defender)
        {
            if (!candidate.InBounds(map)) return null;
            if (map.reachability.CanReach(entry, candidate, PathEndMode.OnCell, traverse))
            {
                using PawnPath path = map.pathFinder.FindPathNow(entry, candidate, traverse, null, PathEndMode.OnCell);
                if (path == null || !path.Found) return null;
                var nodes = path.NodesReversed.AsEnumerable().Reverse().ToList();
                return FinalizeNodes(candidate, nodes, map, defender);
            }
            if (!map.reachability.CanReach(entry, candidate, PathEndMode.OnCell, TraverseMode.PassAllDestroyableThings, Danger.Deadly))
                return null;
            RouteSegmentSnapshot breach = VanillaBreachRoutes.Preplan(map, entry, candidate);
            if (breach == null || breach.Nodes.Count < 2) return null;
            return BuildRouteFromNodes(entry, breach.Nodes, candidate, traverse, map, defender);
        }

        private static PlannedRoute BuildRouteFromNodes(IntVec3 entry, IReadOnlyList<IntVec3> rawNodes, IntVec3 goal,
            TraverseParms traverse, Map map, Faction defender)
        {
            IntVec3 blocker = IntVec3.Invalid;
            int cut = rawNodes.Count;
            for (int i = 0; i < rawNodes.Count; i++)
            {
                if (BlockingThingAt(rawNodes[i], map, defender) == null) continue;
                blocker = rawNodes[i];
                cut = i;
                break;
            }
            if (cut <= 0) return null;

            IntVec3 dest = IntVec3.Invalid;
            for (int i = cut - 1; i >= 0; i--)
            {
                IntVec3 c = rawNodes[i];
                if (c.InBounds(map) && IsoFloor.SameFloor(c, entry, map) && c.Standable(map) &&
                    map.reachability.CanReach(entry, c, PathEndMode.OnCell, traverse))
                {
                    dest = c;
                    break;
                }
            }
            if (!dest.IsValid) return null;
            if (blocker.IsValid && !dest.AdjacentTo8WayOrInside(blocker))
                blocker = IntVec3.Invalid;

            using PawnPath walk = map.pathFinder.FindPathNow(entry, dest, traverse, null, PathEndMode.OnCell);
            if (walk == null || !walk.Found || walk.NodesReversed.Count < 2) return null;
            var walkNodes = walk.NodesReversed.AsEnumerable().Reverse().ToList();
            return new PlannedRoute
            {
                Nodes = walkNodes,
                Goal = goal,
                BlockerCell = blocker
            };
        }

        private static PlannedRoute FinalizeNodes(IntVec3 candidate, List<IntVec3> nodes, Map map, Faction defender)
        {
            if (nodes.Count < 2) return null;
            IntVec3 blocker = IntVec3.Invalid;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (BlockingThingAt(nodes[i], map, defender) == null) continue;
                blocker = nodes[i];
                nodes = nodes.Take(i).ToList();
                break;
            }
            if (nodes.Count < 2) return null;
            return new PlannedRoute
            {
                Nodes = nodes,
                Goal = candidate,
                BlockerCell = blocker
            };
        }

        internal static Thing BlockingThingAt(IntVec3 cell, Map map, Faction defender)
        {
            if (!cell.InBounds(map)) return null;
            Building edifice = cell.GetEdifice(map);
            if (edifice == null) return null;
            if (edifice.def.building?.isNaturalRock == true && !edifice.def.IsSmoothed)
                return null;
            if (edifice is Building_Door door)
                return !door.FreePassage && door.Faction == defender ? door : null;
            return edifice.Faction == defender &&
                edifice.def.passability == Traversability.Impassable
                ? edifice
                : null;
        }
    }
}
