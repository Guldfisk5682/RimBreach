using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimSiege.Raids.Encirclement;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimSiege.Integration
{
    [HarmonyPatch]
    internal static class IsoRimEncirclementSky
    {
        private delegate float WeatherShade(Map map, out float storm);
        private static readonly MethodInfo WeatherMethod = AccessTools.DeclaredMethod(typeof(IsoRim.Sky), "WeatherGrey");
        private static readonly WeatherShade ReadWeather = WeatherMethod == null ? null :
            AccessTools.MethodDelegate<WeatherShade>(WeatherMethod);

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.DeclaredMethod(typeof(IsoRim.Sky), "Colours");
            if (AccessTools.DeclaredMethod(typeof(IsoRim.Sky), "PaintColours") is { } painted)
                yield return painted;
        }

        private static float Veil(Map map) => GameCondition_Encirclement.Blocking(map)?.SkyTargetLerpFactor(map) ?? 0f;

        private static float ApparentSunGlow(Map map) => GenCelestial.CurCelestialSunGlow(map) * (1f - Veil(map));

        private static float ApparentWeatherShade(Map map, out float storm) =>
            Mathf.Lerp(ReadWeather(map, out storm), 1f, Veil(map));

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var sun = AccessTools.DeclaredMethod(typeof(GenCelestial), nameof(GenCelestial.CurCelestialSunGlow));
            var apparentSun = AccessTools.DeclaredMethod(typeof(IsoRimEncirclementSky), nameof(ApparentSunGlow));
            var apparentShade = AccessTools.DeclaredMethod(typeof(IsoRimEncirclementSky), nameof(ApparentWeatherShade));
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(sun)) instruction.operand = apparentSun;
                else if (WeatherMethod != null && instruction.Calls(WeatherMethod)) instruction.operand = apparentShade;
                yield return instruction;
            }
        }

        // Older IsoRim builds calculate weather shade directly inside Colours.
        private static void Postfix(Map map, bool space, ref float sunShow, ref float moonShow, ref float grey)
        {
            if (ReadWeather != null || space) return;
            float veil = Veil(map);
            sunShow *= 1f - veil;
            moonShow *= 1f - veil;
            grey = Mathf.Lerp(grey, 1f, veil);
        }
    }
}
