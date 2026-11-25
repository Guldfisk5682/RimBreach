using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using CombatExtended;
using HarmonyLib;
using RimSiege.Integration;
using RimSiege.Raids.Artillery;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Raids
{
    [HarmonyPatch(typeof(JobGiver_AIFightEnemy), "TryGiveJob")]
    internal static class RaidStationaryCombat
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,
            ILGenerator generator)
        {
            MethodInfo canHit = AccessTools.Method(typeof(Verb), nameof(Verb.CanHitTarget));
            MethodInfo canHitChecked = AccessTools.Method(typeof(RaidStationaryCombat), nameof(CanHitTargetChecked));
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

            // Insert before start has already happened, so hit is unchanged.
            CodeInstruction loadPawn = new CodeInstruction(OpCodes.Ldarg_1).MoveLabelsFrom(body[hit]);
            body[hit].opcode = OpCodes.Call;
            body[hit].operand = canHitChecked;
            body.Insert(hit, loadPawn);
            return body;
        }

        internal static bool AppliesTo(Pawn pawn, SiegeRaidMap raids)
        {
            if (raids == null || pawn == null) return false;
            if (raids.Applies(pawn)) return true;
            Lord lord = pawn.GetLord();
            if (lord?.LordJob is LordJob_ArtilleryMechEscort) return SiegeRaidMap.Enabled;
            if (!SiegeRoles.IsHumanRaidEscortMech(pawn) || raids.Party(pawn)?.TacticsEnabled != true) return false;
            ArtilleryRaid raid = pawn.Map.GetComponent<ArtilleryMap>().ForPawn(pawn);
            return raid != null && (lord == raid.CampLord && pawn.mindState.duty?.def == ArtillerySmokeDefOf.RS_ArtilleryMechSmokeEscort ||
                lord == raid.AssaultLord && lord?.CurLordToil is LordToil_AssaultColonyBreaching);
        }

        internal static bool IsIndirectRangedVerb(Verb verb)
        {
            if (verb == null || verb.verbProps.IsMeleeAttack || verb is Verb_CastAbility) return false;
            if (verb is Verb_ShootMortarCE) return true;
            ThingDef projectile = ProjectileOf(verb);
            if (projectile?.projectile?.flyOverhead == true) return true;
            return !verb.verbProps.requireLineOfSight && verb.verbProps.LaunchesProjectile;
        }

        internal static bool ProjectileFliesOverhead(Verb verb) =>
            ProjectileOf(verb)?.projectile?.flyOverhead == true;

        private static ThingDef ProjectileOf(Verb verb) =>
            (verb as Verb_LaunchProjectileCE)?.Projectile ?? verb?.GetProjectile();

        internal static bool CanShootOverheadFrom(Pawn pawn, Verb verb) =>
            pawn is { Spawned: true, Map: not null } &&
            (!ProjectileFliesOverhead(verb) || IsoFloor.OpenSkyOver(pawn.Position, pawn.Map));

        private static bool CanHitTargetChecked(Verb verb, LocalTargetInfo targ, Pawn pawn) =>
            verb.CanHitTarget(targ) && CanShootOverheadFrom(pawn, verb);

        internal static Thing BestIndirectShootTargetFromCurrentPosition(Pawn pawn, Verb verb)
        {
            if (!CanShootOverheadFrom(pawn, verb)) return null;
            TargetScanFlags flags = TargetScanFlags.NeedThreat | TargetScanFlags.NeedAutoTargetable;
            if (ProjectileFliesOverhead(verb)) flags |= TargetScanFlags.NeedNotUnderThickRoof;
            if (verb.IsIncendiary_Ranged()) flags |= TargetScanFlags.NeedNonBurning;
            return (Thing)AttackTargetFinder.BestShootTargetFromCurrentPosition(
                pawn, flags, target => verb.CanHitTarget(target));
        }

        internal static bool HasAutoShootTarget(Pawn pawn, Verb verb)
        {
            return verb != null && !verb.IsMeleeAttack && BestShootTarget(pawn, verb, verifyDirectShot: false) != null;
        }

        internal static Thing BestShootTarget(Pawn pawn, Verb verb, bool verifyDirectShot = true)
        {
            if (IsIndirectRangedVerb(verb))
                return BestIndirectShootTargetFromCurrentPosition(pawn, verb);
            TargetScanFlags flags = TargetScanFlags.NeedLOSToAll | TargetScanFlags.NeedThreat | TargetScanFlags.NeedAutoTargetable;
            if (verb.IsIncendiary_Ranged()) flags |= TargetScanFlags.NeedNonBurning;
            Predicate<Thing> validator = verifyDirectShot ? target => verb.CanHitTarget(CastTarget(verb, target)) : null;
            return AttackTargetFinder.BestShootTargetFromCurrentPosition(pawn, flags, validator)?.Thing;
        }

        internal static LocalTargetInfo CastTarget(Verb verb, Thing target) =>
            verb.verbProps.ai_RangedAlawaysShootGroundBelowTarget ? new LocalTargetInfo(target.Position) : target;

        internal static bool TryMakeStationaryCombatJob(Pawn pawn, out Job job, bool checkAutoShootTarget = true)
        {
            job = null;
            if (!pawn.Position.WalkableBy(pawn.Map, pawn) ||
                !pawn.Map.pawnDestinationReservationManager.CanReserve(pawn.Position, pawn, pawn.Drafted))
                return false;

            if (checkAutoShootTarget &&
                (pawn.CurrentEffectiveVerb is not { } effectiveVerb || !HasAutoShootTarget(pawn, effectiveVerb)))
                return false;

            pawn.pather?.StopDead();
            job = JobMaker.MakeJob(JobDefOf.Wait_Combat,
                JobGiver_AIFightEnemy.ExpiryInterval_ShooterSucceeded.RandomInRange, checkOverrideOnExpiry: true);
            return true;
        }

        private static bool TryPreferJob(bool canHit, Pawn pawn, out Job job)
        {
            job = null;
            // The native job giver has already handled melee and selected a ranged verb.
            if (pawn.GetLord()?.LordJob is Fortress.LordJob_FortressSortie)
                return canHit && TryMakeStationaryCombatJob(pawn, out job);

            SiegeRaidMap raids = pawn.Map.GetComponent<SiegeRaidMap>();
            if (!AppliesTo(pawn, raids)) return false;

            if (!canHit)
            {
                if (!raids.Applies(pawn)) return false;
                // Let the duty's ladder node open a route (or let close-assault use an existing route) before repositioning.
                SiegeParty party = raids.Party(pawn);
                return SiegeRoles.CanOpenLadderRoute(pawn, party) ||
                    (SiegeRoles.CloseAssault(pawn) && SiegeRoles.Eligible(pawn, party) && party.HasLadderRoute(pawn));
            }

            bool escort = SiegeRoles.IsHumanRaidEscortMech(pawn);
            if (!escort && SiegeRoles.CloseAssault(pawn)) return false;

            // The escort already checked its current target with the actual verb.
            return TryMakeStationaryCombatJob(pawn, out job, checkAutoShootTarget: !escort);
        }
    }

    [HarmonyPatch]
    internal static class RaidAssaultJobRecheck
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.DeclaredMethod(typeof(JobGiver_AIFightEnemy), "TryGiveJob");
            yield return AccessTools.DeclaredMethod(typeof(JobGiver_AIBreaching), "TryGiveJob");
            yield return AccessTools.DeclaredMethod(typeof(JobGiver_AIGotoNearestHostile), "TryGiveJob");
            yield return AccessTools.DeclaredMethod(typeof(JobGiver_AITrashColonyClose), "TryGiveJob");
            yield return AccessTools.DeclaredMethod(typeof(JobGiver_AITrashBuildingsDistant), "TryGiveJob");
        }

        private static void Postfix(Pawn pawn, Job __result, MethodBase __originalMethod)
        {
            if (__result == null || pawn.Map == null) return;
            bool trashing = __originalMethod.DeclaringType == typeof(JobGiver_AITrashColonyClose) ||
                __originalMethod.DeclaringType == typeof(JobGiver_AITrashBuildingsDistant);
            bool escortMoving = __result.def == JobDefOf.Goto && SiegeRoles.IsHumanRaidEscortMech(pawn);
            if (!escortMoving && !trashing) return;
            SiegeRaidMap raids = pawn.Map.GetComponent<SiegeRaidMap>();
            if (!escortMoving && LadderInventory.Get(pawn) == null && raids.Party(pawn)?.Encirclement != true) return;
            if (escortMoving ? !RaidStationaryCombat.AppliesTo(pawn, raids) : !raids.Applies(pawn)) return;

            // Reconsider the job in the normal think tree when a shot or ladder opens up.
            int interval = escortMoving ? 60 : 120;
            __result.intervalScalingTarget = TargetIndex.None;
            __result.expiryInterval = __result.expiryInterval > 0 ? Math.Min(__result.expiryInterval, interval) : interval;
            __result.checkOverrideOnExpire = true;
            __result.expireRequiresEnemiesNearby = false;
        }
    }

    [HarmonyPatch(typeof(JobGiver_AIFightEnemy), "FindAttackTarget")]
    internal static class IndirectAttackTargetPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo bestAttack = AccessTools.Method(typeof(AttackTargetFinder), nameof(AttackTargetFinder.BestAttackTarget));
            MethodInfo adjusted = AccessTools.Method(typeof(IndirectAttackTargetPatch), nameof(FindAttackTarget));
            bool replaced = false;
            foreach (CodeInstruction code in instructions)
            {
                if (code.Calls(bestAttack))
                {
                    code.opcode = OpCodes.Call;
                    code.operand = adjusted;
                    replaced = true;
                }
                yield return code;
            }
            if (!replaced)
                Log.Warning("[RimBreach] Could not patch JobGiver_AIFightEnemy.FindAttackTarget for indirect-fire targeting.");
        }

        private static IAttackTarget FindAttackTarget(
            IAttackTargetSearcher searcher, TargetScanFlags flags, Predicate<Thing> validator,
            float minDist, float maxDist, IntVec3 locus, float maxTravelRadiusFromLocus,
            bool canBashDoors, bool canTakeTargetsCloserThanEffectiveMinRange, bool canBashFences, bool onlyRanged)
        {
            if (searcher is Pawn pawn &&
                pawn.TryGetAttackVerb(null, !pawn.IsColonist && !pawn.IsColonySubhuman) is { IsMeleeAttack: false } verb)
            {
                bool indirect = RaidStationaryCombat.IsIndirectRangedVerb(verb);
                if (indirect)
                {
                    flags &= ~(TargetScanFlags.NeedLOSToPawns | TargetScanFlags.NeedLOSToNonPawns);
                    if (RaidStationaryCombat.ProjectileFliesOverhead(verb)) flags |= TargetScanFlags.NeedNotUnderThickRoof;
                }
                if (maxDist >= 56f && (indirect || SiegeRoles.IsHumanRaidEscortMech(pawn) &&
                    RaidStationaryCombat.AppliesTo(pawn, pawn.Map.GetComponent<SiegeRaidMap>())))
                    maxDist = Mathf.Max(maxDist, verb.EffectiveRange);
            }
            return AttackTargetFinder.BestAttackTarget(searcher, flags, validator, minDist, maxDist,
                locus, maxTravelRadiusFromLocus, canBashDoors, canTakeTargetsCloserThanEffectiveMinRange,
                canBashFences, onlyRanged);
        }
    }

    [HarmonyPatch(typeof(CastPositionFinder), nameof(CastPositionFinder.TryFindCastPosition))]
    internal static class IndirectShootingPositionPatch
    {
        private static void Prefix(ref CastPositionRequest newReq)
        {
            if (newReq.caster is not { Spawned: true, Map: { } map }) return;
            if (newReq.caster.GetLord()?.LordJob is Fortress.LordJob_FortressSortie sortie &&
                sortie.Ground.Outside(newReq.caster.Position))
            {
                Func<IntVec3, bool> existing = newReq.validator;
                newReq.validator = existing == null
                    ? c => sortie.Ground.Outside(c)
                    : c => existing(c) && sortie.Ground.Outside(c);
            }
            if (!RaidStationaryCombat.IsIndirectRangedVerb(newReq.verb) ||
                !RaidStationaryCombat.ProjectileFliesOverhead(newReq.verb))
                return;
            Func<IntVec3, bool> current = newReq.validator;
            newReq.validator = current == null
                ? c => IsoFloor.OpenSkyOver(c, map)
                : c => current(c) && IsoFloor.OpenSkyOver(c, map);
        }
    }

    [HarmonyPatch(typeof(JobDriver_Wait), "CheckForAutoAttack")]
    internal static class IndirectWaitAutoAttackPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo bestShoot = AccessTools.Method(typeof(AttackTargetFinder),
                nameof(AttackTargetFinder.BestShootTargetFromCurrentPosition));
            MethodInfo adjusted = AccessTools.Method(typeof(IndirectWaitAutoAttackPatch), nameof(FindAutoAttackTarget));
            bool replaced = false;
            foreach (CodeInstruction code in instructions)
            {
                if (code.Calls(bestShoot))
                {
                    code.opcode = OpCodes.Call;
                    code.operand = adjusted;
                    replaced = true;
                }
                yield return code;
            }
            if (!replaced)
                Log.Warning("[RimBreach] Could not patch JobDriver_Wait.CheckForAutoAttack for indirect-fire targeting.");
        }

        private static IAttackTarget FindAutoAttackTarget(IAttackTargetSearcher searcher, TargetScanFlags flags,
            Predicate<Thing> validator, float minDistance, float maxDistance)
        {
            if (searcher is Pawn pawn && pawn.CurrentEffectiveVerb is { } verb &&
                RaidStationaryCombat.IsIndirectRangedVerb(verb) &&
                (pawn.GetLord()?.LordJob is not (Fortress.LordJob_FortressDefense or Fortress.LordJob_FortressSortie) ||
                    Fortress.FortressCombatSearch.AllowIndirect(pawn, pawn.GetLord()?.LordJob)))
                return (IAttackTarget)RaidStationaryCombat.BestIndirectShootTargetFromCurrentPosition(pawn, verb);
            return AttackTargetFinder.BestShootTargetFromCurrentPosition(searcher, flags, validator, minDistance, maxDistance);
        }
    }
}
