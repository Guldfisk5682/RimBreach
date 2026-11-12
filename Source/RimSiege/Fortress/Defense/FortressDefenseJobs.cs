using System.Linq;
using CombatExtended;
using CombatExtended.Compatibility;
using CombatExtended.CombatExtended.Jobs.Utils;
using ProjectZ;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Fortress
{
    [DefOf]
    internal static class FortressDutyDefOf
    {
        public static DutyDef RS_FortressGuard = null;
        public static DutyDef RS_FortressIntercept = null;
        public static DutyDef RS_FortressReserve = null;
        public static DutyDef RS_FortressRecover = null;
        public static DutyDef RS_FortressSortie = null;

        static FortressDutyDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(FortressDutyDefOf));
    }

    public sealed class JobGiver_FortressPost : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            PawnDuty duty = pawn.mindState.duty;
            var defense = (LordJob_FortressDefense)pawn.GetLord().LordJob;
            bool sortie = duty.def == FortressDutyDefOf.RS_FortressSortie;
            if (sortie && defense.Ground.Outside(pawn.Position))
            {
                if (duty.tag != FortressDefensePosts.CounterSortieTag ||
                    defense.SortieAdvanceTarget(pawn) is not Thing target ||
                    pawn.CurrentEffectiveVerb?.CanHitTarget(target) == true ||
                    Api.RangeDistance(pawn.Position, target.Position, pawn.Map) <= 16f) return null;
                Map map = pawn.Map;
                IntVec3 ground = Api.GroundCellOf(target.Position, map);
                IntVec3 near = Api.CellOnFloor(ground, Api.FloorOf(pawn.Position, map), map);
                if (!CellFinder.TryFindRandomReachableCellNearPosition(pawn.Position, near, map, 12f,
                    TraverseParms.For(pawn, Danger.Deadly),
                    cell => defense.Ground.Outside(cell) && Api.SameFloor(cell, near, map) &&
                        cell.Standable(map) && cell.GetFirstPawn(map) == null, null, out IntVec3 advance,
                    maxRegions: 64)) return null;
                Job charge = JobMaker.MakeJob(JobDefOf.Goto, advance);
                charge.locomotionUrgency = LocomotionUrgency.Sprint;
                charge.expiryInterval = 120;
                charge.checkOverrideOnExpire = true;
                return charge;
            }
            IntVec3 cell = sortie ? duty.focusSecond.Cell : duty.focus.Cell;
            if (!sortie && Api.SameFloor(pawn.Position, cell, pawn.Map) && pawn.Position.InHorDistOf(cell, duty.radius))
                return null;
            if (!cell.IsValid || !pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly)) return null;
            Job job = JobMaker.MakeJob(JobDefOf.Goto, cell);
            job.locomotionUrgency = sortie || duty.def == FortressDutyDefOf.RS_FortressGuard
                ? LocomotionUrgency.Sprint : LocomotionUrgency.Jog;
            return job;
        }
    }

    public sealed class JobGiver_FortressFight : JobGiver_AIDefendPoint
    {
        protected override Thing FindAttackTarget(Pawn pawn) =>
            FortressTargeting.PreferCombat(pawn, () => base.FindAttackTarget(pawn));

        protected override bool ShouldLoseTarget(Pawn pawn) =>
            !((LordJob_FortressDefense)pawn.GetLord().LordJob).ValidateAttackTarget(pawn, pawn.mindState.enemyTarget) ||
            base.ShouldLoseTarget(pawn);

        protected override bool TryFindShootingPosition(Pawn pawn, out IntVec3 dest, Verb verbToUse = null)
        {
            PawnDuty duty = pawn.mindState.duty;
            Thing target = pawn.mindState.enemyTarget;
            Verb verb = verbToUse ?? pawn.TryGetAttackVerb(target, !pawn.IsColonist);
            if (verb != null && pawn.Position.InHorDistOf(duty.focus.Cell, duty.radius) &&
                pawn.Position.WalkableBy(pawn.Map, pawn) &&
                pawn.Map.pawnDestinationReservationManager.CanReserve(pawn.Position, pawn, pawn.Drafted) &&
                verb.CanHitTarget(target))
            {
                dest = pawn.Position;
                return true;
            }
            return base.TryFindShootingPosition(pawn, out dest, verb);
        }
    }

    public sealed class JobGiver_FortressMobileFight : JobGiver_AIFightEnemies
    {
        protected override Thing FindAttackTarget(Pawn pawn) =>
            FortressTargeting.PreferCombat(pawn, () => base.FindAttackTarget(pawn));

        protected override bool ShouldLoseTarget(Pawn pawn) =>
            !((LordJob_FortressDefense)pawn.GetLord().LordJob).ValidateAttackTarget(pawn, pawn.mindState.enemyTarget) ||
            base.ShouldLoseTarget(pawn);
    }

    public sealed class JobGiver_FortressSeekIntruder : ThinkNode_JobGiver
    {
        private const int RecheckTicks = 120;

        protected override Job TryGiveJob(Pawn pawn)
        {
            var defense = (LordJob_FortressDefense)pawn.GetLord().LordJob;
            Map map = pawn.Map;
            Pawn intruder = defense.Intruders.Where(target => target.Spawned && !target.Downed &&
                    defense.ValidateAttackTarget(pawn, target))
                .MinBy(target => Api.Distance3D(pawn.Position, target.Position, map));
            if (intruder == null) return null;

            IntVec3 center = intruder.Position;
            bool found = TryApproach(center, out IntVec3 approach);
            if (!found && !Api.SameFloor(pawn.Position, center, map))
            {
                IntVec3 ground = Api.GroundCellOf(center, map);
                IntVec3 sameFloor = Api.CellOnFloor(ground, Api.FloorOf(pawn.Position, map), map);
                if (sameFloor.IsValid) found = TryApproach(sameFloor, out approach);
            }
            Job job = found ? JobMaker.MakeJob(JobDefOf.Goto, approach) : JobMaker.MakeJob(JobDefOf.Wait_Combat);
            job.locomotionUrgency = LocomotionUrgency.Sprint;
            job.expiryInterval = RecheckTicks;
            job.checkOverrideOnExpire = true;
            return job;

            bool TryApproach(IntVec3 destination, out IntVec3 cell) =>
                CellFinder.TryFindRandomReachableCellNearPosition(pawn.Position, destination, map, 9f,
                    TraverseParms.For(pawn, Danger.Deadly), candidate => candidate != pawn.Position &&
                        Api.SameFloor(candidate, destination, map) && candidate.Standable(map) &&
                        candidate.GetFirstPawn(map) == null && !defense.Ground.Outside(candidate),
                    null, out cell, maxRegions: 64);
        }
    }

    public sealed class JobGiver_FortressSortieFallback : JobGiver_AITrashDutyFocus
    {
        protected override Job TryGiveJob(Pawn pawn) =>
            pawn.mindState.duty.focus.Thing is Thing target &&
            ((LordJob_FortressDefense)pawn.GetLord().LordJob).ValidateAttackTarget(pawn, target)
                ? base.TryGiveJob(pawn) : null;
    }

    public sealed class JobGiver_FortressGun : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (pawn.WorkTagIsDisabled(WorkTags.Violent) ||
                pawn.mindState.duty.focusSecond.Thing is not Building_TurretGunCE gun ||
                gun.MannableComp == null || !gun.Spawned || gun.Faction != pawn.Faction ||
                !pawn.CanReserveAndReach(gun, PathEndMode.InteractionCell, Danger.Deadly)) return null;
            if (gun.GetAmmo() is { UseAmmo: true, EmptyMagazine: true }) return FortressSupportJobs.Reload(pawn, gun);
            Job job = JobMaker.MakeJob(JobDefOf.ManTurret, gun);
            job.locomotionUrgency = LocomotionUrgency.Sprint;
            job.expiryInterval = 2000;
            job.checkOverrideOnExpire = true;
            return job;
        }
    }

    public sealed class JobGiver_FortressSupport : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            Thing target = pawn.mindState.duty.focusSecond.Thing;
            if (target is not { Spawned: true } || target.Faction != pawn.Faction) return null;
            if (pawn.mindState.duty.tag == FortressDefensePosts.ReloadTag && target is Building_TurretGunCE gun)
                return FortressSupportJobs.Reload(pawn, gun);
            if (target is Pawn patient && !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Doctor))
                return FortressSupportJobs.Care(pawn, patient);
            return null;
        }
    }

    public sealed class JobGiver_FortressSelfTend : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn) => FortressSupportJobs.Treat(pawn, pawn);
    }

    public sealed class JobGiver_FortressFood : JobGiver_GetFood
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (pawn.Downed || pawn.needs.food is not Need_Food food ||
                food.CurLevelPercentage > FortressDefensePosts.FoodThreshold ||
                FoodUtility.ShouldBeFedBySomeone(pawn)) return null;
            Job ordinary = base.TryGiveJob(pawn);
            if (ordinary != null) return ordinary;

            // A hungry defender can take a portable ration directly from its dining room.
            Map map = pawn.Map;
            var site = (FortressSite)map.Parent;
            CellRect dining = site.Blueprint.Facilities.Living.Interior(map, 1);
            ThingDef ration = site.Blueprint.Facilities.Industrial ? ThingDefOf.MealSurvivalPack : ThingDefOf.Pemmican;
            Thing source = map.listerThings.ThingsOfDef(ration)
                .Where(thing => dining.Contains(thing.Position) && thing.IngestibleNow &&
                    !thing.IsForbidden(pawn) && pawn.WillEat(thing))
                .OrderBy(thing => Api.Distance3D(pawn.Position, thing.Position, map))
                .FirstOrDefault(thing => pawn.CanReserveAndReach(thing, PathEndMode.ClosestTouch,
                    Danger.Some, maxPawns: 10, stackCount: 1));
            if (source == null) return null;

            Job job = JobMaker.MakeJob(JobDefOf.Ingest, source);
            job.count = FoodUtility.WillIngestStackCountOf(pawn, ration, FoodUtility.GetNutrition(pawn, source, ration));
            return job;
        }
    }

    public sealed class JobGiver_FortressRest : JobGiver_GetRest
    {
        protected override Job TryGiveJob(Pawn pawn) =>
            pawn.needs.rest is { CurLevelPercentage: < FortressDefensePosts.RestThreshold } &&
            GenTicks.TicksGame >= pawn.mindState.canSleepTick && RestUtility.CanFallAsleep(pawn)
                ? base.TryGiveJob(pawn) : null;
    }

    public sealed class JobGiver_FortressMedicalRest : JobGiver_PatientGoToBed
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            Job job = base.TryGiveJob(pawn);
            var defense = (LordJob_FortressDefense)pawn.GetLord().LordJob;
            return job != null && (defense.Line != FortressLine.Keep ||
                Api.SameFloor(job.targetA.Cell, pawn.mindState.duty.focus.Cell, pawn.Map) &&
                defense.Ground.InKeep(job.targetA.Cell)) ? job : null;
        }
    }

    internal static class FortressSupportJobs
    {
        private static WorkGiver_Scanner FeedPatient =>
            (WorkGiver_Scanner)DefDatabase<WorkGiverDef>.GetNamed("DoctorFeedHumanlikes").Worker;

        internal static bool CanReload(Pawn pawn, Building_TurretGunCE gun) =>
            gun.ShouldReload() && JobGiverUtils_Reload.CanReload(pawn, gun);

        internal static Job Reload(Pawn pawn, Building_TurretGunCE gun) =>
            CanReload(pawn, gun) ? JobGiverUtils_Reload.MakeReloadJob(pawn, gun) : null;

        internal static bool NeedsFeeding(Pawn patient) =>
            patient.Spawned && !patient.Dead && !patient.IsPrisoner && patient.RaceProps.Humanlike &&
            !patient.DevelopmentalStage.Baby() && patient.InBed() && patient.RaceProps.EatsFood &&
            FeedPatientUtility.IsHungry(patient) && HealthAIUtility.ShouldSeekMedicalRest(patient);

        internal static bool NeedsCare(Pawn patient) => patient.Spawned && !patient.Dead && !patient.IsPrisoner &&
            (patient.health.HasHediffsNeedingTend() || HealthAIUtility.WantsToBeRescued(patient) || NeedsFeeding(patient));

        internal static Pawn Patient(Job job) => job?.def == JobDefOf.FeedPatient ? job.targetB.Pawn :
            job?.def == JobDefOf.TendPatient || job?.def == JobDefOf.Rescue ? job.targetA.Pawn : null;

        internal static Job Treat(Pawn doctor, Pawn patient)
        {
            if (!patient.health.HasHediffsNeedingTend() || !CanCareFor(doctor, patient)) return null;
            return Tend(doctor, patient);
        }

        internal static Job Care(Pawn doctor, Pawn patient)
        {
            if (doctor == patient || !NeedsCare(patient) || !CanCareFor(doctor, patient)) return null;
            bool needsTending = patient.health.HasHediffsNeedingTend();
            if (needsTending && HealthUtility.TicksUntilDeathDueToBloodLoss(patient) < 2500)
                return Tend(doctor, patient);

            if (((LordJob_FortressDefense)doctor.GetLord().LordJob).Line != FortressLine.Keep &&
                HealthAIUtility.CanRescueNow(doctor, patient))
            {
                Building_Bed bed = RestUtility.FindBedFor(patient, doctor, checkSocialProperness: false);
                if (bed != null)
                {
                    Job rescue = JobMaker.MakeJob(JobDefOf.Rescue, patient, bed);
                    rescue.count = 1;
                    return rescue;
                }
            }

            if (needsTending) return Tend(doctor, patient);
            return NeedsFeeding(patient) ? FeedPatient.JobOnThing(doctor, patient) : null;
        }

        private static Job Tend(Pawn doctor, Pawn patient)
        {
            Thing medicine = HealthAIUtility.FindBestMedicine(doctor, patient, onlyUseInventory: true) ??
                GenClosest.ClosestThingReachable(doctor.Position, doctor.Map, ThingRequest.ForGroup(ThingRequestGroup.Medicine),
                    PathEndMode.ClosestTouch, TraverseParms.For(doctor, Danger.Some), 32f,
                    item => !item.IsForbidden(doctor) && doctor.CanReserve(item, 10, 1) &&
                        ((LordJob_FortressDefense)doctor.GetLord().LordJob).Ground.Inside(item.Position));
            Job job = JobMaker.MakeJob(JobDefOf.TendPatient, patient, medicine);
            job.endAfterTendedOnce = doctor == patient;
            return job;
        }

        private static bool CanCareFor(Pawn doctor, Pawn patient) =>
            doctor.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) && !patient.InAggroMentalState &&
            SafeToTreat(doctor, patient) &&
            (doctor == patient ? doctor.CanReserve(patient) : doctor.CanReserveAndReach(patient, PathEndMode.Touch, Danger.Some));

        private static bool SafeToTreat(Pawn doctor, Pawn patient)
        {
            var defense = (LordJob_FortressDefense)doctor.GetLord().LordJob;
            return defense.Ground.Inside(doctor.Position) && !defense.Ground.Outside(patient.Position) &&
                !GenAI.EnemyIsNear(doctor, 12f) && !GenAI.EnemyIsNear(patient, 12f);
        }
    }
}
