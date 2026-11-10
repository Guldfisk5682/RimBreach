using CombatExtended;
using CombatExtended.AI;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Fortress
{
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
            if (__result.IsValid || __instance.MannableComp?.ManningPawn is not Pawn gunner ||
                gunner.mindState.duty?.focusSecond.Thing != __instance ||
                gunner.GetLord()?.LordJob is not LordJob_FortressDefense defense ||
                !__instance.IsHashIntervalTick(25)) return;
            if (defense.GunContact(gunner) is not Thing target ||
                target is Pawn pawn && (pawn.IsPsychologicallyInvisible() || pawn.RaceProps.Animal) ||
                !__instance.AttackVerb.Available() || !FortressDefensePosts.HasShot(__instance, target)) return;
            __result = target;
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
