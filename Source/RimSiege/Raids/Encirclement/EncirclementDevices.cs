using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimSiege.Raids.Encirclement
{
    internal static class EncirclementDevices
    {
        internal static bool IsFieldDevice(BuildableDef def) => def is ThingDef building &&
            building.GetCompProperties<CompProperties_CausesGameCondition>()?.compClass == typeof(CompEncirclementDevice);

        internal static List<ThingDef> Choose(QuestPart_Encirclement siege)
        {
            var pool = new[] { EncirclementDefs.RS_FieldWeatherController, EncirclementDefs.RS_FieldClimateAdjuster,
                EncirclementDefs.RS_FieldToxicSpewer, EncirclementDefs.RS_FieldEMIDynamo };
            int count = siege.Mechanoid ? siege.ForceThreeDevices ? 3 : Rand.RangeInclusive(1, 3) : 1;
            if (siege.ForcedDevice != null) return new() { siege.ForcedDevice };
            return pool.InRandomOrder().Take(count).ToList();
        }
    }

    public sealed class CompEncirclementDevice : CompCauseGameCondition
    {
        private bool commissioned;
        private WeatherDef weather;
        private float temperatureOffset;

        public override bool Active => commissioned && parent.Spawned;

        internal void Commission()
        {
            if (commissioned) return;
            float ambient = parent.Map.mapTemperature.OutdoorTemp;
            weather = ambient < 1f ? DefDatabase<WeatherDef>.GetNamed("SnowGentle") : Props.conditionDef.weatherDef;
            temperatureOffset = ambient < 12f ? -18f : 18f;
            commissioned = true;
        }

        internal void Stop()
        {
            commissioned = false;
            foreach (GameCondition condition in CausedConditions.ToArray())
                if (condition.gameConditionManager.ActiveConditions.Contains(condition)) condition.End();
            base.CompTick();
        }

        public override void CompTick()
        {
            if (commissioned) base.CompTick();
        }

        protected override void SetupCondition(GameCondition condition, Map map)
        {
            base.SetupCondition(condition, map);
            if (condition is GameCondition_ForceWeather forced) forced.weather = weather;
            if (condition is GameCondition_TemperatureOffset climate) climate.tempOffset = temperatureOffset;
        }

        public override string CompInspectStringExtra()
        {
            string text = base.CompInspectStringExtra();
            string effect = Props.conditionDef == EncirclementDefs.RS_EncirclementWeather && weather != null
                ? $"{"Weather".Translate()}: {weather.LabelCap}"
                : Props.conditionDef == EncirclementDefs.RS_EncirclementClimate && commissioned
                    ? $"{"Temperature".Translate()}: {temperatureOffset:+0;-0;+0}"
                    : null;
            if (effect == null) return text;
            return text.NullOrEmpty() ? effect : text + "\n" + effect;
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            Stop();
            base.PostDeSpawn(map, mode);
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref commissioned, "commissioned");
            Scribe_Defs.Look(ref weather, "weather");
            Scribe_Values.Look(ref temperatureOffset, "temperatureOffset");
        }
    }

    // Copy the active language's vanilla text before blueprint and frame labels are generated.
    [HarmonyPatch(typeof(LoadedLanguage), nameof(LoadedLanguage.InjectIntoData_BeforeImpliedDefs))]
    internal static class EncirclementVanillaText
    {
        private static void Postfix()
        {
            CopyText(EncirclementDefs.RS_SiegeMechNode, ThingDefOf.Gloomlight);
            MatchDevice(EncirclementDefs.RS_FieldWeatherController, "WeatherController");
            MatchDevice(EncirclementDefs.RS_FieldClimateAdjuster, "ClimateAdjuster");
            MatchDevice(EncirclementDefs.RS_FieldToxicSpewer, "ToxicSpewer");
            MatchDevice(EncirclementDefs.RS_FieldEMIDynamo, "EMIDynamo");
        }

        private static void MatchDevice(ThingDef variant, string vanillaName)
        {
            // Royalty is optional. The siege defs supply their own text when the source is absent.
            ThingDef vanilla = DefDatabase<ThingDef>.GetNamedSilentFail(vanillaName);
            if (vanilla == null) return;
            CopyText(variant, vanilla);
            GameConditionDef condition = variant.GetCompProperties<CompProperties_CausesGameCondition>().conditionDef;
            GameConditionDef original = vanilla.GetCompProperties<CompProperties_CausesGameCondition>().conditionDef;
            CopyText(condition, original);
            condition.jumpToSourceKey = original.jumpToSourceKey;
        }

        private static void CopyText(Def target, Def source)
        {
            target.label = source.label;
            target.description = source.description;
        }
    }

    [HarmonyPatch(typeof(BuildableDef), "ResolveIcon")]
    internal static class EncirclementDeviceDefIcon
    {
        private static void Postfix(BuildableDef __instance)
        {
            if (EncirclementDevices.IsFieldDevice(__instance)) __instance.uiIcon = BaseContent.ClearTex;
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.UIIconOverride), MethodType.Getter)]
    internal static class EncirclementDeviceThingIcon
    {
        private static void Postfix(Thing __instance, ref Texture __result)
        {
            if (__instance is Building && EncirclementDevices.IsFieldDevice(__instance.def))
                __result = BaseContent.ClearTex;
        }
    }
}
