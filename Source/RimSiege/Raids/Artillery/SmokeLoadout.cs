using System.Collections.Generic;
using CombatExtended;
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
                var inventory = pawn.TryGetComp<CompInventory>();
                if (pawn.equipment.Primary?.def != ArtillerySmokeDefOf.CE_Weapon_GrenadeSmoke &&
                    job.targetB.Thing is ThingWithComps grenade && inventory.container.Contains(grenade))
                    inventory.TrySwitchToWeapon(grenade, stopJob: false);
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
        internal static void Restore(SmokeAssignment assignment)
        {
            Pawn pawn = assignment.Pawn;
            if (pawn is not { Spawned: true, Dead: false, Downed: false }) return;
            var inventory = pawn.TryGetComp<CompInventory>();
            if (assignment.OriginalWeapon is { Destroyed: false } weapon &&
                inventory?.container.Contains(weapon) == true)
                inventory.TrySwitchToWeapon(weapon, stopJob: false);
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
                if (!grenade.Destroyed) grenade.Destroy(DestroyMode.Vanish);
                return false;
            }
        }
    }

    [HarmonyPatch(typeof(CompInventory), nameof(CompInventory.TrySwitchToWeapon))]
    internal static class ArtilleryWeaponSwitch
    {
        // Unequipping can finish an attack job and run the constant think tree
        // before CE has taken the replacement weapon out of the inventory.
        private static readonly HashSet<Pawn> Switching = new();

        internal static bool InProgress(Pawn pawn) => Switching.Contains(pawn);

        private static bool Prefix(CompInventory __instance, out Pawn __state)
        {
            __state = null;
            if (__instance.parent is not Pawn { Spawned: true } pawn) return true;
            if (InProgress(pawn)) return false;
            if (pawn.Map.GetComponent<ArtilleryMap>().ForPawn(pawn) == null) return true;
            Switching.Add(pawn);
            __state = pawn;
            return true;
        }

        private static void Finalizer(Pawn __state)
        {
            if (__state != null) Switching.Remove(__state);
        }
    }

    public sealed class JobGiver_ArtillerySmokeCombat : JobGiver_AIFightEnemies
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (pawn.GetLord()?.LordJob is not LordJob_Artillery job ||
                (job.Raid.Smoke ?? job.Raid.OptionalSmoke)?.AssignmentFor(pawn) is not { } assignment ||
                !assignment.Fighting) return null;
            return base.TryGiveJob(pawn);
        }
    }
}
