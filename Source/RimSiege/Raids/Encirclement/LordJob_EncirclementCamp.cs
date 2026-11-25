using System.Linq;
using System.Collections.Generic;
using CombatExtended;
using CombatExtended.CombatExtended.Jobs.Utils;
using HarmonyLib;
using RimSiege.Fortress;
using RimSiege.Integration;
using RimSiege.Raids.Artillery;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Raids.Encirclement
{
    public sealed class LordJob_EncirclementCamp : LordJob
    {
        private Quest campaignQuest;
        private QuestPart_Encirclement campaign;

        public LordJob_EncirclementCamp() { }
        internal LordJob_EncirclementCamp(QuestPart_Encirclement siege)
        {
            campaign = siege;
            campaignQuest = siege.quest;
        }

        internal QuestPart_Encirclement Campaign => campaign ??=
            campaignQuest?.PartsListForReading.OfType<QuestPart_Encirclement>().FirstOrDefault();
        public override bool AddFleeToil => false;

        internal static bool Builder(Pawn pawn) => pawn is { Spawned: true, Dead: false, Downed: false, InMentalState: false } &&
            pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) &&
            (MechBattery.IsConstructoid(pawn) || pawn.RaceProps.Humanlike && pawn.skills != null &&
                !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Construction));

        public override StateGraph CreateGraph()
        {
            var graph = new StateGraph();
            graph.AddToil(new LordToil_EncirclementCamp());
            return graph;
        }

        public override void Notify_PawnLost(Pawn pawn, PawnLostCondition condition)
        {
            if (condition is PawnLostCondition.Killed or PawnLostCondition.Incapped)
            {
                Campaign?.SignalReview();
                Campaign?.ScreenFor(pawn)?.Work.OnPawnDowned(pawn);
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref campaignQuest, "quest");
        }
    }

    internal sealed class LordToil_EncirclementCamp : LordToil
    {
        private QuestPart_Encirclement Siege => ((LordJob_EncirclementCamp)lord.LordJob).Campaign;
        public override IntVec3 FlagLoc => Siege?.Plan?.Rear ?? IntVec3.Invalid;
        public override bool AllowSatisfyLongNeeds => Siege?.OnAlert != true;

        public override void UpdateAllDuties()
        {
            QuestPart_Encirclement siege = Siege;
            if (siege?.Plan == null) return;
            foreach (Pawn pawn in lord.ownedPawns)
            {
                // A native CutPlant job keeps its progress on the job driver. Do not cancel it when the crew is reviewed.
                if (siege.ClearingBattery(pawn) >= 0) continue;
                DutyDef duty;
                LocalTargetInfo focus;
                int builderBattery = siege.BuilderBattery(pawn);
                IntVec3 rear = siege.Phase == 3 ? siege.Plan.Rear : siege.RearStation(pawn);
                ArtilleryPlacement gun = siege.GunFor(pawn, out ArtilleryRaid battery);
                Pawn patient = siege.PatientFor(pawn);
                bool waitingForDoctor = siege.WaitingForDoctor(pawn);
                if (waitingForDoctor)
                {
                    duty = EncirclementDefs.RS_EncirclementReserve;
                    focus = pawn.Position;
                }
                else if (siege.ScreenFor(pawn)?.Work is { } screen && screen.HasCarrier(pawn))
                {
                    duty = EncirclementDefs.RS_EncirclementSmoke;
                    focus = screen.Rally;
                }
                else if (gun != null)
                {
                    duty = EncirclementDefs.RS_EncirclementGunner;
                    focus = gun.Building?.Spawned == true ? gun.Cell :
                        siege.BatteryRally(siege.Plan.BatteryKeys.IndexOf(battery.Key));
                }
                else if (siege.Escortee(pawn) is { } builder)
                {
                    duty = DutyDefOf.Escort;
                    focus = builder;
                }
                else if (patient != null || pawn.RaceProps.Humanlike && !siege.OnAlert &&
                    (siege.IsRestingCrew(pawn) || QuestPart_Encirclement.NeedsRest(pawn)))
                {
                    duty = EncirclementDefs.RS_EncirclementReserve;
                    focus = patient?.Position ?? rear;
                }
                else if (LordJob_EncirclementCamp.Builder(pawn) && (!siege.OnAlert || !QuestPart_Encirclement.NeedsRest(pawn)))
                {
                    duty = EncirclementDefs.RS_EncirclementBuild;
                    focus = builderBattery > 0 ? siege.BatteryRally(builderBattery) : rear;
                    pawn.workSettings ??= new Pawn_WorkSettings(pawn);
                    pawn.workSettings.EnableAndInitialize();
                    if (pawn.workSettings.GetPriority(WorkTypeDefOf.Construction) == 0)
                        pawn.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                }
                else
                {
                    duty = EncirclementDefs.RS_EncirclementSentry;
                    focus = rear;
                }
                PawnDuty previous = pawn.mindState.duty;
                bool reassigned = previous == null || previous.def != duty || previous.focus != focus ||
                    previous.focusSecond.Pawn != patient;
                if (reassigned)
                    pawn.mindState.duty = new PawnDuty(duty, focus, patient,
                        duty == DutyDefOf.Escort ? EncirclementMechEscort.Radius : 16f);
                Job current = pawn.CurJob;
                if (current == null) continue;
                if (waitingForDoctor && !pawn.Downed && current.def != JobDefOf.Wait_MaintainPosture &&
                    current.def != JobDefOf.LayDown ||
                    siege.OnAlert && !waitingForDoctor && (current.def == JobDefOf.LayDown || current.def == JobDefOf.Ingest) ||
                    current.def == JobDefOf.ManTurret && current.targetA.Thing is Building target &&
                        target != gun?.Building && ArtilleryRegistry.Find(target)?.Encirclement == true ||
                    reassigned && (current.workGiverDef?.workType == WorkTypeDefOf.Construction ||
                        current.jobGiver is JobGiver_WanderNearDutyLocation or JobGiver_EncirclementDeployment or
                            JobGiver_EncirclementSmokeSupply ||
                        current.jobGiver is JobGiver_EncirclementTend &&
                            FortressSupportJobs.Patient(current) != patient ||
                        current.def == JobDefOf.FollowClose && previous?.def == DutyDefOf.Escort ||
                        previous?.def == EncirclementDefs.RS_EncirclementSmoke && duty != previous.def &&
                        (current.def == ArtillerySmokeDefOf.RS_LayArtillerySmoke ||
                            current.def == ArtillerySmokeDefOf.RS_ApproachSmokeThrow)))
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
            }
        }

        public override void Notify_PawnDamaged(Pawn victim, DamageInfo damage)
        {
            if (damage.Instigator is { } attacker && attacker.HostileTo(lord.faction))
                Siege?.AlertAt(victim.Position);
        }

        public override void Notify_ConstructionCompleted(Pawn pawn, Building building) => Siege?.SignalReview();
        public override void Notify_BuildingDespawnedOnMap(Building building)
        {
            if (building.Faction == lord.faction) Siege?.SignalReview();
        }
    }

    [HarmonyPatch(typeof(Lord), nameof(Lord.Notify_BuildingDamaged))]
    internal static class EncirclementBuildingAlert
    {
        private static void Postfix(Lord __instance, Building building, DamageInfo dinfo)
        {
            if (__instance.LordJob is LordJob_EncirclementCamp { Campaign: { Work: { } work } siege } &&
                dinfo.Instigator is { } attacker && attacker.HostileTo(__instance.faction) &&
                work.OwnsBuilding(building))
                siege.AlertAt(building.Position);
        }
    }

    public sealed class JobGiver_EncirclementRetreatFromTurrets : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (pawn?.GetLord()?.LordJob is not LordJob_EncirclementCamp { Campaign: { } siege } ||
                !pawn.RaceProps.Humanlike || siege.CellSafeForCamp(pawn.Position) ||
                siege.WaitingForDoctor(pawn) || pawn.CurJobDef == JobDefOf.ManTurret ||
                pawn.CurJobDef == JobDefOf.Rescue && pawn.carryTracker?.CarriedThing is Pawn)
                return null;
            return JobGiver_EncirclementSafeWander.MakeShelterJob(pawn, siege);
        }
    }

    public class JobGiver_EncirclementFight : JobGiver_AIFightEnemies
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            Job job = base.TryGiveJob(pawn);
            if (job != null && job.def == JobDefOf.Goto && pawn.RaceProps.Humanlike &&
                pawn.GetLord()?.LordJob is LordJob_EncirclementCamp { Campaign: { } siege } &&
                job.targetA.Cell.IsValid && !siege.HasSafePath(pawn, job.targetA.Cell))
            {
                JobMaker.ReturnToPool(job);
                return null;
            }
            return job;
        }
    }

    public sealed class JobGiver_EncirclementBuildFight : JobGiver_EncirclementFight
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            float saved = targetAcquireRadius;
            if (pawn.GetLord()?.LordJob is LordJob_EncirclementCamp { Campaign: { } siege } &&
                siege.BuilderBattery(pawn) > 0 && SmokeCombat.Screened(pawn.Position, pawn.Map))
                targetAcquireRadius = 8f;
            try { return base.TryGiveJob(pawn); }
            finally { targetAcquireRadius = saved; }
        }
    }

    public sealed class JobGiver_EncirclementWork : JobGiver_Work
    {
        public override ThinkResult TryIssueJobPackage(Pawn pawn, JobIssueParams jobParams)
        {
            if (!LordJob_EncirclementCamp.Builder(pawn) ||
                pawn.GetLord()?.LordJob is not LordJob_EncirclementCamp { Campaign: { Work: { } work } campaign })
                return ThinkResult.NoJob;
            if (campaign.ClearingBattery(pawn) >= 0)
                return new ThinkResult(pawn.CurJob, this, pawn.CurJob.workGiverDef.tagToGive);

            Map map = pawn.Map;
            int battery = campaign.BuilderBattery(pawn);
            IEnumerable<ArtilleryRaid> batteries = campaign.Plan.Batteries(map);
            if (campaign.UsesForwardCrews) batteries = batteries.Where(r => r == campaign.Battery(battery));
            if (campaign.Mechanoid && JobGiver_EncirclementGunReload.Find(pawn, batteries) is { } reload)
                return new ThinkResult(reload, this, JobTag.Misc);
            if (battery == 0 && RoofJob(pawn, work) is { } roofJob)
                return new ThinkResult(roofJob, this, roofJob.workGiverDef.tagToGive);

            var projects = work.ActiveProjects.Where(p => p.Current is { Spawned: true } &&
                    (!campaign.UsesForwardCrews || p.Battery == battery))
                .OrderBy(p => IsoFloor.Distance3D(pawn.Position, p.Cell, map)).Take(18);
            foreach (EncirclementProject project in projects)
            {
                Thing target = project.Current;
                if (pawn.RaceProps.Humanlike && !campaign.CellSafeForCamp(project.Cell)) continue;
                if (target is not Blueprint and not Frame && work.UnderFire(map, project.Cell) &&
                    !SmokeCombat.Screened(project.Cell, map)) continue;
                foreach (WorkGiver worker in pawn.workSettings.WorkGiversInOrderNormal)
                {
                    if (worker is not WorkGiver_ConstructFinishFrames and not WorkGiver_ConstructDeliverResources and not WorkGiver_Repair)
                        continue;
                    var scanner = (WorkGiver_Scanner)worker;
                    if (!scanner.PotentialWorkThingRequest.Accepts(target) ||
                        !pawn.CanReach(target, scanner.PathEndMode, scanner.MaxPathDanger(pawn))) continue;
                    if (pawn.RaceProps.Humanlike && !campaign.HasSafePath(pawn, project.Cell, scanner.PathEndMode))
                        continue;
                    Job job = NativeConstruction.AtForFortressMech(this, pawn, worker, project.Cell,
                        campaign.Battery(project.Battery)?.Camp.Center ?? campaign.Plan.Rear, EncirclementWork.MaterialRadius);
                    if (job == null) continue;
                    if (pawn.RaceProps.Humanlike && job.targetA.Thing is { Spawned: true } haulItem &&
                        haulItem != target && !campaign.HasSafePath(pawn, haulItem.Position, PathEndMode.ClosestTouch))
                    {
                        JobMaker.ReturnToPool(job);
                        continue;
                    }
                    return new ThinkResult(job, this, worker.def.tagToGive);
                }
            }

            return ThinkResult.NoJob;
        }

        private static Job RoofJob(Pawn pawn, EncirclementWork work)
        {
            Map map = pawn.Map;
            var roof = (WorkGiver_BuildRoof)DefDatabase<WorkGiverDef>.GetNamed("BuildRoofs").Worker;
            foreach (IntVec3 cell in work.RoofCells.Where(c => !c.Roofed(map))
                .OrderBy(c => IsoFloor.Distance3D(pawn.Position, c, map)).Take(12))
            {
                // NPC builders use the native scanner and roof construction job.
                if (cell.IsForbidden(pawn) || work.UnderFire(map, cell) || !roof.HasJobOnCell(pawn, cell)) continue;
                Job job = roof.JobOnCell(pawn, cell);
                if (job == null) continue;
                job.workGiverDef = roof.def;
                return job;
            }
            return null;
        }
    }

    public sealed class JobGiver_EncirclementNeeds : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (pawn.GetLord()?.LordJob is not LordJob_EncirclementCamp { Campaign: { } siege } ||
                !pawn.RaceProps.Humanlike) return null;

            if (siege.WaitingForDoctor(pawn))
                return pawn.CurJobDef == JobDefOf.LayDown ? pawn.CurJob :
                    JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture, 180, checkOverrideOnExpiry: true);
            if (siege.OnAlert) return null;

            if (pawn.needs?.food?.CurLevelPercentage < 0.30f && siege.Battery(0) is { } rear)
            {
                Thing ration = pawn.Map.GetComponent<ArtilleryMap>().SupplyOwnership.ItemsFor(rear)
                    .Where(t => t.Spawned && t.def == ThingDefOf.MealSurvivalPack && t.IngestibleNow &&
                        !t.IsForbidden(pawn) && siege.CellSafeForCamp(t.Position))
                    .OrderBy(t => t.Position.DistanceToSquared(pawn.Position))
                    .FirstOrDefault(t => pawn.CanReserveAndReach(t, PathEndMode.ClosestTouch, Danger.Some) &&
                        siege.HasSafePath(pawn, t.Position, PathEndMode.ClosestTouch));
                if (ration != null)
                {
                    var meal = JobMaker.MakeJob(JobDefOf.Ingest, ration);
                    meal.count = FoodUtility.WillIngestStackCountOf(pawn, ration.def, FoodUtility.NutritionForEater(pawn, ration));
                    return meal;
                }
            }

            bool patient = HealthAIUtility.ShouldSeekMedicalRest(pawn);
            if (!patient && !(pawn.needs?.rest?.CurLevelPercentage < 0.4f)) return null;
            if (!RestUtility.CanFallAsleep(pawn) || RestUtility.DisturbancePreventsLyingDown(pawn)) return null;
            Building_Bed bed = siege.Work.Projects.Where(p => p.Install &&
                    p.Current is Building_Bed { Spawned: true } b && b.Faction == pawn.Faction &&
                    siege.CellSafeForCamp(b.Position) && (patient || !b.Medical))
                .Select(p => (Building_Bed)p.Current)
                .OrderBy(b => b.Medical == patient ? 0 : 1)
                .ThenBy(b => IsoFloor.Distance3D(pawn.Position, b.Position, pawn.Map))
                .FirstOrDefault(b => RestUtility.IsValidBedFor(b, pawn, pawn, checkSocialProperness: false) &&
                    pawn.CanReserveAndReach(b, PathEndMode.OnCell, Danger.Some) &&
                    siege.HasSafePath(pawn, b.Position, PathEndMode.OnCell));
            return bed == null ? null : JobMaker.MakeJob(JobDefOf.LayDown, bed);
        }
    }

    internal static class JobGiver_EncirclementGunReload
    {
        internal static Job Find(Pawn pawn, IEnumerable<ArtilleryRaid> batteries)
        {
            foreach (ArtilleryPlacement slot in batteries.SelectMany(r => r.Camp.Guns)
                .Where(g => !g.NeedsOperator && g.Building is Building_TurretGunCE
                    { Spawned: true, CompAmmo: { HasMagazine: true, FullMagazine: false } user } turret &&
                    turret.Faction == pawn.Faction && !turret.isReloading &&
                    user.CurMagCount <= user.MagSize / 4 && g.Cell.InHorDistOf(pawn.Position, 35f))
                .OrderBy(g => pawn.Position.DistanceToSquared(g.Cell)))
            {
                var turret = (Building_TurretGunCE)slot.Building;
                if (JobGiverUtils_Reload.CanReload(pawn, turret, emergency: true))
                    return JobGiverUtils_Reload.MakeReloadJob(pawn, turret);
            }
            return null;
        }
    }

    public sealed class JobGiver_EncirclementGunner : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (pawn.GetLord()?.LordJob is not LordJob_EncirclementCamp { Campaign: { } siege }) return null;
            ArtilleryPlacement gun = siege.GunFor(pawn, out ArtilleryRaid raid);
            if (gun == null) return null;
            if (pawn.RaceProps.Humanlike && gun.Building is { Spawned: true } building &&
                pawn.CurJobDef != JobDefOf.ManTurret &&
                (!siege.CellSafeForCamp(building.InteractionCell) ||
                 !siege.HasSafePath(pawn, building.InteractionCell, PathEndMode.OnCell)))
                return null;
            Job job = ArtilleryGunnerPatch.AssignedJob(pawn, raid);
            if (job != null && pawn.RaceProps.Humanlike && job.def != JobDefOf.ManTurret &&
                job.targetA.Cell.IsValid && !siege.HasSafePath(pawn, job.targetA.Cell, PathEndMode.ClosestTouch))
            {
                JobMaker.ReturnToPool(job);
                return null;
            }
            return job;
        }
    }

    public sealed class JobGiver_EncirclementDeployment : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (pawn.GetLord()?.LordJob is not LordJob_EncirclementCamp { Campaign: { UsesHumanBatteryCrews: true } siege } ||
                siege.BuilderBattery(pawn) == 0 && (siege.GunFor(pawn, out ArtilleryRaid raid) == null ||
                    raid == siege.Battery(0))) return null;

            IntVec3 focus = pawn.mindState.duty.focus.Cell;
            Map map = pawn.Map;
            if (!focus.InBounds(map) || pawn.Position.InHorDistOf(focus, 5f)) return null;
            IntVec3 stand = CellFinder.RandomClosewalkCellNear(focus, map, 4,
                c => IsoFloor.SameFloor(c, focus, map) && c.Standable(map) && siege.CellSafeForCamp(c) &&
                    map.pawnDestinationReservationManager.CanReserve(c, pawn) &&
                    pawn.CanReach(c, PathEndMode.OnCell, Danger.Deadly));
            if (!stand.IsValid || !stand.Standable(map) || !siege.CellSafeForCamp(stand) ||
                !siege.HasSafePath(pawn, stand))
                stand = siege.SafeApproachCell(pawn, focus);
            if (!stand.IsValid || stand == pawn.Position)
                return siege.CellSafeForCamp(pawn.Position)
                    ? JobMaker.MakeJob(JobDefOf.Wait_Wander, 90, checkOverrideOnExpiry: true) : null;
            Job move = JobMaker.MakeJob(JobDefOf.Goto, stand, 180, checkOverrideOnExpiry: true);
            move.locomotionUrgency = LocomotionUrgency.Jog;
            return move;
        }
    }

    public sealed class JobGiver_EncirclementSafeWander : JobGiver_WanderNearDutyLocation
    {
        public JobGiver_EncirclementSafeWander()
        {
            wanderDestValidator = (pawn, cell, _) =>
                pawn?.GetLord()?.LordJob is not LordJob_EncirclementCamp { Campaign: { } siege } ||
                !pawn.RaceProps.Humanlike || siege.CellSafeForCamp(cell) && siege.HasSafePath(pawn, cell);
        }

        internal static Job MakeShelterJob(Pawn pawn, QuestPart_Encirclement siege)
        {
            Map map = pawn.Map;
            IntVec3 shelter = GenRadial.RadialCellsAround(pawn.Position, 14f, false)
                .Where(c => c.InBounds(map) && IsoFloor.SameFloor(c, pawn.Position, map) &&
                    c.Standable(map) && siege.CellSafeForCamp(c) &&
                    map.pawnDestinationReservationManager.CanReserve(c, pawn) &&
                    pawn.CanReach(c, PathEndMode.OnCell, Danger.Deadly))
                .OrderBy(c => c.DistanceToSquared(pawn.Position))
                .FirstOrDefault(IntVec3.Invalid);
            if (!shelter.IsValid && siege.Plan?.Rear is { IsValid: true } rear &&
                pawn.CanReach(rear, PathEndMode.OnCell, Danger.Deadly))
                shelter = rear;
            if (!shelter.IsValid || shelter == pawn.Position) return null;
            Job retreat = JobMaker.MakeJob(JobDefOf.Goto, shelter, 120, checkOverrideOnExpiry: true);
            retreat.locomotionUrgency = LocomotionUrgency.Sprint;
            return retreat;
        }

        protected override Job TryGiveJob(Pawn pawn)
        {
            if (pawn?.GetLord()?.LordJob is LordJob_EncirclementCamp { Campaign: { } siege } &&
                pawn.RaceProps.Humanlike && !siege.CellSafeForCamp(pawn.Position) &&
                MakeShelterJob(pawn, siege) is { } retreat)
                return retreat;
            return base.TryGiveJob(pawn);
        }
    }

    public sealed class JobGiver_EncirclementSmokeSupply : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn) =>
            (pawn.GetLord()?.LordJob as LordJob_EncirclementCamp)?.Campaign?.ScreenFor(pawn)?.Collect(pawn);
    }

    public sealed class JobGiver_EncirclementTend : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (pawn.GetLord()?.LordJob is not LordJob_EncirclementCamp { Campaign: { } siege } ||
                siege.PatientFor(pawn) is not { } patient) return null;
            return FortressSupportJobs.Patient(pawn.CurJob) == patient ? pawn.CurJob : siege.MedicalJob(pawn, patient);
        }
    }
}
