using System;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using RimSiege.Raids.Artillery;
using Verse;
using Verse.AI;

namespace RimSiege.Raids.Encirclement
{
    [HarmonyPatch]
    internal static class EncirclementReloadRestore
    {
        private const string SetupMethod = "SetupToils";
        private static readonly Action<JobDriver> SetupToils = AccessTools.MethodDelegate<Action<JobDriver>>(
            AccessTools.DeclaredMethod(typeof(JobDriver), SetupMethod));
        private static readonly AccessTools.FieldRef<JobDriver, int> ToilIndex =
            AccessTools.FieldRefAccess<JobDriver, int>("curToilIndex");
        private static readonly ConditionalWeakTable<JobDriver_ReloadTurret, SavedToil> Pending = new();
        private sealed record SavedToil(int Index);

        [HarmonyPatch(typeof(JobDriver), SetupMethod)]
        [HarmonyPrefix]
        private static bool DeferPreparation(JobDriver __instance)
        {
            if (Scribe.mode != LoadSaveMode.PostLoadInit || __instance is not JobDriver_ReloadTurret reload ||
                reload.job?.jobGiver is not (JobGiver_EncirclementWork or JobGiver_ArtilleryConstruction))
                return true;

            // CE builds a path toil from the interaction cell before a loaded turret has a map.
            Pending.AddOrUpdate(reload, new SavedToil(reload.CurToilIndex));
            ToilIndex(reload) = -1; // Pawn spawning can query the current toil for facing.
            return false;
        }

        [HarmonyPatch(typeof(Pawn), nameof(Pawn.PostMapInit))]
        [HarmonyPrefix]
        private static void PrepareBeforePathRecovery(Pawn __instance)
        {
            if (__instance.jobs?.curDriver is not JobDriver_ReloadTurret reload ||
                !Pending.TryGetValue(reload, out SavedToil saved)) return;
            Pending.Remove(reload);
            // Everything has spawned by now; Pawn.PostMapInit is about to resume the saved path.
            if (reload.job.targetA.Thing is Building_TurretGunCE { Spawned: true } turret && turret.Map == __instance.Map)
            {
                SetupToils(reload);
                if (__instance.jobs.curDriver == reload) ToilIndex(reload) = saved.Index;
            }
            else
                __instance.jobs.EndCurrentJob(JobCondition.Incompletable, startNewJob: false);
        }
    }
}
