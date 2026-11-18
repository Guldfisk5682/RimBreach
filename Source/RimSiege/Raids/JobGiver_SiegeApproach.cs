using System.Collections.Generic;
using System.Linq;
using RimSiege.Integration;
using RimWorld;
using ProjectZ;
using Verse;
using Verse.AI;

namespace RimSiege.Raids
{
    public sealed class ThinkNode_ConditionalLadderDuty : ThinkNode_Conditional
    {
        protected override bool Satisfied(Pawn pawn)
        {
            if (LadderPerch.Get(pawn) != null) return true;
            SiegeRaidMap raids = pawn.Map?.GetComponent<SiegeRaidMap>();
            return raids?.Applies(pawn) == true && SiegeRoles.CloseAssault(pawn) &&
                (LadderInventory.Get(pawn) != null || raids.RecoveryFor(pawn) != null);
        }
    }

    public sealed class ThinkNode_ConditionalLadderAssault : ThinkNode_Conditional
    {
        protected override bool Satisfied(Pawn pawn)
        {
            if (!SiegeRaidMap.Enabled || pawn.Map == null || pawn.mindState?.duty is not { } duty ||
                duty.def != DutyDefOf.Escort) return false;
            SiegeRaidMap raids = pawn.Map.GetComponent<SiegeRaidMap>();
            if (!raids.Applies(pawn) || raids.Party(pawn) is not { } party) return false;
            if (duty.focus.Thing is not Pawn escortee || !escortee.Spawned || escortee.Dead || escortee.Downed ||
                !IsoFloor.SameFloor(pawn.Position, escortee.PositionHeld, pawn.Map))
                return true;
            return (party.HasLadderRoute(pawn) && SiegeRoles.Eligible(pawn, party)) ||
                SiegeRoles.CanOpenLadderRoute(pawn, party);
        }
    }

    public sealed class JobGiver_SiegeApproach : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            SiegeRaidMap raids = pawn.Map.GetComponent<SiegeRaidMap>();
            if (!raids.Applies(pawn)) return null;
            SiegeParty party = raids.Party(pawn);
            if (!SiegeRoles.Eligible(pawn, party))
            {
                LadderInventory.Drop(pawn);
                return null;
            }
            // Close-assault carriers and perched clearers can run before combat. The
            // later node lets ranged raiders advance if they have no current shot.
            LadderPerch.Rec perch = LadderPerch.Get(pawn);
            if (perch?.atTop == true) return RaidVertical.ClearJob(pawn, perch);
            bool carrier = LadderInventory.Get(pawn) != null;
            bool walkableRoute = party.Planner.Destination(pawn, true).IsValid;
            if (carrier && !walkableRoute)
            {
                Job independent = party.Plan(pawn);
                if (independent != null)
                {
                    if (independent.def == PZJobDefOf.PZ_PlaceSiegeLadder)
                        independent.locomotionUrgency = LocomotionUrgency.Sprint;
                    return independent;
                }
            }
            if (walkableRoute) return null;
            IntVec3 existingRoute = party.Planner.Destination(pawn, false, out Thing targetThing);
            if (existingRoute.IsValid)
            {
                // The planner's destination is reachable by touch and can be a building.
                if (IsoFloor.SameFloor(pawn.Position, existingRoute, pawn.Map) &&
                    (pawn.Position.AdjacentTo8WayOrInside(existingRoute) ||
                     (targetThing is { Spawned: true } && targetThing.OccupiedRect().AdjacentCells.Contains(pawn.Position))))
                    return null;
                IntVec3 stand = ExistingRouteStand(pawn, existingRoute, targetThing);
                if (stand.IsValid) return JobMaker.MakeJob(JobDefOf.Goto, stand, 500, checkOverrideOnExpiry: true);
            }
            else
            {
                IntVec3 climbEntry = party.Planner.ClimbEntryForBlockedAssault(pawn, pawn.Faction);
                if (climbEntry.IsValid)
                    return JobMaker.MakeJob(JobDefOf.Goto, climbEntry, 500, checkOverrideOnExpiry: true);
            }

            if (party.Planner.HasUnclaimedClearance(pawn, party.Members))
            {
                Job clearing = party.Plan(pawn, placing: false);
                if (clearing != null && clearing.def != JobDefOf.Wait) return clearing;
            }
            if (!carrier)
            {
                Job recovery = raids.Recovery(pawn, party);
                if (recovery != null) return recovery;
            }
            if (party.Planner.ActiveBlockedLadder(pawn, pawn.Faction) is { } blockedLadder)
            {
                if (IsoFloor.SameFloor(pawn.Position, blockedLadder.foot, pawn.Map) &&
                    pawn.Position.DistanceToSquared(blockedLadder.foot) <= 16)
                    return JobMaker.MakeJob(JobDefOf.Wait, 60, checkOverrideOnExpiry: true);
                IntVec3 stage = CellFinder.RandomClosewalkCellNear(blockedLadder.foot, pawn.Map, 3,
                    c => IsoFloor.SameFloor(c, blockedLadder.foot, pawn.Map) &&
                         pawn.CanReach(c, PathEndMode.OnCell, Danger.Deadly));
                if (stage.IsValid)
                    return JobMaker.MakeJob(JobDefOf.Goto, stage, 120, checkOverrideOnExpiry: true);
            }
            return null;
        }

        private static IntVec3 ExistingRouteStand(Pawn pawn, IntVec3 destination, Thing targetThing)
        {
            Map map = pawn.Map;
            if (destination.StandableBy(map, pawn) && pawn.CanReach(destination, PathEndMode.OnCell, Danger.Deadly))
                return destination;
            IntVec3 best = IntVec3.Invalid;
            float bestDist = float.MaxValue;
            IEnumerable<IntVec3> candidates = targetThing is { Spawned: true }
                ? GenAdj.CellsAdjacent8Way(targetThing)
                : GenAdj.AdjacentCells.Select(offset => destination + offset);
            foreach (IntVec3 cell in candidates)
            {
                if (!cell.InBounds(map) || !IsoFloor.SameFloor(cell, destination, map)) continue;
                if (!cell.WalkableBy(map, pawn) || !pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly)) continue;
                float dist = cell.DistanceToSquared(pawn.Position) + (cell.StandableBy(map, pawn) ? 0f : 10000f);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = cell;
                }
            }
            return best;
        }
    }
}
