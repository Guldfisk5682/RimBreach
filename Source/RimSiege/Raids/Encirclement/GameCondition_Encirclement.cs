using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimSiege.Raids.Encirclement
{
    public sealed class GameCondition_Encirclement : GameCondition
    {
        internal Faction Faction;
        private GameCondition veil;

        internal bool Blockaded => veil != null && gameConditionManager.ActiveConditions.Contains(veil);
        internal TaggedString ShuttleReason =>
            (QuestNode_Root_Encirclement.UsesSunVeil(Faction) ? "RS_EncirclementShuttleVeil" :
                "RS_EncirclementShuttleSmoke").Translate(Faction.NameColored);
        public override string Description
        {
            get
            {
                if (Faction == null) return base.Description;
                if (!Blockaded) return "RS_EncirclementWarningDescription".Translate(Faction.NameColored);
                string key = QuestNode_Root_Encirclement.UsesSunVeil(Faction)
                    ? "RS_EncirclementBlackVeilDescription" : "RS_EncirclementSmokeVeilDescription";
                return key.Translate(Faction.NameColored, SingleMap.Parent.LabelCap);
            }
        }

        public static GameCondition_Encirclement ActiveOn(Map map)
        {
            if (map == null) return null;
            foreach (GameCondition condition in map.GameConditionManager.ActiveConditions)
                if (condition is GameCondition_Encirclement campaign) return campaign;
            return null;
        }

        internal static QuestPart_Encirclement CampaignOn(Map map) =>
            ActiveOn(map)?.quest?.PartsListForReading.OfType<QuestPart_Encirclement>().FirstOrDefault();

        internal static GameCondition_Encirclement Blocking(Map map) =>
            ActiveOn(map) is { Blockaded: true } condition ? condition : null;

        internal void BeginBlockade()
        {
            if (Blockaded) return;
            GameConditionDef def = QuestNode_Root_Encirclement.UsesSunVeil(Faction)
                ? EncirclementDefs.RS_EncirclementSunVeil : EncirclementDefs.RS_EncirclementSmokeVeil;
            veil = GameConditionMaker.MakeCondition(def);
            veil.Permanent = true;
            veil.quest = quest;
            gameConditionManager.RegisterCondition(veil);
        }

        internal bool IsLaunchDenial(string reason) => veil != null && reason == veil.LabelCap;

        public override float SkyTargetLerpFactor(Map map) => Blockaded
            ? GameConditionUtility.LerpInOutValue(veil, 300) : 0f;

        public override SkyTarget? SkyTarget(Map map) => Blockaded
            ? new SkyTarget(0f, GameCondition_NoSunlight.EclipseSkyColors, 1f, 0f) : null;

        public override void End()
        {
            if (!gameConditionManager.ActiveConditions.Contains(this)) return;
            if (veil != null && gameConditionManager.ActiveConditions.Contains(veil)) veil.End();
            veil = null;
            base.End();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref Faction, "faction");
            Scribe_References.Look(ref veil, "veil");
        }
    }

    // Quest signals and forced incidents can execute a raid without CanFireNow.
    [HarmonyPatch(typeof(IncidentWorker), nameof(IncidentWorker.TryExecute))]
    internal static class EncirclementRaidBlockade
    {
        private static bool Prefix(IncidentWorker __instance, IncidentParms parms, ref bool __result)
        {
            if ((__instance is IncidentWorker_Raid || __instance.def.defName == "UnnaturalDarkness") &&
                parms.target is Map map && GameCondition_Encirclement.ActiveOn(map) != null)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(CompShuttle), nameof(CompShuttle.CanLaunch), MethodType.Getter)]
    internal static class EncirclementShuttleLaunch
    {
        private static void Postfix(CompShuttle __instance, ref AcceptanceReport __result)
        {
            if (GameCondition_Encirclement.Blocking(__instance.parent.Map) is { } condition &&
                (__result.Accepted || condition.IsLaunchDenial(__result.Reason)))
                __result = new AcceptanceReport(condition.ShuttleReason);
        }
    }

    [HarmonyPatch(typeof(RoyalTitlePermitWorker_CallShuttle), nameof(RoyalTitlePermitWorker_CallShuttle.ShuttleCanLandHere))]
    internal static class EncirclementShuttleLanding
    {
        private static bool Prefix(Map map, ref AcceptanceReport __result)
        {
            if (GameCondition_Encirclement.Blocking(map) is not { } condition) return true;
            __result = new AcceptanceReport(condition.ShuttleReason);
            return false;
        }
    }

    [HarmonyPatch(typeof(TransportersArrivalAction_LandInSpecificCell),
        nameof(TransportersArrivalAction_LandInSpecificCell.StillValid))]
    internal static class EncirclementShuttleArrival
    {
        private static void Postfix(MapParent ___mapParent, bool ___landInShuttle, ref FloatMenuAcceptanceReport __result)
        {
            if (___landInShuttle && GameCondition_Encirclement.Blocking(___mapParent?.Map) is { } condition)
                __result = FloatMenuAcceptanceReport.WithFailReasonAndMessage(condition.ShuttleReason, condition.ShuttleReason);
        }
    }

    // A queued quest shuttle keeps its native ship job and can retry once the airspace opens.
    [HarmonyPatch(typeof(ShipJob_Arrive), nameof(ShipJob_Arrive.TryStart))]
    internal static class EncirclementScriptedShuttleArrival
    {
        private static bool Prefix(ShipJob_Arrive __instance, ref bool __result)
        {
            if (__instance.transportShip?.ShuttleComp == null) return true;
            MapParent parent = __instance.mapOfPawn?.MapHeld?.Parent ?? __instance.mapParent;
            if (parent is PocketMapParent { sourceMap: { } source }) parent = source.Parent;
            Map map = parent?.Map ?? Find.AnyPlayerHomeMap;
            if (GameCondition_Encirclement.Blocking(map) == null) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(ShipJob_FlyAway), nameof(ShipJob_FlyAway.TryStart))]
    internal static class EncirclementScriptedShuttleDeparture
    {
        private static bool Prefix(ShipJob_FlyAway __instance, ref bool __result)
        {
            TransportShip ship = __instance.transportShip;
            if (ship?.ShuttleComp == null || GameCondition_Encirclement.Blocking(ship.shipThing.Map) == null) return true;
            __result = false;
            return false;
        }
    }

    // A flight that left before the blockade would otherwise fall back to dropping its cargo inside the map.
    [HarmonyPatch(typeof(TravellingTransporters), "Arrived")]
    internal static class EncirclementShuttleDiversion
    {
        private static void Prefix(TravellingTransporters __instance, ref TransportersArrivalAction ___arrivalAction,
            List<ActiveTransporterInfo> ___transporters, PlanetTile ___destinationTile)
        {
            if (___arrivalAction is not TransportersArrivalAction_LandInSpecificCell ||
                !___transporters.Any(t => t.GetShuttle() != null)) return;
            Map map = Find.Maps.FirstOrDefault(m => m.Tile == ___destinationTile);
            if (GameCondition_Encirclement.Blocking(map) is not { } condition ||
                !TransportersArrivalAction_FormCaravan.CanFormCaravanAt(___transporters, map.Tile)) return;

            ___arrivalAction = new TransportersArrivalAction_FormCaravan();
            Messages.Message("RS_EncirclementShuttleDiverted".Translate(condition.ShuttleReason),
                __instance, MessageTypeDefOf.NegativeEvent);
        }
    }
}
