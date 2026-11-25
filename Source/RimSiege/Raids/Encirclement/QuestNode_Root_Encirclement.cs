using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.QuestGen;
using Verse;

namespace RimSiege.Raids.Encirclement
{
    public sealed class QuestNode_Root_Encirclement : QuestNode
    {
        internal static bool UsesSunVeil(Faction faction) =>
            faction == Faction.OfMechanoids || faction.def.techLevel >= TechLevel.Spacer;

        internal static IEnumerable<Faction> Factions() => Find.FactionManager.AllFactionsListForReading.Where(f =>
            !f.defeated && !f.deactivated && !f.def.raidsForbidden && f.HostileTo(Faction.OfPlayer) && (f == Faction.OfMechanoids ||
                f.def.humanlikeFaction && !f.Hidden && f.def.techLevel >= TechLevel.Industrial) &&
            f.def.pawnGroupMakers?.Any(g => g.kindDef == PawnGroupKindDefOf.Combat) == true);

        internal static bool Eligible(Map map, float points, bool debug)
        {
            if (map == null || !map.IsPlayerHome || map.Tile.LayerDef.isSpace || map.generatorDef.isUnderground ||
                GameCondition_Encirclement.ActiveOn(map) != null ||
                ModsConfig.AnomalyActive && map.GameConditionManager.ConditionIsActive(GameConditionDefOf.UnnaturalDarkness))
                return false;
            if (!debug && (RimSiegeMod.Settings?.EnableEncirclement != true ||
                points < RimSiegeMod.Settings.EncirclementMinPoints || GenDate.DaysPassedSinceSettle < 60 ||
                map.mapPawns.FreeColonistsSpawnedCount == 0))
                return false;
            return EncirclementSiting.HasEntry(map);
        }

        protected override bool TestRunInt(Slate slate) => Eligible(slate.Get<Map>("map") ?? QuestGen_Get.GetMap(),
            slate.Get("points", 0f), slate.Get("encirclementDebug", false)) && Factions().Any();

        protected override void RunInt()
        {
            Slate slate = QuestGen.slate;
            Map map = slate.Get<Map>("map") ?? QuestGen_Get.GetMap();
            Faction faction = slate.Get<Faction>("encirclementFaction") ?? Factions().RandomElementWithFallback();
            if (map == null || faction == null) return;
            slate.Set("map", map);
            QuestGen.quest.AddPart(new QuestPart_Encirclement
            {
                inSignalEnable = QuestGen.quest.InitiateSignal,
                mapRemovedSignal = QuestGenUtility.HardcodedSignalWithQuestID("map.MapRemoved"),
                MapParent = map.Parent,
                Faction = faction,
                BasePoints = slate.Get("points", 8000f),
                Fast = slate.Get("encirclementFast", false),
                ForcedDevice = slate.Get<ThingDef>("encirclementDevice"),
                ForceThreeDevices = slate.Get("encirclementThreeDevices", false),
                ForceExposed = slate.Get("encirclementExposed", false),
                ForceNoCamp = slate.Get("encirclementNoCamp", false)
            });
        }

        internal static QuestPart_Encirclement Start(Map map, Faction faction, float points, bool debug = false,
            bool fast = false, ThingDef device = null, bool threeDevices = false, bool exposed = false, bool noCamp = false)
        {
            if (faction == null || !Eligible(map, points, debug)) return null;
            var slate = new Slate();
            slate.Set("map", map);
            slate.Set("points", points);
            slate.Set("encirclementFaction", faction);
            slate.Set("encirclementDebug", debug);
            slate.Set("encirclementFast", fast);
            slate.Set("encirclementDevice", device);
            slate.Set("encirclementThreeDevices", threeDevices);
            slate.Set("encirclementExposed", exposed);
            slate.Set("encirclementNoCamp", noCamp);
            return QuestUtility.GenerateQuestAndMakeAvailable(EncirclementDefs.RS_Encirclement, slate)
                .PartsListForReading.OfType<QuestPart_Encirclement>().Single();
        }
    }

    public sealed class IncidentWorker_Encirclement : IncidentWorker_GiveQuest
    {
        protected override bool CanFireNowSub(IncidentParms parms) =>
            QuestNode_Root_Encirclement.Eligible(parms.target as Map, parms.points, false) &&
            QuestNode_Root_Encirclement.Factions().Any();

        protected override bool TryExecuteWorker(IncidentParms parms) =>
            QuestNode_Root_Encirclement.Start(parms.target as Map,
                QuestNode_Root_Encirclement.Factions().RandomElementWithFallback(), parms.points) != null;
    }
}
