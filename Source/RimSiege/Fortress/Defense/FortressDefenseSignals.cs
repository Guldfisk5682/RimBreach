using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
            if (__instance.LordJob is IFortressDefenseLord defense && defense.IsDefenseActive)
                defense.Attacked(building, dinfo);
        }
    }

    internal static class FortressNonMortarHitScope
    {
        [ThreadStatic] internal static bool InMortarHit;
        [ThreadStatic] internal static bool ThrowingMortarFrags;
        [ThreadStatic] internal static bool SpawningMortarFrag;
        private static readonly ConditionalWeakTable<ProjectileCE, object> MortarFragments = new();
        private static readonly object Marker = new();

        internal static void MarkMortarFragment(ProjectileCE projectile)
        {
            if (projectile != null) MortarFragments.AddOrUpdate(projectile, Marker);
        }

        internal static bool IsMortarProjectile(ProjectileCE projectile) =>
            projectile != null && (IsMortarWeapon(projectile.def) || IsMortarWeapon(projectile.equipmentDef) ||
                MortarFragments.TryGetValue(projectile, out _));

        internal static bool IsMortarWeapon(ThingDef weapon)
        {
            if (weapon == null) return false;
            if (weapon.building?.IsMortar == true || weapon.projectile?.flyOverhead == true) return true;
            if (weapon.Verbs == null) return false;
            foreach (VerbProperties verb in weapon.Verbs)
                if (typeof(Verb_ShootMortarCE).IsAssignableFrom(verb.verbClass) ||
                    verb.defaultProjectile?.projectile?.flyOverhead == true)
                    return true;
            return false;
        }

        internal static bool IsOverheadVerb(Verb verb)
        {
            if (verb == null || verb.verbProps.IsMeleeAttack) return false;
            if (verb is Verb_ShootMortarCE) return true;
            ThingDef projectile = (verb as Verb_LaunchProjectileCE)?.Projectile ?? verb.GetProjectile();
            return projectile?.projectile?.flyOverhead == true ||
                verb.verbProps?.defaultProjectile?.projectile?.flyOverhead == true;
        }

        internal static IEnumerator WrapMortarFragRoutine(IEnumerator inner)
        {
            while (true)
            {
                bool previous = SpawningMortarFrag;
                SpawningMortarFrag = true;
                bool moved;
                try { moved = inner.MoveNext(); }
                finally { SpawningMortarFrag = previous; }
                if (!moved) yield break;
                yield return inner.Current;
            }
        }
    }

    [HarmonyPatch(typeof(CompFragments), nameof(CompFragments.Throw))]
    internal static class FortressMortarFragmentsThrowPatch
    {
        private static void Prefix(CompFragments __instance, out bool __state)
        {
            __state = FortressNonMortarHitScope.ThrowingMortarFrags;
            FortressNonMortarHitScope.ThrowingMortarFrags = __instance.parent is ProjectileCE projectile
                ? FortressNonMortarHitScope.IsMortarProjectile(projectile)
                : FortressNonMortarHitScope.IsMortarWeapon(__instance.parent?.def);
        }

        private static void Finalizer(bool __state) => FortressNonMortarHitScope.ThrowingMortarFrags = __state;
    }

    [HarmonyPatch(typeof(CompFragments), nameof(CompFragments.FragRoutine))]
    internal static class FortressMortarFragmentsRoutinePatch
    {
        private static void Postfix(ref IEnumerator __result)
        {
            if (FortressNonMortarHitScope.ThrowingMortarFrags && __result != null)
                __result = FortressNonMortarHitScope.WrapMortarFragRoutine(__result);
        }
    }

    [HarmonyPatch]
    internal static class FortressMortarFragmentLaunchPatch
    {
        private static IEnumerable<MethodBase> TargetMethods() =>
            AccessTools.GetDeclaredMethods(typeof(ProjectileCE)).Where(m => m.Name == nameof(ProjectileCE.Launch));

        private static void Postfix(ProjectileCE __instance)
        {
            if (FortressNonMortarHitScope.SpawningMortarFrag)
                FortressNonMortarHitScope.MarkMortarFragment(__instance);
        }
    }

    [HarmonyPatch]
    internal static class FortressProjectileMortarHitScopePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.DeclaredMethod(typeof(ProjectileCE), nameof(ProjectileCE.Impact), new[] { typeof(Thing) });
            yield return AccessTools.DeclaredMethod(typeof(BulletCE), nameof(BulletCE.Impact), new[] { typeof(Thing) });
        }

        private static void Prefix(ProjectileCE __instance, out bool __state)
        {
            __state = FortressNonMortarHitScope.InMortarHit;
            if (FortressNonMortarHitScope.IsMortarProjectile(__instance))
                FortressNonMortarHitScope.InMortarHit = true;
        }

        private static void Finalizer(bool __state) => FortressNonMortarHitScope.InMortarHit = __state;
    }

    [HarmonyPatch(typeof(Explosion), "AffectCell")]
    internal static class FortressExplosionMortarHitScopePatch
    {
        private static void Prefix(Explosion __instance, out bool __state)
        {
            __state = FortressNonMortarHitScope.InMortarHit;
            if (FortressNonMortarHitScope.IsMortarWeapon(__instance.weapon) ||
                FortressNonMortarHitScope.IsMortarWeapon(__instance.projectile))
                FortressNonMortarHitScope.InMortarHit = true;
        }

        private static void Finalizer(bool __state) => FortressNonMortarHitScope.InMortarHit = __state;
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.PostApplyDamage))]
    internal static class FortressNonMortarHitPatch
    {
        private static void Postfix(Thing __instance, DamageInfo dinfo, float totalDamageDealt)
        {
            if (totalDamageDealt <= 0f || !FortressTargeting.IsCombatTarget(__instance) ||
                dinfo.Instigator is not Thing instigator || instigator.Faction == null) return;
            Map map = instigator.Map ?? __instance.MapHeld;
            if (map?.Parent is not FortressSite) return;
            foreach (Lord lord in map.lordManager.lords)
            {
                if (lord.faction != instigator.Faction ||
                    lord.LordJob is not IFortressDefenseLord defense || !defense.IsDefenseActive) continue;
                if (defense.HasPendingCounterSortie && lord.faction.HostileTo(__instance.Faction) &&
                    !IsMortarOrOverhead(dinfo, instigator))
                    defense.CancelCounterSortie();
                break;
            }
        }

        private static bool IsMortarOrOverhead(DamageInfo dinfo, Thing instigator)
        {
            if (FortressNonMortarHitScope.InMortarHit || FortressNonMortarHitScope.IsMortarWeapon(dinfo.Weapon))
                return true;
            if (dinfo.Weapon != null) return false;
            if (instigator is Building_Turret turret)
                return turret.def.building?.IsMortar == true || FortressNonMortarHitScope.IsOverheadVerb(turret.AttackVerb);
            if (instigator is Pawn pawn)
            {
                if (pawn.CurJobDef == JobDefOf.ManTurret && pawn.CurJob?.targetA.Thing is Building_Turret manned)
                    return manned.def.building?.IsMortar == true || FortressNonMortarHitScope.IsOverheadVerb(manned.AttackVerb);
                return FortressNonMortarHitScope.IsOverheadVerb(pawn.CurrentEffectiveVerb);
            }
            return false;
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
                if (lord.faction != gun.Faction || lord.LordJob is not IFortressDefenseLord defense ||
                    !defense.IsDefenseActive) continue;
                defense.RequestResupply();
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
