using System.Collections.Generic;
using System.Linq;
using RimSiege.Raids.Artillery;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace RimSiege.Fortress
{
    public sealed class LordJob_FortressAlliedAssault : LordJob_AssaultColony
    {
        public LordJob_FortressAlliedAssault() { }

        internal LordJob_FortressAlliedAssault(Faction faction, RaidStrategyDef strategy)
            : base(faction, canKidnap: false, canSteal: false,
                sappers: strategy.Worker is RaidStrategyWorker_ImmediateAttackSappers,
                useAvoidGridSmart: strategy.Worker is RaidStrategyWorker_ImmediateAttackSmart or
                    RaidStrategyWorker_ImmediateAttackSappers || strategy.defName == "ImmediateAttackBreachingSmart",
                breachers: strategy.Worker is RaidStrategyWorker_ImmediateAttackBreaching)
        {
        }

        public override bool GuiltyOnDowned => false;

        public override StateGraph CreateGraph() => FortressAlliedWithdrawal.Configure(base.CreateGraph());
    }

    public sealed class LordJob_FortressAlliedArtillery : LordJob_Artillery
    {
        public LordJob_FortressAlliedArtillery() { }

        internal LordJob_FortressAlliedArtillery(ArtilleryRaid raid) : base(raid) { }

        public override bool GuiltyOnDowned => false;
        internal override bool UsesNativeAssault => false;

        internal override LordJob CreateAssaultJob(ArtilleryRaid raid, Map map, List<Pawn> pawns) =>
            new LordJob_FortressAlliedAssault(raid.Faction, raid.Strategy);

        public override StateGraph CreateGraph()
        {
            StateGraph graph = base.CreateGraph();
            foreach (Transition transition in graph.transitions)
                if (transition.target is LordToil_ArtilleryTransfer)
                    transition.preActions.RemoveAll(action => action is TransitionAction_Message);
            return FortressAlliedWithdrawal.Configure(graph);
        }

        public override void LordJobTick()
        {
            if (lord.CurLordToil is LordToil_ExitMap or LordToil_PanicFlee)
            {
                if (Raid is { Phase: not ArtilleryPhase.Abandoned } raid)
                    ArtilleryCamp.Abandon(raid, Map, retainWithAssault: false);
                return;
            }
            base.LordJobTick();
        }
    }

    internal static class FortressAlliedWithdrawal
    {
        private const string BattleEnded = "RS_FortressAlliedBattleEnded";

        internal static void NotifyVictory(FortressSite site)
        {
            if (!site.HasMap) return;
            foreach (Lord lord in site.Map.lordManager.lords.ToArray())
                if (lord.LordJob is LordJob_FortressAlliedAssault or LordJob_FortressAlliedArtillery)
                    lord.ReceiveMemo(BattleEnded);
        }

        internal static StateGraph Configure(StateGraph graph)
        {
            LordToil_ExitMap plainExit = graph.lordToils.OfType<LordToil_ExitMap>().First();
            var recover = new LordToil_FortressRecoverCamp();
            var active = graph.lordToils.Where(toil => toil is not LordToil_ExitMap).ToArray();
            graph.AddToil(recover);

            // Colony diplomacy and damage triggers concern the colony being raided.
            graph.transitions.RemoveAll(transition => transition.triggers.Any(trigger =>
                trigger is Trigger_BecameNonHostileToPlayer or Trigger_FractionColonyDamageTaken));
            foreach (Transition transition in graph.transitions)
            {
                if (transition.target is not LordToil_ExitMap) continue;
                transition.target = recover;
                transition.preActions.RemoveAll(action => action is TransitionAction_Message);
            }

            var end = new Transition(active[0], recover);
            end.AddSources(active.Skip(1));
            end.AddTrigger(new Trigger_Memo(BattleEnded));
            end.AddTrigger(new Trigger_FortressSupportEnds());
            graph.AddTransition(end, highPriority: true);

            var leave = new Transition(active[0], plainExit);
            leave.AddSources(active.Skip(1));
            leave.AddSource(recover);
            leave.AddTrigger(new Trigger_FortressSupportEnds(hostileToPlayer: true));
            graph.AddTransition(leave, highPriority: true);
            return graph;
        }

        private sealed class Trigger_FortressSupportEnds(bool hostileToPlayer = false) : Trigger
        {
            public override bool ActivateOn(Lord lord, TriggerSignal signal)
            {
                if (signal.type != TriggerSignalType.FactionRelationsChanged &&
                    (signal.type != TriggerSignalType.Tick || (GenTicks.TicksGame + lord.loadID) % 60 != 0))
                    return false;
                if (hostileToPlayer) return lord.faction.HostileTo(Faction.OfPlayer);
                return lord.Map.Parent is not FortressSite { Resolved: false } site ||
                    !site.CanCoordinateWith(lord.faction);
            }
        }
    }
}
