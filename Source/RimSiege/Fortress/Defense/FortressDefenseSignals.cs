using CombatExtended;
using CombatExtended.AI;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Fortress
{
    [HarmonyPatch(typeof(FoodUtility), nameof(FoodUtility.ShouldBeFedBySomeone))]
    internal static class FortressFeedPatientPatch
    {
        private static void Postfix(Pawn pawn, ref bool __result)
        {
            if (__result || pawn.Map?.Parent is not FortressSite ||
                pawn.GetLord() is not { CurLordToil: LordToil_FortressDefense } lord ||
                !FortressSupportJobs.NeedsFeeding(pawn)) return;
            // The native job reserves its patient before checking this predicate.
            Pawn feeder = pawn.Map.reservationManager.FirstRespectedReserver(pawn, pawn);
            __result = feeder != null && feeder.GetLord() == lord &&
                feeder.CurJobDef == JobDefOf.FeedPatient && feeder.CurJob.targetB.Pawn == pawn;
        }
    }

    [HarmonyPatch(typeof(JobDriver_TakeToBed), "CheckMakeTakeeGuest")]
    internal static class FortressRescueGuestPatch
    {
        private static bool Prefix(JobDriver_TakeToBed __instance) =>
            __instance.job.def != JobDefOf.Rescue ||
            __instance.pawn.GetLord()?.LordJob is not LordJob_FortressDefense ||
            __instance.job.targetA.Pawn?.Faction != __instance.pawn.Faction;
    }

    [HarmonyPatch(typeof(CompTend), nameof(CompTend.TryGiveTacticalJob))]
    internal static class FortressTendReservationPatch
    {
        private static bool Prefix(CompTend __instance)
        {
            Pawn pawn = __instance.SelPawn;
            if (pawn.GetLord()?.CurLordToil is not LordToil_FortressDefense) return true;
            if (pawn.CurJobDef == JobDefOf.TendPatient && pawn.CurJob.targetA.Pawn == pawn) return false;
            return pawn.CanReserve(pawn);
        }
    }

    [HarmonyPatch(typeof(Building_TurretGunCE), nameof(Building_TurretGunCE.TryFindNewTarget))]
    internal static class FortressGunContactPatch
    {
        private static void Postfix(Building_TurretGunCE __instance, ref LocalTargetInfo __result)
        {
            if (!TryGetDefense(__instance, out Pawn gunner, out LordJob_FortressDefense defense)) return;
            bool allowed = __result.Thing is Thing native && defense.ValidateAttackTarget(gunner, native);
            if (allowed && FortressTargeting.IsCombatTarget(__result.Thing)) return;
            if (!allowed) __result = LocalTargetInfo.Invalid;
            if ((!allowed && !__instance.IsHashIntervalTick(25)) || !__instance.AttackVerb.Available()) return;

            Thing preferred = defense.PreferredGunTarget(__instance, gunner);
            if (preferred != null) __result = preferred;
            else if (!allowed && defense.GunContact(gunner) is Thing contact &&
                (contact is not Pawn pawn || !pawn.IsPsychologicallyInvisible() && !pawn.RaceProps.Animal) &&
                FortressDefensePosts.HasShot(__instance, contact)) __result = contact;
        }

        internal static bool TryGetDefense(Building_TurretGunCE gun, out Pawn gunner, out LordJob_FortressDefense defense)
        {
            gunner = gun.MannableComp?.ManningPawn;
            defense = null;
            if (gunner?.mindState.duty?.focusSecond.Thing != gun ||
                gunner.GetLord() is not { CurLordToil: LordToil_FortressDefense, LordJob: LordJob_FortressDefense job })
                return false;
            defense = job;
            return true;
        }
    }

    [HarmonyPatch(typeof(Building_TurretGunCE), nameof(Building_TurretGunCE.BeginBurst))]
    internal static class FortressGunBurstPatch
    {
        private static bool Prefix(Building_TurretGunCE __instance)
        {
            if (__instance.CurrentTarget.Thing is not Thing target ||
                !FortressGunContactPatch.TryGetDefense(__instance, out Pawn gunner, out LordJob_FortressDefense defense) ||
                defense.ValidateAttackTarget(gunner, target)) return true;
            __instance.ResetCurrentTarget();
            return false;
        }
    }

    [HarmonyPatch(typeof(Lord), nameof(Lord.Notify_BuildingDamaged))]
    internal static class FortressBuildingAttackedPatch
    {
        private static void Postfix(Lord __instance, Building building, DamageInfo dinfo)
        {
            if (__instance.CurLordToil is LordToil_FortressDefense &&
                __instance.LordJob is LordJob_FortressDefense defense)
                defense.Attacked(building, dinfo);
        }
    }

    [HarmonyPatch(typeof(Verb_LaunchProjectileCE), nameof(Verb_LaunchProjectileCE.TryCastShot))]
    internal static class FortressTurretRetaliationPatch
    {
        private static void Postfix(Verb_LaunchProjectileCE __instance, bool __result)
        {
            if (!__result || __instance.Caster is not Building_TurretGunCE gun ||
                gun.Map?.Parent is not FortressSite || __instance.CurrentTarget.Thing is not Thing target) return;
            foreach (Lord lord in gun.Map.lordManager.lords)
                if (lord.faction == gun.Faction && lord.CurLordToil is LordToil_FortressDefense &&
                    lord.LordJob is LordJob_FortressDefense defense)
                {
                    defense.RecordRetaliation(target);
                    break;
                }
        }
    }

    [HarmonyPatch(typeof(CompAmmoUser), nameof(CompAmmoUser.Notify_ShotFired))]
    internal static class FortressAutomaticGunEmptyPatch
    {
        private static void Postfix(CompAmmoUser __instance)
        {
            if (__instance.turret is not Building_TurretGunCE { Spawned: true, MannableComp: null } gun ||
                !__instance.EmptyMagazine || gun.Map.Parent is not FortressSite) return;
            foreach (Lord lord in gun.Map.lordManager.lords)
            {
                if (lord.faction != gun.Faction || lord.CurLordToil is not LordToil_FortressDefense) continue;
                ((LordJob_FortressDefense)lord.LordJob).RequestResupply();
                break;
            }
        }
    }

    [HarmonyPatch(typeof(Lord), nameof(Lord.Notify_PawnLost))]
    internal static class FortressRememberLostPostPatch
    {
        private static void Prefix(Lord __instance, Pawn pawn, PawnLostCondition cond)
        {
            if (cond != PawnLostCondition.Incapped && pawn.GetLord() == __instance &&
                __instance.LordJob is LordJob_FortressDefense defense)
                defense.RememberPost(pawn);
        }
    }

    // Vanilla clears the duty before it sends the downed notification.
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.ClearMind_NewTemp))]
    internal static class FortressRememberDownedPostPatch
    {
        private static void Prefix(Pawn __instance, bool wasDowned)
        {
            if (wasDowned && __instance.GetLord()?.LordJob is LordJob_FortressDefense defense)
                defense.RememberPost(__instance);
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.PostApplyDamage))]
    internal static class FortressDefenderBleedingPatch
    {
        private static void Postfix(Pawn __instance, float totalDamageDealt)
        {
            if (totalDamageDealt > 0f && __instance.GetLord()?.LordJob is LordJob_FortressDefense defense &&
                __instance.health.hediffSet.BleedRateTotal > 0f &&
                __instance.mindState.duty?.def != FortressDutyDefOf.RS_FortressRecover)
                defense.QueueRelief(__instance);
        }
    }
}
