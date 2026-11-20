using System;
using System.Collections.Generic;
using HarmonyLib;
using ProjectZ;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimSiege.Integration
{
    internal static class NativeConstruction
    {
        [ThreadStatic] private static Pawn fortressBuilder;
        [ThreadStatic] private static (IntVec3 Center, float MaxRadius) fortressHaulBounds;
        [ThreadStatic] private static (Map Map, CellRect Footprint) hiddenPlacement;

        // The same targeted work path used by vanilla prioritized work. It owns
        // worker eligibility, required capacities, blockers and resource jobs.
        private static readonly Func<JobGiver_Work, Pawn, WorkGiver, IntVec3, Job> Targeted =
            AccessTools.MethodDelegate<Func<JobGiver_Work, Pawn, WorkGiver, IntVec3, Job>>(
                AccessTools.Method(typeof(JobGiver_Work), "GiverTryGiveJobPrioritized"));

        internal static Job At(JobGiver_Work giver, Pawn pawn, WorkGiver worker, IntVec3 cell) =>
            Targeted(giver, pawn, worker, cell);

        internal static Job AtForFortressMech(JobGiver_Work giver, Pawn pawn, WorkGiver worker, IntVec3 cell,
            IntVec3 haulCenter = default, float haulMaxRadius = 0f)
        {
            Pawn previousBuilder = fortressBuilder;
            var previousBounds = fortressHaulBounds;
            fortressBuilder = pawn;
            fortressHaulBounds = (haulCenter, haulMaxRadius);
            try { return At(giver, pawn, worker, cell); }
            finally
            {
                fortressBuilder = previousBuilder;
                fortressHaulBounds = previousBounds;
            }
        }

        internal static bool CanPlaceFortressRebuild(BuildableDef def, IntVec3 cell, Rot4 rotation, Map map, ThingDef stuff)
        {
            var previous = hiddenPlacement;
            hiddenPlacement = (map, GenAdj.OccupiedRect(cell, rotation, def.Size));
            try { return GenConstruct.CanPlaceBlueprintAt(def, cell, rotation, map, stuffDef: stuff).Accepted; }
            finally { hiddenPlacement = previous; }
        }

        [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.CanConstruct),
            typeof(Thing), typeof(Pawn), typeof(WorkTypeDef), typeof(bool), typeof(JobDef))]
        private static class FortressMechWorkType
        {
            private static bool Prefix(Thing t, Pawn pawn, WorkTypeDef workType, bool forced, JobDef jobForReservation,
                ref bool __result)
            {
                if (pawn != fortressBuilder || workType != WorkTypeDefOf.Construction || forced) return true;
                __result = !pawn.WorkTypeIsDisabled(workType) &&
                    GenConstruct.CanConstruct(t, pawn, checkSkills: true, forced: false, jobForReservation: jobForReservation);
                return false;
            }
        }

        [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.CanPlaceBlueprintAt_NewTemp))]
        private static class FortressRebuildVisibility
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var fogged = AccessTools.Method(typeof(GridsUtility), nameof(GridsUtility.Fogged),
                    new[] { typeof(IntVec3), typeof(Map) });
                var replacement = AccessTools.Method(typeof(FortressRebuildVisibility), nameof(FoggedForPlacement));
                foreach (CodeInstruction instruction in instructions)
                {
                    if (instruction.Calls(fogged)) instruction.operand = replacement;
                    yield return instruction;
                }
            }

            private static bool FoggedForPlacement(IntVec3 cell, Map map) =>
                (map != hiddenPlacement.Map || !hiddenPlacement.Footprint.Contains(cell)) && cell.Fogged(map);
        }

        [HarmonyPatch(typeof(HaulAIUtility), nameof(HaulAIUtility.PawnCanAutomaticallyHaulFast_NewTemp))]
        private static class FortressMechHaulVisibility
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var fogged = AccessTools.Method(typeof(GridsUtility), nameof(GridsUtility.Fogged),
                    new[] { typeof(Thing) });
                var replacement = AccessTools.Method(typeof(FortressMechHaulVisibility), nameof(FoggedForHaul));
                foreach (CodeInstruction instruction in instructions)
                {
                    if (instruction.Calls(fogged)) instruction.operand = replacement;
                    yield return instruction;
                }
            }

            private static bool FoggedForHaul(Thing thing)
            {
                if (fortressBuilder == null || thing?.Map != fortressBuilder.Map)
                    return thing.Fogged();
                if (fortressHaulBounds.MaxRadius > 0f &&
                    !Api.GroundCellOf(thing.Position, thing.Map).InHorDistOf(fortressHaulBounds.Center, fortressHaulBounds.MaxRadius))
                    return true;
                return false;
            }
        }
    }
}
