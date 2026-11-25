using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimSiege.Raids.Encirclement
{
    public sealed partial class QuestPart_Encirclement : QuestPartActivable
    {
        internal MapParent MapParent;
        internal Faction Faction;
        internal float BasePoints;
        internal ThingDef ForcedDevice;
        internal bool Fast, ForceThreeDevices, ForceExposed, ForceNoCamp;
        internal string mapRemovedSignal;
        internal GameCondition_Encirclement Condition;
        internal int Phase;
        internal int PhaseStart, PhaseDeadline;

        internal Map Map => MapParent?.Map;
        internal int Now => Find.TickManager.TicksGame;
        internal int Timer(int ticks) => Fast ? ticks / 10 : ticks;
        internal bool Mechanoid => Faction == Faction.OfMechanoids;

        protected override void Enable(SignalArgs receivedArgs)
        {
            base.Enable(receivedArgs);
            PhaseStart = Now;
            PhaseDeadline = Now + Timer(GenDate.TicksPerDay);
            Condition = (GameCondition_Encirclement)GameConditionMaker.MakeCondition(EncirclementDefs.Condition);
            Condition.Permanent = true;
            Condition.Faction = Faction;
            Condition.quest = quest;
            Map.GameConditionManager.RegisterCondition(Condition);
            Pawn scout = Map.mapPawns.FreeColonistsSpawned.RandomElementWithFallback();
            string veil = QuestNode_Root_Encirclement.UsesSunVeil(Faction)
                ? "RS_EncirclementSkyVeil" : "RS_EncirclementSkySmoke";
            Find.LetterStack.ReceiveLetter("RS_EncirclementWarningTitle".Translate(),
                "RS_EncirclementWarning".Translate(scout == null ? "RS_EncirclementScout".Translate() : scout.LabelShortCap,
                    Faction.NameColored, MapParent.LabelCap, veil.Translate()), LetterDefOf.NegativeEvent,
                new LookTargets(Map.Center, Map), Faction, quest);
        }

        protected override void ProcessQuestSignal(Signal signal)
        {
            if (signal.tag == mapRemovedSignal) End(false);
        }

        internal void BeginBlockade()
        {
            if (Condition.Blockaded) return;
            Condition.BeginBlockade();
            Find.LetterStack.ReceiveLetter("RS_EncirclementBlockadeTitle".Translate(),
                Condition.ShuttleReason, LetterDefOf.ThreatBig, new LookTargets(Map.Center, Map), Faction, quest);
        }

        public override void QuestPartTick()
        {
            Map map = Map;
            if (map == null || !map.IsPlayerHome || Condition == null ||
                Faction?.HostileTo(RimWorld.Faction.OfPlayer) != true || GameCondition_Encirclement.ActiveOn(map) != Condition)
            {
                End(false);
                return;
            }
            if (Phase > 0 || Now >= PhaseStart + Timer(12 * GenDate.TicksPerHour)) BeginBlockade();
            TickCampaign();
        }

        partial void TickCampaign();
        partial void CleanCampaign();

        internal void End(bool success)
        {
            if (!quest.Historical)
                quest.End(success ? QuestEndOutcome.Success : QuestEndOutcome.Fail, sendLetter: false);
        }

        public override void Cleanup()
        {
            base.Cleanup();
            Condition?.End();
            Condition = null;
            CleanCampaign();
            MapParent = null;
            Faction = null;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref MapParent, "mapParent");
            Scribe_References.Look(ref Faction, "faction");
            Scribe_References.Look(ref Condition, "condition");
            Scribe_Defs.Look(ref ForcedDevice, "forcedDevice");
            Scribe_Values.Look(ref BasePoints, "points");
            Scribe_Values.Look(ref Fast, "fast");
            Scribe_Values.Look(ref ForceThreeDevices, "threeDevices");
            Scribe_Values.Look(ref ForceExposed, "exposed");
            Scribe_Values.Look(ref ForceNoCamp, "noCamp");
            Scribe_Values.Look(ref mapRemovedSignal, "mapRemovedSignal");
            Scribe_Values.Look(ref Phase, "phase");
            Scribe_Values.Look(ref PhaseStart, "phaseStart");
            Scribe_Values.Look(ref PhaseDeadline, "phaseDeadline");
            ExposeCampaign();
        }

        partial void ExposeCampaign();
    }
}
