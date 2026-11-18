using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimSiege.Raids
{
    internal static class YttakinAnimalPolicy
    {
        internal const float AnimalRaidCost = 10f;
        internal const int MaxAnimalsPerRaid = 5;
        private const string YttakinFactionDefName = "PirateYttakin";

        internal sealed class RaidState
        {
            internal int AnimalsPicked;
        }

        [ThreadStatic] internal static RaidState CurrentRaid;
        [ThreadStatic] internal static bool Active;

        internal static bool IsAnimal(PawnKindDef kind) => kind?.race?.race?.Animal == true;

        internal static bool Applies(PawnGroupMakerParms groupParms) =>
            CurrentRaid != null
            && RimSiegeMod.Settings?.EnhancedAssault == true
            && groupParms?.groupKind == PawnGroupKindDefOf.Combat
            && groupParms.faction?.def?.defName == YttakinFactionDefName
            && groupParms.faction.HostileTo(Faction.OfPlayer);
    }

    [HarmonyPatch(typeof(IncidentWorker_Raid), nameof(IncidentWorker_Raid.TryGenerateRaidInfo))]
    internal static class YttakinRaidScopePatch
    {
        private static void Prefix(IncidentWorker_Raid __instance, out YttakinAnimalPolicy.RaidState __state)
        {
            __state = YttakinAnimalPolicy.CurrentRaid;
            YttakinAnimalPolicy.CurrentRaid = __instance is IncidentWorker_RaidEnemy &&
                RimSiegeMod.Settings?.EnhancedAssault == true
                ? new YttakinAnimalPolicy.RaidState()
                : null;
        }

        private static void Finalizer(YttakinAnimalPolicy.RaidState __state) =>
            YttakinAnimalPolicy.CurrentRaid = __state;
    }

    [HarmonyPatch(typeof(PawnGroupMakerUtility), nameof(PawnGroupMakerUtility.ChoosePawnGenOptionsByPoints))]
    internal static class YttakinChooseOptionsScopePatch
    {
        private static void Prefix(PawnGroupMakerParms groupParms, out bool __state)
        {
            __state = YttakinAnimalPolicy.Active;
            YttakinAnimalPolicy.Active = YttakinAnimalPolicy.Applies(groupParms);
        }

        private static void Postfix(IEnumerable<PawnGenOptionWithXenotype> __result)
        {
            if (!YttakinAnimalPolicy.Active || YttakinAnimalPolicy.CurrentRaid == null || __result == null) return;
            foreach (PawnGenOptionWithXenotype chosen in __result)
            {
                if (YttakinAnimalPolicy.IsAnimal(chosen.Option?.kind))
                    YttakinAnimalPolicy.CurrentRaid.AnimalsPicked++;
            }
        }

        private static void Finalizer(bool __state) => YttakinAnimalPolicy.Active = __state;
    }

    [HarmonyPatch(typeof(PawnGenOption), nameof(PawnGenOption.Cost), MethodType.Getter)]
    internal static class YttakinAnimalOptionCostPatch
    {
        private static void Postfix(PawnGenOption __instance, ref float __result)
        {
            if (YttakinAnimalPolicy.Active && YttakinAnimalPolicy.IsAnimal(__instance.kind))
                __result = YttakinAnimalPolicy.AnimalRaidCost;
        }
    }

    [HarmonyPatch(typeof(PawnGenOptionWithXenotype), nameof(PawnGenOptionWithXenotype.Cost), MethodType.Getter)]
    internal static class YttakinAnimalXenotypeOptionCostPatch
    {
        private static void Postfix(ref PawnGenOptionWithXenotype __instance, ref float __result)
        {
            if (YttakinAnimalPolicy.Active && YttakinAnimalPolicy.IsAnimal(__instance.Option?.kind))
                __result = YttakinAnimalPolicy.AnimalRaidCost;
        }
    }

    [HarmonyPatch(typeof(PawnGroupMakerUtility), "CanUseOption")]
    internal static class YttakinAnimalCanUseOptionPatch
    {
        private static void Prefix(PawnGenOption o, ref XenotypeDef xenotype)
        {
            if (YttakinAnimalPolicy.Active && YttakinAnimalPolicy.IsAnimal(o?.kind))
                xenotype = null;
        }
    }

    [HarmonyPatch(typeof(PawnGroupMakerUtility), nameof(PawnGroupMakerUtility.PawnGenOptionValid))]
    internal static class YttakinAnimalCapPatch
    {
        private static void Postfix(PawnGenOption o, List<PawnGenOptionWithXenotype> chosenOptions, ref bool __result)
        {
            if (!__result || !YttakinAnimalPolicy.Active || chosenOptions == null ||
                !YttakinAnimalPolicy.IsAnimal(o?.kind)) return;

            int totalAnimals = YttakinAnimalPolicy.CurrentRaid?.AnimalsPicked ?? 0;
            for (int i = 0; i < chosenOptions.Count; i++)
            {
                if (YttakinAnimalPolicy.IsAnimal(chosenOptions[i].Option?.kind))
                {
                    totalAnimals++;
                    if (totalAnimals >= YttakinAnimalPolicy.MaxAnimalsPerRaid)
                    {
                        __result = false;
                        return;
                    }
                }
            }
            if (totalAnimals >= YttakinAnimalPolicy.MaxAnimalsPerRaid)
                __result = false;
        }
    }
}
