using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimSiege.Raids
{
    [HarmonyPatch(typeof(JobGiver_AIFightEnemy), "TryGiveJob")]
    internal static class RaidStationaryCombat
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,
            ILGenerator generator)
        {
            MethodInfo canHit = AccessTools.Method(typeof(Verb), nameof(Verb.CanHitTarget));
            MethodInfo preferJob = AccessTools.Method(typeof(RaidStationaryCombat), nameof(TryPreferJob));
            MethodInfo findPosition = AccessTools.Method(typeof(JobGiver_AIFightEnemy), "TryFindShootingPosition");
            List<CodeInstruction> body = instructions.ToList();
            int hit = body.FindIndex(instruction => instruction.Calls(canHit));
            int reposition = body.FindIndex(hit + 1, instruction => instruction.Calls(findPosition));
            int start = reposition < 0 ? 0 : body.FindLastIndex(reposition, instruction => instruction.opcode == OpCodes.Ret) + 1;
            if (hit < 0 || start <= hit || !body[hit + 1].IsStloc())
            {
                Log.Warning("[RimBreach] Could not identify the raid shooting-position branch. Using native combat jobs.");
                return body;
            }

            // The earlier CanHitTarget call runs with vanilla's cover result still on the stack.
            // Enter on the reposition branch with an empty stack and read vanilla's saved verdict.
            int verdict = body[hit + 1].LocalIndex();
            LocalBuilder job = generator.DeclareLocal(typeof(Job));
            Label continueNative = generator.DefineLabel();
            CodeInstruction entry = CodeInstruction.LoadLocal(verdict).WithLabels(body[start].labels);
            body[start].labels.Clear();
            body[start].labels.Add(continueNative);
            body.InsertRange(start, new[]
            {
                entry,
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Ldloca, job),
                new CodeInstruction(OpCodes.Call, preferJob),
                new CodeInstruction(OpCodes.Brfalse, continueNative),
                new CodeInstruction(OpCodes.Ldloc, job),
                new CodeInstruction(OpCodes.Ret)
            });
            return body;
        }

        internal static bool HasAutoShootTarget(Pawn pawn, Verb verb)
        {
            if (verb == null || verb.verbProps.IsMeleeAttack) return false;
            TargetScanFlags flags = TargetScanFlags.NeedLOSToAll | TargetScanFlags.NeedThreat | TargetScanFlags.NeedAutoTargetable;
            if (verb.IsIncendiary_Ranged()) flags |= TargetScanFlags.NeedNonBurning;
            return AttackTargetFinder.BestShootTargetFromCurrentPosition(pawn, flags) != null;
        }

        private static bool TryPreferJob(bool canHit, Pawn pawn, out Job job)
        {
            job = null;
            // The native job giver has already handled melee and selected a ranged verb.
            if (!SiegeRaidMap.Enabled) return false;
            SiegeRaidMap raids = pawn.Map.GetComponent<SiegeRaidMap>();
            if (!raids.Applies(pawn)) return false;

            if (!canHit)
            {
                // Let the duty's ladder node open a route (or let close-assault use an existing route) before repositioning.
                SiegeParty party = raids.Party(pawn);
                return SiegeRoles.CanOpenLadderRoute(pawn, party) ||
                    (SiegeRoles.CloseAssault(pawn) && party.HasLadderRoute(pawn));
            }

            if (SiegeRoles.CloseAssault(pawn)) return false;

            if (!pawn.Position.WalkableBy(pawn.Map, pawn) ||
                !pawn.Map.pawnDestinationReservationManager.CanReserve(pawn.Position, pawn, pawn.Drafted))
                return false;

            if (pawn.CurrentEffectiveVerb is not { } effectiveVerb || !HasAutoShootTarget(pawn, effectiveVerb))
                return false;

            pawn.pather.StopDead();
            job = JobMaker.MakeJob(JobDefOf.Wait_Combat,
                JobGiver_AIFightEnemy.ExpiryInterval_ShooterSucceeded.RandomInRange, checkOverrideOnExpiry: true);
            return true;
        }
    }
}
