using System.Collections.Generic;
using System.Linq;
using ProjectZ;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Fortress
{
    public sealed class LordJob_FortressSortie : LordJob
    {
        private const int CheckInterval = 60;
        private const int IdleLimit = LordJob_FortressDefense.ReviewInterval;
        private const string RoutMemo = "RimSiege.Fortress.SortieRout";

        private Thing primaryTarget;
        private IntVec3 rallyExit = IntVec3.Invalid;
        private bool counterSortie;
        private int nextCheck;
        private int returnAt;
        private float bestApproach = float.MaxValue;

        private FortressDefenseGround ground;
        private FortressTargeting targeting;

        public LordJob_FortressSortie() { }

        internal LordJob_FortressSortie(Thing primaryTarget, IntVec3 rallyExit, bool counterSortie)
        {
            this.primaryTarget = primaryTarget;
            this.rallyExit = rallyExit;
            this.counterSortie = counterSortie;
            returnAt = GenTicks.TicksGame + IdleLimit;
        }

        private FortressSite Site => (FortressSite)Map.Parent;
        internal FortressDefenseGround Ground => ground ??= FindDefense()?.Ground ?? new FortressDefenseGround(Map, Site.Blueprint);
        internal FortressTargeting Targeting => targeting ??= new FortressTargeting(Map, lord.faction);
        internal IntVec3 RallyExit => rallyExit;
        internal bool CounterSortie => counterSortie;
        internal LocalTargetInfo DutyFocus => ValidTarget(primaryTarget)
            ? new LocalTargetInfo(primaryTarget)
            : rallyExit.IsValid ? new LocalTargetInfo(rallyExit) : LocalTargetInfo.Invalid;

        public override bool AddFleeToil => false;
        public override bool CanAutoAddPawns => false;
        public override bool ShouldRemovePawn(Pawn pawn, PawnLostCondition reason) => reason != PawnLostCondition.Incapped;

        public override StateGraph CreateGraph()
        {
            var attack = new LordToil_FortressSortie();
            var flee = new LordToil_PanicFlee { useAvoidGrid = true };
            var graph = new StateGraph { StartingToil = attack };
            graph.AddToil(flee);
            var transition = new Transition(attack, flee);
            transition.AddTrigger(new Trigger_Memo(RoutMemo));
            transition.AddPostAction(new TransitionAction_EndAllJobs());
            graph.AddTransition(transition);
            return graph;
        }

        public override void LordJobTick()
        {
            if (lord.CurLordToil is not LordToil_FortressSortie) return;
            int now = GenTicks.TicksGame;
            if (now < nextCheck) return;
            nextCheck = now + CheckInterval;

            Lord defenseLord = FindDefenseLord(Map, lord.faction);
            if (defenseLord?.LordJob is not IFortressDefenseLord defense || !defense.IsDefenseActive)
            {
                if (lord.faction != Faction.OfMechanoids && lord.faction?.def != FactionDefOf.Mechanoid)
                    TriggerRout();
                return;
            }

            if (returnAt == 0) returnAt = now + IdleLimit;
            if (!defense.CanContinueSortie ||
                !Targeting.HasPlayerCombatThreats() || !lord.ownedPawns.Any(FortressDefensePosts.CanFight))
            {
                ReturnTo(defense, now);
                return;
            }

            if (!ValidTarget(primaryTarget))
            {
                IntVec3 origin = rallyExit.IsValid ? rallyExit : Site.Blueprint.Outer.CenterCell;
                Thing replacement = Targeting.CombatTargets()
                    .Where(target => Ground.Outside(target.Position) &&
                        FortressTargeting.IsActivePlayerCombatThreat(target, lord.faction, Map))
                    .MinBy(target => Api.Distance3D(origin, target.Position, Map));
                if (replacement != primaryTarget)
                {
                    primaryTarget = replacement;
                    bestApproach = float.MaxValue;
                    lord.CurLordToil.UpdateAllDuties();
                }
            }

            if (primaryTarget != null) RecordAdvance(now);
            if (now >= returnAt) ReturnTo(defense, now);
        }

        private void RecordAdvance(int now)
        {
            IntVec3 target = primaryTarget.Position;
            float distanceFromExit = rallyExit.IsValid ? Api.Distance3D(rallyExit, target, Map) : 0f;
            float distance = float.MaxValue;
            foreach (Pawn pawn in lord.ownedPawns)
            {
                if (!FortressDefensePosts.CanFight(pawn)) continue;
                float candidate = rallyExit.IsValid && !Ground.Outside(pawn.Position)
                    ? Api.Distance3D(pawn.Position, rallyExit, Map) + distanceFromExit
                    : Api.Distance3D(pawn.Position, target, Map);
                distance = Mathf.Min(distance, candidate);
            }
            if (distance + 2f >= bestApproach) return;
            if (bestApproach < float.MaxValue) returnAt = now + IdleLimit;
            bestApproach = distance;
        }

        private void ReturnTo(IFortressDefenseLord defense, int now)
        {
            List<Pawn> returning = lord.ownedPawns.ToList();
            lord.RemovePawns(returning);
            defense.MergeSortie(returning, now);
        }

        internal void RecordContact(Thing target)
        {
            if (FortressTargeting.IsCombatTarget(target) && ValidTarget(target))
                returnAt = GenTicks.TicksGame + IdleLimit;
        }

        internal void TriggerRout()
        {
            if (lord.CurLordToil is LordToil_FortressSortie)
                lord.ReceiveMemo(RoutMemo);
        }

        public override void Notify_PawnDowned(Pawn pawn)
        {
            base.Notify_PawnDowned(pawn);
            FindDefense()?.RequestRoutCheck(GenTicks.TicksGame);
        }

        public override void Notify_PawnLost(Pawn pawn, PawnLostCondition condition) =>
            FindDefense()?.RequestRoutCheck(GenTicks.TicksGame);

        public override bool ValidateAttackTarget(Pawn pawn, Thing target)
        {
            if (lord.CurLordToil is not LordToil_FortressSortie) return true;
            return Targeting.Allows(target, pawn);
        }

        internal bool ValidTarget(Thing target) => target is { Spawned: true } && target.Map == Map &&
            lord.faction.HostileTo(target.Faction) && (target is not IAttackTarget attack || !attack.ThreatDisabled(null));

        private IFortressDefenseLord FindDefense() =>
            FindDefenseLord(Map, lord.faction)?.LordJob as IFortressDefenseLord;

        private static Lord FindDefenseLord(Map map, Faction faction) =>
            map?.lordManager.lords.FirstOrDefault(other =>
                other.faction == faction && other.LordJob is IFortressDefenseLord);

        public override void ExposeData()
        {
            Scribe_References.Look(ref primaryTarget, "primaryTarget");
            Scribe_Values.Look(ref rallyExit, "rallyExit", IntVec3.Invalid);
            Scribe_Values.Look(ref counterSortie, "counterSortie");
            Scribe_Values.Look(ref returnAt, "returnAt");
            Scribe_Values.Look(ref bestApproach, "bestApproach", float.MaxValue);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
                nextCheck = 0;
        }
    }

    internal sealed class LordToil_FortressSortie : LordToil
    {
        private LordJob_FortressSortie Sortie => (LordJob_FortressSortie)lord.LordJob;

        public override bool ShouldFail => lord.ownedPawns.Count == 0;

        public override void Notify_PawnDamaged(Pawn victim, DamageInfo damage) =>
            Sortie.RecordContact(damage.Instigator);

        public override void UpdateAllDuties()
        {
            LocalTargetInfo focus = Sortie.DutyFocus;
            LocalTargetInfo exit = Sortie.RallyExit.IsValid
                ? new LocalTargetInfo(Sortie.RallyExit) : LocalTargetInfo.Invalid;
            string tag = Sortie.CounterSortie ? FortressDefensePosts.CounterSortieTag : null;
            foreach (Pawn pawn in lord.ownedPawns)
            {
                PawnDuty existing = pawn.mindState.duty;
                if (existing != null && existing.def == FortressDutyDefOf.RS_FortressSortie &&
                    existing.focus == focus && existing.focusSecond == exit && existing.tag == tag)
                    continue;
                pawn.mindState.duty = new PawnDuty(FortressDutyDefOf.RS_FortressSortie, focus, exit) { tag = tag };
            }
        }
    }
}
