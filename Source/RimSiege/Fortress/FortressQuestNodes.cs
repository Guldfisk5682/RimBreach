using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using RimWorld.QuestGen;
using Verse;
using Verse.AI.Group;

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
            if (slate.Get("points", 0f) < 2000f) return false;
            if (slate.Get("fortressIgnoreEligibility", false)) return true;
            if (!SlotAvailable) return false;
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
            if (slate.TryGet("fortressForcedLayout", out FortressLayoutKind forced))
                site.ForcedLayoutKind = forced;
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

    public sealed class QuestNode_GetFortressSitePartsAndFaction : QuestNode
    {
        public SitePartDef sitePartDef;

        [NoTranslate]
        public SlateRef<string> storeAs;

        [NoTranslate]
        public SlateRef<string> storeFactionAs;

        public SlateRef<Thing> mustBeHostileToFactionOf;

        protected override bool TestRunInt(Slate slate) => TrySetVars(slate);

        protected override void RunInt()
        {
            if (!TrySetVars(QuestGen.slate))
                Log.Error("[RimSiege] Could not resolve fortress site parts and faction.");
        }

        private bool TrySetVars(Slate slate)
        {
            if (!sitePartDef.Worker.IsAvailable()) return false;

            float originalPoints = slate.Get("pointsOriginal", slate.Get("points", 0f));
            Faction forcedFaction = slate.Get<Faction>("fortressForcedFaction");
            bool hasForcedLayout = slate.TryGet("fortressForcedLayout", out FortressLayoutKind forcedLayout);
            Faction faction = forcedFaction ?? slate.Get<Faction>("enemyFaction");
            Faction asker = slate.Get<Pawn>("asker")?.Faction;
            Faction hostileTo = mustBeHostileToFactionOf.GetValue(slate)?.Faction;
            SitePartDef[] siteParts = { sitePartDef };

            if (faction == null)
            {
                if (!Find.FactionManager.AllFactionsListForReading.Where(CanOwnFortress).TryRandomElement(out faction))
                    return false;
            }
            else if (!CanOwnFortress(faction)) return false;

            slate.Set(storeAs.GetValue(slate), siteParts);
            slate.Set("sitePartCount", siteParts.Length);
            if (QuestGen.Working)
            {
                QuestGen.AddQuestDescriptionConstants(new Dictionary<string, string>
                {
                    [sitePartDef.defName + "_exists"] = "True"
                });
            }
            if (!storeFactionAs.GetValue(slate).NullOrEmpty())
                slate.Set(storeFactionAs.GetValue(slate), faction);
            return true;

            bool CanOwnFortress(Faction candidate)
            {
                if (candidate == asker || (hostileTo != null && !candidate.HostileTo(hostileTo)))
                    return false;

                bool isMech = FortressGarrison.IsMechanoidFaction(candidate);
                if (hasForcedLayout && isMech != (forcedLayout == FortressLayoutKind.MechCircularBunker))
                    return false;
                if (!isMech)
                    return SiteMakerHelper.FactionCanOwn(siteParts, candidate, disallowNonHostileFactions: true,
                        extraFactionValidator: null);

                if (forcedFaction != candidate && originalPoints < FortressGarrison.MechCompactMinPoints)
                    return false;

                // Vanilla rejects hidden factions before reaching the site worker.
                // Allow the mech hive to own this fortress while keeping the other ownership checks.
                return !candidate.IsPlayer && !candidate.defeated && !candidate.temporary &&
                    candidate.HostileTo(Faction.OfPlayer) &&
                    (sitePartDef.minFactionTechLevel == TechLevel.Undefined ||
                        candidate.def.techLevel >= sitePartDef.minFactionTechLevel) &&
                    sitePartDef.Worker.FactionCanOwn(candidate);
            }
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
            if (Site.Map is { } map && Site.Blueprint is { LayoutKind: FortressLayoutKind.MechCircularBunker } blueprint)
            {
                foreach (Lord lord in map.lordManager.lords)
                    if (lord.faction == Site.Faction && lord.LordJob is LordJob_MechFortressDefense defense)
                        defense.CancelVictoryWork();
                map.GetComponent<FortressMechResurrection>()?.CancelAll();
                map.GetComponent<FortressRebuildQueue>().CancelPendingProjects(blueprint, Site.Faction);
            }
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
