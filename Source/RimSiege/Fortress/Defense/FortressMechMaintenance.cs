using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using ProjectZ;
using RimSiege.Integration;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Fortress
{
    public sealed class JobGiver_MechFortressReload : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            PawnDuty duty = pawn.mindState?.duty;
            if (duty?.tag != FortressDefensePosts.ReloadTag ||
                pawn.GetLord()?.LordJob is not LordJob_MechFortressDefense { IsDefenseActive: true } ||
                duty.focusSecond.Thing is not Building_TurretGunCE { Spawned: true } gun ||
                gun.Faction != pawn.Faction) return null;
            return FortressSupportJobs.Reload(pawn, gun, threshold: 0.85f);
        }
    }

    internal static class FortressMechMaintenance
    {
        private static readonly JobGiver_MechFortressMaintenance Giver = new();

        internal static bool CanWorkAt(Pawn pawn, IntVec3 cell)
        {
            Job job = Giver.JobAt(pawn, cell, out _);
            if (job == null) return false;
            JobMaker.ReturnToPool(job);
            return true;
        }

        internal static Thing[] Projects(Map map, Faction faction, LordJob_MechFortressDefense defense) =>
            map.listerBuildingsRepairable.RepairableBuildings(faction).Cast<Thing>()
                .Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame))
                .Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint))
                .Where(thing => thing.Faction == faction && defense.CanMaintain(thing.Position) &&
                    !OccupiedByHostile(thing, map, faction))
                .ToArray();

        internal static Thing FindProject(Pawn builder, Thing[] projects, HashSet<IntVec3> assigned,
            ref int cursor, out bool skipped)
        {
            skipped = false;
            if (builder.WorkTypeIsDisabled(WorkTypeDefOf.Construction)) return null;
            Map map = builder.Map;
            Thing[] available = projects
                .Where(thing => !assigned.Contains(thing.Position))
                .OrderBy(Priority)
                .ThenBy(thing => Api.Distance3D(builder.Position, thing.Position, map))
                .ToArray();
            const int nearestCount = 4;
            const int extraCount = 2;
            skipped = available.Length > nearestCount + extraCount;
            if (available.Length == 0) return null;

            var candidates = available.Take(nearestCount);
            int remaining = available.Length - nearestCount;
            if (remaining > 0)
            {
                int offset = cursor % remaining;
                cursor = (cursor + extraCount) & int.MaxValue;
                candidates = candidates.Concat(Enumerable.Range(0, Math.Min(extraCount, remaining))
                    .Select(i => available[nearestCount + (offset + i) % remaining]));
            }

            foreach (Thing project in candidates)
            {
                if (!builder.CanReach(project, PathEndMode.Touch, builder.NormalMaxDanger())) continue;
                if (CanWorkAt(builder, project.Position)) return project;
            }
            return null;
        }

        private static int Priority(Thing project)
        {
            if (project is Building_Turret) return 0;
            bool isTurretPlan = project.def.entityDefToBuild is ThingDef { building.turretGunDef: not null };
            if (project is Frame) return isTurretPlan ? 1 : 4;
            if (project is Blueprint) return isTurretPlan ? 2 : 5;
            return 3;
        }

        internal static bool OccupiedByHostile(Thing project, Map map, Faction faction) =>
            project is Blueprint or Frame && OccupiedByHostile(project.OccupiedRect(), map, faction);

        internal static bool OccupiedByHostile(CellRect footprint, Map map, Faction faction)
        {
            foreach (IntVec3 cell in footprint)
            {
                if (!cell.InBounds(map)) continue;
                if (cell.GetThingList(map).Any(thing => thing is Pawn { Dead: false } pawn && pawn.HostileTo(faction)))
                    return true;
            }
            return false;
        }
    }

    public sealed class JobGiver_MechFortressMaintenance : JobGiver_Work
    {
        public override ThinkResult TryIssueJobPackage(Pawn pawn, JobIssueParams jobParams)
        {
            PawnDuty duty = pawn.mindState?.duty;
            if (duty?.tag != FortressMaintenance.DutyTag ||
                pawn.GetLord()?.LordJob is not LordJob_MechFortressDefense defense ||
                !defense.CanMaintain(duty.focusSecond.Cell)) return ThinkResult.NoJob;
            Job job = JobAt(pawn, duty.focusSecond.Cell, out WorkGiverDef worker);
            return job != null ? new ThinkResult(job, this, worker.tagToGive) : ThinkResult.NoJob;
        }

        internal Job JobAt(Pawn pawn, IntVec3 cell, out WorkGiverDef source)
        {
            Map map = pawn.Map;
            IntVec3 haulCenter = IntVec3.Invalid;
            float haulRadius = 0f;
            if (pawn.GetLord()?.LordJob is LordJob_MechFortressDefense defense)
            {
                haulCenter = defense.Ground.Geometry.Outer.CenterCell;
                haulRadius = defense.Ground.MechLogisticsRadius;
            }
            List<Thing> things = cell.GetThingList(map);
            foreach (WorkGiverDef def in WorkTypeDefOf.Construction.workGiversByPriority)
            {
                if (def.Worker is not WorkGiver_Scanner scanner ||
                    scanner is not (WorkGiver_Repair or WorkGiver_ConstructFinishFrames or WorkGiver_ConstructDeliverResources))
                    continue;
                if (!things.Any(thing => thing.Faction == pawn.Faction && scanner.PotentialWorkThingRequest.Accepts(thing) &&
                    !FortressMechMaintenance.OccupiedByHostile(thing, map, pawn.Faction)))
                    continue;
                Job job = NativeConstruction.AtForFortressMech(this, pawn, scanner, cell, haulCenter, haulRadius);
                if (job == null) continue;
                source = def;
                return job;
            }
            source = null;
            return null;
        }
    }
}
