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
        private const int ReviewInterval = 1800;
        private const int ResponseReviewDelay = GenTicks.TicksPerRealSecond;
        private const string Rout = "RimSiege.Fortress.Rout";
        private const float SortiePoints = 1000f;

        private FortressLine line;
        private int initialStrength;
        private int nextReview;
        private int nextQuiet = int.MaxValue;
        private int openSectors;
        private int breachedSectors;
        private List<FortressWatch> watches = Enumerable.Range(0, 4).Select(_ => new FortressWatch()).ToList();
        private Pawn sortieTarget;

        private FortressDefenseGround ground;
        private FortressDefensePosts posts;
        private int changedCurtain;
        private int contactSectors;
        private bool maintenanceReviewRequested;
        private bool resupplyRequested;
        private int nextRelief = int.MaxValue;
        private readonly HashSet<Pawn> relief = new();
        private FortressSite Site => (FortressSite)Map.Parent;
        internal FortressDefenseGround Ground => ground ??= new FortressDefenseGround(Map, Site.Blueprint);
        private FortressDefensePosts Posts => posts ??= new FortressDefensePosts(Map, Site.Blueprint, Ground);
        internal FortressLine Line => line;
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
            Review(GenTicks.TicksGame);
        }

        public override void LordJobTick()
        {
            if (lord.CurLordToil is not LordToil_FortressDefense || initialStrength == 0) return;
            int now = GenTicks.TicksGame;
            if (now >= nextRelief)
            {
                bool postLost = false;
                foreach (Pawn pawn in relief) postLost |= Posts.Relieve(lord, pawn, this);
                relief.Clear();
                nextRelief = int.MaxValue;
                if (postLost) RequestResponseReview(now);
            }
            bool reviewDue = now >= nextReview;
            if (maintenanceReviewRequested || contactSectors != 0)
            {
                maintenanceReviewRequested = false;
                Posts.SuspendExposedMaintenance(lord, this);
            }
            if (contactSectors != 0)
            {
                int sectors = contactSectors;
                contactSectors = 0;
                if (!reviewDue)
                {
                    int infantry = InfantryPerSector(now);
                    for (int sector = 0; sector < 4; sector++)
                        if ((sectors & (1 << sector)) != 0) Posts.ArmContact(lord, this, sector, AimAt(watches[sector]), infantry);
                }
                resupplyRequested = true;
            }
            if (reviewDue) Review(now);
            else if (now >= nextQuiet) UpdateContacts(now);
            if (resupplyRequested)
            {
                resupplyRequested = false;
                Posts.Replenish(lord, line, watches, now);
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
        }

        internal void RequestResupply() => resupplyRequested = true;

        private void RequestResponseReview(int now) => nextReview = Math.Min(nextReview, now + ResponseReviewDelay);

        internal bool SectorUnderWatch(int sector) => watches[sector].Until > GenTicks.TicksGame;

        internal bool CanMaintain(IntVec3 cell)
        {
            if (line == FortressLine.Keep || Ground.Outside(cell)) return false;
            int sector = Ground.Sector(cell);
            if ((openSectors & (1 << sector)) != 0) return false;
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
            nextRelief = Math.Min(nextRelief, GenTicks.TicksGame + 1);
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
            int responseSector = attacker.Spawned && attacker.Map == Map && Ground.Outside(attacker.Position)
                ? Ground.Sector(attacker.Position) : damagedSector;
            Alarm(responseSector, attacker, sighted: false);
        }

        internal void Sighted(Pawn guard, Thing target)
        {
            if (!guard.HostileTo(target)) return;
            int sector = Ground.Sector(target.Position);
            Alarm(sector, target);
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
            if (lord.CurLordToil is not LordToil_FortressDefense ||
                !int.TryParse(gunner.mindState.duty.tag, out int sector)) return null;
            FortressWatch watch = watches[sector];
            if (watch.Until <= GenTicks.TicksGame) return null;
            Thing assigned = gunner.mindState.duty.focusThird.Thing;
            Thing target = ValidTarget(assigned) ? assigned : AimAt(watch);
            return target != null && ValidateAttackTarget(gunner, target) ? target : null;
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
                if (requestReview && line == FortressLine.Curtain && target is Pawn && Ground.Inside(target.Position))
                    RequestResponseReview(now);
            }
            nextQuiet = Math.Min(nextQuiet, watch.Until);
            if (newContact) contactSectors |= 1 << sector;
        }

        internal void BuildingChanged(Building building)
        {
            if (building is Building_TurretGunCE && building.Faction == lord.faction) Posts.InvalidateGuns();
            IntVec3 groundCell = Api.GroundCellOf(building.Position, Map);
            if (Api.FloorOf(building.Position, Map) == 0 && Ground.IsCurtain(groundCell) && building.def.Fillage == FillCategory.Full)
                changedCurtain |= 1 << Ground.Sector(building.Position);
        }

        public override bool ValidateAttackTarget(Pawn pawn, Thing target)
        {
            if (lord.CurLordToil is not LordToil_FortressDefense) return true;
            if (pawn.mindState.duty?.def == FortressDutyDefOf.RS_FortressSortie)
                return target == sortieTarget || Ground.Outside(target.Position);
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
            ((openSectors | breachedSectors) & (1 << sector)) != 0 && Ground.TowerFaces(turret, sector);

        private void Review(int now)
        {
            nextReview = now + ReviewInterval;
            Pawn[] attackers = !lord.faction.HostileTo(Faction.OfPlayer) ? Array.Empty<Pawn>() :
                Map.mapPawns.PawnsInFaction(Faction.OfPlayer).Where(pawn => pawn.Spawned && !pawn.Downed && !pawn.Dead &&
                    (pawn.IsFreeColonist || pawn.IsColonyMech ||
                        pawn.IsAnimal && pawn.training?.HasLearned(TrainableDefOf.Release) == true)).ToArray();

            int intruderSectors = 0;
            bool inside = false;
            foreach (Pawn pawn in attackers)
            {
                bool inCourtyard = Ground.Inside(pawn.Position);
                if (!inCourtyard && !Ground.OnCurtain(pawn.Position)) continue;
                int sector = Ground.Sector(pawn.Position);
                intruderSectors |= 1 << sector;
                inside |= inCourtyard;
                Alarm(sector, pawn, requestReview: false);
            }
            UpdateContacts(now);
            for (int sector = 0; sector < 4; sector++)
            {
                int mask = 1 << sector;
                if ((changedCurtain & mask) == 0) continue;
                if (Ground.PassableBreach(sector)) breachedSectors |= mask;
                else breachedSectors &= ~mask;
            }
            changedCurtain = 0;
            openSectors = intruderSectors;

            int ready = lord.ownedPawns.Count(FortressDefensePosts.CanFight);
            if (ready > 0 && ready * 10 <= initialStrength)
            {
                lord.ReceiveMemo(Rout);
                return;
            }

            if (intruderSectors != 0 && ready * 5 <= initialStrength) line = FortressLine.Keep;
            else if (inside || intruderSectors != 0 && ready * 2 <= initialStrength) line = FortressLine.Courtyard;
            else line = FortressLine.Curtain;

            if (line != FortressLine.Curtain || !ValidTarget(sortieTarget) || !Ground.Outside(sortieTarget.Position))
                sortieTarget = null;
            contactSectors = 0;
            Posts.Assign(lord, this, watches, sortieTarget, now);
            resupplyRequested = true;
            if (line == FortressLine.Curtain && sortieTarget == null && now >= Site.NextSortieTick)
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
                if (!ValidTarget(target)) continue;
                int latest = manned ? now : Math.Max(pawn.mindState.lastEngageTargetTick, pawn.mindState.lastAttackTargetTick);
                if (latest + ReviewInterval <= now) continue;
                FortressWatch watch = watches[Ground.Sector(target.Position)];
                watch.Until = Math.Max(watch.Until, latest + ReviewInterval);
                watch.Target = target;
            }
            for (int sector = 0; sector < watches.Count; sector++)
            {
                FortressWatch watch = watches[sector];
                if (watch.Until > now) nextQuiet = Math.Min(nextQuiet, watch.Until);
                else if (watch.Until != 0)
                {
                    watch.Until = 0;
                    watch.Target = null;
                    watch.DamageSource = null;
                    openSectors &= ~(1 << sector);
                    Posts.StandDown(lord, sector, line);
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
            if (!Posts.Dispatch(lord, target, inside / 10)) return;
            sortieTarget = target;
            Site.NextSortieTick = now + GenDate.TicksPerDay;
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
            if (Scribe.mode == LoadSaveMode.PostLoadInit) nextReview = 0;
            Scribe_Collections.Look(ref watches, "watches", LookMode.Deep);
            Scribe_References.Look(ref sortieTarget, "sortieTarget");
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
