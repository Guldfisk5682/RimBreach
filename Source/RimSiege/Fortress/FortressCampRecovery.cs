using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using RimSiege.Raids.Artillery;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Fortress
{
    [DefOf]
    internal static class FortressAlliedDutyDefOf
    {
        public static DutyDef RS_FortressRecoverCamp = null;

        static FortressAlliedDutyDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(FortressAlliedDutyDefOf));
    }

    internal sealed class LordToil_FortressRecoverCamp : LordToil_ExitMap
    {
        internal LordToil_FortressRecoverCamp() : base(LocomotionUrgency.Jog, canDig: false, interruptCurrentJob: true)
        {
            useAvoidGrid = true;
        }

        public override DutyDef ExitDuty => FortressAlliedDutyDefOf.RS_FortressRecoverCamp;

        public override void Init()
        {
            base.Init();
            ArtilleryRaid raid = Map.GetComponent<ArtilleryMap>().ForLord(lord);
            if (raid != null && (raid.CampLord == lord || raid.CampLord == null))
                ArtilleryCamp.Abandon(raid, Map, retainWithAssault: false);
        }
    }

    public sealed class JobGiver_FortressRecoverCamp : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (FortressSite.SupportSite(pawn) == null || pawn.HostileTo(Faction.OfPlayer) || !pawn.RaceProps.Humanlike ||
                pawn.carryTracker.CarriedThing != null || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
                return null;
            Map map = pawn.Map;
            ArtilleryMap artillery = map.GetComponent<ArtilleryMap>();
            ArtilleryRaid raid = artillery.ForLord(pawn.GetLord());
            if (raid?.Camp == null || raid.CampLord?.CurLordToil is { } toil &&
                toil is not (LordToil_ExitMap or LordToil_PanicFlee)) return null;

            var guns = ArtilleryCamp.Placements(raid.Camp).Select(item => RecoverableGun(item.Building, raid.Faction));
            var ammo = artillery.SupplyOwnership.ItemsFor(raid)
                .Where(item => item.def is AmmoDef || raid.Camp.Guns.Any(gun => gun.Ammo == item.def));
            List<Thing> targets = guns.Concat(ammo).Where(item => item is { Spawned: true } && item.Map == map &&
                !item.IsForbidden(pawn) && !item.IsBurning() &&
                item.TryGetComp<CompExplosive>()?.wickStarted != true).ToList();
            if (targets.Count == 0 || GenAI.InDangerousCombat(pawn) ||
                !RCellFinder.TryFindBestExitSpot(pawn, out IntVec3 exit, canBash: false)) return null;

            float capacity = pawn.GetStatValue(StatDefOf.CarryingCapacity);
            foreach (Thing thing in targets.OrderBy(item => item is Building or MinifiedThing ? 0 : 1)
                .ThenBy(item => pawn.Position.DistanceToSquared(item.Position)))
            {
                int count = UnityEngine.Mathf.Min(thing.stackCount, (int)(capacity / thing.def.VolumePerUnit));
                if (count <= 0 || !pawn.CanReserveAndReach(thing, PathEndMode.ClosestTouch, Danger.Some)) continue;
                Job job = JobMaker.MakeJob(JobDefOf.Steal, thing, exit);
                job.count = count;
                return job;
            }
            return null;
        }

        private static Thing RecoverableGun(Building building, Faction faction)
        {
            if (building is not Building_Turret || building.Destroyed || !building.def.Minifiable ||
                building.Faction != null && building.Faction != faction) return null;
            return building.Spawned ? building : building.ParentHolder as MinifiedThing;
        }
    }
}
