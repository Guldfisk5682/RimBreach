using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using CombatExtended.AI;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Raids.Artillery
{
    public sealed class JobDriver_LayArtillerySmoke : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            yield return Toils_General.Do(() =>
            {
                if (pawn.equipment.Primary?.def != ArtillerySmokeDefOf.CE_Weapon_GrenadeSmoke &&
                    job.targetB.Thing is ThingWithComps grenade)
                    SmokeLoadout.EquipGrenade(pawn, grenade);
                if (pawn.equipment.Primary?.def != ArtillerySmokeDefOf.CE_Weapon_GrenadeSmoke ||
                    pawn.equipment.Primary.TryGetComp<CompEquippable>()?.PrimaryVerb is not Verb verb)
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                pawn.pather.StopDead();
                job.verbToUse = verb;
            });
            yield return Toils_Combat.CastVerb(TargetIndex.A);
        }
    }

    internal static class SmokeLoadout
    {
        internal static bool IsSmokeLocked(Pawn pawn) =>
            pawn?.mindState?.duty?.def == ArtillerySmokeDefOf.RS_ArtillerySmoke &&
            pawn.GetLord()?.LordJob is LordJob_Artillery job &&
            (job.Raid.Smoke ?? job.Raid.OptionalSmoke)?.HasCarrier(pawn) == true;

        internal static void EquipGrenade(Pawn pawn, ThingWithComps grenade)
        {
            if (pawn.GetLord()?.LordJob is not LordJob_Artillery job ||
                (job.Raid.Smoke ?? job.Raid.OptionalSmoke)?.AssignmentFor(pawn) is not { Fighting: false } assignment)
                return;
            ThingWithComps original = pawn.equipment.Primary;
            if (original != null && original.def != ArtillerySmokeDefOf.CE_Weapon_GrenadeSmoke)
                assignment.OriginalWeapon = original;
            if (!ArtilleryWeaponSwitch.Exchange(pawn, grenade))
            {
                assignment.Fighting = true;
                Restore(assignment);
            }
        }

        internal static void Restore(SmokeAssignment assignment)
        {
            Pawn pawn = assignment.Pawn;
            if (assignment.LoadoutRestored) return;
            if (pawn == null || pawn.Dead)
            {
                assignment.LoadoutRestored = true;
                assignment.OriginalWeapon = null;
                return;
            }
            if (!pawn.Spawned || pawn.Downed) return;
            assignment.LoadoutRestored = true;
            ThingWithComps original = assignment.OriginalWeapon;
            assignment.OriginalWeapon = null;
            var inventory = pawn.TryGetComp<CompInventory>();
            if (inventory == null) return;
            ThingWithComps primary = pawn.equipment.Primary;
            if (primary != null && primary.def != ArtillerySmokeDefOf.CE_Weapon_GrenadeSmoke) return;
            if (original is { Destroyed: false } && inventory.container.Contains(original) &&
                original.TryGetComp<CompAmmoUser>()?.HasAndUsesAmmoOrMagazine != false &&
                EquipmentUtility.CanEquip(original, pawn) && ArtilleryWeaponSwitch.Exchange(pawn, original)) return;
            if (primary != null && !ArtilleryWeaponSwitch.Exchange(pawn, null)) return;
            if (FindFallbackWeapon(pawn, inventory) is { } alternate)
                ArtilleryWeaponSwitch.Exchange(pawn, alternate);
        }

        private static ThingWithComps FindFallbackWeapon(Pawn pawn, CompInventory inventory)
        {
            ThingWithComps aoeFallback = null;
            if (inventory.rangedWeaponList != null)
            {
                foreach (ThingWithComps gun in inventory.rangedWeaponList)
                {
                    if (gun == null || gun.Destroyed || gun.def == ArtillerySmokeDefOf.CE_Weapon_GrenadeSmoke ||
                        gun.def.IsIlluminationDevice() || !inventory.container.Contains(gun) ||
                        !EquipmentUtility.CanEquip(gun, pawn)) continue;
                    if (gun.TryGetComp<CompAmmoUser>() is { } ammo && !ammo.HasAndUsesAmmoOrMagazine) continue;
                    if (!gun.def.IsAOEWeapon()) return gun;
                    aoeFallback ??= gun;
                }
            }
            if (aoeFallback != null) return aoeFallback;
            return inventory.meleeWeaponList?.FirstOrDefault(weapon =>
                weapon != null && !weapon.Destroyed && inventory.container.Contains(weapon) &&
                EquipmentUtility.CanEquip(weapon, pawn));
        }

        [HarmonyPatch(typeof(CompOpportunisticSwitch), nameof(CompOpportunisticSwitch.StartCastChecks))]
        private static class KeepSmokeCast
        {
            private static bool Prefix(CompOpportunisticSwitch __instance, Verb verb, ref bool __result)
            {
                if (__instance.SelPawn?.CurJobDef != ArtillerySmokeDefOf.RS_LayArtillerySmoke ||
                    verb.EquipmentSource?.def != ArtillerySmokeDefOf.CE_Weapon_GrenadeSmoke) return true;
                // CE's opportunistic attack job equips directly and can interrupt the smoke throw.
                __result = true;
                return false;
            }
        }

        [HarmonyPatch(typeof(Verb_ShootCEOneUse), "SelfConsume")]
        private static class RetainSmokeLoadout
        {
            private static bool Prefix(Verb_ShootCEOneUse __instance)
            {
                if (__instance.Caster is not Pawn pawn || pawn.CurJobDef != ArtillerySmokeDefOf.RS_LayArtillerySmoke ||
                    __instance.EquipmentSource is not Thing grenade ||
                    grenade.def != ArtillerySmokeDefOf.CE_Weapon_GrenadeSmoke) return true;
                // Keep CE's one-use consumption; the smoke duty chooses the next grenade.
                if (!grenade.Destroyed) ArtilleryWeaponSwitch.Consume(pawn, grenade);
                return false;
            }
        }
    }

    internal static class ArtilleryWeaponSwitch
    {
        // Equipment notifications can re-enter CE before the transfer finishes.
        [System.ThreadStatic] private static Pawn switching;
        [System.ThreadStatic] private static ThingWithComps requested;

        internal static bool InProgress(Pawn pawn) => switching == pawn;

        internal static bool Exchange(Pawn pawn, ThingWithComps weapon)
        {
            var inventory = pawn.TryGetComp<CompInventory>();
            if (inventory == null || (weapon != null && !inventory.container.Contains(weapon))) return false;
            Pawn previous = switching;
            ThingWithComps previousRequest = requested;
            switching = pawn;
            requested = null;
            try
            {
                ThingWithComps held = pawn.equipment.Primary;
                if (held != null && !pawn.equipment.TryTransferEquipmentToContainer(held, inventory.container))
                    return false;
                if (weapon == null) return true;
                requested = weapon;
                inventory.TrySwitchToWeapon(weapon, stopJob: false);
                return pawn.equipment.Primary?.def == weapon.def;
            }
            finally { switching = previous; requested = previousRequest; }
        }

        internal static void Consume(Pawn pawn, Thing grenade)
        {
            Pawn previous = switching;
            ThingWithComps previousRequest = requested;
            switching = pawn;
            requested = null;
            try { grenade.Destroy(DestroyMode.Vanish); }
            finally { switching = previous; requested = previousRequest; }
        }

        [HarmonyPatch(typeof(CompInventory), nameof(CompInventory.TrySwitchToWeapon))]
        private static class Equip
        {
            private static bool Prefix(CompInventory __instance, ThingWithComps newEq)
            {
                if (__instance.parent is not Pawn pawn) return true;
                if (switching == pawn)
                {
                    if (requested == null || requested != newEq) return false;
                    requested = null;
                    return true;
                }
                return !SmokeLoadout.IsSmokeLocked(pawn);
            }
        }

        [HarmonyPatch(typeof(CompInventory), nameof(CompInventory.SwitchToNextViableWeapon))]
        private static class AutoSwitch
        {
            private static bool Prefix(CompInventory __instance, ref bool __result)
            {
                if (__instance.parent is not Pawn pawn) return true;
                if (switching == pawn) return false;
                if (!SmokeLoadout.IsSmokeLocked(pawn)) return true;
                // Report handled so CE's urgent weapon pickup does not scan the floor mid-smoke.
                __result = true;
                return false;
            }
        }
    }

    public sealed class JobGiver_ArtillerySmokeCombat : JobGiver_AIFightEnemies
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (pawn?.RaceProps?.Humanlike != true ||
                pawn.GetLord()?.LordJob is not LordJob_Artillery job ||
                (job.Raid.Smoke ?? job.Raid.OptionalSmoke)?.AssignmentFor(pawn) is not { } assignment ||
                !assignment.Fighting) return null;
            return base.TryGiveJob(pawn);
        }
    }
}
