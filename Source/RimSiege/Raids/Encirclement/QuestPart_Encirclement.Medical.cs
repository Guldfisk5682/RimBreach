using System.Collections.Generic;
using System.Linq;
using RimSiege.Fortress;
using RimSiege.Integration;
using RimSiege.Raids.Artillery;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimSiege.Raids.Encirclement
{
    public sealed partial class QuestPart_Encirclement
    {
        private readonly Dictionary<Pawn, Pawn> medics = new();

        internal bool IsMedic(Pawn pawn) => medics.ContainsKey(pawn);
        internal Pawn PatientFor(Pawn pawn) => medics.GetValueOrDefault(pawn);
        internal bool WaitingForDoctor(Pawn pawn) => !medics.ContainsKey(pawn) && medics.ContainsValue(pawn);

        private void RefreshMedics()
        {
            medics.Clear();
            if (Mechanoid || CampLord == null) return;
            var patients = Waves.SelectMany(w => w.Pawns).Where(TreatmentSite)
                .OrderBy(HealthUtility.TicksUntilDeathDueToBloodLoss)
                .ThenByDescending(p => p.Downed).ToList();
            if (patients.Count == 0 && !CampLord.ownedPawns.Any(p => p.CurJobDef == JobDefOf.Rescue &&
                p.carryTracker.CarriedThing is Pawn)) return;

            var candidates = CampLord.ownedPawns.Where(p => InBattle(p) && !p.InMentalState && p.RaceProps.Humanlike &&
                p.skills != null && !p.WorkTypeIsDisabled(WorkTypeDefOf.Doctor) &&
                p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) &&
                GunFor(p, out _) == null && ClearingBattery(p) < 0 && ScreenFor(p) == null &&
                !forwardBuilders.ContainsKey(p)).ToList();
            var assigned = new HashSet<Pawn>();
            int limit = Mathf.Clamp(CampLord.ownedPawns.Count / 20, 1, 4);
            foreach (Pawn doctor in candidates)
            {
                if (medics.Count >= limit) break;
                Pawn patient = FortressSupportJobs.Patient(doctor.CurJob);
                if (patient != null && (doctor.CurJobDef == JobDefOf.Rescue && doctor.carryTracker.CarriedThing == patient ||
                    patients.Contains(patient) && CanAttend(doctor, patient)) && assigned.Add(patient))
                    medics.Add(doctor, patient);
            }

            foreach (Pawn patient in patients.Where(p => !assigned.Contains(p)))
            {
                if (medics.Count >= limit) break;
                foreach (Pawn doctor in candidates.Where(p => !medics.ContainsKey(p) && !assigned.Contains(p))
                    .OrderBy(p => p == patient).ThenBy(NeedsRest)
                    .ThenBy(p => IsoFloor.Distance3D(p.Position, patient.Position, Map))
                    .ThenByDescending(p => p.skills.GetSkill(SkillDefOf.Medicine).Level))
                {
                    Job care = MedicalJob(doctor, patient);
                    if (care == null) continue;
                    JobMaker.ReturnToPool(care);
                    medics.Add(doctor, patient);
                    assigned.Add(patient);
                    break;
                }
            }
        }

        internal Job MedicalJob(Pawn doctor, Pawn patient)
        {
            if (!TreatmentSite(patient) || !CanAttend(doctor, patient)) return null;
            return FortressSupportJobs.Care(doctor, patient, () => FindCampBed(doctor, patient),
                () => NearbyMedicine(doctor, patient));
        }

        private bool TreatmentSite(Pawn patient) => patient is { Spawned: true, Dead: false, InAggroMentalState: false } &&
            patient.Map == Map && patient.Faction == Faction && patient.RaceProps.Humanlike &&
            FortressSupportJobs.NeedsCare(patient) &&
            (NearRear(patient.Position, 12f) || Phase is 2 or 3 && Plan.Batteries(Map).Skip(1).Any(b =>
                IsoFloor.Distance3D(patient.Position, b.Camp.Center, Map) <= 18f));

        private bool CanAttend(Pawn doctor, Pawn patient) =>
            doctor.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) &&
            !doctor.WorkTypeIsDisabled(WorkTypeDefOf.Doctor) &&
            !GenAI.EnemyIsNear(doctor, 12f) && !GenAI.EnemyIsNear(patient, 12f) &&
            (SmokeCombat.Screened(patient.Position, Map) || !Work.UnderFire(Map, patient.Position)) &&
            doctor.CanReserveAndReach(patient, PathEndMode.Touch, Danger.Some) &&
            HasSafePath(doctor, patient.Position, PathEndMode.Touch);

        private Building_Bed FindCampBed(Pawn doctor, Pawn patient) => Work.Projects
            .Where(p => p.Install && p.Current is Building_Bed { Spawned: true } bed && bed.Faction == Faction &&
                CellSafeForCamp(bed.Position) && IsoFloor.Distance3D(bed.Position, patient.Position, Map) <= 24f)
            .Select(p => (Building_Bed)p.Current)
            .OrderByDescending(b => b.Medical)
            .ThenBy(b => IsoFloor.Distance3D(b.Position, patient.Position, Map))
            .FirstOrDefault(b => RestUtility.IsValidBedFor(b, patient, doctor, checkSocialProperness: false) &&
                doctor.CanReserveAndReach(b, PathEndMode.OnCell, Danger.Some) &&
                HasSafePath(doctor, b.Position, PathEndMode.OnCell));

        private Thing NearbyMedicine(Pawn doctor, Pawn patient) => Battery(0) is { } rear
            ? Map.GetComponent<ArtilleryMap>().SupplyOwnership.ItemsFor(rear)
                .Where(t => t is Medicine { Spawned: true } && !t.IsForbidden(doctor) &&
                    CellSafeForCamp(t.Position) && IsoFloor.Distance3D(t.Position, patient.Position, Map) <= 32f)
                .OrderBy(t => IsoFloor.Distance3D(t.Position, doctor.Position, Map))
                .FirstOrDefault(t => doctor.CanReserveAndReach(t, PathEndMode.ClosestTouch, Danger.Some, 10, 1) &&
                    HasSafePath(doctor, t.Position, PathEndMode.ClosestTouch))
            : null;
    }
}
