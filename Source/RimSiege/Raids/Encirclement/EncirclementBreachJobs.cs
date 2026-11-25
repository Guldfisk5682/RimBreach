using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimSiege.Raids.Encirclement
{
    [HarmonyPatch(typeof(Toils_Combat), nameof(Toils_Combat.GotoCastPosition))]
    internal static class EncirclementBreachJobs
    {
        private sealed class Retry { internal int Until; }
        private static readonly ConditionalWeakTable<Pawn, Retry> Retries = new();

        private static void Postfix(TargetIndex targetInd, TargetIndex castPositionInd, Toil __result)
        {
            Toil toil = __result;
            var native = toil.initAction;
            toil.initAction = () =>
            {
                Pawn pawn = toil.actor;
                Job job = pawn.CurJob;
                if (castPositionInd != TargetIndex.B || job?.jobGiver is not JobGiver_AIBreaching ||
                    pawn.Map?.GetComponent<SiegeRaidMap>().Party(pawn)?.Encirclement != true)
                {
                    native();
                    return;
                }

                // The breach planner has already selected a same- or cross-floor firing cell.
                // Keep its checked cell instead of starting the unrelated generic cast-position search.
                Thing target = job.GetTarget(targetInd).Thing;
                IntVec3 cell = job.GetTarget(castPositionInd).Cell;
                Map map = pawn.Map;
                if (target?.Spawned == true && target.Map == map && job.verbToUse is { } verb &&
                    cell.InBounds(map) && cell.Standable(map) &&
                    map.pawnDestinationReservationManager.CanReserve(cell, pawn) &&
                    pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly) && verb.CanHitTargetFrom(cell, target))
                {
                    pawn.pather.StartPath(cell, PathEndMode.OnCell);
                    map.pawnDestinationReservationManager.Reserve(pawn, job, cell);
                    return;
                }

                Retries.GetOrCreateValue(pawn).Until = GenTicks.TicksGame + 60;
                pawn.jobs.EndCurrentJob(JobCondition.Incompletable, startNewJob: false);
            };
        }

        [HarmonyPatch(typeof(JobGiver_AIBreaching), "TryGiveJob")]
        private static class DeferInvalidPosition
        {
            private static bool Prefix(Pawn pawn, ref Job __result)
            {
                if (!Retries.TryGetValue(pawn, out Retry retry) || retry.Until <= GenTicks.TicksGame ||
                    pawn.Map?.GetComponent<SiegeRaidMap>().Party(pawn)?.Encirclement != true) return true;
                __result = null;
                return false;
            }
        }
    }
}
