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
            if (raids?.Applies(pawn) != true || raids.Party(pawn) is not { } party) return false;
            if (SiegeRoles.MeleeAssault(pawn))
                return LadderInventory.Get(pawn) != null || raids.RecoveryFor(pawn) != null;
            return LadderInventory.Get(pawn) != null && !party.HasAssaultLadder(pawn.Faction);
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
            return party.HasLadderRoute(pawn);
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
            // Carriers and perched clearers take priority over ordinary combat;
            // the later assault node also lets other troops reuse built routes.
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
            IntVec3 existingRoute = party.Planner.Destination(pawn, false);
            if (existingRoute.IsValid)
            {
                // The planner's destination is reachable by touch and can be a building.
                if (pawn.Position.AdjacentTo8WayOrInside(existingRoute)) return null;
                IntVec3 stand = existingRoute.Standable(pawn.Map) ? existingRoute : ExistingRouteStand(pawn, existingRoute);
                if (stand.IsValid) return JobMaker.MakeJob(JobDefOf.Goto, stand, 500, checkOverrideOnExpiry: true);
            }

            if (carrier) return null;
            if (party.Planner.HasUnclaimedClearance(pawn, party.Members))
            {
                Job clearing = party.Plan(pawn, placing: false);
                if (clearing != null && clearing.def != JobDefOf.Wait) return clearing;
            }
            return raids.Recovery(pawn, party);
        }

        private static IntVec3 ExistingRouteStand(Pawn pawn, IntVec3 destination)
        {
            Map map = pawn.Map;
            foreach (IntVec3 offset in GenAdj.AdjacentCells)
            {
                IntVec3 cell = destination + offset;
                if (cell.InBounds(map) && cell.Standable(map) && pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly))
                    return cell;
            }
            return IntVec3.Invalid;
        }
    }
}
