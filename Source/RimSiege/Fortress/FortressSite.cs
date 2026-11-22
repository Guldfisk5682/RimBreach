using System.Collections.Generic;
using System.Linq;
using RimSiege.Integration;
using RimWorld;
using RimWorld.Planet;
using RimWorld.QuestGen;
using UnityEngine;
using Verse;
using Verse.AI.Group;
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
        internal Faction AlliedFaction;
        internal bool Harassment;
        internal bool Resolved;
        internal int NextSortieTick;
        internal int NextCounterSortieTick;

        private int forcedLayoutKind = -1;
        private int nextRaidTick;
        private int firstPlayerEntryTick = -1;
        private int alliedArrivalTick = -1;
        private IntVec3 playerEntry = IntVec3.Invalid;
        private bool alliedSupportConsumed;
        private HashSet<int> alliedMembers;
        private List<Building> assaultBuildings;
        private int nextBuildingRefreshTick;

        internal FortressLayoutKind? ForcedLayoutKind
        {
            get => forcedLayoutKind >= 0 ? (FortressLayoutKind)forcedLayoutKind : null;
            set => forcedLayoutKind = value.HasValue ? (int)value.Value : -1;
        }

        internal int TicksUntilRaid => System.Math.Max(0, nextRaidTick - Find.TickManager.TicksGame);

        private bool RaidScheduled => Harassment && !Resolved && !Destroyed && !HasMap;

        internal static bool CanFieldSupport(Faction faction) =>
            faction is { IsPlayer: false, defeated: false, deactivated: false } &&
            !faction.HostileTo(Faction.OfPlayer) && faction.def.humanlikeFaction && !faction.def.raidsForbidden &&
            faction.def.pawnGroupMakers?.Any(maker => maker.kindDef == PawnGroupKindDefOf.Combat) == true;

        internal bool CanCoordinateWith(Faction faction) =>
            faction == AlliedFaction && CanFieldSupport(faction) && faction.HostileTo(Faction);

        internal bool IsSupportPawn(Pawn pawn) =>
            pawn.Faction == AlliedFaction && alliedMembers?.Contains(pawn.thingIDNumber) == true &&
            !pawn.IsPrisoner && !pawn.IsSlave;

        internal static FortressSite SupportSite(Pawn pawn) =>
            pawn.MapHeld?.Parent is FortressSite site && site.IsSupportPawn(pawn) ? site : null;

        internal static FortressSite SupportSite(Lord lord) =>
            lord?.LordJob is LordJob_FortressAlliedAssault or LordJob_FortressAlliedArtillery
                ? lord.Map.Parent as FortressSite : null;

        internal List<Building> AssaultBuildings(ListerBuildings buildings)
        {
            int now = GenTicks.TicksGame;
            if (assaultBuildings == null || now >= nextBuildingRefreshTick)
            {
                assaultBuildings = buildings.allBuildingsNonColonist.Where(building => building.Faction == Faction).ToList();
                nextBuildingRefreshTick = now + 60;
            }
            return assaultBuildings;
        }

        internal void RegisterSupport(IEnumerable<Pawn> pawns)
        {
            alliedMembers = pawns.Select(pawn => pawn.thingIDNumber).ToHashSet();
            alliedSupportConsumed = true;
        }

        internal IntVec3 AssaultGoal => Blueprint?.Outer.CenterCell ?? IntVec3.Invalid;
        internal IntVec3 PlayerEntry => playerEntry;

        internal int DefenderMortarsReadyTick => firstPlayerEntryTick >= 0
            ? firstPlayerEntryTick + GenDate.TicksPerHour : int.MaxValue;

        internal int SupportTicksLeft => alliedArrivalTick >= 0 && !alliedSupportConsumed && !Resolved
            ? System.Math.Max(0, alliedArrivalTick - GenTicks.TicksGame) : -1;

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
            alliedMembers = null;
            assaultBuildings = null;
            playerEntry = IntVec3.Invalid;
            if (Harassment && !Resolved) StartRaidCooldown();
        }

        public override void FinalizeLoading()
        {
            base.FinalizeLoading();
            if (firstPlayerEntryTick >= 0 || Resolved) return;
            if (alliedArrivalTick >= 0)
                firstPlayerEntryTick = alliedArrivalTick - GenDate.TicksPerHour;
            else if (Map is { } map && map.mapPawns.SpawnedPawnsInFaction(RimWorld.Faction.OfPlayer).Any(IsPlayerForce))
                firstPlayerEntryTick = map.generationTick;
        }

        protected override void TickInterval(int delta)
        {
            base.TickInterval(delta);
            if (assaultBuildings != null && GenTicks.TicksGame >= nextBuildingRefreshTick)
                assaultBuildings = null;
            if (AlliedFaction != null && !alliedSupportConsumed && !Resolved && Map is { } map)
                TickAlliedSupport(map);
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
            int ticks = SupportTicksLeft;
            if (HasMap && ticks >= 0)
                text += "\n" + "RS_FortressAlliedCountdown".Translate(ticks.ToStringTicksToPeriod());
            if (!RaidScheduled) return text;

            return text + "\n" + "RS_FortressNextRaid".Translate(TicksUntilRaid.ToStringTicksToPeriod());
        }

        private void StartRaidCooldown() => nextRaidTick = Find.TickManager.TicksGame + RaidInterval;

        private void TickAlliedSupport(Map map)
        {
            if (playerEntry.IsValid && SupportTicksLeft > 0) return;
            Pawn entrant = map.mapPawns.SpawnedPawnsInFaction(RimWorld.Faction.OfPlayer)
                .FirstOrDefault(IsPlayerForce);
            if (entrant == null) return;

            NotifyPlayerEntry(entrant, map);
            if (SupportTicksLeft == 0)
                alliedSupportConsumed = FortressAlliedReinforcement.TryDispatch(this, map);
        }

        internal void NotifyPlayerEntry(Pawn pawn, Map map)
        {
            if (Resolved || !IsPlayerForce(pawn)) return;
            if (firstPlayerEntryTick < 0) firstPlayerEntryTick = GenTicks.TicksGame;
            if (AlliedFaction == null || alliedSupportConsumed) return;
            if (!playerEntry.IsValid) playerEntry = IsoFloor.GroundCellOf(pawn.Position, map);
            if (alliedArrivalTick > GenTicks.TicksGame) return;

            if (!CanCoordinateWith(AlliedFaction))
            {
                alliedSupportConsumed = true;
                return;
            }
            if (alliedArrivalTick >= 0) return;
            alliedArrivalTick = GenTicks.TicksGame + GenDate.TicksPerHour;
            Find.LetterStack.ReceiveLetter("RS_FortressAlliedOnWay".Translate(),
                "RS_FortressAlliedOnWayText".Translate(AlliedFaction.Name, LabelCap,
                    GenDate.TicksPerHour.ToStringTicksToPeriod()), LetterDefOf.NeutralEvent, this);
        }

        private static bool IsPlayerForce(Pawn pawn) => pawn.Faction == RimWorld.Faction.OfPlayer && !pawn.Dead &&
            (pawn.IsFreeColonist || pawn.IsColonyMech || pawn.IsPlayerControlled);

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
            Scribe_References.Look(ref AlliedFaction, "fortressAlliedFaction");
            Scribe_Values.Look(ref Harassment, "fortressHarassment");
            Scribe_Values.Look(ref Resolved, "fortressResolved");
            Scribe_Values.Look(ref NextSortieTick, "fortressNextSortieTick");
            Scribe_Values.Look(ref NextCounterSortieTick, "fortressNextCounterSortieTick");
            Scribe_Values.Look(ref forcedLayoutKind, "fortressForcedLayoutKind", -1);
            Scribe_Values.Look(ref nextRaidTick, "fortressNextRaidTick");
            Scribe_Values.Look(ref firstPlayerEntryTick, "fortressFirstPlayerEntryTick", -1);
            Scribe_Values.Look(ref alliedArrivalTick, "fortressAlliedArrivalTick", -1);
            Scribe_Values.Look(ref playerEntry, "fortressPlayerEntry", IntVec3.Invalid);
            Scribe_Values.Look(ref alliedSupportConsumed, "fortressAlliedSupportConsumed");
            Scribe_Collections.Look(ref alliedMembers, "fortressAlliedMembers", LookMode.Value);
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
            bool isLarge = slate.Get("fortressStructurePoints", part.parms.threatPoints) >= FortressGarrison.MechLargeMinPoints;
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
