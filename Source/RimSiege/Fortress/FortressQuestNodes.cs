using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using RimWorld.QuestGen;
using UnityEngine;
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

    public sealed class QuestNode_FortressPoints : QuestNode
    {
        protected override bool TestRunInt(Slate slate)
        {
            SetPoints(slate);
            return true;
        }

        protected override void RunInt() => SetPoints(QuestGen.slate);

        private static void SetPoints(Slate slate)
        {
            float original = slate.Get<float>("pointsOriginal");
            int stars = slate.Get("challengeRating", 1);
            bool harassment = slate.Get("fortressHarassment", false);
            float points = harassment
                ? Mathf.Max(2000f, original * (stars switch { 1 => 0.35f, 2 => 0.5f, _ => 0.65f }))
                : original * (stars switch { 1 => 0.8f, 2 => 1f, _ => 1.2f });
            slate.Set("points", points);
            slate.Set("fortressStructurePoints", harassment ? points : original);
        }
    }

    public sealed class QuestNode_GenerateFortressSite : QuestNode_GenerateSite
    {
        protected override void RunInt()
        {
            base.RunInt();
            Slate slate = QuestGen.slate;
            var site = slate.Get<FortressSite>(storeAs.GetValue(slate));
            site.OriginalPoints = slate.Get<float>("fortressStructurePoints");
            site.DefenderPoints = slate.Get<float>("points");
            site.Harassment = slate.Get("fortressHarassment", false);
            if (slate.TryGet("fortressForcedLayout", out FortressLayoutKind forced))
                site.ForcedLayoutKind = forced;
            if (site.Harassment) site.RaidTarget = slate.Get<Map>("map").Parent;
            else site.AlliedFaction = slate.Get<Pawn>("asker")?.Faction;
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

        protected override bool TestRunInt(Slate slate) => TrySetVars(slate, generating: false);

        protected override void RunInt()
        {
            if (!TrySetVars(QuestGen.slate, generating: true))
                Log.Error("[RimSiege] Could not resolve fortress site parts and faction.");
        }

        private bool TrySetVars(Slate slate, bool generating)
        {
            if (!sitePartDef.Worker.IsAvailable()) return false;

            float structurePoints = slate.Get<float>("fortressStructurePoints");
            Faction forcedFaction = slate.Get<Faction>("fortressForcedFaction");
            bool hasForcedLayout = slate.TryGet("fortressForcedLayout", out FortressLayoutKind forcedLayout);
            Faction requested = forcedFaction ?? slate.Get<Faction>("enemyFaction");
            SitePartDef[] siteParts = { sitePartDef };
            var factions = Find.FactionManager.AllFactionsListForReading;
            var defenders = (requested == null ? factions : Enumerable.Repeat(requested, 1))
                .Where(CanOwnFortress).ToList();
            if (!slate.Get("fortressHarassment", false))
            {
                bool CanCommission(Faction publisher, Faction defender) => publisher.HostileTo(defender) &&
                    (publisher.def.techLevel >= TechLevel.Industrial || !FortressGarrison.IsMechanoidFaction(defender));

                bool CanSend(Pawn leader) => leader is { Dead: false, IsPrisoner: false } &&
                    leader.Faction is { Hidden: false, temporary: false } publisher && publisher.leader == leader &&
                    FortressSite.CanFieldSupport(publisher) &&
                    (!leader.IsWorldPawn() || Find.WorldPawns.GetSituation(leader) != WorldPawnSituation.ReservedByQuest) &&
                    defenders.Any(defender => CanCommission(publisher, defender));

                Pawn asker = slate.Get<Pawn>("asker");
                if (!CanSend(asker) && !factions.Select(publisher => publisher.leader).Where(CanSend)
                        .TryRandomElement(out asker)) return false;

                slate.Set("asker", asker);
                defenders.RemoveAll(defender => !CanCommission(asker.Faction, defender));
                if (generating)
                {
                    var involved = new QuestPart_InvolvedFactions();
                    involved.factions.Add(asker.Faction);
                    QuestGen.quest.AddPart(involved);
                }
            }
            if (!defenders.TryRandomElement(out Faction faction)) return false;

            slate.Set(storeAs.GetValue(slate), siteParts);
            slate.Set("sitePartCount", siteParts.Length);
            if (generating)
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
                bool isMech = FortressGarrison.IsMechanoidFaction(candidate);
                if (hasForcedLayout && isMech != (forcedLayout == FortressLayoutKind.MechCircularBunker))
                    return false;
                if (!isMech)
                    return SiteMakerHelper.FactionCanOwn(siteParts, candidate, disallowNonHostileFactions: true,
                        extraFactionValidator: null);

                if (forcedFaction != candidate && structurePoints < FortressGarrison.MechCompactMinPoints)
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
            FortressAlliedWithdrawal.NotifyVictory(Site);
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
