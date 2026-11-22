using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimSiege.Integration
{
    // Keep the native obstacle filters and breach ranking with a different defender faction.
    [HarmonyPatch]
    internal static class VanillaBreachTargets
    {
        [ThreadStatic] private static Faction defender;

        internal static Faction Enter(Faction target)
        {
            Faction previous = defender;
            defender = target;
            return previous;
        }

        internal static void Restore(Faction previous) => defender = previous;

        internal static bool CanBreach(Thing thing, Faction faction)
        {
            if (faction == Faction.OfPlayer) return BreachingUtility.ShouldBreachBuilding(thing);
            Faction previous = Enter(faction);
            try { return BreachingUtility.ShouldBreachBuilding(thing); }
            finally { Restore(previous); }
        }

        private static Faction DefendedFaction() => defender ?? Faction.OfPlayer;

        private static Faction ExcludedFaction() => defender == Faction.OfMechanoids ? null : Faction.OfMechanoids;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(BreachingUtility), nameof(BreachingUtility.ShouldBreachBuilding));
            yield return AccessTools.Method(typeof(BreachingUtility), nameof(BreachingUtility.CanSoloAttackTargetBuilding));
            yield return AccessTools.Method(typeof(TrashUtility), nameof(TrashUtility.ShouldTrashBuilding), new[] { typeof(Building) });
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo player = AccessTools.PropertyGetter(typeof(Faction), nameof(Faction.OfPlayer));
            MethodInfo mechs = AccessTools.PropertyGetter(typeof(Faction), nameof(Faction.OfMechanoids));
            MethodInfo target = AccessTools.Method(typeof(VanillaBreachTargets), nameof(DefendedFaction));
            MethodInfo excluded = AccessTools.Method(typeof(VanillaBreachTargets), nameof(ExcludedFaction));
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(player)) instruction.operand = target;
                if (instruction.Calls(mechs)) instruction.operand = excluded;
                yield return instruction;
            }
        }
    }
}
