using System.Linq;
using RimWorld;
using RimWorld.QuestGen;
using Verse;

namespace RimSiege.Fortress
{
    public sealed class QuestNode_FortressEligibility : QuestNode
    {
        internal static bool SlotAvailable =>
            !Find.QuestManager.ActiveQuestsListForReading.Any(quest =>
                (quest.State == QuestState.NotYetAccepted || quest.State == QuestState.Ongoing) &&
                quest.PartsListForReading.OfType<QuestPart_SpawnWorldObject>().Any(part => part.worldObject is FortressSite)) &&
            !Find.WorldObjects.AllWorldObjects.OfType<FortressSite>().Any(site => !site.Resolved);

        protected override bool TestRunInt(Slate slate)
        {
            if (slate.Get("points", 0f) < 2000f || !SlotAvailable) return false;
            if (!slate.Get("fortressHarassment", false)) return true;

            Map map = slate.Get<Map>("map");
            return map?.IsPlayerHome == true &&
                (GenDate.DaysPassedSinceSettle > 60 || map.wealthWatcher.WealthTotal > 100000f);
        }

        protected override void RunInt() { }
    }

    public sealed class QuestNode_GenerateFortressSite : QuestNode_GenerateSite
    {
        protected override void RunInt()
        {
            base.RunInt();
            Slate slate = QuestGen.slate;
            var site = slate.Get<FortressSite>(storeAs.GetValue(slate));
            site.OriginalPoints = slate.Get<float>("pointsOriginal");
            site.DefenderPoints = slate.Get<float>("points");
            site.Harassment = slate.Get("fortressHarassment", false);
            if (site.Harassment) site.RaidTarget = slate.Get<Map>("map").Parent;
        }
    }

    public sealed class QuestNode_FortressVictory : QuestNode
    {
        protected override bool TestRunInt(Slate slate) => true;

        protected override void RunInt()
        {
            QuestGen.quest.AddPart(new QuestPart_FortressVictory
            {
                Site = QuestGen.slate.Get<FortressSite>("site"),
                Signal = QuestGen.slate.Get<string>("inSignal")
            });
        }
    }

    public sealed class QuestPart_FortressVictory : QuestPart
    {
        public FortressSite Site;
        public string Signal;

        public override void Notify_QuestSignalReceived(Signal signal)
        {
            base.Notify_QuestSignalReceived(signal);
            if (signal.tag != Signal || Site == null || Site.Resolved) return;
            Site.Resolved = true;
            FortressLoot.PlaceVictoryLoot(Site);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref Site, "site");
            Scribe_Values.Look(ref Signal, "signal");
        }
    }
}
