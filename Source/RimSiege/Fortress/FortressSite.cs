using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using RimWorld.QuestGen;
using UnityEngine;
using Verse;
using Verse.Grammar;

namespace RimSiege.Fortress
{
    public sealed class FortressSite : Site
    {
        internal const int RaidInterval = 5 * GenDate.TicksPerDay;
        internal FortressBlueprint Blueprint;
        internal float OriginalPoints;
        internal float DefenderPoints;
        internal MapParent RaidTarget;
        internal bool Harassment;
        internal bool Resolved;
        internal int NextSortieTick;
        internal int NextCounterSortieTick;

        private int forcedLayoutKind = -1;
        private int nextRaidTick;

        internal FortressLayoutKind? ForcedLayoutKind
        {
            get => forcedLayoutKind >= 0 ? (FortressLayoutKind)forcedLayoutKind : null;
            set => forcedLayoutKind = value.HasValue ? (int)value.Value : -1;
        }

        internal int TicksUntilRaid => System.Math.Max(0, nextRaidTick - Find.TickManager.TicksGame);

        private bool RaidScheduled => Harassment && !Resolved && !Destroyed && !HasMap;

        internal Map RaidHome => RaidScheduled && RaidTarget?.Map is { IsPlayerHome: true } home &&
            !Faction.deactivated && !Faction.def.raidsForbidden && Faction.HostileTo(Faction.OfPlayer) ? home : null;

        public override void PostAdd()
        {
            base.PostAdd();
            if (Harassment) StartRaidCooldown();
        }

        public override bool ShouldRemoveMapNow(out bool alsoRemoveWorldObject)
        {
            bool remove = base.ShouldRemoveMapNow(out alsoRemoveWorldObject);
            if (!Resolved) alsoRemoveWorldObject = false;
            return remove;
        }

        public override void Notify_MyMapRemoved(Map map)
        {
            base.Notify_MyMapRemoved(map);
            if (Harassment && !Resolved) StartRaidCooldown();
        }

        protected override void TickInterval(int delta)
        {
            base.TickInterval(delta);
            if (!RaidScheduled || TicksUntilRaid > 0) return;

            StartRaidCooldown();
            if (RaidHome is not Map home) return;

            IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, home);
            parms.faction = Faction;
            parms.points = DefenderPoints / 3f;
            parms.customLetterText = "{BASETEXT}\n\n" + "RS_FortressRaidSource".Translate(LabelCap).Resolve();
            IncidentWorker worker = IncidentDefOf.RaidEnemy.Worker;
            if (worker.CanFireNow(parms)) worker.TryExecute(parms);
        }

        public override string GetInspectString()
        {
            string text = base.GetInspectString();
            if (!RaidScheduled) return text;

            return text + "\n" + "RS_FortressNextRaid".Translate(TicksUntilRaid.ToStringTicksToPeriod());
        }

        private void StartRaidCooldown() => nextRaidTick = Find.TickManager.TicksGame + RaidInterval;

        internal void GenerationFailed()
        {
            Resolved = true;
            QuestUtility.SendQuestTargetSignals(questTags, "FortressGenerationFailed", this.Named("SUBJECT"));
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Deep.Look(ref Blueprint, "fortressBlueprint");
            Scribe_Values.Look(ref OriginalPoints, "fortressOriginalPoints");
            Scribe_Values.Look(ref DefenderPoints, "fortressDefenderPoints");
            Scribe_References.Look(ref RaidTarget, "fortressRaidTarget");
            Scribe_Values.Look(ref Harassment, "fortressHarassment");
            Scribe_Values.Look(ref Resolved, "fortressResolved");
            Scribe_Values.Look(ref NextSortieTick, "fortressNextSortieTick");
            Scribe_Values.Look(ref NextCounterSortieTick, "fortressNextCounterSortieTick");
            Scribe_Values.Look(ref forcedLayoutKind, "fortressForcedLayoutKind", -1);
            Scribe_Values.Look(ref nextRaidTick, "fortressNextRaidTick");
        }
    }

    public sealed class SitePartWorker_Fortress : SitePartWorker_Outpost
    {
        public override bool FactionCanOwn(Faction faction)
        {
            if (!base.FactionCanOwn(faction) || faction == null) return false;
            if (FortressGarrison.IsMechanoidFaction(faction))
            {
                PawnGroupKindDef kind = FortressGarrison.MechGroupKind(faction);
                return !faction.deactivated && !faction.def.raidsForbidden &&
                    faction.def.pawnGroupMakers?.Any(maker => maker.kindDef == kind) == true;
            }
            return faction.def.humanlikeFaction &&
                faction.def.pawnGroupMakers?.Any(maker => maker.kindDef == PawnGroupKindDefOf.Settlement) == true;
        }

        public override SitePartParams GenerateDefaultParams(float myThreatPoints, PlanetTile tile, Faction faction)
        {
            if (!FortressGarrison.IsMechanoidFaction(faction))
                return base.GenerateDefaultParams(myThreatPoints, tile, faction);
            var parms = new SitePartParams
            {
                randomValue = Rand.Int,
                threatPoints = def.wantsThreatPoints ? myThreatPoints : 0f
            };
            parms.threatPoints = Mathf.Max(parms.threatPoints,
                faction.def.MinPointsToGeneratePawnGroup(FortressGarrison.MechGroupKind(faction)));
            parms.lootMarketValue = ThreatPointsLootMarketValue.Evaluate(parms.threatPoints);
            return parms;
        }

        public override void Notify_GeneratedByQuestGen(SitePart part, Slate slate, List<Rule> outExtraDescriptionRules,
            Dictionary<string, string> outExtraDescriptionConstants)
        {
            if (!FortressGarrison.IsMechanoidFaction(part.site.Faction))
            {
                base.Notify_GeneratedByQuestGen(part, slate, outExtraDescriptionRules, outExtraDescriptionConstants);
                return;
            }
            outExtraDescriptionRules.AddRange(GrammarUtility.RulesForDef("", part.def));
            outExtraDescriptionConstants.Add("sitePart", part.def.defName);
            bool isLarge = slate.Get("pointsOriginal", part.parms.threatPoints) >= FortressGarrison.MechLargeMinPoints;
            int enemiesCount = FortressGarrison.EstimateEnemiesCount(part.site, part.parms, isLarge);
            outExtraDescriptionRules.Add(new Rule_String("enemiesCount", enemiesCount.ToString()));
            outExtraDescriptionRules.Add(new Rule_String("enemiesLabel", GetEnemiesLabel(part.site, enemiesCount)));
        }

        public override string GetPostProcessedThreatLabel(Site site, SitePart sitePart)
        {
            if (!FortressGarrison.IsMechanoidFaction(site.Faction))
                return base.GetPostProcessedThreatLabel(site, sitePart);
            if (site.Faction.IsPlayer) return null;
            var fortress = site as FortressSite;
            bool isLarge = fortress?.Blueprint?.IsLarge ??
                ((fortress is { OriginalPoints: > 0f } ? fortress.OriginalPoints : sitePart.parms.threatPoints)
                    >= FortressGarrison.MechLargeMinPoints);
            int enemiesCount = FortressGarrison.EstimateEnemiesCount(site, sitePart.parms, isLarge);
            return def.label + ": " + "KnownSiteThreatEnemyCountAppend".Translate(enemiesCount, "Enemies".Translate());
        }
    }
}
