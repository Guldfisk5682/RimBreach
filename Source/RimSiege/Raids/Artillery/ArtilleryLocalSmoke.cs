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
        private const int ResupplyInterval = 300;
        internal ArtillerySmoke Work;
        internal Thing Supply;
        private int nextSupplyTick;

        public ArtilleryLocalSmoke() { }

        internal ArtilleryLocalSmoke(ArtillerySmoke opening, IntVec3 focus) : this(opening.LocalScreen(focus)) { }

        internal ArtilleryLocalSmoke(ArtillerySmoke work, ArtilleryLocalSmoke previous = null)
        {
            Work = work;
            Work.IsLocal = true;
            if (previous != null)
            {
                Supply = previous.Supply;
                nextSupplyTick = previous.nextSupplyTick;
            }
        }

        internal void Review(ArtilleryRaid raid, Lord camp, IReadOnlyCollection<Pawn> carriers, int now,
            System.Predicate<IntVec3> dropPosition = null, bool issueInitialKit = false)
        {
            foreach (SmokeAssignment assignment in Work.Assignments.ToArray())
            {
                if (carriers.Contains(assignment.Pawn)) continue;
                Work.ReleaseCarrier(assignment);
                Work.Assignments.Remove(assignment);
            }
            if (Work.Targets.Count == 0) return;
            foreach (SmokeTarget target in Work.Targets)
                if (!target.Required && !target.Covered && ArtillerySmoke.Screened(target.Cell, camp.Map))
                    target.Covered = true;
            Work.PruneSteppingStones(camp.Map);
            ThingDef grenadeDef = Work.SmokeWeapon ?? ArtillerySmokeDefOf.CE_Weapon_GrenadeSmoke;
            foreach (Pawn pawn in carriers)
            {
                if (Work.AssignmentFor(pawn) is { } existing)
                {
                    if (pawn.CurJobDef != ArtillerySmokeDefOf.RS_LayArtillerySmoke) existing.Fighting = false;
                }
                else
                {
                    if (issueInitialKit && Work.FindSmokeWeapon(pawn) == null &&
                        SmokeCombat.GiveSmoke(pawn, grenadeDef, ArtillerySmoke.GrenadesPerCarrier) == null)
                        continue;
                    Work.Assignments.Add(new SmokeAssignment { Pawn = pawn, LoadoutRestored = true });
                }
            }

            int activeCarriers = Work.Assignments.Count;
            bool encirclement = camp.LordJob is Encirclement.LordJob_EncirclementCamp;
            if (now < nextSupplyTick || activeCarriers == 0 || IsOutstanding(camp.Map, activeCarriers, encirclement)) return;
            int totalStock = 0;
            bool anyEmpty = false;
            foreach (SmokeAssignment assignment in Work.Assignments)
            {
                Pawn pawn = assignment.Pawn;
                int held = SmokeCombat.CountSmokeWeapon(pawn, grenadeDef);
                totalStock += held;
                if (held == 0 && pawn.CurJobDef != ArtillerySmokeDefOf.RS_LayArtillerySmoke) anyEmpty = true;
            }
            if (!anyEmpty && totalStock > activeCarriers * 2) return;

            nextSupplyTick = now + ResupplyInterval;
            Thing payload = ThingMaker.MakeThing(grenadeDef);
            payload.stackCount = System.Math.Min(ArtillerySmoke.GrenadesPerCarrier * activeCarriers, payload.def.stackLimit);
            var groups = new List<List<Thing>> { new() { payload } };
            System.Predicate<IntVec3> safeDrop = c => (dropPosition == null || dropPosition(c)) &&
                Work.CanAdvance(c, camp.Map);
            bool dropped = false;
            if (encirclement)
            {
                var centers = new List<IntVec3>();
                if (Work.CanAdvance(Work.Rally, camp.Map)) centers.Add(Work.Rally);
                foreach (SmokeAssignment a in Work.Assignments)
                    if (a.Pawn is { Spawned: true } p && Work.CanAdvance(p.Position, camp.Map) &&
                        !centers.Any(c => c.InHorDistOf(p.Position, 6f)))
                        centers.Add(p.Position);
                if (camp.LordJob is Encirclement.LordJob_EncirclementCamp { Campaign.Plan.Rear: { IsValid: true } rear } &&
                    Work.CanAdvance(rear, camp.Map) && !centers.Any(c => c.InHorDistOf(rear, 6f)))
                    centers.Add(rear);
                foreach (IntVec3 center in centers)
                {
                    if (!CampSupplies.DropGroups(camp.Map, raid, groups, 60, instaDrop: false,
                        leaveSlag: false, canRoofPunch: true, forbid: false, allowFogged: true, faction: raid.Faction,
                        dropCenter: center, dropPosition: safeDrop)) continue;
                    dropped = true;
                    break;
                }
            }
            else
            {
                dropped = CampSupplies.DropGroups(camp.Map, raid, groups, 60, instaDrop: false,
                    leaveSlag: false, canRoofPunch: true, forbid: false, allowFogged: true, faction: raid.Faction,
                    dropCenter: Work.Rally, dropPosition: safeDrop) ||
                    CampSupplies.DropGroups(camp.Map, raid, groups, 60, instaDrop: false,
                    leaveSlag: false, canRoofPunch: true, forbid: false, allowFogged: true, faction: raid.Faction,
                    dropCenter: Work.Rally, dropPosition: dropPosition);
            }
            if (dropped) Supply = payload;
            else CampSupplies.Discard(groups);
        }

        private bool IsOutstanding(Map map, int carriers, bool requireSafe) => Supply is { Destroyed: false } &&
            (Supply.ParentHolder is ActiveTransporterInfo ||
                Supply.Spawned && Supply.Map == map && Supply.stackCount >= System.Math.Min(2, carriers) &&
                (!requireSafe || Work.CanAdvance(Supply.Position, map)));

        internal Job Collect(Pawn pawn)
        {
            if (!Work.HasCarrier(pawn) || Supply is not { Spawned: true } || Supply.Map != pawn.Map ||
                Work.FindSmokeWeapon(pawn) != null) return null;
            if (pawn.GetLord()?.LordJob is Encirclement.LordJob_EncirclementCamp { Campaign: { } siege } &&
                (!siege.CellSafeForCamp(Supply.Position) || !siege.HasSafePath(pawn, Supply.Position, PathEndMode.ClosestTouch)))
                return null;
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
