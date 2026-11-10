using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ProjectZ;
using RimSiege.Integration;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Fortress
{
    [HarmonyPatch(typeof(ThingUtility), nameof(ThingUtility.CheckAutoRebuildOnDestroyed))]
    internal static class FortressRebuildBlueprintPatch
    {
        private static void Postfix(Thing thing, DestroyMode mode, Map map, BuildableDef buildingDef, ref Blueprint_Build __result)
        {
            if (__result != null || mode != DestroyMode.KillFinalize || thing is not Building ||
                buildingDef.blueprintDef == null || map.Parent is not FortressSite { Blueprint: { } fortress } ||
                thing.Faction == null || thing.Faction == Faction.OfPlayer || thing.Faction != map.ParentFaction ||
                !fortress.Outer.Contains(Api.GroundCellOf(thing.Position, map))) return;
            if (GenConstruct.CanPlaceBlueprintAt(buildingDef, thing.Position, thing.Rotation, map, stuffDef: thing.Stuff).Accepted)
                __result = GenConstruct.PlaceBlueprintForBuild(buildingDef, thing.Position, map, thing.Rotation,
                    thing.Faction, thing.Stuff, thing.StyleSourcePrecept, thing.StyleDef);
        }
    }

    internal static class FortressMaintenance
    {
        internal const string DutyTag = "maintenance";
        private static readonly JobGiver_FortressMaintenance Giver = new();

        internal static Thing[] Projects(Map map, Faction faction, LordJob_FortressDefense defense) =>
            map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame)
                .Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint))
                .Concat(map.listerBuildingsRepairable.RepairableBuildings(faction))
                .Where(thing => thing.Faction == faction && defense.CanMaintain(thing.Position)).ToArray();

        internal static Thing FindProject(Pawn builder, Thing[] projects, HashSet<IntVec3> assigned)
        {
            Map map = builder.Map;
            var available = projects.Where(thing => !assigned.Contains(thing.Position))
                .OrderBy(thing => thing is Frame ? 0 : thing is Blueprint ? 1 : 2)
                .ThenBy(thing => Api.Distance3D(builder.Position, thing.Position, map));
            using var candidates = available.GetEnumerator();
            if (!candidates.MoveNext()) return null;
            builder.workSettings ??= new Pawn_WorkSettings(builder);
            builder.workSettings.EnableAndInitializeIfNotAlreadyInitialized();
            if (!builder.workSettings.WorkIsActive(WorkTypeDefOf.Construction))
                builder.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
            do
            {
                Thing project = candidates.Current;
                if (!builder.CanReach(project, PathEndMode.Touch, builder.NormalMaxDanger())) continue;
                Job job = Giver.JobAt(builder, project.Position, out _);
                if (job == null) continue;
                JobMaker.ReturnToPool(job);
                return project;
            } while (candidates.MoveNext());
            return null;
        }
    }

    public sealed class JobGiver_FortressMaintenance : JobGiver_Work
    {
        public override ThinkResult TryIssueJobPackage(Pawn pawn, JobIssueParams jobParams)
        {
            PawnDuty duty = pawn.mindState.duty;
            if (duty.tag != FortressMaintenance.DutyTag ||
                pawn.GetLord()?.LordJob is not LordJob_FortressDefense defense ||
                !defense.CanMaintain(duty.focusSecond.Cell)) return ThinkResult.NoJob;
            Job job = JobAt(pawn, duty.focusSecond.Cell, out WorkGiverDef worker);
            return job != null ? new ThinkResult(job, this, worker.tagToGive) : ThinkResult.NoJob;
        }

        internal Job JobAt(Pawn pawn, IntVec3 cell, out WorkGiverDef source)
        {
            List<Thing> things = cell.GetThingList(pawn.Map);
            foreach (WorkGiver worker in pawn.workSettings.WorkGiversInOrderNormal)
            {
                if (worker is not (WorkGiver_ConstructFinishFrames or WorkGiver_ConstructDeliverResources or WorkGiver_Repair))
                    continue;
                var scanner = (WorkGiver_Scanner)worker;
                if (!things.Any(thing => thing.Faction == pawn.Faction && scanner.PotentialWorkThingRequest.Accepts(thing)))
                    continue;
                Job job = NativeConstruction.At(this, pawn, worker, cell);
                if (job == null) continue;
                source = worker.def;
                return job;
            }
            source = null;
            return null;
        }
    }
}
