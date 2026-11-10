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

        private static bool Ready(Pawn pawn) => CanFight(pawn) && !pawn.InMentalState &&
            pawn.CurJobDef != JobDefOf.LayDown && pawn.CurJobDef != JobDefOf.Ingest &&
            pawn.health.hediffSet.BleedRateTotal <= 0f &&
            (pawn.needs.food?.CurLevelPercentage ?? 1f) >= FoodThreshold &&
            (pawn.needs.rest?.CurLevelPercentage ?? 1f) >= RestThreshold;

        private static bool Guard(Pawn pawn) => pawn.mindState?.duty?.def == FortressDutyDefOf.RS_FortressGuard;
        private static bool Reserve(Pawn pawn) => pawn.mindState?.duty?.def == FortressDutyDefOf.RS_FortressReserve;
        private static bool Sortie(Pawn pawn) => pawn.mindState.duty?.def == FortressDutyDefOf.RS_FortressSortie;
        private static bool Recovering(Pawn pawn) => pawn.mindState.duty?.def == FortressDutyDefOf.RS_FortressRecover;
        internal static bool Patrol(Pawn pawn) => Guard(pawn) &&
            pawn.mindState.duty.tag?.StartsWith(PatrolTag, StringComparison.Ordinal) == true;
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
                    Pawn replacement = ResponsePool(lord, defense).Where(CanMan)
                        .OrderByDescending(Reserve).ThenBy(other => Api.Distance3D(other.Position, previous.focus.Cell, map))
                        .FirstOrDefault(other => previous.focusSecond.Thing is Building_TurretGunCE gun
                            ? Usable(gun) && CanReachGun(other, gun)
                            : other.CanReach(previous.focus.Cell, PathEndMode.OnCell, Danger.Deadly));
                    if (replacement != null)
                    {
                        SetDuty(replacement, previous.def, previous.focus, previous.radius, previous.focusSecond, previous.tag);
                        replacement.mindState.duty.focusThird = previous.focusThird;
                    }
                }
                if (injured && pawn.health.HasHediffsNeedingTend())
                {
                    var activePatients = lord.ownedPawns.Where(Reserve)
                        .Select(other => other.mindState.duty.focusSecond.Pawn).OfType<Pawn>().ToList();
                    if (activePatients.Count < MedicalCrews && !activePatients.Contains(pawn))
                    {
                        Pawn medic = FreeReserve(lord).Where(other => !other.WorkTypeIsDisabled(WorkTypeDefOf.Doctor))
                            .OrderByDescending(other => other.skills?.GetSkill(SkillDefOf.Medicine).Level ?? 0)
                            .FirstOrDefault(other => FortressSupportJobs.CanTreat(other, pawn));
                        if (medic != null)
                            SetDuty(medic, FortressDutyDefOf.RS_FortressReserve, ReservePost(line), 8f, pawn);
                    }
                }
            }
            else if (Recovering(pawn) && !HealthAIUtility.ShouldSeekMedicalRest(pawn))
                SetDuty(pawn, FortressDutyDefOf.RS_FortressReserve, ReservePost(line), 8f);
            return postLost;
        }

        internal void Assign(Lord lord, LordJob_FortressDefense defense, List<FortressWatch> watches, Pawn sortieTarget, int now)
        {
            FortressLine line = defense.Line;
            var healthy = new List<Pawn>();
            var patients = new List<Pawn>();
            foreach (Pawn pawn in lord.ownedPawns)
            {
                if (!pawn.Spawned || pawn.Dead || pawn.IsPrisoner) continue;
                if (pawn.health.HasHediffsNeedingTend()) patients.Add(pawn);
                if (pawn.Downed || pawn.health.hediffSet.BleedRateTotal > 0f ||
                    Recovering(pawn) && HealthAIUtility.ShouldSeekMedicalRest(pawn))
                {
                    Recover(pawn, line);
                }
                else if (!pawn.InMentalState && !(Sortie(pawn) && sortieTarget != null)) healthy.Add(pawn);
            }

            int current = lord.ownedPawns.Count(pawn => pawn.Spawned && !pawn.IsPrisoner && !ground.Outside(pawn.Position));
            int minimum = line == FortressLine.Keep ? healthy.Count : (current * 3 + 9) / 10;
            var gunners = healthy.Where(pawn => Guard(pawn) && Ready(pawn) &&
                pawn.mindState.duty.focusSecond.Thing is Building_TurretGunCE gun &&
                int.TryParse(pawn.mindState.duty.tag, out int sector) &&
                watches[sector].Until > now && EligibleGun(defense, sector, gun)).ToHashSet();
            List<Pawn> guards = healthy.Where(pawn => !gunners.Contains(pawn) && Ready(pawn))
                .OrderByDescending(Patrol)
                .ThenByDescending(Guard)
                .Take(minimum).ToList();
            var guardSet = new HashSet<Pawn>(guards);
            var reserves = healthy.Where(pawn => !guardSet.Contains(pawn) && !gunners.Contains(pawn)).ToList();
            int[] sectorOrder = Enumerable.Range(0, 4).OrderByDescending(sector => watches[sector].Until > now).ToArray();
            int[] patrolSectors = line == FortressLine.Curtain
                ? sectorOrder.Where(sector => ground.CurtainPost(sector).IsValid).ToArray() : Array.Empty<int>();
            int patrolCount = patrolSectors.Length == 0 ? 0 : Math.Min(guards.Count, (current + 9) / 10);
            for (int i = 0; i < guards.Count; i++)
            {
                bool patrol = i < patrolCount;
                int sector = patrol ? patrolSectors[i % patrolSectors.Length] : sectorOrder[(i - patrolCount) % 4];
                IntVec3 post = patrol ? ground.CurtainPost(sector) : ground.Post(line, sector, i / 4);
                SetDuty(guards[i], FortressDutyDefOf.RS_FortressGuard, post.IsValid ? post : ReservePost(line), 6f,
                    tag: patrol ? PatrolTag + sector : sector.ToString());
            }

            var medics = AssignMedics(reserves, patients);
            var builders = AssignBuilders(lord, defense, reserves.Where(pawn => !medics.ContainsKey(pawn)));
            IntVec3 reservePost = ReservePost(line);
            foreach (Pawn pawn in reserves)
            {
                if (builders.TryGetValue(pawn, out IntVec3 project))
                {
                    SetDuty(pawn, FortressDutyDefOf.RS_FortressReserve, reservePost, 8f, project, FortressMaintenance.DutyTag);
                    continue;
                }
                medics.TryGetValue(pawn, out Pawn work);
                if (line != FortressLine.Keep && work == null && pawn.mindState.duty?.tag == ReloadTag) continue;
                SetDuty(pawn, FortressDutyDefOf.RS_FortressReserve, reservePost, 8f, work);
            }

            if (line != FortressLine.Keep)
            {
                int infantry = defense.InfantryPerSector(now);
                foreach (int sector in sectorOrder)
                    if (watches[sector].Until > now) ArmContact(lord, defense, sector, defense.AimAt(watches[sector]), infantry);
            }
        }

        private Dictionary<Pawn, Pawn> AssignMedics(List<Pawn> reserves, List<Pawn> patients)
        {
            var medics = new Dictionary<Pawn, Pawn>();
            var assigned = new HashSet<Pawn>();
            foreach (Pawn pawn in reserves)
            {
                if (Reserve(pawn) && pawn.mindState.duty.focusSecond.Pawn is Pawn patient &&
                    (pawn.CurJobDef == JobDefOf.Rescue || pawn.CurJobDef == JobDefOf.TendPatient) &&
                    pawn.CurJob.targetA.Pawn == patient && assigned.Add(patient))
                    medics.Add(pawn, patient);
            }
            var candidates = reserves.Where(pawn => Ready(pawn) && pawn.mindState.duty?.tag != ReloadTag &&
                !medics.ContainsKey(pawn) && !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Doctor)).ToList();
            foreach (Pawn patient in patients.OrderByDescending(pawn => pawn.Downed)
                .ThenByDescending(pawn => pawn.health.hediffSet.BleedRateTotal))
            {
                if (medics.Count >= MedicalCrews || candidates.Count == 0) break;
                if (assigned.Contains(patient)) continue;
                Pawn medic = candidates.Where(pawn => pawn != patient)
                    .OrderByDescending(pawn => pawn.mindState.duty?.focusSecond.Pawn == patient)
                    .ThenByDescending(pawn => pawn.skills?.GetSkill(SkillDefOf.Medicine).Level ?? 0)
                    .ThenBy(pawn => Api.Distance3D(pawn.Position, patient.Position, map))
                    .FirstOrDefault(pawn => FortressSupportJobs.CanTreat(pawn, patient));
                if (medic == null) continue;
                medics.Add(medic, patient);
                candidates.Remove(medic);
            }
            return medics;
        }

        private Dictionary<Pawn, IntVec3> AssignBuilders(Lord lord, LordJob_FortressDefense defense, IEnumerable<Pawn> reserves)
        {
            var builders = new Dictionary<Pawn, IntVec3>();
            if (defense.Line == FortressLine.Keep) return builders;
            Thing[] projects = FortressMaintenance.Projects(map, lord.faction, defense);
            if (projects.Length == 0) return builders;
            var assigned = new HashSet<IntVec3>();
            foreach (Pawn builder in reserves.Where(pawn => Ready(pawn) && pawn.mindState.duty?.tag != ReloadTag &&
                    !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Construction) &&
                    pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
                .OrderByDescending(pawn => pawn.mindState.duty?.tag == FortressMaintenance.DutyTag)
                .ThenByDescending(pawn => pawn.skills?.GetSkill(SkillDefOf.Construction).Level ?? 0)
                .Take(MaintenanceCrews * 2))
            {
                IntVec3 previous = builder.mindState.duty?.focusSecond.Cell ?? IntVec3.Invalid;
                if (builder.mindState.duty?.tag == FortressMaintenance.DutyTag && builder.CurJob?.workGiverDef != null &&
                    defense.CanMaintain(previous) && assigned.Add(previous))
                    builders.Add(builder, previous);
                else if (FortressMaintenance.FindProject(builder, projects, assigned) is Thing project)
                {
                    builders.Add(builder, project.Position);
                    assigned.Add(project.Position);
                }
                if (builders.Count == MaintenanceCrews) break;
            }
            return builders;
        }

        internal void SuspendExposedMaintenance(Lord lord, LordJob_FortressDefense defense)
        {
            foreach (Pawn pawn in lord.ownedPawns)
                if (Reserve(pawn) && pawn.mindState.duty.tag == FortressMaintenance.DutyTag &&
                    !defense.CanMaintain(pawn.mindState.duty.focusSecond.Cell))
                    SetDuty(pawn, FortressDutyDefOf.RS_FortressReserve, ReservePost(defense.Line), 8f);
        }

        internal void Replenish(Lord lord, FortressLine line, List<FortressWatch> watches, int now)
        {
            if (line == FortressLine.Keep) return;
            var available = lord.ownedPawns.Where(pawn => Reserve(pawn) &&
                (!pawn.mindState.duty.focusSecond.IsValid || pawn.mindState.duty.tag == ReloadTag)).ToList();
            var crews = new Dictionary<Pawn, Building_TurretGunCE>();
            var assigned = new HashSet<Building_TurretGunCE>();
            foreach (Pawn pawn in available)
            {
                if (pawn.mindState.duty.tag == ReloadTag && pawn.CurJobDef == CE_JobDefOf.ReloadTurret &&
                    pawn.CurJob.targetA.Thing is Building_TurretGunCE gun && Usable(gun))
                {
                    crews.Add(pawn, gun);
                    assigned.Add(gun);
                }
            }
            var candidates = available.Where(pawn => Ready(pawn) && CanMan(pawn) && !crews.ContainsKey(pawn)).ToList();
            if (crews.Count < ReloadCrews && candidates.Count > 0)
            {
                foreach (Building_TurretGunCE gun in Guns.Where(gun => Usable(gun) && gun.MannableComp == null &&
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
                    SetDuty(pawn, FortressDutyDefOf.RS_FortressReserve, post, 8f, gun, ReloadTag);
                else if (pawn.mindState.duty.tag == ReloadTag)
                    SetDuty(pawn, FortressDutyDefOf.RS_FortressReserve, post, 8f);
            }
        }

        internal void ArmContact(Lord lord, LordJob_FortressDefense defense, int sector, Thing contact, int infantry)
        {
            if (defense.Line == FortressLine.Keep) return;
            string sectorTag = sector.ToString();
            var local = lord.ownedPawns.Where(pawn => Guard(pawn) && CanFight(pawn) && OnSector(pawn, sector)).ToList();
            foreach (Pawn guard in local)
                if (guard.CurJob is Job job && job.lord == lord && job.def == JobDefOf.GotoWander)
                    guard.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);

            Reinforce(lord, defense, sector, infantry, local);
            var reserves = ResponsePool(lord, defense).Where(CanMan).ToList();
            var crews = lord.ownedPawns.Where(pawn => Guard(pawn) && CanFight(pawn) &&
                pawn.mindState.duty.focusSecond.Thing is Building_TurretGunCE)
                .ToLookup(pawn => (Building_TurretGunCE)pawn.mindState.duty.focusSecond.Thing);
            if (reserves.Count == 0 && !local.Any(pawn => pawn.mindState.duty.focusSecond.HasThing)) return;

            IntVec3 front = blueprint.Outer.GetCenterCellOnEdge(new Rot4(sector));
            Thing[] targets = map.mapPawns.PawnsInFaction(Faction.OfPlayer)
                .Where(pawn => pawn.RaceProps.Humanlike || pawn.RaceProps.IsMechanoid).Cast<Thing>()
                .Concat(map.listerBuildings.allBuildingsColonist)
                .Where(target => defense.ValidTarget(target) && ground.Sector(target.Position) == sector)
                .OrderByDescending(target => target is Pawn or Building_Turret)
                .ThenBy(target => Api.Distance3D(front, target.Position, map))
                .Prepend(contact).Where(target => target != null).Distinct().ToArray();

            foreach (Building_TurretGunCE gun in Guns.Where(gun => EligibleGun(defense, sector, gun))
                .OrderByDescending(Indirect).ThenBy(gun => Api.Distance3D(front, gun.Position, map)))
            {
                Pawn gunner = crews[gun].FirstOrDefault();
                if (gunner != null && !OnSector(gunner, sector) || gunner == null && reserves.Count == 0) continue;

                Thing target = targets.Where(candidate => CanAimAt(gun, candidate))
                    .Take(ShotChecksPerGun).FirstOrDefault(candidate => gun.AttackVerb.CanHitTargetFrom(gun.Position, candidate));
                if (target == null) continue;

                if (gunner == null)
                {
                    gunner = reserves.OrderByDescending(Reserve).ThenBy(pawn => GunDistance(pawn, gun))
                        .FirstOrDefault(pawn => CanReachGun(pawn, gun));
                    if (gunner == null) continue;
                    SetDuty(gunner, FortressDutyDefOf.RS_FortressGuard, gun.InteractionCell, 6f, gun, sectorTag);
                    reserves.Remove(gunner);
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
                if (Patrol(guard) || !Ready(guard) || guard.mindState.duty.focusSecond.HasThing) continue;
                IntVec3 post = guard.mindState.duty.focus.Cell;
                if (!posts.ContainsKey(post)) continue;
                posts[post]++;
                stationed++;
            }

            for (int i = stationed; i < infantry; i++)
            {
                IntVec3 post = posts.MinBy(pair => pair.Value).Key;
                Pawn reinforcement = ResponsePool(lord, defense).Where(CanMan)
                    .OrderByDescending(Reserve).ThenBy(pawn => Api.Distance3D(pawn.Position, post, map))
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

        private bool EligibleGun(LordJob_FortressDefense defense, int sector, Building_TurretGunCE gun) =>
            defense.Line != FortressLine.Keep && Usable(gun) && gun.Active && gun.MannableComp != null &&
            (!ground.OnCurtain(gun.Position) || defense.Line == FortressLine.Curtain) &&
            (!ground.TowerMiddle(gun.Position) || defense.MiddleOpen(gun.Position, sector)) &&
            (Indirect(gun) || ground.Sector(gun.Position) == sector || ground.TowerFaces(gun.Position, sector));

        private bool CanAimAt(Building_TurretGunCE gun, Thing target) =>
            Api.RangeDistance(gun.Position, target.Position, map) <= gun.AttackVerb.EffectiveRange &&
            (gun.CompFireArc?.WithinFireArc(target) ?? true);

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

        internal bool Dispatch(Lord lord, Pawn target, int count)
        {
            IntVec3 exit = blueprint.Outer.GetCenterCellOnEdge(blueprint.GateSide) + blueprint.GateSide.FacingCell;
            Pawn[] raiders = FreeReserve(lord).Where(pawn => ground.Inside(pawn.Position) &&
                !pawn.WorkTagIsDisabled(WorkTags.Violent))
                .OrderBy(pawn => Api.Distance3D(pawn.Position, exit, map))
                .Where(pawn => pawn.CanReach(exit, PathEndMode.OnCell, Danger.Deadly)).Take(count).ToArray();
            if (raiders.Length < count) return false;
            foreach (Pawn pawn in raiders)
                SetDuty(pawn, FortressDutyDefOf.RS_FortressSortie, target, second: exit);
            return true;
        }

        private static IEnumerable<Pawn> FreeReserve(Lord lord) =>
            lord.ownedPawns.Where(pawn => Reserve(pawn) && Ready(pawn) && !pawn.mindState.duty.focusSecond.IsValid);

        private static IEnumerable<Pawn> ResponsePool(Lord lord, LordJob_FortressDefense defense) =>
            FreeReserve(lord).Concat(lord.ownedPawns.Where(pawn => Guard(pawn) && !Patrol(pawn) && Ready(pawn) &&
                !pawn.mindState.duty.focusSecond.IsValid &&
                int.TryParse(pawn.mindState.duty.tag, out int sector) && !defense.SectorUnderWatch(sector)));

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
            if (!pawn.Downed && pawn.CurJob is Job current && current.lord == pawn.GetLord() &&
                (def != FortressDutyDefOf.RS_FortressReserve ||
                    current.def != JobDefOf.Ingest && current.def != JobDefOf.LayDown))
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
        }
    }
}
