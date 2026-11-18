using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Raids.Artillery
{
    // Sustained smoke at an occupied position uses the same cast and grenade
    // ownership as the march, with its own finite targets and supply stock.
    internal sealed class ArtilleryLocalSmoke : IExposable
    {
        private const int ResupplyInterval = 600;
        internal ArtillerySmoke Work;
        internal Thing Supply;
        private int nextSupplyTick;

        public ArtilleryLocalSmoke() { }

        internal ArtilleryLocalSmoke(ArtillerySmoke opening, IntVec3 focus)
        {
            Work = opening.LocalScreen(focus);
        }

        internal void Review(ArtilleryRaid raid, Lord camp, IReadOnlyCollection<Pawn> carriers, int now,
            System.Predicate<IntVec3> dropPosition = null)
        {
            foreach (SmokeAssignment assignment in Work.Assignments.ToArray())
            {
                if (carriers.Contains(assignment.Pawn)) continue;
                Work.ReleaseCarrier(assignment);
                Work.Assignments.Remove(assignment);
            }
            if (Work.Targets.Count == 0) return;
            foreach (Pawn pawn in carriers)
            {
                if (Work.AssignmentFor(pawn) is { } existing)
                {
                    if (pawn.CurJobDef != ArtillerySmokeDefOf.RS_LayArtillerySmoke) existing.Fighting = false;
                }
                else Work.Assignments.Add(new SmokeAssignment { Pawn = pawn, LoadoutRestored = true });
            }

            if (now < nextSupplyTick || IsOutstanding(camp.Map) ||
                !carriers.Any(pawn => Work.FindSmokeWeapon(pawn) == null &&
                    pawn.CurJobDef != ArtillerySmokeDefOf.RS_LayArtillerySmoke)) return;

            nextSupplyTick = now + ResupplyInterval;
            Thing payload = ThingMaker.MakeThing(ArtillerySmokeDefOf.CE_Weapon_GrenadeSmoke);
            payload.stackCount = System.Math.Min(ArtillerySmoke.GrenadesPerCarrier * carriers.Count, payload.def.stackLimit);
            var groups = new List<List<Thing>> { new() { payload } };
            if (CampSupplies.DropGroups(camp.Map, raid, groups, 110, instaDrop: false,
                leaveSlag: false, canRoofPunch: true, forbid: false, allowFogged: true, faction: raid.Faction,
                dropCenter: Work.Rally, dropPosition: dropPosition)) Supply = payload;
            else CampSupplies.Discard(groups);
        }

        private bool IsOutstanding(Map map) => Supply is { Destroyed: false } &&
            (Supply.Spawned && Supply.Map == map || Supply.ParentHolder is ActiveTransporterInfo);

        internal Job Collect(Pawn pawn)
        {
            if (!Work.HasCarrier(pawn) || Supply is not { Spawned: true } || Supply.Map != pawn.Map ||
                Work.FindSmokeWeapon(pawn) != null) return null;
            if (pawn.CurJobDef == JobDefOf.TakeInventory && pawn.CurJob.targetA.Thing == Supply) return pawn.CurJob;
            int count = System.Math.Min(ArtillerySmoke.GrenadesPerCarrier, Supply.stackCount);
            if (!pawn.CanReserveAndReach(Supply, PathEndMode.ClosestTouch, Danger.Deadly, maxPawns: 10, stackCount: count)) return null;
            Job job = JobMaker.MakeJob(JobDefOf.TakeInventory, Supply);
            job.count = count;
            return job;
        }

        public void ExposeData()
        {
            Scribe_Deep.Look(ref Work, "work");
            Scribe_References.Look(ref Supply, "supply");
            Scribe_Values.Look(ref nextSupplyTick, "nextSupplyTick");
        }
    }

    public sealed class JobGiver_CollectArtillerySmoke : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn) =>
            (pawn.GetLord()?.LordJob as LordJob_Artillery)?.Raid.ActiveLocalSmokeFor(pawn)?.Collect(pawn);
    }
}
