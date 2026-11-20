using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using CombatExtended.Compatibility;
using ProjectZ;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Fortress
{
    internal sealed class FortressDefensePosts
    {
        internal const float FoodThreshold = 0.30f;
        internal const float RestThreshold = 0.40f;
        internal const string ReloadTag = "reload";
        internal const string CounterSortieTag = "counter";
        private const string PatrolTag = "wall:";
        private const int ShotChecksPerGun = 4;
        private const int MedicalCrews = 4;
        private const int MaintenanceCrews = 3;
        private const int ReloadCrews = 3;
        private const int LoaderCandidatesPerGun = 2;
        private readonly Map map;
        private readonly FortressBlueprint blueprint;
        private readonly FortressDefenseGround ground;
        private List<Building_TurretGunCE> guns;
        private readonly Dictionary<Pawn, PawnDuty> vacancies = new();
        private int maintenanceCursor;
        private int interiorAimCursor;

        internal FortressDefensePosts(Map map, FortressBlueprint blueprint, FortressDefenseGround ground)
        {
            this.map = map;
            this.blueprint = blueprint;
            this.ground = ground;
        }

        private List<Building_TurretGunCE> Guns => guns ??= blueprint.Defenses
            .Where(piece => piece.Def?.building?.turretGunDef != null)
            .Select(piece => piece.CellOn(map).GetFirstThing(map, piece.Def))
            .OfType<Building_TurretGunCE>().ToList();

        internal void InvalidateGuns() => guns = null;

        internal static bool CanFight(Pawn pawn) => pawn.Spawned && !pawn.Downed && !pawn.Dead && !pawn.IsPrisoner;

        internal static bool CanAnswer(Pawn pawn) => CanFight(pawn) && !pawn.InMentalState &&
            !Recovering(pawn) && pawn.health.hediffSet.BleedRateTotal <= 0f;

        private static bool Ready(Pawn pawn) => CanFight(pawn) && !pawn.InMentalState &&
            pawn.CurJobDef != JobDefOf.LayDown && pawn.CurJobDef != JobDefOf.Ingest &&
            pawn.health.hediffSet.BleedRateTotal <= 0f &&
            (pawn.needs.food?.CurLevelPercentage ?? 1f) >= FoodThreshold &&
            (pawn.needs.rest?.CurLevelPercentage ?? 1f) >= RestThreshold;

        internal static bool Guard(Pawn pawn) => pawn.mindState?.duty?.def == FortressDutyDefOf.RS_FortressGuard;
        private static bool Reserve(Pawn pawn) => pawn.mindState?.duty?.def == FortressDutyDefOf.RS_FortressReserve;
        internal static bool Intercepting(Pawn pawn) =>
            pawn.mindState?.duty?.def == FortressDutyDefOf.RS_FortressIntercept;
        private static bool Recovering(Pawn pawn) => pawn.mindState.duty?.def == FortressDutyDefOf.RS_FortressRecover;
        internal static bool Patrol(Pawn pawn) => Guard(pawn) &&
            pawn.mindState.duty.tag?.StartsWith(PatrolTag, StringComparison.Ordinal) == true;
        internal static bool TryGetDutySector(Pawn pawn, out int sector)
        {
            sector = -1;
            string tag = pawn.mindState?.duty?.tag;
            if (tag == null) return false;
            if (tag.StartsWith(PatrolTag, StringComparison.Ordinal))
                tag = tag.Substring(PatrolTag.Length);
            return int.TryParse(tag, out sector) && sector >= 0 && sector < 4;
        }
        private static bool OnSector(Pawn pawn, int sector) =>
            pawn.mindState.duty.tag == (Patrol(pawn) ? PatrolTag + sector : sector.ToString());
        private static bool Indirect(Building_TurretGunCE gun) =>
            gun.AttackVerb is Verb_ShootMortarCE || gun.def.building.IsMortar;

        internal void RememberPost(Pawn pawn)
        {
            if (Guard(pawn)) vacancies[pawn] = pawn.mindState.duty;
        }

        internal bool Relieve(Lord lord, Pawn pawn, LordJob_FortressDefense defense)
        {
            FortressLine line = defense.Line;
            PawnDuty previous = vacancies.Remove(pawn, out PawnDuty vacancy) ? vacancy : pawn.mindState?.duty;
            bool present = pawn.Spawned && !pawn.Dead && !pawn.IsPrisoner && pawn.GetLord() == lord;
            bool injured = present && (pawn.Downed || pawn.health.hediffSet.BleedRateTotal > 0f);
            bool postLost = (injured || !present) && previous?.def == FortressDutyDefOf.RS_FortressGuard;
            if (injured || !present)
            {
                if (injured) Recover(pawn, line);
                if (postLost)
                {
                    Pawn replacement = DefenseResponsePool(lord, defense).Where(CanMan)
                        .OrderBy(DefenseResponsePriority).ThenBy(other => Api.Distance3D(other.Position, previous.focus.Cell, map))
                        .Take(4)
                        .FirstOrDefault(other => previous.focusSecond.Thing is Building_TurretGunCE gun
                            ? Usable(gun) && CanReachGun(other, gun)
                            : other.CanReach(previous.focus.Cell, PathEndMode.OnCell, Danger.Deadly));
                    if (replacement != null)
                    {
                        SetDuty(replacement, previous.def, previous.focus, previous.radius, previous.focusSecond, previous.tag);
                        replacement.mindState.duty.focusThird = previous.focusThird;
                    }
                }
            }
            else if (Recovering(pawn) && !HealthAIUtility.ShouldSeekMedicalRest(pawn))
                SetDuty(pawn, FortressDutyDefOf.RS_FortressReserve, ReservePost(line), 8f);
            return postLost;
        }

        internal void Assign(Lord lord, LordJob_FortressDefense defense, List<FortressWatch> watches,
            int now, bool sameLine)
        {
            FortressLine line = defense.Line;
            var healthy = new List<Pawn>();
            foreach (Pawn pawn in lord.ownedPawns)
            {
                if (!pawn.Spawned || pawn.Dead || pawn.IsPrisoner) continue;
                if (pawn.Downed || pawn.health.hediffSet.BleedRateTotal > 0f ||
                    Recovering(pawn) && HealthAIUtility.ShouldSeekMedicalRest(pawn))
                {
                    Recover(pawn, line);
                }
                else if (!pawn.InMentalState) healthy.Add(pawn);
            }

            int current = GarrisonSize(lord);
            var gunners = healthy.Where(pawn => Guard(pawn) && CanAnswer(pawn) &&
                pawn.mindState.duty.focusSecond.Thing is Building_TurretGunCE gun &&
                int.TryParse(pawn.mindState.duty.tag, out int sector) &&
                watches[sector].Until > now && EligibleGun(defense, sector, gun)).ToHashSet();
            var retained = sameLine ? RotateTiredGuards(lord, healthy, gunners, watches, now) : new HashSet<Pawn>();
            int remainingHealthy = healthy.Count - gunners.Count;
            int minimum = line == FortressLine.Keep
                ? Math.Min((remainingHealthy * 3 + 3) / 4, Math.Max(1, remainingHealthy - 1))
                : (current * 3 + 9) / 10;
            List<Pawn> guards = healthy.Where(pawn => !gunners.Contains(pawn) && !retained.Contains(pawn) && Ready(pawn))
                .OrderByDescending(Patrol)
                .ThenByDescending(Guard)
                .Take(Math.Max(0, minimum - retained.Count)).ToList();
            var guardSet = new HashSet<Pawn>(guards);
            guardSet.UnionWith(retained);
            var reserves = healthy.Where(pawn => !guardSet.Contains(pawn) && !gunners.Contains(pawn)).ToList();
            int[] sectorOrder = Enumerable.Range(0, 4).OrderByDescending(sector => watches[sector].Until > now).ToArray();
            int[] patrolSectors = line == FortressLine.Curtain
                ? sectorOrder.Where(sector => ground.CurtainPost(sector).IsValid).ToArray() : Array.Empty<int>();
            int patrolCount = patrolSectors.Length == 0 ? 0 :
                Math.Min(guards.Count, Math.Max(0, (current + 9) / 10 - retained.Count(Patrol)));
            for (int i = 0; i < guards.Count; i++)
            {
                bool patrol = i < patrolCount;
                int sector = patrol ? patrolSectors[i % patrolSectors.Length] : sectorOrder[(i - patrolCount) % 4];
                IntVec3 post = patrol ? ground.CurtainPost(sector) : ground.Post(line, sector, i / 4);
                SetDuty(guards[i], FortressDutyDefOf.RS_FortressGuard, post.IsValid ? post : ReservePost(line), 6f,
                    tag: patrol ? PatrolTag + sector : sector.ToString());
            }

            IntVec3 reservePost = ReservePost(line);
            foreach (Pawn pawn in reserves)
            {
                PawnDuty duty = pawn.mindState.duty;
                bool preserve = Reserve(pawn) && (duty.focusSecond.Pawn != null ||
                    duty.tag == FortressMaintenance.DutyTag && defense.CanMaintain(duty.focusSecond.Cell) ||
                    duty.tag == ReloadTag && (line != FortressLine.Keep ||
                        ground.InKeep(duty.focusSecond.Cell) && Api.FloorOf(duty.focusSecond.Cell, map) > 0));
                if (preserve && duty.focus.Cell == reservePost) continue;
                SetDuty(pawn, FortressDutyDefOf.RS_FortressReserve, reservePost, 8f,
                    preserve ? duty.focusSecond : null, preserve ? duty.tag : null);
            }

            int infantry = defense.InfantryPerSector(now);
            foreach (int sector in sectorOrder)
                if (watches[sector].Until > now) ArmContact(lord, defense, sector, defense.AimAt(watches[sector]), infantry);
        }

        private HashSet<Pawn> RotateTiredGuards(Lord lord, List<Pawn> healthy,
            HashSet<Pawn> gunners, List<FortressWatch> watches, int now)
        {
            var retained = new HashSet<Pawn>();
            var available = FreeReserve(lord).ToList();
            foreach (Pawn guard in healthy.Where(pawn => Guard(pawn) && CanAnswer(pawn) &&
                !Ready(pawn) && !gunners.Contains(pawn)))
            {
                PawnDuty duty = guard.mindState.duty;
                IntVec3 post = duty.focus.Cell;
                if (!post.IsValid || !post.Standable(map)) continue;
                int sector = Enumerable.Range(0, 4).FirstOrDefault(candidate => OnSector(guard, candidate), -1);
                if (sector < 0 || watches[sector].Until > now)
                {
                    retained.Add(guard);
                    continue;
                }
                Pawn relief = available.Where(CanMan).OrderBy(pawn => Api.Distance3D(pawn.Position, post, map))
                    .Take(4).FirstOrDefault(pawn => duty.focusSecond.Thing is Building_TurretGunCE gun
                        ? Usable(gun) && CanReachGun(pawn, gun)
                        : pawn.CanReach(post, PathEndMode.OnCell, Danger.Deadly));
                if (relief == null)
                {
                    retained.Add(guard);
                    continue;
                }
                SetDuty(relief, duty.def, duty.focus, duty.radius, duty.focusSecond, duty.tag);
                relief.mindState.duty.focusThird = duty.focusThird;
                available.Remove(relief);
                retained.Add(relief);
            }
            return retained;
        }

        internal void AssignInterior(Lord lord, LordJob_FortressDefense defense)
        {
            var fighters = new List<Pawn>();
            foreach (Pawn pawn in lord.ownedPawns)
            {
                if (!pawn.Spawned || pawn.Dead || pawn.IsPrisoner) continue;
                if (pawn.Downed || pawn.health.hediffSet.BleedRateTotal > 0f ||
                    Recovering(pawn) && HealthAIUtility.ShouldSeekMedicalRest(pawn))
                {
                    Recover(pawn, defense.Line);
                    continue;
                }
                if (pawn.InMentalState) continue;
                if (!pawn.WorkTagIsDisabled(WorkTags.Violent))
                    fighters.Add(pawn);
                else if (!Reserve(pawn))
                    SetDuty(pawn, FortressDutyDefOf.RS_FortressReserve, ReservePost(defense.Line), 8f);
            }

            if (fighters.Count == 0) return;
            var incumbents = fighters.Where(pawn => pawn.mindState.duty?.focusSecond.Thing is Building_TurretGunCE &&
                    (Guard(pawn) || Intercepting(pawn)))
                .ToLookup(pawn => (Building_TurretGunCE)pawn.mindState.duty.focusSecond.Thing);
            Thing[] targets = defense.Targeting.CombatTargets().Where(defense.ValidTarget).ToArray();
            var assigned = new HashSet<Pawn>();
            int aimOffset = interiorAimCursor;
            interiorAimCursor = (interiorAimCursor + 2) & int.MaxValue;
            foreach (Building_TurretGunCE gun in Guns.Where(gun => Usable(gun) && gun.Active && gun.MannableComp != null)
                .OrderBy(gun => ground.OnCurtain(gun.Position))
                .ThenByDescending(gun => incumbents.Contains(gun)))
            {
                Thing[] candidates = targets.Where(candidate => CanAimAt(gun, candidate) &&
                        (candidate is not Pawn pawn || !pawn.RaceProps.Animal && !pawn.IsPsychologicallyInvisible()))
                    .OrderByDescending(candidate => candidate == gun.CurrentTarget.Thing)
                    .ThenByDescending(candidate => ground.InDefenseArea(candidate.Position))
                    .ThenBy(candidate => Api.Distance3D(gun.Position, candidate.Position, map))
                    .ToArray();
                Thing target = candidates.Take(2).FirstOrDefault(candidate => HasShot(gun, candidate));
                int remaining = candidates.Length - 2;
                for (int i = 0; target == null && i < Math.Min(ShotChecksPerGun - 2, remaining); i++)
                {
                    Thing candidate = candidates[2 + (aimOffset % remaining + i) % remaining];
                    if (HasShot(gun, candidate)) target = candidate;
                }
                if (target == null) continue;

                Pawn gunner = incumbents[gun].FirstOrDefault(pawn => !assigned.Contains(pawn) && CanOperate(pawn, gun));
                gunner ??= fighters.Where(pawn => !assigned.Contains(pawn) && CanMan(pawn))
                    .OrderByDescending(Ready).ThenBy(pawn => GunDistance(pawn, gun))
                    .Take(4).FirstOrDefault(pawn => CanOperate(pawn, gun));
                if (gunner == null) continue;

                SetDuty(gunner, FortressDutyDefOf.RS_FortressIntercept, gun.InteractionCell, 6f, gun);
                gunner.mindState.duty.focusThird = target;
                assigned.Add(gunner);
            }

            foreach (Pawn fighter in fighters)
                if (!assigned.Contains(fighter) &&
                    (!Intercepting(fighter) || fighter.mindState.duty.focusSecond.IsValid &&
                        fighter.mindState.duty.tag != ReloadTag))
                    SetDuty(fighter, FortressDutyDefOf.RS_FortressIntercept, fighter.Position);

            bool CanOperate(Pawn pawn, Building_TurretGunCE gun) =>
                (gun.MannableComp.ManningPawn == pawn || CanReachGun(pawn, gun)) &&
                (gun.GetAmmo() is not { UseAmmo: true, EmptyMagazine: true } ||
                    pawn.CurJobDef == CE_JobDefOf.ReloadTurret || FortressSupportJobs.CanReload(pawn, gun));
        }

        internal int GarrisonSize(Lord lord) => lord.ownedPawns.Count(pawn =>
            pawn.Spawned && !pawn.Dead && !pawn.IsPrisoner && !ground.Outside(pawn.Position));

        internal bool FillMedical(Lord lord, FortressLine line)
        {
            int limit = line == FortressLine.Curtain ? Math.Max(5, GarrisonSize(lord) / 10) : MedicalCrews;
            List<Pawn> reserves = SupportReserves(lord);
            var medics = AssignMedics(reserves, lord.ownedPawns.Where(FortressSupportJobs.NeedsCare), limit);
            IntVec3 post = ReservePost(line);
            bool builderReassigned = false;
            foreach (Pawn pawn in reserves)
            {
                if (medics.TryGetValue(pawn, out Pawn patient))
                {
                    builderReassigned |= pawn.mindState.duty.tag == FortressMaintenance.DutyTag;
                    SetDuty(pawn, FortressDutyDefOf.RS_FortressReserve, post, 8f, patient);
                }
                else if (pawn.mindState.duty.focusSecond.Pawn != null)
                    SetDuty(pawn, FortressDutyDefOf.RS_FortressReserve, post, 8f);
            }
            return builderReassigned;
        }

        private Dictionary<Pawn, Pawn> AssignMedics(List<Pawn> reserves, IEnumerable<Pawn> patients, int limit)
        {
            var medics = new Dictionary<Pawn, Pawn>();
            var assigned = new HashSet<Pawn>();
            foreach (Pawn pawn in reserves)
            {
                if (medics.Count == limit) break;
                if (CanAnswer(pawn) && pawn.mindState.duty.focusSecond.Pawn is Pawn patient &&
                    FortressSupportJobs.Patient(pawn.CurJob) == patient && assigned.Add(patient))
                    medics.Add(pawn, patient);
            }
            var candidates = reserves.Where(pawn => Ready(pawn) &&
                !medics.ContainsKey(pawn) && !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Doctor)).ToList();
            foreach (Pawn patient in patients.OrderBy(HealthUtility.TicksUntilDeathDueToBloodLoss)
                .ThenByDescending(pawn => pawn.Downed)
                .ThenByDescending(pawn => pawn.health.HasHediffsNeedingTend()))
            {
                if (medics.Count >= limit || candidates.Count == 0) break;
                if (assigned.Contains(patient)) continue;
                Pawn medic = candidates.Where(pawn => pawn != patient)
                    .OrderByDescending(pawn => pawn.mindState.duty?.focusSecond.Pawn == patient)
                    .ThenBy(pawn => pawn.mindState.duty.tag == FortressMaintenance.DutyTag)
                    .ThenByDescending(pawn => pawn.skills?.GetSkill(SkillDefOf.Medicine).Level ?? 0)
                    .ThenBy(pawn => Api.Distance3D(pawn.Position, patient.Position, map))
                    .FirstOrDefault(pawn =>
                    {
                        Job job = FortressSupportJobs.Care(pawn, patient);
                        if (job == null) return false;
                        JobMaker.ReturnToPool(job);
                        return true;
                    });
                if (medic == null) continue;
                medics.Add(medic, patient);
                candidates.Remove(medic);
            }
            return medics;
        }

        internal bool FillMaintenance(Lord lord, LordJob_FortressDefense defense)
        {
            int limit = defense.Line == FortressLine.Curtain ? Math.Max(5, GarrisonSize(lord) / 10) : MaintenanceCrews;
            List<Pawn> reserves = SupportReserves(lord).Where(pawn => pawn.mindState.duty.focusSecond.Pawn == null).ToList();
            var builders = AssignBuilders(lord, defense, reserves, limit, out bool moreCandidates);
            IntVec3 post = ReservePost(defense.Line);
            foreach (Pawn pawn in reserves)
            {
                if (builders.TryGetValue(pawn, out IntVec3 project))
                    SetDuty(pawn, FortressDutyDefOf.RS_FortressReserve, post, 8f, project, FortressMaintenance.DutyTag);
                else if (pawn.mindState.duty.tag == FortressMaintenance.DutyTag)
                    SetDuty(pawn, FortressDutyDefOf.RS_FortressReserve, post, 8f);
            }
            return moreCandidates;
        }

        private Dictionary<Pawn, IntVec3> AssignBuilders(Lord lord, LordJob_FortressDefense defense,
            List<Pawn> reserves, int limit, out bool moreCandidates)
        {
            var builders = new Dictionary<Pawn, IntVec3>();
            moreCandidates = false;
            if (defense.Line == FortressLine.Keep) return builders;
            Thing[] projects = FortressMaintenance.Projects(map, lord.faction, defense);
            if (projects.Length == 0) return builders;
            var assigned = new HashSet<IntVec3>();
            foreach (Pawn builder in reserves.Where(pawn => Ready(pawn) &&
                    !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Construction) &&
                    pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
                .OrderByDescending(pawn => pawn.mindState.duty?.tag == FortressMaintenance.DutyTag)
                .ThenByDescending(pawn => pawn.skills?.GetSkill(SkillDefOf.Construction).Level ?? 0)
                .Take(limit * 2))
            {
                IntVec3 previous = builder.mindState.duty?.focusSecond.Cell ?? IntVec3.Invalid;
                if (builder.mindState.duty?.tag == FortressMaintenance.DutyTag && builder.CurJob?.workGiverDef != null &&
                    defense.CanMaintain(previous) && assigned.Add(previous))
                    builders.Add(builder, previous);
                else if (FortressMaintenance.FindProject(builder, projects, assigned, ref maintenanceCursor,
                    out bool skipped) is Thing project)
                {
                    builders.Add(builder, project.Position);
                    assigned.Add(project.Position);
                }
                else moreCandidates |= skipped;
                if (builders.Count == limit)
                {
                    moreCandidates = false;
                    break;
                }
            }
            return builders;
        }

        private List<Pawn> SupportReserves(Lord lord) => lord.ownedPawns
            .Where(pawn => Reserve(pawn) && CanFight(pawn) && !pawn.InMentalState &&
                pawn.mindState.duty.tag != ReloadTag && !ground.Outside(pawn.Position)).ToList();

        internal void SuspendExposedMaintenance(Lord lord, LordJob_FortressDefense defense)
        {
            foreach (Pawn pawn in lord.ownedPawns)
                if (Reserve(pawn) && pawn.mindState.duty.tag == FortressMaintenance.DutyTag &&
                    !defense.CanMaintain(pawn.mindState.duty.focusSecond.Cell))
                    SetDuty(pawn, FortressDutyDefOf.RS_FortressReserve, ReservePost(defense.Line), 8f);
        }

        internal void Replenish(Lord lord, LordJob_FortressDefense defense, List<FortressWatch> watches, int now)
        {
            FortressLine line = defense.Line;
            bool interior = defense.IntrusionActive;
            bool EligibleAutoGun(Building_TurretGunCE gun) =>
                Usable(gun) && gun.MannableComp == null &&
                (interior || line != FortressLine.Keep || ground.InKeep(gun.Position) && Api.FloorOf(gun.Position, map) > 0);

            var available = lord.ownedPawns.Where(pawn => (Reserve(pawn) || interior && Intercepting(pawn)) &&
                (!pawn.mindState.duty.focusSecond.IsValid || pawn.mindState.duty.tag == ReloadTag)).ToList();
            var crews = new Dictionary<Pawn, Building_TurretGunCE>();
            var assigned = new HashSet<Building_TurretGunCE>();
            foreach (Pawn pawn in available)
            {
                if (pawn.mindState.duty.tag == ReloadTag && pawn.CurJobDef == CE_JobDefOf.ReloadTurret &&
                    pawn.CurJob.targetA.Thing is Building_TurretGunCE gun && EligibleAutoGun(gun))
                {
                    crews.Add(pawn, gun);
                    assigned.Add(gun);
                }
            }
            var candidates = available.Where(pawn => (interior ? CanAnswer(pawn) : Ready(pawn)) &&
                CanMan(pawn) && !crews.ContainsKey(pawn)).ToList();
            if (crews.Count < ReloadCrews && candidates.Count > 0)
            {
                foreach (Building_TurretGunCE gun in Guns.Where(gun => EligibleAutoGun(gun) &&
                    !assigned.Contains(gun) && gun.ShouldReload())
                    .OrderByDescending(gun => gun.CompAmmo.EmptyMagazine)
                    .ThenByDescending(gun => watches[ground.Sector(gun.Position)].Until > now))
                {
                    Pawn loader = candidates.OrderByDescending(pawn => pawn.mindState.duty.focusSecond.Thing == gun)
                        .ThenBy(pawn => GunDistance(pawn, gun)).Take(LoaderCandidatesPerGun)
                        .FirstOrDefault(pawn => FortressSupportJobs.CanReload(pawn, gun));
                    if (loader == null) continue;
                    crews.Add(loader, gun);
                    candidates.Remove(loader);
                    if (crews.Count == ReloadCrews || candidates.Count == 0) break;
                }
            }
            IntVec3 post = ReservePost(line);
            foreach (Pawn pawn in available)
            {
                if (crews.TryGetValue(pawn, out Building_TurretGunCE gun))
                {
                    if (interior && Intercepting(pawn))
                        SetDuty(pawn, FortressDutyDefOf.RS_FortressIntercept, pawn.mindState.duty.focus, 8f, gun, ReloadTag);
                    else
                        SetDuty(pawn, FortressDutyDefOf.RS_FortressReserve, post, 8f, gun, ReloadTag);
                }
                else if (pawn.mindState.duty.tag == ReloadTag)
                {
                    if (interior && Intercepting(pawn))
                        SetDuty(pawn, FortressDutyDefOf.RS_FortressIntercept, pawn.mindState.duty.focus);
                    else
                        SetDuty(pawn, FortressDutyDefOf.RS_FortressReserve, post, 8f);
                }
            }
        }

        internal void ArmContact(Lord lord, LordJob_FortressDefense defense, int sector, Thing contact, int infantry)
        {
            string sectorTag = sector.ToString();
            var local = lord.ownedPawns.Where(pawn => Guard(pawn) && CanAnswer(pawn) && OnSector(pawn, sector)).ToList();
            foreach (Pawn guard in local)
                if (IsEatingOrSleeping(guard) ||
                    guard.CurJob is Job job && job.lord == lord && job.def == JobDefOf.GotoWander)
                    guard.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);

            if (defense.Line != FortressLine.Keep) Reinforce(lord, defense, sector, infantry, local);
            var reserves = (defense.Line == FortressLine.Keep
                ? local.Where(pawn => !pawn.mindState.duty.focusSecond.IsValid)
                    .Concat(DefenseResponsePool(lord, defense).Where(Reserve))
                : DefenseResponsePool(lord, defense)).Where(CanMan).ToList();
            var crews = lord.ownedPawns.Where(pawn => Guard(pawn) && CanFight(pawn) &&
                pawn.mindState.duty.focusSecond.Thing is Building_TurretGunCE)
                .ToLookup(pawn => (Building_TurretGunCE)pawn.mindState.duty.focusSecond.Thing);
            if (reserves.Count == 0 && !local.Any(pawn => pawn.mindState.duty.focusSecond.HasThing)) return;

            IntVec3 front = ground.GateCell(sector);
            Thing[] targets = defense.Targeting.CombatTargets()
                .Concat(map.listerBuildings.allBuildingsColonist)
                .Prepend(contact).Where(target => target != null && defense.ValidTarget(target) &&
                    defense.Targeting.Allows(target))
                .Distinct().OrderByDescending(FortressTargeting.IsCombatTarget)
                .ThenByDescending(target => ground.Sector(target.Position) == sector)
                .ThenBy(target => Api.Distance3D(front, target.Position, map)).ToArray();

            int opposite = (sector + 2) & 3;
            foreach (Building_TurretGunCE gun in Guns.Where(gun => EligibleGun(defense, sector, gun))
                .OrderByDescending(Indirect).ThenBy(gun => Api.Distance3D(front, gun.Position, map)))
            {
                Pawn gunner = crews[gun].FirstOrDefault();
                bool stealGunner = gunner != null && !OnSector(gunner, sector);
                if (stealGunner && (gun.CurrentTarget.Thing is Thing active && defense.ValidTarget(active) ||
                    TryGetDutySector(gunner, out int gunnerSector) && defense.SectorHasContact(gunnerSector)))
                    continue;
                if (gunner == null && reserves.Count == 0) continue;

                bool anySector = Indirect(gun) || defense.Line == FortressLine.Keep || ground.InKeep(gun.Position);
                bool middle = ground.TowerMiddle(gun.Position);
                int covered = ground.CoveredSectors(gun.Position);
                Thing target = targets.Where(candidate =>
                        (anySector || candidate == contact ||
                            (covered != 0
                                ? (covered & FortressGeometry.SectorBit(ground.Sector(candidate.Position))) != 0
                                : ground.Sector(candidate.Position) != opposite)) &&
                        (!middle || defense.MiddleOpen(gun.Position, ground.Sector(candidate.Position))) &&
                        CanAimAt(gun, candidate))
                    .Take(ShotChecksPerGun).FirstOrDefault(candidate => gun.AttackVerb.CanHitTargetFrom(gun.Position, candidate));
                if (target == null) continue;

                if (gunner == null)
                {
                    gunner = reserves.OrderBy(pawn => defense.Line == FortressLine.Keep && Guard(pawn)
                            ? (Ready(pawn) ? -1 : 3) : DefenseResponsePriority(pawn))
                        .ThenBy(pawn => GunDistance(pawn, gun))
                        .Take(4)
                        .FirstOrDefault(pawn => CanReachGun(pawn, gun));
                    if (gunner == null) continue;
                    SetDuty(gunner, FortressDutyDefOf.RS_FortressGuard, gun.InteractionCell, 6f, gun, sectorTag);
                    reserves.Remove(gunner);
                }
                else if (stealGunner)
                {
                    gunner.mindState.duty.tag = sectorTag;
                }
                gunner.mindState.duty.focusThird = target;
            }
        }

        private void Reinforce(Lord lord, LordJob_FortressDefense defense, int sector, int infantry, List<Pawn> local)
        {
            var posts = Enumerable.Range(0, 4).Select(index => ground.Post(defense.Line, sector, index))
                .Where(cell => cell.IsValid).Distinct().ToDictionary(cell => cell, _ => 0);
            if (posts.Count == 0) return;
            int stationed = 0;
            foreach (Pawn guard in local)
            {
                if (Patrol(guard) || guard.mindState.duty.focusSecond.HasThing) continue;
                IntVec3 post = guard.mindState.duty.focus.Cell;
                if (!posts.ContainsKey(post)) continue;
                posts[post]++;
                stationed++;
            }

            for (int i = stationed; i < infantry; i++)
            {
                IntVec3 post = posts.MinBy(pair => pair.Value).Key;
                Pawn reinforcement = DefenseResponsePool(lord, defense).Where(CanMan)
                    .OrderBy(DefenseResponsePriority).ThenBy(pawn => Api.Distance3D(pawn.Position, post, map))
                    .Take(4)
                    .FirstOrDefault(pawn => pawn.CanReach(post, PathEndMode.OnCell, Danger.Deadly));
                if (reinforcement == null)
                {
                    posts.Remove(post);
                    if (posts.Count == 0) break;
                    i--;
                    continue;
                }
                SetDuty(reinforcement, FortressDutyDefOf.RS_FortressGuard, post, 6f, tag: sector.ToString());
                posts[post]++;
            }
        }

        internal List<Building_TurretGunCE> DirectLookoutGuns(LordJob_FortressDefense defense) =>
            Guns.Where(gun => Usable(gun) && gun.Active && gun.MannableComp != null && !Indirect(gun) &&
                (defense.Line != FortressLine.Keep || ground.InKeep(gun.Position) && Api.FloorOf(gun.Position, map) > 0) &&
                (!ground.OnCurtain(gun.Position) || defense.Line == FortressLine.Curtain)).ToList();

        internal bool IsGunEligibleForSector(LordJob_FortressDefense defense, int sector, Building_TurretGunCE gun) =>
            EligibleGun(defense, sector, gun);

        private bool EligibleGun(LordJob_FortressDefense defense, int sector, Building_TurretGunCE gun)
        {
            if (!Usable(gun) || !gun.Active || gun.MannableComp == null ||
                defense.Line == FortressLine.Keep && (!ground.InKeep(gun.Position) || Api.FloorOf(gun.Position, map) <= 0) ||
                ground.OnCurtain(gun.Position) && defense.Line != FortressLine.Curtain ||
                ground.TowerMiddle(gun.Position) && !defense.MiddleOpen(gun.Position, sector))
                return false;
            if (defense.Line == FortressLine.Keep || Indirect(gun) || ground.InKeep(gun.Position))
                return true;
            int covered = ground.CoveredSectors(gun.Position);
            if (covered != 0) return (covered & FortressGeometry.SectorBit(sector)) != 0;
            return ground.Sector(gun.Position) == sector || ground.TowerFaces(gun.Position, sector);
        }

        internal bool CanAimAt(Building_TurretGunCE gun, Thing target) =>
            Api.RangeDistance(gun.Position, target.Position, map) <= gun.AttackVerb.EffectiveRange &&
            (gun.CompFireArc?.WithinFireArc(target) ?? true);

        internal Thing PreferredGunTarget(LordJob_FortressDefense defense, Building_TurretGunCE gun, Pawn gunner) =>
            defense.Targeting.CombatTargets().Where(target => defense.ValidateAttackTarget(gunner, target) &&
                CanAimAt(gun, target) &&
                (target is not Pawn pawn || !pawn.RaceProps.Animal && !pawn.IsPsychologicallyInvisible()))
                .OrderBy(target => Api.Distance3D(gun.Position, target.Position, map))
                .Take(ShotChecksPerGun)
                .FirstOrDefault(target => gun.AttackVerb.CanHitTargetFrom(gun.Position, target));

        internal static bool HasShot(Building_TurretGunCE gun, Thing target) =>
            (gun.CompFireArc?.WithinFireArc(target) ?? true) && gun.AttackVerb.CanHitTargetFrom(gun.Position, target);

        internal void StandDown(Lord lord, int sector, FortressLine line)
        {
            int index = 0;
            foreach (Pawn pawn in lord.ownedPawns)
            {
                PawnDuty duty = pawn.mindState.duty;
                if (duty?.def != FortressDutyDefOf.RS_FortressGuard || duty.tag != sector.ToString() ||
                    !duty.focusSecond.HasThing || !CanFight(pawn)) continue;
                IntVec3 post = ground.Post(line, sector, index++);
                SetDuty(pawn, duty.def, post.IsValid ? post : ReservePost(line), 6f, tag: duty.tag);
            }
        }

        internal bool Dispatch(Lord lord, LordJob_FortressDefense defense, Thing target, int readyInside, bool counterSortie)
        {
            IEnumerable<Pawn> pool = counterSortie ? ResponsePool(lord) : FreeReserve(lord);
            Pawn[] candidates = pool.Where(pawn => !ground.Outside(pawn.Position) &&
                !pawn.WorkTagIsDisabled(WorkTags.Violent)).ToArray();
            int desired = counterSortie ? Math.Max(4, (candidates.Length * 3 + 9) / 10) : Math.Max(1, readyInside / 10);
            int retained = (readyInside * 3 + 9) / 10;
            int count = Math.Min(desired, Math.Min(candidates.Length, readyInside - retained));
            int minRequired = counterSortie ? 4 : Math.Min(4, desired);
            if (count < minRequired) return false;
            foreach (IntVec3 exit in ground.Gates
                .Select(gate => gate.Approach)
                .Where(cell => cell.Standable(map))
                .OrderBy(cell => Api.Distance3D(cell, target.Position, map)))
            {
                Pawn[] raiders = candidates.OrderBy(pawn => SortiePriority(pawn, defense))
                    .ThenBy(pawn => Api.Distance3D(pawn.Position, exit, map))
                    .Where(pawn => pawn.CanReach(exit, PathEndMode.OnCell, Danger.Deadly)).Take(count).ToArray();
                if (raiders.Length < minRequired) continue;
                foreach (Pawn pawn in raiders)
                {
                    lord.RemovePawn(pawn);
                    if (!pawn.Downed && pawn.CurJob is Job current && current.lord == lord)
                        pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                }
                LordMaker.MakeNewLord(lord.faction, new LordJob_FortressSortie(target, exit, counterSortie), map, raiders);
                return true;
            }
            return false;
        }

        private static IEnumerable<Pawn> FreeReserve(Lord lord) =>
            lord.ownedPawns.Where(pawn => Reserve(pawn) && Ready(pawn) && !pawn.mindState.duty.focusSecond.IsValid);

        private static IEnumerable<Pawn> ResponsePool(Lord lord) => lord.ownedPawns.Where(pawn => Ready(pawn) &&
            (Reserve(pawn) && (!pawn.mindState.duty.focusSecond.IsValid ||
                pawn.mindState.duty.tag == FortressMaintenance.DutyTag) ||
             Guard(pawn) && !Patrol(pawn) && !pawn.mindState.duty.focusSecond.IsValid));

        private static int SortiePriority(Pawn pawn, LordJob_FortressDefense defense)
        {
            if (Reserve(pawn) && !pawn.mindState.duty.focusSecond.IsValid) return 0;
            if (Guard(pawn) && TryGetDutySector(pawn, out int sector) && !defense.SectorUnderWatch(sector)) return 1;
            return Reserve(pawn) ? 2 : 3;
        }

        private static IEnumerable<Pawn> DefenseResponsePool(Lord lord, LordJob_FortressDefense defense) =>
            lord.ownedPawns.Where(pawn => CanAnswer(pawn) &&
                (Reserve(pawn) && (pawn.mindState.duty.tag != ReloadTag) &&
                    (defense.Line == FortressLine.Curtain || !pawn.mindState.duty.focusSecond.IsValid) ||
                Guard(pawn) && !Patrol(pawn) && !pawn.mindState.duty.focusSecond.IsValid &&
                    int.TryParse(pawn.mindState.duty.tag, out int sector) && !defense.SectorUnderWatch(sector)));

        private static int DefenseResponsePriority(Pawn pawn)
        {
            int fatigue = Ready(pawn) ? 0 : 4;
            if (!Reserve(pawn)) return fatigue + 1;
            if (pawn.mindState.duty.tag == FortressMaintenance.DutyTag) return fatigue + 2;
            return fatigue + (pawn.mindState.duty.focusSecond.Pawn == null ? 0 : 3);
        }

        private static bool IsEatingOrSleeping(Pawn pawn) =>
            pawn.CurJobDef == JobDefOf.Ingest || pawn.CurJobDef == JobDefOf.LayDown;

        private void Recover(Pawn pawn, FortressLine line)
        {
            IntVec3 post = line == FortressLine.Keep ? ReservePost(line) : ground.MedicalPost;
            SetDuty(pawn, FortressDutyDefOf.RS_FortressRecover, post, 7f);
        }

        private IntVec3 ReservePost(FortressLine line) =>
            line == FortressLine.Keep ? ground.Post(line, 0, 0) : ground.RestPost;

        private bool Usable(Building_TurretGunCE gun) => gun.Spawned && gun.Faction == map.ParentFaction;
        private static bool CanMan(Pawn pawn) => !pawn.WorkTagIsDisabled(WorkTags.Violent) &&
            pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation);
        private static bool CanReachGun(Pawn pawn, Building_TurretGunCE gun) =>
            pawn.CanReserveAndReach(gun, PathEndMode.InteractionCell, Danger.Deadly);
        private float GunDistance(Pawn pawn, Building_TurretGunCE gun) => Api.Distance3D(pawn.Position, gun.Position, map);

        private static void SetDuty(Pawn pawn, DutyDef def, LocalTargetInfo focus, float radius = -1f,
            LocalTargetInfo? second = null, string tag = null)
        {
            PawnDuty old = pawn.mindState.duty;
            LocalTargetInfo target = second ?? LocalTargetInfo.Invalid;
            if (old != null && old.def == def && old.focus == focus && old.focusSecond == target && old.tag == tag) return;
            pawn.mindState.duty = new PawnDuty(def, focus, target, radius) { tag = tag };
            if (def == FortressDutyDefOf.RS_FortressIntercept && target.Thing is Building_TurretGunCE gun &&
                pawn.CurJobDef == JobDefOf.ManTurret && pawn.CurJob.targetA.Thing == gun) return;
            if (!pawn.Downed && pawn.CurJob is Job current &&
                (current.lord == pawn.GetLord() ||
                    (def == FortressDutyDefOf.RS_FortressGuard || def == FortressDutyDefOf.RS_FortressIntercept) &&
                    IsEatingOrSleeping(pawn)) &&
                (def != FortressDutyDefOf.RS_FortressReserve ||
                    current.def != JobDefOf.Ingest && current.def != JobDefOf.LayDown))
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
        }
    }
}
