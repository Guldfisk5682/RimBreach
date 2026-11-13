using RimWorld;
using ProjectZ;
using Verse;
using Verse.AI;

namespace RimSiege.Raids
{
    public sealed class ThinkNode_ConditionalLadderDuty : ThinkNode_Conditional
    {
        protected override bool Satisfied(Pawn pawn) => LadderPerch.Get(pawn) != null ||
            !SiegeRoles.FireSupportMech(pawn) && (LadderInventory.Get(pawn) != null ||
                pawn.Map?.GetComponent<SiegeRaidMap>().RecoveryFor(pawn) != null);
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
                if (independent != null) return independent;
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
            if (party.Planner.HasClearance)
            {
                Job clearing = party.Plan(pawn, placing: false);
                if (clearing != null) return clearing;
            }
            return raids.Recovery(pawn, party);
        }

        private static IntVec3 ExistingRouteStand(Pawn pawn, IntVec3 destination)
        {
            Map map = pawn.Map;
            foreach (IntVec3 cell in GenAdjFast.AdjacentCells8Way(destination))
                if (cell.InBounds(map) && cell.Standable(map) && pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly))
                    return cell;
            return IntVec3.Invalid;
        }
    }
}
