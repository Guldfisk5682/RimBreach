using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using ProjectZ;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Fortress
{
    public sealed class LordJob_FortressDefense : LordJob
    {
        internal const int ReviewInterval = 1800;
        private const int ResponseReviewDelay = GenTicks.TicksPerRealSecond;
        private const int IntrusionCheckInterval = 60;
        private const int InteriorManningInterval = 180;
        private const int MaintenanceRetryDelay = 300;
        private const int MaintenanceRetries = 2;
        private const string Rout = "RimSiege.Fortress.Rout";
        private const float SortiePoints = 1000f;

        private FortressLine line;
        private int initialStrength;
        private int nextReview;
        private int nextQuiet = int.MaxValue;
        private int openSectors;
        private int breachedSectors;
        private List<FortressWatch> watches = Enumerable.Range(0, 4).Select(_ => new FortressWatch()).ToList();
        private Thing sortieTarget;
        private int sortieUntil;
        private Thing bombardmentSource;
        private int bombardmentUntil;
        private int counterSortieAt;

        private FortressDefenseGround ground;
        private FortressDefensePosts posts;
        private FortressTargeting targeting;
        private FortressSlabRepairs slabRepairs;
        private bool slabPlansRequested;
        private int changedCurtain;
        private int contactSectors;
        private bool maintenanceReviewRequested;
        private bool resupplyRequested;
        private int nextMedical = int.MaxValue;
        private int nextMaintenance = int.MaxValue;
        private int maintenanceRetriesLeft;
        private int nextRelief = int.MaxValue;
        private int nextIntrusionCheck;
        private int nextInteriorManning;
        private readonly HashSet<Pawn> relief = new();
        private readonly List<Pawn> intruders = new();
        private FortressSite Site => (FortressSite)Map.Parent;
        internal FortressDefenseGround Ground => ground ??= new FortressDefenseGround(Map, Site.Blueprint);
        internal FortressTargeting Targeting => targeting ??= new FortressTargeting(Map, lord.faction);
        private FortressDefensePosts Posts => posts ??= new FortressDefensePosts(Map, Site.Blueprint, Ground);
        internal FortressLine Line => line;
        internal bool IntrusionActive => openSectors != 0;
        internal IReadOnlyList<Pawn> Intruders => intruders;
        public override bool AddFleeToil => false;
        public override bool CanAutoAddPawns => false;
        public override bool ShouldRemovePawn(Pawn pawn, PawnLostCondition reason) => reason != PawnLostCondition.Incapped;

        public override StateGraph CreateGraph()
        {
            var hold = new LordToil_FortressDefense();
            var flee = new LordToil_PanicFlee { useAvoidGrid = true };
            var graph = new StateGraph { StartingToil = hold };
            graph.AddToil(flee);
            var transition = new Transition(hold, flee);
            transition.AddTrigger(new Trigger_Memo(Rout));
            transition.AddPreAction(new TransitionAction_Message(
                "MessageFightersFleeing".Translate(lord.faction.def.pawnsPlural.CapitalizeFirst(), lord.faction.Name)));
            transition.AddPostAction(new TransitionAction_EndAllJobs());
            graph.AddTransition(transition);
            return graph;
        }

        internal void Start()
        {
            initialStrength = lord.ownedPawns.Count;
            int now = GenTicks.TicksGame;
            CheckIntrusion(now);
            Review(now);
        }

        public override void LordJobTick()
        {
            if (lord.CurLordToil is not LordToil_FortressDefense || initialStrength == 0) return;
            int now = GenTicks.TicksGame;
            if (now >= nextIntrusionCheck) CheckIntrusion(now);
            if (now >= nextRelief)
            {
                bool postLost = false;
                foreach (Pawn pawn in relief) postLost |= Posts.Relieve(lord, pawn, this);
                relief.Clear();
                nextRelief = int.MaxValue;
                if (postLost) RequestResponseReview(now);
            }
            bool reviewDue = now >= nextReview || sortieUntil != 0 && now >= sortieUntil;
            if (maintenanceReviewRequested || contactSectors != 0)
            {
                maintenanceReviewRequested = false;
                Posts.SuspendExposedMaintenance(lord, this);
            }
            if (contactSectors != 0)
            {
                int sectors = contactSectors;
                contactSectors = 0;
                if (!reviewDue && !IntrusionActive)
                {
                    int infantry = InfantryPerSector(now);
                    for (int sector = 0; sector < 4; sector++)
                        if ((sectors & (1 << sector)) != 0) Posts.ArmContact(lord, this, sector, AimAt(watches[sector]), infantry);
                }
                resupplyRequested = true;
                RequestSupport(now + ResponseReviewDelay);
            }
            if (reviewDue) Review(now);
            else if (now >= nextQuiet) UpdateContacts(now);
            if (lord.CurLordToil is not LordToil_FortressDefense) return;
            if (IntrusionActive && now >= nextInteriorManning)
            {
                nextInteriorManning = now + InteriorManningInterval;
                Posts.AssignInterior(lord, this);
            }
            if (counterSortieAt != 0 && now >= counterSortieAt) CheckCounterSortie(now);
            if (resupplyRequested)
            {
                resupplyRequested = false;
                Posts.Replenish(lord, this, watches, now);
            }
            if (now >= nextMedical)
            {
                nextMedical = int.MaxValue;
                if (Posts.FillMedical(lord, line)) RequestMaintenance(now);
            }
            if (now >= nextMaintenance)
            {
                nextMaintenance = int.MaxValue;
                if (slabPlansRequested)
                {
                    slabPlansRequested = false;
                    (slabRepairs ??= new FortressSlabRepairs(Map, Site.Blueprint)).PlacePending(Map, lord.faction);
                }
                if (Posts.FillMaintenance(lord, this) && maintenanceRetriesLeft > 0)
                {
                    maintenanceRetriesLeft--;
                    nextMaintenance = now + MaintenanceRetryDelay;
                }
            }
        }

        public override void Notify_PawnDowned(Pawn pawn) => QueueRelief(pawn);
        public override void Notify_PawnUndowned(Pawn pawn) => QueueRelief(pawn);

        public override void Notify_PawnLost(Pawn pawn, PawnLostCondition condition) => QueueRelief(pawn);

        public override void Notify_PawnJobDone(Pawn pawn, JobCondition condition)
        {
            if (condition == JobCondition.Succeeded && pawn.CurJobDef == CE_JobDefOf.ReloadTurret &&
                pawn.mindState.duty?.tag == FortressDefensePosts.ReloadTag)
                resupplyRequested = true;
            if (initialStrength == 0 || lord.CurLordToil is not LordToil_FortressDefense) return;
            PawnDuty duty = pawn.mindState.duty;
            int next = GenTicks.TicksGame + ResponseReviewDelay;
            if (duty?.focusSecond.Pawn is Pawn patient && FortressSupportJobs.Patient(pawn.CurJob) == patient)
                nextMedical = Math.Min(nextMedical, next);
            if (duty?.tag == FortressMaintenance.DutyTag && pawn.CurJob?.workGiverDef?.workType == WorkTypeDefOf.Construction)
                RequestMaintenance(next);
        }

        private void RequestSupport(int tick)
        {
            nextMedical = Math.Min(nextMedical, tick);
            RequestMaintenance(tick);
        }

        private void RequestMaintenance(int tick)
        {
            nextMaintenance = Math.Min(nextMaintenance, tick);
            maintenanceRetriesLeft = MaintenanceRetries;
        }

        internal void RequestResupply() => resupplyRequested = true;

        internal void RecordRetaliation(Thing target)
        {
            int now = GenTicks.TicksGame;
            if (bombardmentSource == null || target == null || now >= bombardmentUntil) return;
            if (target == bombardmentSource ||
                bombardmentSource is Building_Turret turret && turret.TryGetComp<CompMannable>()?.ManningPawn == target ||
                target is Building_Turret targetGun && targetGun.TryGetComp<CompMannable>()?.ManningPawn == bombardmentSource)
                counterSortieAt = now + ReviewInterval;
        }

        private void RequestResponseReview(int now) => nextReview = Math.Min(nextReview, now + ResponseReviewDelay);

        internal bool SectorUnderWatch(int sector) => watches[sector].Until > GenTicks.TicksGame;

        internal bool CanMaintain(IntVec3 cell)
        {
            if (IntrusionActive || line == FortressLine.Keep || Ground.Outside(cell)) return false;
            int sector = Ground.Sector(cell);
            bool exposed = !Ground.Inside(cell) || Api.FloorOf(cell, Map) == Site.Blueprint.Top;
            return !exposed || !SectorUnderWatch(sector) && watches[sector].DamageUntil <= GenTicks.TicksGame;
        }

        internal void RememberPost(Pawn pawn)
        {
            if (initialStrength != 0 && lord.CurLordToil is LordToil_FortressDefense)
                Posts.RememberPost(pawn);
        }

        internal void QueueRelief(Pawn pawn)
        {
            if (initialStrength == 0 || lord.CurLordToil is not LordToil_FortressDefense) return;
            Posts.RememberPost(pawn);
            relief.Add(pawn);
            int next = GenTicks.TicksGame + 1;
            nextRelief = Math.Min(nextRelief, next);
            if (IntrusionActive)
            {
                nextInteriorManning = Math.Min(nextInteriorManning, next);
                RequestResponseReview(GenTicks.TicksGame);
            }
            RequestSupport(next);
        }

        internal void Attacked(Thing victim, DamageInfo damage)
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
            watches[damagedSector].DamageUntil = now + ReviewInterval;
            nextQuiet = Math.Min(nextQuiet, watches[damagedSector].DamageUntil);
            int responseSector = attacker.Spawned && attacker.Map == Map && Ground.Outside(attacker.Position)
                ? Ground.Sector(attacker.Position) : damagedSector;
            Alarm(responseSector, attacker, sighted: false);
            if (line != FortressLine.Curtain || !ValidTarget(attacker) || !Ground.Outside(attacker.Position)) return;
            if (counterSortieAt == 0 || now >= bombardmentUntil)
                counterSortieAt = now + ReviewInterval;
            bombardmentSource = attacker;
            bombardmentUntil = now + ReviewInterval;
        }

        internal void Sighted(Pawn guard, Thing target)
        {
            if (!guard.HostileTo(target)) return;
            RecordSortieContact(guard, target);
            int sector = Ground.Sector(target.Position);
            Alarm(sector, target);
        }

        internal void RecordSortieContact(Pawn pawn, Thing target)
        {
            if (sortieUntil == 0 || pawn.mindState.duty?.def != FortressDutyDefOf.RS_FortressSortie ||
                !Ground.Outside(pawn.Position) || !FortressTargeting.IsCombatTarget(target) ||
                !ValidateAttackTarget(pawn, target)) return;
            sortieUntil = GenTicks.TicksGame + ReviewInterval;
        }

        internal Thing SortieAdvanceTarget(Pawn pawn)
        {
            Thing current = pawn.mindState.enemyTarget;
            if (FortressTargeting.IsCombatTarget(current) && ValidateAttackTarget(pawn, current)) return current;
            return Targeting.CombatTargets().Where(target => ValidateAttackTarget(pawn, target) &&
                    Api.RangeDistance(pawn.Position, target.Position, Map) <= 75f)
                .MinBy(target => Api.Distance3D(pawn.Position, target.Position, Map)) ??
                (sortieTarget != null && ValidateAttackTarget(pawn, sortieTarget) ? sortieTarget : null);
        }

        internal Thing AimAt(FortressWatch watch) =>
            ValidTarget(watch.Target) ? watch.Target : ValidTarget(watch.DamageSource) ? watch.DamageSource : null;

        internal int InfantryPerSector(int now)
        {
            int active = Math.Max(1, watches.Count(watch => watch.Until > now));
            int divisor = 4 * active;
            return Math.Max(2, (lord.ownedPawns.Count(FortressDefensePosts.CanFight) + divisor - 1) / divisor);
        }

        internal Thing GunContact(Pawn gunner)
        {
            if (IntrusionActive && FortressDefensePosts.Intercepting(gunner))
            {
                Thing assignedTarget = gunner.mindState.duty.focusThird.Thing;
                return assignedTarget != null && ValidateAttackTarget(gunner, assignedTarget) ? assignedTarget : null;
            }
            if (lord.CurLordToil is not LordToil_FortressDefense ||
                !int.TryParse(gunner.mindState.duty.tag, out int sector)) return null;
            FortressWatch watch = watches[sector];
            if (watch.Until <= GenTicks.TicksGame) return null;
            Thing assigned = gunner.mindState.duty.focusThird.Thing;
            Thing target = ValidTarget(assigned) && Targeting.Allows(assigned, gunner) ? assigned : AimAt(watch);
            return target != null && ValidateAttackTarget(gunner, target) ? target : null;
        }

        internal Thing PreferredGunTarget(Building_TurretGunCE gun, Pawn gunner) =>
            Posts.PreferredGunTarget(this, gun, gunner);

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

        internal void BuildingChanged(Building building)
        {
            if (building is Building_TurretGunCE && building.Faction == lord.faction) Posts.InvalidateGuns();
            if (building.def.holdsRoof && slabRepairs?.HasPending == true)
                RequestSlabPlans();
            IntVec3 groundCell = Api.GroundCellOf(building.Position, Map);
            if (Api.FloorOf(building.Position, Map) == 0 && Ground.IsCurtain(groundCell) && building.def.Fillage == FillCategory.Full)
                changedCurtain |= 1 << Ground.Sector(building.Position);
        }

        internal void NotifySlabChanged(IntVec3 cell, bool built)
        {
            if (slabRepairs == null || slabRepairs.FloorChanged(cell, built))
                RequestSlabPlans();
        }

        private void RequestSlabPlans()
        {
            slabPlansRequested = true;
            RequestMaintenance(GenTicks.TicksGame + ResponseReviewDelay);
        }

        public override bool ValidateAttackTarget(Pawn pawn, Thing target)
        {
            if (lord.CurLordToil is not LordToil_FortressDefense) return true;
            if (!Targeting.Allows(target, pawn)) return false;
            if (IntrusionActive && FortressDefensePosts.Intercepting(pawn))
                return FortressTargeting.IsCombatTarget(target);
            if (pawn.mindState.duty?.def == FortressDutyDefOf.RS_FortressSortie)
                return Ground.Outside(target.Position);
            PawnDuty duty = pawn.mindState.duty;
            if (duty?.def == FortressDutyDefOf.RS_FortressReserve && !Ground.Inside(target.Position)) return false;
            if (FortressDefensePosts.Patrol(pawn) && !Ground.OnCurtain(pawn.Position) ||
                line == FortressLine.Courtyard && Ground.OnCurtain(pawn.Position) ||
                line == FortressLine.Keep && (!Ground.InKeep(pawn.Position) || Api.FloorOf(pawn.Position, Map) == 0)) return false;
            bool manning = duty?.focusSecond.Thing is Building_TurretGunCE gun && gun.MannableComp?.ManningPawn == pawn;
            if (!manning && duty?.focus.IsValid == true && pawn.CurrentEffectiveVerb?.verbProps.IsMeleeAttack == true &&
                (!Api.SameFloor(duty.focus.Cell, target.Position, Map) ||
                    !target.Position.InHorDistOf(duty.focus.Cell, duty.radius + 2f))) return false;
            return !Ground.TowerMiddle(pawn.Position) || MiddleOpen(pawn.Position, Ground.Sector(target.Position));
        }

        internal bool MiddleOpen(IntVec3 turret, int sector) =>
            Ground.InKeep(turret) ? line == FortressLine.Keep :
            (line != FortressLine.Curtain || ((openSectors | breachedSectors) & (1 << sector)) != 0) &&
            Ground.TowerFaces(turret, sector);

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
            nextInteriorManning = now;
            if (IntrusionActive)
                foreach (Pawn intruder in intruders) Alarm(Ground.Sector(intruder.Position), intruder, requestReview: false);
        }

        private void Review(int now)
        {
            nextReview = now + ReviewInterval;
            Pawn[] attackers = !lord.faction.HostileTo(Faction.OfPlayer) ? Array.Empty<Pawn>() :
                Map.mapPawns.PawnsInFaction(Faction.OfPlayer).Where(pawn => pawn.Spawned && !pawn.Downed && !pawn.Dead &&
                    (pawn.IsFreeColonist || pawn.IsColonyMech ||
                        pawn.IsAnimal && pawn.training?.HasLearned(TrainableDefOf.Release) == true)).ToArray();

            UpdateContacts(now);
            for (int sector = 0; sector < 4; sector++)
            {
                int mask = 1 << sector;
                if ((changedCurtain & mask) == 0) continue;
                if (Ground.PassableBreach(sector)) breachedSectors |= mask;
                else breachedSectors &= ~mask;
            }
            changedCurtain = 0;

            int ready = lord.ownedPawns.Count(FortressDefensePosts.CanFight);
            if (ready > 0 && ready * 10 <= initialStrength)
            {
                lord.ReceiveMemo(Rout);
                return;
            }

            FortressLine previousLine = line;
            if (IntrusionActive && ready * 5 <= initialStrength) line = FortressLine.Keep;
            else if (IntrusionActive) line = FortressLine.Courtyard;
            else line = FortressLine.Curtain;

            if (line != FortressLine.Curtain || now >= sortieUntil)
            {
                sortieTarget = null;
                sortieUntil = 0;
            }
            if (line != FortressLine.Curtain)
            {
                bombardmentSource = null;
                counterSortieAt = 0;
            }
            contactSectors = 0;
            if (IntrusionActive) nextInteriorManning = now;
            else Posts.Assign(lord, this, watches, sortieUntil != 0, now, sameLine: previousLine == line);
            resupplyRequested = true;
            slabPlansRequested = true;
            RequestSupport(now);
            if (line == FortressLine.Curtain && sortieUntil == 0 && now >= Site.NextSortieTick)
                TrySortie(attackers, now);
        }

        private void UpdateContacts(int now)
        {
            nextQuiet = int.MaxValue;
            foreach (Pawn pawn in lord.ownedPawns)
            {
                if (!FortressDefensePosts.CanFight(pawn)) continue;
                var gun = pawn.mindState.duty?.focusSecond.Thing as Building_TurretGunCE;
                bool manned = gun?.MannableComp?.ManningPawn == pawn && gun.Spawned && ValidTarget(gun.CurrentTarget.Thing);
                Thing target = manned ? gun.CurrentTarget.Thing : pawn.mindState.enemyTarget;
                if (!ValidTarget(target) || !Targeting.Allows(target, pawn)) continue;
                int latest = manned ? now : Math.Max(pawn.mindState.lastEngageTargetTick, pawn.mindState.lastAttackTargetTick);
                if (latest + ReviewInterval <= now) continue;
                FortressWatch watch = watches[Ground.Sector(target.Position)];
                watch.Until = Math.Max(watch.Until, latest + ReviewInterval);
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
                }
                if (watch.Until > now) nextQuiet = Math.Min(nextQuiet, watch.Until);
                else if (watch.Until != 0)
                {
                    watch.Until = 0;
                    watch.Target = null;
                    watch.DamageSource = null;
                    if (!IntrusionActive) Posts.StandDown(lord, sector, line);
                    RequestSupport(now);
                }
            }
        }

        private void TrySortie(Pawn[] attackers, int now)
        {
            int inside = lord.ownedPawns.Count(pawn => pawn.Spawned && !Ground.Outside(pawn.Position));
            if (inside <= 30 || !attackers.Any(pawn => Ground.Outside(pawn.Position))) return;
            Pawn target = watches.Select(watch => watch.Target).OfType<Pawn>()
                .FirstOrDefault(pawn => ValidTarget(pawn) && Ground.Outside(pawn.Position));
            if (target == null || StorytellerUtility.DefaultThreatPointsNow(Map) >= SortiePoints) return;
            if (!Posts.Dispatch(lord, this, target, inside / 10, counterSortie: false)) return;
            sortieTarget = target;
            sortieUntil = now + ReviewInterval;
            Site.NextSortieTick = now + GenDate.TicksPerDay;
        }

        private void CheckCounterSortie(int now)
        {
            Thing target = bombardmentSource;
            if (line != FortressLine.Curtain || now >= bombardmentUntil ||
                !ValidTarget(target) || !Ground.Outside(target.Position))
            {
                bombardmentSource = null;
                counterSortieAt = 0;
                return;
            }

            counterSortieAt = now + ReviewInterval;
            if (sortieUntil != 0) return;
            int inside = lord.ownedPawns.Count(pawn => FortressDefensePosts.CanFight(pawn) && !Ground.Outside(pawn.Position));
            if (inside < 12) return;
            int count = Math.Clamp(inside / 4, 4, 12);
            if (Posts.Dispatch(lord, this, target, count, counterSortie: true))
            {
                sortieTarget = target;
                sortieUntil = now + ReviewInterval;
            }
        }

        internal bool ValidTarget(Thing target) => target is { Spawned: true } && target.Map == Map &&
            lord.faction.HostileTo(target.Faction) && (target is not IAttackTarget attack || !attack.ThreatDisabled(null));

        public override ThinkResult Notify_DutyResult(ThinkResult result, Pawn pawn, JobIssueParams issueParams)
        {
            if (line != FortressLine.Keep || result.Job is not Job job ||
                (job.def != JobDefOf.LayDown && job.def != JobDefOf.Ingest) ||
                (job.targetA.HasThing && !job.targetA.Thing.Spawned)) return result;
            IntVec3 cell = job.targetA.Cell;
            if (pawn.mindState.duty?.def == FortressDutyDefOf.RS_FortressReserve)
                return Ground.Inside(cell) ? result : ThinkResult.NoJob;
            return Ground.InKeep(cell) && Api.FloorOf(cell, Map) > 0 ? result : ThinkResult.NoJob;
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref line, "defenseLine");
            Scribe_Values.Look(ref initialStrength, "initialStrength");
            Scribe_Values.Look(ref nextReview, "nextReview");
            Scribe_Values.Look(ref nextQuiet, "nextQuiet", int.MaxValue);
            Scribe_Values.Look(ref openSectors, "openSectors");
            Scribe_Values.Look(ref breachedSectors, "breachedSectors");
            Scribe_Values.Look(ref changedCurtain, "changedCurtain");
            Scribe_Values.Look(ref contactSectors, "contactSectors");
            Scribe_Values.Look(ref bombardmentUntil, "bombardmentUntil");
            Scribe_Values.Look(ref counterSortieAt, "counterSortieAt");
            Scribe_References.Look(ref bombardmentSource, "bombardmentSource");
            Scribe_Collections.Look(ref watches, "watches", LookMode.Deep);
            Scribe_References.Look(ref sortieTarget, "sortieTarget");
            Scribe_Values.Look(ref sortieUntil, "sortieUntil");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                nextReview = 0;
                if (sortieUntil == 0 && sortieTarget != null) sortieUntil = GenTicks.TicksGame + ReviewInterval;
            }
        }
    }

    internal sealed class FortressWatch : IExposable
    {
        internal int Until;
        internal int DamageUntil;
        internal Thing Target;
        internal Thing DamageSource;

        public void ExposeData()
        {
            Scribe_Values.Look(ref Until, "until");
            Scribe_Values.Look(ref DamageUntil, "damageUntil");
            Scribe_References.Look(ref Target, "target");
            Scribe_References.Look(ref DamageSource, "damageSource");
        }
    }

    internal sealed class LordToil_FortressDefense : LordToil
    {
        private LordJob_FortressDefense Defense => (LordJob_FortressDefense)lord.LordJob;

        public override void UpdateAllDuties()
        {
            foreach (Pawn pawn in lord.ownedPawns)
                if (pawn.mindState.duty == null)
                    pawn.mindState.duty = new PawnDuty(FortressDutyDefOf.RS_FortressReserve, Defense.Ground.RestPost);
        }

        public override void Notify_PawnDamaged(Pawn victim, DamageInfo damage) => Defense.Attacked(victim, damage);
        public override void Notify_PawnAcquiredTarget(Pawn detector, Thing target) => Defense.Sighted(detector, target);
        public override void Notify_BuildingSpawnedOnMap(Building building) => Defense.BuildingChanged(building);
        public override void Notify_BuildingDespawnedOnMap(Building building) => Defense.BuildingChanged(building);
    }
}
