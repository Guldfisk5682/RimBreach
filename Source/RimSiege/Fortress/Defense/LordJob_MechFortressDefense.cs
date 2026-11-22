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
    public sealed class LordJob_MechFortressDefense : LordJob, IFortressDefenseLord
    {
        private const int ReviewInterval = 1800;
        private const int MaintenanceDamageCooldown = 600;
        private const int CounterSortieCooldown = GenDate.TicksPerDay / 2;
        private const int ResponseReviewDelay = GenTicks.TicksPerRealSecond;
        private const int IntrusionCheckInterval = 60;
        private const int LookoutInterval = 210;
        private const int LogisticsInterval = 300;
        private const float LookoutRange = 120f;
        private const int LookoutChecksPerTarget = 6;
        private const int LookoutChecksPerScan = 24;
        private const int LookoutTargetsPerScan = 8;
        private const int MaintenanceRetryDelay = 300;
        private const int MaintenanceRetries = 2;

        private int initialCombatStrength;
        private int nextReview;
        private int nextQuiet = int.MaxValue;
        private int openSectors;
        private List<FortressWatch> watches = Enumerable.Range(0, 4).Select(_ => new FortressWatch()).ToList();
        private Thing bombardmentSource;
        private int bombardmentUntil;
        private int counterSortieAt;
        private bool counterSortiesClosed;
        private int nextLookout;
        private int lookoutCursor;
        private int contactSectors;
        private bool maintenanceReviewRequested;
        private bool resupplyRequested;
        private int nextMaintenance = int.MaxValue;
        private int maintenanceRetriesLeft;
        private int maintenanceCursor;
        private int nextIntrusionCheck;
        private int nextLogistics;

        private FortressDefenseGround ground;
        private FortressTargeting targeting;
        private List<Building_TurretGunCE> guns;
        private readonly List<Pawn> intruders = new();

        private FortressSite Site => (FortressSite)Map.Parent;
        public bool IsDefenseActive => lord.CurLordToil is LordToil_MechFortressDefense &&
            Map?.Parent is FortressSite { Resolved: false };
        public bool CanContinueSortie => IsDefenseActive && !IntrusionActive;
        public bool CanRout => false;
        public bool HasPendingCounterSortie => counterSortieAt != 0 || bombardmentSource != null || bombardmentUntil != 0;
        internal FortressDefenseGround Ground => ground ??= new FortressDefenseGround(Map, Site.Blueprint);
        FortressDefenseGround IFortressDefenseLord.Ground => Ground;
        internal FortressTargeting Targeting => targeting ??= new FortressTargeting(Map, lord.faction);
        FortressTargeting IFortressDefenseLord.Targeting => Targeting;
        public bool IntrusionActive => openSectors != 0;
        public IReadOnlyList<Pawn> Intruders => intruders;
        public override bool AddFleeToil => false;
        public override bool CanAutoAddPawns => false;
        public override bool ShouldRemovePawn(Pawn pawn, PawnLostCondition reason) => reason != PawnLostCondition.Incapped;

        private List<Building_TurretGunCE> Guns => guns ??= Site.Blueprint.Defenses
            .Where(piece => piece.Def?.building?.turretGunDef != null)
            .Select(piece => piece.CellOn(Map).GetFirstThing(Map, piece.Def))
            .OfType<Building_TurretGunCE>()
            .Where(gun => gun.Spawned && gun.Faction == lord.faction)
            .ToList();

        public override StateGraph CreateGraph()
        {
            var hold = new LordToil_MechFortressDefense();
            return new StateGraph { StartingToil = hold };
        }

        internal void Start()
        {
            initialCombatStrength = lord.ownedPawns.Count(pawn => !IsLogistics(pawn));
            int now = GenTicks.TicksGame;
            CheckIntrusion(now);
            Review(now);
        }

        public override void LordJobTick()
        {
            if (!IsDefenseActive) return;
            int now = GenTicks.TicksGame;
            if (now >= nextIntrusionCheck) CheckIntrusion(now);
            if (!IntrusionActive && now >= nextLookout) LookoutScan(now);

            bool reviewDue = now >= nextReview;
            if (maintenanceReviewRequested || contactSectors != 0)
            {
                maintenanceReviewRequested = false;
                SuspendExposedLogistics(now);
            }
            if (contactSectors != 0)
            {
                contactSectors = 0;
                if (!reviewDue && !IntrusionActive) AssignCombat(now);
                resupplyRequested = true;
                RequestMaintenance(now + ResponseReviewDelay);
            }
            if (reviewDue) Review(now);
            else if (now >= nextQuiet) UpdateContacts(now);

            if (counterSortieAt != 0 && now >= counterSortieAt) CheckCounterSortie(now);

            if (now >= nextLogistics)
            {
                nextLogistics = now + LogisticsInterval;
                resupplyRequested = true;
                if (nextMaintenance == int.MaxValue) RequestMaintenance(now);
            }

            if (resupplyRequested)
            {
                resupplyRequested = false;
                ReplenishAndResurrect(now);
            }
            if (now >= nextMaintenance)
            {
                nextMaintenance = int.MaxValue;
                if (FillMaintenance() && maintenanceRetriesLeft > 0)
                {
                    maintenanceRetriesLeft--;
                    nextMaintenance = now + MaintenanceRetryDelay;
                }
            }
        }

        public override void Notify_PawnJobDone(Pawn pawn, JobCondition condition)
        {
            if (!IsDefenseActive) return;
            int now = GenTicks.TicksGame;
            PawnDuty duty = pawn.mindState?.duty;
            if (duty?.tag == FortressDefensePosts.ReloadTag && pawn.CurJobDef == CE_JobDefOf.ReloadTurret)
            {
                ClearLogisticsTask(pawn);
                resupplyRequested = true;
            }
            else if (duty?.tag == FortressMechResurrection.DutyTag &&
                pawn.CurJobDef == FortressMechJobDefOf.RS_MechFortressLoadGestator)
            {
                ClearLogisticsTask(pawn);
                resupplyRequested = true;
            }
            else if (duty?.tag == FortressMaintenance.DutyTag &&
                pawn.CurJob?.workGiverDef?.workType == WorkTypeDefOf.Construction)
            {
                ClearLogisticsTask(pawn);
                RequestMaintenance(now + ResponseReviewDelay);
            }
        }

        public void RequestResupply() => resupplyRequested = true;

        public void RequestRoutCheck(int now) { }

        public void CancelCounterSortie()
        {
            bombardmentSource = null;
            bombardmentUntil = 0;
            counterSortieAt = 0;
        }

        public void MergeSortie(List<Pawn> returning, int now)
        {
            if (returning == null || returning.Count == 0) return;
            lord.AddPawns(returning, updateDuties: true);
            foreach (Pawn pawn in returning)
                if (!pawn.Downed && pawn.CurJob != null)
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
            Review(now);
        }

        public bool SectorUnderWatch(int sector) => watches[sector].Until > GenTicks.TicksGame;

        public bool AnySectorUnderWatch()
        {
            int now = GenTicks.TicksGame;
            return watches.Any(watch => watch.Until > now);
        }

        public bool CanMaintain(IntVec3 cell)
        {
            if (!IsDefenseActive || IntrusionActive) return false;
            int floor = Api.FloorOf(cell, Map);
            if (floor > 1 || !Ground.WithinMechLogisticsRange(cell) || !Ground.ContainsFortressStructure(cell)) return false;
            int now = GenTicks.TicksGame;
            int sector = Ground.Sector(cell);
            bool exterior = Ground.Outside(cell);
            if (exterior)
                return !SectorUnderWatch(sector) && watches[sector].DamageUntil <= now;
            return watches[sector].DamageUntil <= now;
        }

        public void Attacked(Thing victim, DamageInfo damage)
        {
            if (damage.Instigator is not Thing attacker || !victim.HostileTo(attacker) ||
                !damage.Def.ExternalViolenceFor(victim)) return;
            if (attacker is Pawn operatorPawn && operatorPawn.CurJobDef == JobDefOf.ManTurret &&
                operatorPawn.CurJob.targetA.Thing is Building_Turret turret &&
                turret.TryGetComp<CompMannable>()?.ManningPawn == operatorPawn && ValidTarget(turret))
                attacker = turret;

            int damagedSector = Ground.Sector(victim.Position);
            int now = GenTicks.TicksGame;
            if (watches[damagedSector].DamageUntil <= now) maintenanceReviewRequested = true;
            watches[damagedSector].DamageUntil = now + MaintenanceDamageCooldown;
            nextQuiet = Math.Min(nextQuiet, watches[damagedSector].DamageUntil);
            int responseSector = attacker.Spawned && attacker.Map == Map && Ground.Outside(attacker.Position)
                ? Ground.Sector(attacker.Position) : damagedSector;
            Alarm(responseSector, attacker, sighted: false);

            if (counterSortiesClosed || IntrusionActive || !ValidTarget(attacker) ||
                !Ground.Outside(attacker.Position) || now < Site.NextCounterSortieTick || SortieActive(counter: true))
                return;
            if (counterSortieAt == 0 || now >= bombardmentUntil)
                counterSortieAt = now + ReviewInterval;
            bombardmentSource = attacker;
            bombardmentUntil = now + ReviewInterval;
        }

        internal void Sighted(Pawn guard, Thing target)
        {
            if (!guard.HostileTo(target)) return;
            Alarm(Ground.Sector(target.Position), target);
        }

        internal void BuildingChanged(Building building)
        {
            if (building.Faction != lord.faction) return;
            if (building is Building_TurretGunCE)
            {
                guns = null;
                resupplyRequested = true;
            }
            RequestMaintenance(GenTicks.TicksGame + ResponseReviewDelay);
        }

        internal void CancelVictoryWork()
        {
            Map?.GetComponent<FortressMechResurrection>()?.CancelAll();
            CancelCounterSortie();
            foreach (Pawn pawn in lord.ownedPawns)
            {
                if (!IsLogistics(pawn) || pawn.Dead) continue;
                ClearLogisticsTask(pawn);
                if (pawn.Spawned && !pawn.Downed && pawn.CurJob != null)
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
            }
            resupplyRequested = false;
            nextMaintenance = int.MaxValue;
            maintenanceRetriesLeft = 0;
        }

        public override bool ValidateAttackTarget(Pawn pawn, Thing target)
        {
            if (lord.CurLordToil is not LordToil_MechFortressDefense) return true;
            if (IsLogistics(pawn)) return false;
            if (!Targeting.Allows(target, pawn)) return false;
            if (IntrusionActive) return FortressTargeting.IsCombatTarget(target);

            PawnDuty duty = pawn.mindState?.duty;
            if (duty?.focus.IsValid == true && pawn.CurrentEffectiveVerb?.verbProps.IsMeleeAttack == true &&
                (!Api.SameFloor(duty.focus.Cell, target.Position, Map) ||
                    !target.Position.InHorDistOf(duty.focus.Cell, duty.radius + 3f)))
                return false;
            return true;
        }

        internal static bool IsLifter(Pawn pawn) => pawn != null && (
            pawn.kindDef.defName == "Mech_Lifter" ||
            pawn.RaceProps.IsMechanoid && pawn.WorkTagIsDisabled(WorkTags.Violent) &&
                !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Hauling));

        internal static bool IsConstructoid(Pawn pawn) => pawn != null && (
            pawn.kindDef.defName == "Mech_Constructoid" ||
            pawn.RaceProps.IsMechanoid && pawn.WorkTagIsDisabled(WorkTags.Violent) &&
                !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Construction));

        internal static bool IsLogistics(Pawn pawn) => IsLifter(pawn) || IsConstructoid(pawn);

        private static bool IsCloseAnchor(Pawn pawn) => pawn != null && (
            pawn.kindDef.defName == "Mech_Tunneler" ||
            pawn.equipment?.Primary == null && pawn.CurrentEffectiveVerb?.verbProps.IsMeleeAttack == true);

        private void RequestMaintenance(int tick)
        {
            nextMaintenance = Math.Min(nextMaintenance, tick);
            maintenanceRetriesLeft = MaintenanceRetries;
        }

        private void Alarm(int sector, Thing target, bool sighted = true, bool requestReview = true)
        {
            int now = GenTicks.TicksGame;
            FortressWatch watch = watches[sector];
            bool hasTarget = ValidTarget(target);
            bool newContact = watch.Until <= now || hasTarget && (sighted ? !ValidTarget(watch.Target) : AimAt(watch) == null);
            watch.Until = now + ReviewInterval;
            if (hasTarget)
            {
                if (sighted) watch.Target = target;
                else watch.DamageSource = target;
                if (requestReview && !IntrusionActive && target is Pawn && Ground.InDefenseArea(target.Position))
                    nextIntrusionCheck = now;
            }
            nextQuiet = Math.Min(nextQuiet, watch.Until);
            if (newContact) contactSectors |= 1 << sector;
        }

        private void CheckIntrusion(int now)
        {
            nextIntrusionCheck = now + IntrusionCheckInterval;
            bool wasActive = IntrusionActive;
            intruders.Clear();
            openSectors = 0;
            foreach (Thing threat in Targeting.CombatTargets())
            {
                if (threat is not Pawn pawn || pawn.Downed || !ValidTarget(pawn) ||
                    !Ground.InDefenseArea(pawn.Position) ||
                    pawn.IsAnimal && pawn.training?.HasLearned(TrainableDefOf.Release) != true) continue;
                intruders.Add(pawn);
                openSectors |= 1 << Ground.Sector(pawn.Position);
            }
            if (wasActive == IntrusionActive) return;
            nextReview = now;
            maintenanceReviewRequested = true;
            if (IntrusionActive)
            {
                CancelCounterSortie();
                foreach (Pawn intruder in intruders)
                    Alarm(Ground.Sector(intruder.Position), intruder, requestReview: false);
            }
        }

        private void Review(int now)
        {
            nextReview = now + ReviewInterval;
            UpdateContacts(now);
            if (!counterSortiesClosed) CloseCounterSortiesIfDepleted();
            if (IntrusionActive) CancelCounterSortie();
            contactSectors = 0;
            AssignCombat(now);
            EnsureLogisticsPosts();
            resupplyRequested = true;
            RequestMaintenance(now);

            if (!IntrusionActive && !SortieActive(counter: false) && now >= Site.NextSortieTick)
                TryArrivalSortie(now);
        }

        private void AssignCombat(int now)
        {
            var closeAnchors = new List<Pawn>();
            var rangedGuards = new List<Pawn>();
            foreach (Pawn pawn in lord.ownedPawns)
            {
                if (!FortressDefensePosts.CanFight(pawn) || pawn.InMentalState || IsLogistics(pawn)) continue;
                if (IntrusionActive)
                {
                    if (pawn.mindState.duty?.def != FortressDutyDefOf.RS_MechFortressIntercept)
                    {
                        pawn.mindState.duty = new PawnDuty(FortressDutyDefOf.RS_MechFortressIntercept, Ground.CoreGroundPost, 8f);
                        InterruptIdle(pawn);
                    }
                    continue;
                }
                if (IsCloseAnchor(pawn)) closeAnchors.Add(pawn);
                else rangedGuards.Add(pawn);
            }
            if (IntrusionActive) return;

            int[] activeSectors = Enumerable.Range(0, 4).Where(sector => watches[sector].Until > now).ToArray();
            int activeMask = 0;
            foreach (int sector in activeSectors) activeMask |= FortressGeometry.SectorBit(sector);
            int[] sectorOrder = activeSectors.Length > 0
                ? activeSectors.Concat(Enumerable.Range(0, 4).Where(sector => watches[sector].Until <= now)).ToArray()
                : new[] { 0, 1, 2, 3 };

            // Assign ranged mechanoids (any body size, including Centipedes) to both Floor-1 oblique cupolas and Floor-0 frontal pillboxes.
            int cupolaCount = Ground.MechCupolaCount;
            int[] cupolaOrder = cupolaCount > 0
                ? Enumerable.Range(0, cupolaCount)
                    .OrderByDescending(idx => (FortressDefenseGround.MechCupolaSectorMask(idx) & activeMask) != 0)
                    .ToArray()
                : Array.Empty<int>();
            int cupolaSlots = cupolaCount > 0 ? Math.Min(rangedGuards.Count / 2, cupolaCount * 2) : 0;
            if (cupolaCount > 0 && rangedGuards.Count >= 4 && cupolaSlots < cupolaCount)
                cupolaSlots = Math.Min(cupolaCount, rangedGuards.Count);

            int groundIndex = 0;
            for (int i = 0; i < rangedGuards.Count; i++)
            {
                if (i < cupolaSlots)
                {
                    int cupolaIdx = cupolaOrder[i % cupolaOrder.Length];
                    IntVec3 cupolaPost = Ground.MechCupolaPost(cupolaIdx);
                    if (cupolaPost.IsValid)
                    {
                        int sector = Ground.Sector(cupolaPost);
                        SetGuardDuty(rangedGuards[i], cupolaPost, sector, radius: 2.5f);
                        continue;
                    }
                }
                int groundSector = activeSectors.Length > 0 && groundIndex < activeSectors.Length * 2
                    ? activeSectors[groundIndex % activeSectors.Length]
                    : sectorOrder[groundIndex % 4];
                IntVec3 post = Ground.MechSectorPost(groundSector, groundIndex / 4);
                SetGuardDuty(rangedGuards[i], post, groundSector);
                groundIndex++;
            }

            for (int i = 0; i < closeAnchors.Count; i++)
            {
                int sector = sectorOrder[i % sectorOrder.Length];
                IntVec3 post = i % 2 == 0
                    ? Ground.MechSectorPost(sector, 2)
                    : Ground.CoreGroundPost;
                SetGuardDuty(closeAnchors[i], post, sector);
            }
        }

        private void SetGuardDuty(Pawn pawn, IntVec3 post, int sector, float radius = 6f)
        {
            IntVec3 dest = post.IsValid ? post : Ground.CoreGroundPost;
            string tag = sector.ToString();
            PawnDuty current = pawn.mindState.duty;
            if (current != null && current.def == FortressDutyDefOf.RS_MechFortressGuard &&
                current.focus.Cell == dest && current.tag == tag && Math.Abs(current.radius - radius) < 0.1f) return;
            pawn.mindState.duty = new PawnDuty(FortressDutyDefOf.RS_MechFortressGuard, dest, radius) { tag = tag };
            InterruptIdle(pawn);
        }

        private void EnsureLogisticsPosts()
        {
            int index = 0;
            foreach (Pawn pawn in lord.ownedPawns)
            {
                if (!IsLogistics(pawn) || !FortressDefensePosts.CanFight(pawn)) continue;
                PawnDuty current = pawn.mindState.duty;
                if (current != null && current.def == FortressDutyDefOf.RS_MechFortressLogistics &&
                    current.focus.IsValid)
                {
                    index++;
                    continue;
                }
                pawn.mindState.duty = new PawnDuty(FortressDutyDefOf.RS_MechFortressLogistics,
                    Ground.MechLogisticsPost(index++), 6f);
            }
        }

        private bool CanReloadGun(Building_TurretGunCE gun, int now)
        {
            if (gun is not { Spawned: true } || gun.Faction != lord.faction ||
                !Ground.WithinMechLogisticsRange(gun.Position)) return false;
            int sector = Ground.Sector(gun.Position);
            bool quiet = !SectorUnderWatch(sector) && watches[sector].DamageUntil <= now;
            float threshold = quiet ? 0.85f : 0.5f;
            if (!gun.ShouldReload(threshold)) return false;
            if (!Ground.Outside(gun.Position)) return true;
            return !IntrusionActive && quiet;
        }

        private bool CanHaulCorpse(Corpse corpse, int now)
        {
            if (corpse is not { Spawned: true } || corpse.InnerPawn == null ||
                corpse.InnerPawn.Faction != lord.faction || !corpse.InnerPawn.RaceProps.IsMechanoid)
                return false;
            if (IntrusionActive || Api.FloorOf(corpse.Position, Map) > 1 ||
                !Ground.WithinMechLogisticsRange(corpse.Position)) return false;
            int sector = Ground.Sector(corpse.Position);
            if (!Ground.Outside(corpse.Position))
                return watches[sector].DamageUntil <= now;
            return !SectorUnderWatch(sector) && watches[sector].DamageUntil <= now;
        }

        private void ReplenishAndResurrect(int now)
        {
            EnsureLogisticsPosts();
            FortressMechResurrection resurrection = Map.GetComponent<FortressMechResurrection>();
            var busyGuns = new HashSet<Building_TurretGunCE>();
            var busyCorpses = new HashSet<Corpse>();
            var busyGestators = new HashSet<Building>();
            var freeLifters = new List<Pawn>();

            foreach (Pawn pawn in lord.ownedPawns)
            {
                if (!IsLifter(pawn) || !FortressDefensePosts.CanFight(pawn) || pawn.InMentalState) continue;
                PawnDuty duty = pawn.mindState.duty;
                if (duty?.tag == FortressDefensePosts.ReloadTag &&
                    duty.focusSecond.Thing is Building_TurretGunCE currentGun &&
                    CanReloadGun(currentGun, now) &&
                    (pawn.CurJobDef == CE_JobDefOf.ReloadTurret || FortressSupportJobs.CanReload(pawn, currentGun, threshold: 0.85f)))
                {
                    busyGuns.Add(currentGun);
                    continue;
                }
                if (duty?.tag == FortressMechResurrection.DutyTag &&
                    duty.focusSecond.Thing is Corpse currentCorpse &&
                    duty.focusThird.Thing is Building currentGestator &&
                    CanHaulCorpse(currentCorpse, now) &&
                    resurrection?.CanGestatorAccept(currentGestator, currentCorpse.InnerPawn) == true)
                {
                    busyCorpses.Add(currentCorpse);
                    busyGestators.Add(currentGestator);
                    continue;
                }
                if (duty?.tag == FortressDefensePosts.ReloadTag || duty?.tag == FortressMechResurrection.DutyTag)
                    ClearLogisticsTask(pawn);
                freeLifters.Add(pawn);
            }
            if (freeLifters.Count == 0) return;

            // 1. Prioritize turret ammo reload (empty magazines & mortars first).
            var needyGuns = Guns
                .Where(gun => !busyGuns.Contains(gun) && CanReloadGun(gun, now))
                .OrderByDescending(gun => gun.GetAmmo()?.EmptyMagazine == true)
                .ThenByDescending(gun => gun.def.building?.IsMortar == true)
                .ThenByDescending(gun => !Ground.Outside(gun.Position))
                .ThenBy(gun => gun.GetAmmo() is { MagSize: > 0 } ammo ? (float)ammo.CurMagCount / ammo.MagSize : 1f)
                .ToList();

            foreach (Building_TurretGunCE gun in needyGuns)
            {
                if (freeLifters.Count == 0) break;
                Pawn loader = freeLifters
                    .OrderBy(pawn => Api.Distance3D(pawn.Position, gun.Position, Map))
                    .FirstOrDefault(pawn => FortressSupportJobs.CanReload(pawn, gun, threshold: 0.85f));
                if (loader == null) continue;
                PawnDuty duty = loader.mindState.duty;
                duty.focusSecond = gun;
                duty.focusThird = LocalTargetInfo.Invalid;
                duty.tag = FortressDefensePosts.ReloadTag;
                InterruptIdle(loader);
                freeLifters.Remove(loader);
            }

            if (freeLifters.Count == 0 || resurrection == null) return;

            // 2. Assign remaining free Lifters to haul dead allied mechanoid corpses into powered Core Gestators.
            List<Building> freeGestators = resurrection.AvailableGestators(lord.faction)
                .Where(g => !busyGestators.Contains(g)).ToList();
            if (freeGestators.Count == 0) return;

            var corpses = Map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse)
                .OfType<Corpse>()
                .Where(corpse => !busyCorpses.Contains(corpse) && CanHaulCorpse(corpse, now))
                .OrderByDescending(corpse => !IsLogistics(corpse.InnerPawn))
                .ThenByDescending(corpse => FortressMechResurrection.WeightOrder(corpse.InnerPawn))
                .ToList();

            foreach (Corpse corpse in corpses)
            {
                if (freeLifters.Count == 0 || freeGestators.Count == 0) break;
                // Prefer small MechGestator for Light/Medium so LargeMechGestator stays free for Heavy mechs.
                Building gestator = freeGestators
                    .Where(pod => resurrection.CanGestatorAccept(pod, corpse.InnerPawn))
                    .OrderBy(pod => pod.def.defName == "LargeMechGestator")
                    .FirstOrDefault();
                if (gestator == null) continue;

                Pawn hauler = freeLifters
                    .OrderBy(pawn => Api.Distance3D(pawn.Position, corpse.Position, Map))
                    .FirstOrDefault(pawn =>
                        pawn.CanReserveAndReach(corpse, PathEndMode.ClosestTouch, Danger.Deadly) &&
                        pawn.CanReserveAndReach(gestator, PathEndMode.InteractionCell, Danger.Deadly));
                if (hauler == null) continue;

                PawnDuty duty = hauler.mindState.duty;
                duty.focusSecond = corpse;
                duty.focusThird = gestator;
                duty.tag = FortressMechResurrection.DutyTag;
                InterruptIdle(hauler);
                freeLifters.Remove(hauler);
                freeGestators.Remove(gestator);
            }
        }

        private bool FillMaintenance()
        {
            EnsureLogisticsPosts();
            var assigned = new HashSet<IntVec3>();
            var builders = new List<Pawn>();
            foreach (Pawn pawn in lord.ownedPawns)
            {
                if (!IsConstructoid(pawn) || !FortressDefensePosts.CanFight(pawn) || pawn.InMentalState) continue;
                PawnDuty duty = pawn.mindState.duty;
                if (duty?.tag == FortressMaintenance.DutyTag && duty.focusSecond.IsValid)
                {
                    IntVec3 cell = duty.focusSecond.Cell;
                    if (CanMaintain(cell) && HasPendingProjectAt(cell) &&
                        (pawn.CurJob?.workGiverDef?.workType == WorkTypeDefOf.Construction ||
                            FortressMechMaintenance.CanWorkAt(pawn, cell)))
                    {
                        assigned.Add(cell);
                        continue;
                    }
                    ClearLogisticsTask(pawn);
                }
                builders.Add(pawn);
            }
            if (builders.Count == 0) return false;

            Map.GetComponent<FortressRebuildQueue>().PlacePending(this, lord.faction);
            Thing[] projects = FortressMechMaintenance.Projects(Map, lord.faction, this);
            if (projects.Length == 0) return false;

            bool skippedAny = false;
            foreach (Pawn builder in builders)
            {
                Thing project = FortressMechMaintenance.FindProject(builder, projects, assigned,
                    ref maintenanceCursor, out bool skipped);
                skippedAny |= skipped;
                if (project == null) continue;
                PawnDuty duty = builder.mindState.duty;
                duty.focusSecond = project.Position;
                duty.focusThird = LocalTargetInfo.Invalid;
                duty.tag = FortressMaintenance.DutyTag;
                InterruptIdle(builder);
                assigned.Add(project.Position);
            }
            return skippedAny;
        }

        private bool HasPendingProjectAt(IntVec3 cell)
        {
            if (!cell.InBounds(Map)) return false;
            foreach (Thing thing in cell.GetThingList(Map))
            {
                if (thing.Faction != lord.faction) continue;
                if (thing is Blueprint or Frame) return true;
                if (thing is Building building && building.HitPoints < building.MaxHitPoints) return true;
            }
            return false;
        }

        private void SuspendExposedLogistics(int now)
        {
            foreach (Pawn pawn in lord.ownedPawns)
            {
                if (!IsLogistics(pawn) || !FortressDefensePosts.CanFight(pawn)) continue;
                PawnDuty duty = pawn.mindState?.duty;
                if (duty == null) continue;
                if (duty.tag == FortressMaintenance.DutyTag && (!duty.focusSecond.IsValid || !CanMaintain(duty.focusSecond.Cell)))
                {
                    ClearLogisticsTask(pawn);
                    if (pawn.CurJob?.workGiverDef?.workType == WorkTypeDefOf.Construction)
                        pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                }
                else if (duty.tag == FortressDefensePosts.ReloadTag &&
                    (duty.focusSecond.Thing is not Building_TurretGunCE gun || !CanReloadGun(gun, now)))
                {
                    ClearLogisticsTask(pawn);
                    if (pawn.CurJobDef == CE_JobDefOf.ReloadTurret)
                        pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                }
                else if (duty.tag == FortressMechResurrection.DutyTag &&
                    (duty.focusSecond.Thing is not Corpse corpse || !CanHaulCorpse(corpse, now)))
                {
                    ClearLogisticsTask(pawn);
                    if (pawn.CurJobDef == FortressMechJobDefOf.RS_MechFortressLoadGestator)
                        pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                }
            }
        }

        private static void ClearLogisticsTask(Pawn pawn)
        {
            PawnDuty duty = pawn.mindState?.duty;
            if (duty == null) return;
            duty.focusSecond = LocalTargetInfo.Invalid;
            duty.focusThird = LocalTargetInfo.Invalid;
            duty.tag = null;
        }

        private static void InterruptIdle(Pawn pawn)
        {
            if (pawn.CurJobDef == JobDefOf.GotoWander || pawn.CurJobDef == JobDefOf.Wait_Wander ||
                pawn.CurJobDef == JobDefOf.Wait)
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
        }

        private IEnumerable<Lord> ActiveSortieLords() => Map.lordManager.lords.Where(other =>
            other.faction == lord.faction && other.LordJob is LordJob_FortressSortie);

        private bool SortieActive(bool counter) => ActiveSortieLords().Any(sortie =>
            ((LordJob_FortressSortie)sortie.LordJob).CounterSortie == counter &&
            sortie.CurLordToil is LordToil_FortressSortie &&
            sortie.ownedPawns.Any(FortressDefensePosts.CanFight));

        private int ReadyCombatInsideCount() =>
            lord.ownedPawns.Count(pawn => !IsLogistics(pawn) && FortressDefensePosts.CanFight(pawn) &&
                !Ground.Outside(pawn.Position));

        private bool CloseCounterSortiesIfDepleted()
        {
            if (initialCombatStrength <= 0) return false;
            int total = lord.ownedPawns.Count(pawn => !IsLogistics(pawn) && FortressDefensePosts.CanFight(pawn)) +
                ActiveSortieLords().Sum(sortie => sortie.ownedPawns.Count(FortressDefensePosts.CanFight));
            if (total * 2 >= initialCombatStrength) return false;
            counterSortiesClosed = true;
            CancelCounterSortie();
            return true;
        }

        private void TryArrivalSortie(int now)
        {
            int readyInside = ReadyCombatInsideCount();
            if (readyInside < 10) return;

            Thing target = Targeting.CombatTargets()
                .Where(candidate => ValidTarget(candidate) && Ground.Outside(candidate.Position) &&
                    FortressTargeting.IsActiveAttacker(candidate, lord.faction, Map))
                .OrderBy(candidate => Api.Distance3D(Site.Blueprint.Outer.CenterCell, candidate.Position, Map))
                .FirstOrDefault();
            if (target == null) return;

            if (!DispatchSortie(target, readyInside, counterSortie: false)) return;
            Site.NextSortieTick = now + GenDate.TicksPerDay;
        }

        private void CheckCounterSortie(int now)
        {
            Thing target = bombardmentSource;
            if (counterSortiesClosed || IntrusionActive || now >= bombardmentUntil ||
                now < Site.NextCounterSortieTick || SortieActive(counter: true) ||
                !ValidTarget(target) || !Ground.Outside(target.Position))
            {
                CancelCounterSortie();
                return;
            }

            if (CloseCounterSortiesIfDepleted()) return;
            int readyInside = ReadyCombatInsideCount();
            if (readyInside < 8 || !DispatchSortie(target, readyInside, counterSortie: true))
            {
                counterSortieAt = now + ReviewInterval;
                return;
            }

            Site.NextCounterSortieTick = now + CounterSortieCooldown;
            CancelCounterSortie();
        }

        private bool DispatchSortie(Thing target, int readyInside, bool counterSortie)
        {
            Pawn[] candidates = lord.ownedPawns.Where(pawn =>
                !IsLogistics(pawn) && FortressDefensePosts.CanFight(pawn) && !pawn.InMentalState &&
                !Ground.Outside(pawn.Position)).ToArray();
            int desired = counterSortie
                ? Math.Max(4, (candidates.Length * 35 + 99) / 100)
                : Math.Clamp((readyInside + 4) / 5, 3, 6);
            int retained = (readyInside * 4 + 9) / 10;
            int count = Math.Min(desired, Math.Min(candidates.Length, readyInside - retained));
            int minRequired = counterSortie ? 4 : 3;
            if (count < minRequired) return false;

            foreach (IntVec3 exit in Ground.Gates
                .Select(gate => gate.Approach)
                .Where(cell => cell.Standable(Map))
                .OrderBy(cell => Api.Distance3D(cell, target.Position, Map)))
            {
                // Prefer agile Floor-0 mechs (non-Cupola, faster move speed) so elevated cupola fire stays active.
                Pawn[] raiders = candidates
                    .OrderBy(pawn => Api.FloorOf(pawn.Position, Map) > 0)
                    .ThenByDescending(pawn => pawn.GetStatValue(StatDefOf.MoveSpeed))
                    .ThenBy(pawn => Api.Distance3D(pawn.Position, exit, Map))
                    .Where(pawn => pawn.CanReach(exit, PathEndMode.OnCell, Danger.Deadly))
                    .Take(count).ToArray();
                if (raiders.Length < minRequired) continue;

                foreach (Pawn pawn in raiders)
                {
                    lord.RemovePawn(pawn);
                    if (!pawn.Downed && pawn.CurJob is Job current && current.lord == lord)
                        pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                }
                LordMaker.MakeNewLord(lord.faction, new LordJob_FortressSortie(target, exit, counterSortie), Map, raiders);
                return true;
            }
            return false;
        }

        private void LookoutScan(int now)
        {
            nextLookout = now + LookoutInterval;
            if (!AnySectorNeedsSight(now)) return;

            Thing[] targets = Targeting.CombatTargets().Where(target => ValidTarget(target) &&
                (target is not Pawn pawn || !pawn.IsPsychologicallyInvisible() &&
                    (!pawn.IsAnimal || pawn.training?.HasLearned(TrainableDefOf.Release) == true))).ToArray();
            if (targets.Length == 0) return;

            Pawn[] lookouts = lord.ownedPawns.Where(pawn =>
                !IsLogistics(pawn) && FortressDefensePosts.CanFight(pawn)).ToArray();
            Building_TurretGunCE[] directGuns = Guns.Where(gun =>
                gun.Active && gun.def.building?.IsMortar != true && gun.AttackVerb != null).ToArray();
            int checksLeft = LookoutChecksPerScan;

            for (int i = 0; i < Math.Min(targets.Length, LookoutTargetsPerScan) && checksLeft > 0; i++)
            {
                Thing target = targets[lookoutCursor % targets.Length];
                lookoutCursor = (lookoutCursor + 1) % targets.Length;
                int targetSector = Ground.Sector(target.Position);
                if (!SectorNeedsSight(targetSector, now)) continue;

                bool spotted = false;
                foreach (Pawn guard in lookouts
                    .Where(guard => Api.RangeDistance(guard.Position, target.Position, Map) <= LookoutRange)
                    .OrderBy(guard => Api.Distance3D(guard.Position, target.Position, Map))
                    .Take(Math.Min(LookoutChecksPerTarget, checksLeft)))
                {
                    checksLeft--;
                    if (!guard.CanSee(target)) continue;
                    Alarm(targetSector, target, sighted: true);
                    spotted = true;
                    break;
                }
                if (spotted || checksLeft == 0) continue;

                foreach (Building_TurretGunCE gun in directGuns
                    .Where(gun => Api.RangeDistance(gun.Position, target.Position, Map) <= gun.AttackVerb.EffectiveRange)
                    .OrderBy(gun => Api.Distance3D(gun.Position, target.Position, Map))
                    .Take(Math.Min(LookoutChecksPerTarget, checksLeft)))
                {
                    checksLeft--;
                    if (!gun.AttackVerb.CanHitTargetFrom(gun.Position, target)) continue;
                    Alarm(targetSector, target, sighted: true);
                    break;
                }
                if (!AnySectorNeedsSight(now)) break;
            }
        }

        private bool AnySectorNeedsSight(int now)
        {
            for (int sector = 0; sector < 4; sector++)
                if (SectorNeedsSight(sector, now)) return true;
            return false;
        }

        private bool SectorNeedsSight(int sector, int now) =>
            watches[sector].Until <= now || !ValidTarget(watches[sector].Target);

        private void UpdateContacts(int now)
        {
            nextQuiet = int.MaxValue;
            foreach (Pawn pawn in lord.ownedPawns)
            {
                if (IsLogistics(pawn) || !FortressDefensePosts.CanFight(pawn)) continue;
                Thing target = pawn.mindState.enemyTarget;
                if (!ValidTarget(target) || !Targeting.Allows(target, pawn)) continue;
                int latest = Math.Max(pawn.mindState.lastEngageTargetTick, pawn.mindState.lastAttackTargetTick);
                if (latest + ReviewInterval <= now) continue;
                FortressWatch watch = watches[Ground.Sector(target.Position)];
                watch.Until = Math.Max(watch.Until, latest + ReviewInterval);
                watch.Target = target;
            }
            foreach (Building_TurretGunCE gun in Guns)
            {
                Thing target = gun.CurrentTarget.Thing;
                if (!ValidTarget(target)) continue;
                FortressWatch watch = watches[Ground.Sector(target.Position)];
                watch.Until = Math.Max(watch.Until, now + ReviewInterval);
                watch.Target = target;
            }
            for (int sector = 0; sector < watches.Count; sector++)
            {
                FortressWatch watch = watches[sector];
                if (watch.DamageUntil > now) nextQuiet = Math.Min(nextQuiet, watch.DamageUntil);
                else if (watch.DamageUntil != 0)
                {
                    watch.DamageUntil = 0;
                    RequestMaintenance(now);
                    resupplyRequested = true;
                }
                if (watch.Until > now) nextQuiet = Math.Min(nextQuiet, watch.Until);
                else if (watch.Until != 0)
                {
                    watch.Until = 0;
                    watch.Target = null;
                    watch.DamageSource = null;
                    RequestMaintenance(now);
                    resupplyRequested = true;
                }
            }
        }

        private Thing AimAt(FortressWatch watch) =>
            ValidTarget(watch.Target) ? watch.Target : ValidTarget(watch.DamageSource) ? watch.DamageSource : null;

        private bool ValidTarget(Thing target) => target is { Spawned: true } && target.Map == Map &&
            lord.faction.HostileTo(target.Faction) && (target is not IAttackTarget attack || !attack.ThreatDisabled(null));

        public override void ExposeData()
        {
            Scribe_Values.Look(ref initialCombatStrength, "initialCombatStrength");
            Scribe_Values.Look(ref nextReview, "nextReview");
            Scribe_Values.Look(ref nextQuiet, "nextQuiet", int.MaxValue);
            Scribe_Values.Look(ref openSectors, "openSectors");
            Scribe_Values.Look(ref contactSectors, "contactSectors");
            Scribe_Values.Look(ref bombardmentUntil, "bombardmentUntil");
            Scribe_Values.Look(ref counterSortieAt, "counterSortieAt");
            Scribe_Values.Look(ref counterSortiesClosed, "counterSortiesClosed");
            Scribe_References.Look(ref bombardmentSource, "bombardmentSource");
            Scribe_Collections.Look(ref watches, "watches", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                watches ??= Enumerable.Range(0, 4).Select(_ => new FortressWatch()).ToList();
                nextReview = 0;
                nextLookout = 0;
                nextLogistics = 0;
            }
        }
    }

    internal sealed class LordToil_MechFortressDefense : LordToil
    {
        private LordJob_MechFortressDefense Defense => (LordJob_MechFortressDefense)lord.LordJob;

        public override void UpdateAllDuties()
        {
            int logisticsIndex = 0;
            foreach (Pawn pawn in lord.ownedPawns)
            {
                if (pawn.mindState.duty != null) continue;
                pawn.mindState.duty = LordJob_MechFortressDefense.IsLogistics(pawn)
                    ? new PawnDuty(FortressDutyDefOf.RS_MechFortressLogistics, Defense.Ground.MechLogisticsPost(logisticsIndex++), 6f)
                    : new PawnDuty(FortressDutyDefOf.RS_MechFortressGuard, Defense.Ground.CoreGroundPost, 6f);
            }
        }

        public override void Notify_PawnDamaged(Pawn victim, DamageInfo damage) => Defense.Attacked(victim, damage);
        public override void Notify_PawnAcquiredTarget(Pawn detector, Thing target) => Defense.Sighted(detector, target);
        public override void Notify_BuildingSpawnedOnMap(Building building) => Defense.BuildingChanged(building);
        public override void Notify_BuildingDespawnedOnMap(Building building) => Defense.BuildingChanged(building);
    }
}
