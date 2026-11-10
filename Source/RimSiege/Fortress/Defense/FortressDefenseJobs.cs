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
            IntVec3 cell = sortie ? duty.focusSecond.Cell : duty.focus.Cell;
            if (sortie ? defense.Ground.Outside(pawn.Position) :
                Api.SameFloor(pawn.Position, cell, pawn.Map) && pawn.Position.InHorDistOf(cell, duty.radius))
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

    public sealed class JobGiver_FortressGun : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (pawn.WorkTagIsDisabled(WorkTags.Violent) ||
                pawn.mindState.duty.focusSecond.Thing is not Building_TurretGunCE gun ||
                !gun.Spawned || gun.Faction != pawn.Faction ||
                !pawn.CanReserveAndReach(gun, PathEndMode.InteractionCell, Danger.Deadly)) return null;
            if (gun.GetAmmo() is { UseAmmo: true, EmptyMagazine: true }) return FortressSupportJobs.Reload(pawn, gun);
            Job job = JobMaker.MakeJob(JobDefOf.ManTurret, gun);
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
                return FortressSupportJobs.Treat(pawn, patient);
            return null;
        }
    }

    public sealed class JobGiver_FortressSelfTend : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn) => FortressSupportJobs.Treat(pawn, pawn);
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
        internal static bool CanReload(Pawn pawn, Building_TurretGunCE gun) =>
            gun.ShouldReload() && JobGiverUtils_Reload.CanReload(pawn, gun);

        internal static Job Reload(Pawn pawn, Building_TurretGunCE gun) =>
            CanReload(pawn, gun) ? JobGiverUtils_Reload.MakeReloadJob(pawn, gun) : null;

        internal static bool CanTreat(Pawn doctor, Pawn patient) =>
            doctor.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) &&
            patient.health.HasHediffsNeedingTend() && !patient.InAggroMentalState &&
            SafeToTreat(doctor, patient) &&
            (doctor == patient ? doctor.CanReserve(patient) : doctor.CanReserveAndReach(patient, PathEndMode.Touch, Danger.Some));

        internal static Job Treat(Pawn doctor, Pawn patient)
        {
            if (!CanTreat(doctor, patient)) return null;

            if (doctor != patient && patient.Downed && !patient.InBed() &&
                HealthAIUtility.CanRescueNow(doctor, patient))
            {
                Building_Bed bed = RestUtility.FindBedFor(patient, doctor, checkSocialProperness: false);
                if (bed != null && ((LordJob_FortressDefense)doctor.GetLord().LordJob).Line != FortressLine.Keep)
                {
                    Job rescue = JobMaker.MakeJob(JobDefOf.Rescue, patient, bed);
                    rescue.count = 1;
                    return rescue;
                }
            }

            Thing medicine = HealthAIUtility.FindBestMedicine(doctor, patient, onlyUseInventory: true) ??
                GenClosest.ClosestThingReachable(doctor.Position, doctor.Map, ThingRequest.ForGroup(ThingRequestGroup.Medicine),
                    PathEndMode.ClosestTouch, TraverseParms.For(doctor, Danger.Some), 32f,
                    item => !item.IsForbidden(doctor) && doctor.CanReserve(item, 10, 1) &&
                        ((LordJob_FortressDefense)doctor.GetLord().LordJob).Ground.Inside(item.Position));
            Job job = JobMaker.MakeJob(JobDefOf.TendPatient, patient, medicine);
            job.endAfterTendedOnce = doctor == patient;
            return job;
        }

        private static bool SafeToTreat(Pawn doctor, Pawn patient)
        {
            var defense = (LordJob_FortressDefense)doctor.GetLord().LordJob;
            return defense.Ground.Inside(doctor.Position) && !defense.Ground.Outside(patient.Position) &&
                !GenAI.EnemyIsNear(doctor, 12f) && !GenAI.EnemyIsNear(patient, 12f);
        }
    }
}
