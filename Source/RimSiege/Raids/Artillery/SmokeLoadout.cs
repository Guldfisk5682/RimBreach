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
        private bool castStarted;
        private bool castLaunched;
        private bool castRejected;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        internal void MarkCastLaunched() => castLaunched = true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref castStarted, "castStarted");
            Scribe_Values.Look(ref castLaunched, "castLaunched");
            Scribe_Values.Look(ref castRejected, "castRejected");
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            AddFinishAction(condition =>
            {
                if (!castLaunched && !castRejected)
                {
                    if (castStarted && condition == JobCondition.Succeeded)
                        SmokeLoadout.OnThrowRejected(pawn, job.targetA.Cell);
                    else
                        SmokeLoadout.ReleaseThrowClaim(pawn, job.targetA.Cell);
                }
                SmokeLoadout.FinishLocalThrow(pawn);
            });

            yield return Toils_General.Do(() =>
            {
                if (!SmokeLoadout.TryClaim(pawn, job.targetA.Cell, 120, throwing: true))
                {
                    castRejected = true;
                    pawn.jobs.EndCurrentJob(JobCondition.Incompletable, startNewJob: false);
                    return;
                }

                ThingWithComps smokeWeapon = job.targetB.Thing as ThingWithComps;
                if ((smokeWeapon == null || smokeWeapon.Destroyed) &&
                    pawn.equipment?.Primary is { Destroyed: false } equipped &&
                    SmokeCombat.IsSmokeWeapon(equipped.def))
                    smokeWeapon = equipped;
                ThingDef expectedDef = smokeWeapon?.def;
                if (expectedDef == null || !SmokeCombat.IsSmokeWeapon(expectedDef))
                {
                    castRejected = true;
                    SmokeLoadout.OnThrowRejected(pawn, job.targetA.Cell);
                    pawn.jobs.EndCurrentJob(JobCondition.Incompletable, startNewJob: false);
                    return;
                }
                if (pawn.equipment?.Primary?.def != expectedDef)
                    SmokeLoadout.EquipGrenade(pawn, smokeWeapon);
                if (pawn.equipment?.Primary?.def != expectedDef ||
                    pawn.equipment.Primary.TryGetComp<CompEquippable>()?.PrimaryVerb is not Verb verb)
                {
                    castRejected = true;
                    SmokeLoadout.OnThrowRejected(pawn, job.targetA.Cell);
                    pawn.jobs.EndCurrentJob(JobCondition.Incompletable, startNewJob: false);
                    return;
                }
                pawn.pather.StopDead();
                job.verbToUse = verb;
            });

            Toil cast = Toils_Combat.CastVerb(TargetIndex.A);
            cast.initAction = () =>
            {
                bool accepted = job.verbToUse.TryStartCastOn(job.targetA, LocalTargetInfo.Invalid,
                    surpriseAttack: false, canHitNonTargetPawns: true, preventFriendlyFire: job.preventFriendlyFire);
                if (pawn.jobs.curDriver != this) return;
                castStarted = accepted;
                if (castStarted) return;

                castRejected = true;
                SmokeLoadout.OnThrowRejected(pawn, job.targetA.Cell);
                pawn.jobs.EndCurrentJob(JobCondition.Incompletable, startNewJob: false);
            };
            yield return cast;
        }
    }

    public sealed class JobDriver_ApproachSmokeThrow : JobDriver_Goto
    {
        protected override IEnumerable<Toil> MakeNewToils()
        {
            AddFinishAction(condition => SmokeLoadout.ReleaseMoveClaim(pawn, job.targetC.Cell, condition));
            yield return Toils_General.Do(() =>
            {
                if (!SmokeLoadout.TryClaim(pawn, job.targetC.Cell, job.expiryInterval + 15,
                    throwing: false))
                    pawn.jobs.EndCurrentJob(JobCondition.Incompletable, startNewJob: false);
            });
            foreach (Toil toil in base.MakeNewToils())
                yield return toil;
        }
    }

    internal static class SmokeLoadout
    {
        private static ArtillerySmoke ResolveArtilleryScreen(Pawn pawn)
        {
            if (pawn?.GetLord()?.LordJob is LordJob_Artillery job)
                return job.Raid.SmokeFor(pawn);
            ArtilleryRaid raid = pawn?.MapHeld?.GetComponent<ArtilleryMap>()?.ForPawn(pawn);
            return raid?.SmokeFor(pawn);
        }

        private static ArtillerySmoke ResolveScreen(Pawn pawn) =>
            ResolveArtilleryScreen(pawn) ?? ResolveTribalScreen(pawn);

        private static ArtillerySmoke ResolveTribalScreen(Pawn pawn) =>
            pawn.MapHeld?.GetComponent<SiegeRaidMap>()?.Party(pawn)?.TribalSmoke?.ScreenForAssignedPawn(pawn);

        internal static ArtillerySmoke ActiveScreen(Pawn pawn)
        {
            if (pawn == null) return null;
            if (pawn.GetLord()?.LordJob is LordJob_ArtilleryMechEscort escort &&
                pawn.mindState?.duty?.def == ArtillerySmokeDefOf.RS_ArtilleryMechSmokeEscort)
                return escort.Raid?.Smoke;
            if (pawn.GetLord()?.LordJob is LordJob_Artillery job)
            {
                DutyDef duty = pawn.mindState?.duty?.def;
                if (pawn.RaceProps.Humanlike)
                {
                    if (duty == ArtillerySmokeDefOf.RS_ArtillerySmoke) return job.Raid.Smoke ?? job.Raid.OptionalSmoke;
                    return job.Raid.ActiveLocalSmokeFor(pawn)?.Work;
                }
                if (SiegeRoles.IsHumanRaidEscortMech(pawn) && job.Raid?.Kind != ArtilleryRaidKind.Mechanoid &&
                    duty == ArtillerySmokeDefOf.RS_ArtilleryMechSmokeEscort)
                    return job.Raid.Smoke ?? job.Raid.InfantryHold?.MechScreen ?? job.Raid.OptionalSmoke;
                return null;
            }
            if (!pawn.RaceProps.Humanlike) return null;
            TribalNativeGroup group = TribalNativeGroup.For(pawn);
            return group?.Includes(pawn) == true ? group.Screen : null;
        }

        internal static bool IsSmokeLocked(Pawn pawn) =>
            pawn is { Dead: false, Downed: false } && ActiveScreen(pawn) is { } screen && screen.HasCarrier(pawn) &&
            (!screen.IsLocal || pawn.CurJobDef == ArtillerySmokeDefOf.RS_LayArtillerySmoke ||
             pawn.CurJobDef == ArtillerySmokeDefOf.RS_ApproachSmokeThrow);

        internal static bool TryClaim(Pawn pawn, IntVec3 targetCell, int duration, bool throwing)
        {
            ArtillerySmoke screen = ActiveScreen(pawn);
            if (pawn.Map is not Map map || screen?.StartedScreen != true || !screen.HasCarrier(pawn) ||
                !targetCell.InBounds(map) || !ArtillerySmoke.Ready(pawn)) return false;

            int now = GenTicks.TicksGame;
            if (!screen.IsLocal && TribalNativeGroup.For(pawn) == null && ArtillerySmoke.IsInCombat(pawn, now)) return false;

            float range = screen.ThrowRange > 0f ? screen.ThrowRange : ArtillerySmoke.DefaultThrowRange;
            if (throwing && (!screen.CanThrowFrom(pawn.Position, map) ||
                !SmokeCombat.CanThrowNow(pawn.Position, targetCell, map, range))) return false;
            return screen.TryClaim(pawn, targetCell, duration);
        }

        internal static void ReleaseMoveClaim(Pawn pawn, IntVec3 targetCell, JobCondition condition)
        {
            if (pawn == null || !targetCell.IsValid) return;
            int tightenTicks = condition == JobCondition.Succeeded ? 15 : 0;
            ResolveScreen(pawn)?.ReleaseClaim(pawn, targetCell, tightenTicks);
        }

        internal static void ReleaseThrowClaim(Pawn pawn, IntVec3 targetCell) =>
            ResolveScreen(pawn)?.ReleaseClaim(pawn, targetCell, 0);

        internal static void OnThrowRejected(Pawn pawn, IntVec3 targetCell) =>
            ResolveScreen(pawn)?.OnThrowRejected(pawn, targetCell);

        private static void NotifySmokeThrown(Pawn pawn, IntVec3 targetCell, int graceTicks) =>
            ResolveScreen(pawn)?.NotifySmokeThrown(pawn, targetCell, graceTicks);

        internal static void FinishLocalThrow(Pawn pawn)
        {
            if (ResolveArtilleryScreen(pawn) is { IsLocal: true } screen)
                Restore(screen.AssignmentFor(pawn));
        }

        internal static void EquipGrenade(Pawn pawn, ThingWithComps grenade)
        {
            if (grenade == null || ActiveScreen(pawn)?.AssignmentFor(pawn) is not { Fighting: false } assignment)
                return;
            ThingWithComps original = pawn.equipment?.Primary;
            if (original != null && !SmokeCombat.IsSmokeWeapon(original.def))
            {
                assignment.OriginalWeapon = original;
                pawn.MapHeld?.GetComponent<SiegeRaidMap>()?.TrackPendingSmoke(pawn, original);
            }
            assignment.LoadoutRestored = false;
            if (!ArtilleryWeaponSwitch.Exchange(pawn, grenade))
            {
                assignment.Fighting = true;
                Restore(assignment);
            }
        }

        internal static void OnPawnDowned(Pawn pawn)
        {
            if (pawn == null) return;
            ArtilleryRaid raid = pawn.MapHeld?.GetComponent<ArtilleryMap>()?.ForPawn(pawn);
            if (raid != null)
            {
                raid.Smoke?.OnPawnDowned(pawn);
                raid.OptionalSmoke?.OnPawnDowned(pawn);
                raid.LocalSmokeFor(pawn)?.Work.OnPawnDowned(pawn);
            }
            ResolveTribalScreen(pawn)?.OnPawnDowned(pawn);
        }

        internal static void OnPawnKilled(Pawn pawn)
        {
            if (pawn == null) return;
            ArtilleryRaid raid = pawn.MapHeld?.GetComponent<ArtilleryMap>()?.ForPawn(pawn);
            if (raid != null)
            {
                raid.Smoke?.OnPawnKilled(pawn);
                raid.OptionalSmoke?.OnPawnKilled(pawn);
                raid.LocalSmokeFor(pawn)?.Work.OnPawnKilled(pawn);
            }
            SiegeRaidMap raids = pawn.MapHeld?.GetComponent<SiegeRaidMap>();
            if (raids != null)
            {
                ResolveTribalScreen(pawn)?.OnPawnKilled(pawn);
                raids.ClearPendingSmoke(pawn);
            }
        }

        internal static void OnPawnRecovered(Pawn pawn)
        {
            if (pawn == null) return;
            SiegeRaidMap raids = pawn.MapHeld?.GetComponent<SiegeRaidMap>();
            if (raids != null && !SiegeRaidMap.StillApplicableForRestore(pawn))
            {
                InvalidateAssignments(pawn, raids);
                return;
            }
            if (!pawn.Spawned || pawn.Dead || pawn.Downed) return;

            if (ResolveArtilleryScreen(pawn) is { } screen &&
                screen.AssignmentFor(pawn) is { LoadoutRestored: false } artilleryAssignment &&
                (pawn.GetLord()?.LordJob is not LordJob_Artillery || !screen.HasCarrier(pawn) ||
                 ActiveScreen(pawn) != screen))
            {
                artilleryAssignment.Fighting = true;
                Restore(artilleryAssignment);
            }
            if (!IsSmokeLocked(pawn) && raids?.PendingSmokeFor(pawn) is { LoadoutRestored: false } pending)
                Restore(pending);
        }

        private static void InvalidateAssignments(Pawn pawn, SiegeRaidMap raids)
        {
            raids.ClearPendingSmoke(pawn);
            ArtilleryRaid raid = pawn.MapHeld?.GetComponent<ArtilleryMap>()?.ForPawn(pawn);
            void ClearAssignment(SmokeAssignment a)
            {
                if (a == null) return;
                a.Fighting = true;
                a.LoadoutRestored = true;
                a.OriginalWeapon = null;
            }
            ClearAssignment(raid?.Smoke?.AssignmentFor(pawn));
            ClearAssignment(raid?.OptionalSmoke?.AssignmentFor(pawn));
            ClearAssignment(raid?.LocalSmokeFor(pawn)?.Work.AssignmentFor(pawn));
            ClearAssignment(ResolveTribalScreen(pawn)?.AssignmentFor(pawn));
        }

        internal static void Restore(SmokeAssignment assignment)
        {
            if (assignment == null || assignment.LoadoutRestored) return;
            Pawn pawn = assignment.Pawn;
            if (!SiegeRaidMap.StillApplicableForRestore(pawn))
            {
                assignment.LoadoutRestored = true;
                assignment.OriginalWeapon = null;
                pawn?.MapHeld?.GetComponent<SiegeRaidMap>()?.ClearPendingSmoke(pawn);
                return;
            }
            if (!pawn.Spawned || pawn.Downed) return;
            assignment.LoadoutRestored = true;
            ThingWithComps original = assignment.OriginalWeapon;
            assignment.OriginalWeapon = null;
            pawn.MapHeld?.GetComponent<SiegeRaidMap>()?.ClearPendingSmoke(pawn);
            var inventory = pawn.TryGetComp<CompInventory>();
            if (inventory == null) return;
            ThingWithComps primary = pawn.equipment.Primary;
            if (primary != null && !SmokeCombat.IsSmokeWeapon(primary.def)) return;
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
                    if (gun == null || gun.Destroyed || SmokeCombat.IsSmokeWeapon(gun.def) ||
                        gun.def.IsIlluminationDevice() || !inventory.container.Contains(gun) ||
                        !EquipmentUtility.CanEquip(gun, pawn)) continue;
                    if (gun.TryGetComp<CompAmmoUser>() is { } ammo && !ammo.HasAndUsesAmmoOrMagazine) continue;
                    if (!gun.def.IsAOEWeapon()) return gun;
                    aoeFallback ??= gun;
                }
            }
            if (aoeFallback != null) return aoeFallback;
            return inventory.meleeWeaponList?.FirstOrDefault(weapon =>
                weapon != null && !weapon.Destroyed && !SmokeCombat.IsSmokeWeapon(weapon.def) &&
                inventory.container.Contains(weapon) && EquipmentUtility.CanEquip(weapon, pawn));
        }

        [HarmonyPatch(typeof(CompOpportunisticSwitch), nameof(CompOpportunisticSwitch.StartCastChecks))]
        private static class KeepSmokeCast
        {
            private static bool Prefix(CompOpportunisticSwitch __instance, Verb verb, ref bool __result)
            {
                if (__instance.SelPawn?.CurJobDef != ArtillerySmokeDefOf.RS_LayArtillerySmoke ||
                    !SmokeCombat.IsSmokeWeapon(verb.EquipmentSource?.def)) return true;
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
                    !SmokeCombat.IsSmokeWeapon(grenade.def)) return true;
                // Keep CE's one-use consumption; the smoke duty chooses the next grenade.
                if (!grenade.Destroyed) ArtilleryWeaponSwitch.Consume(pawn, grenade);
                return false;
            }
        }

        [HarmonyPatch(typeof(Verb_ShootCEOneUse), nameof(Verb_ShootCEOneUse.TryCastShot))]
        private static class RecordSmokeShot
        {
            private static void Prefix(Verb_ShootCEOneUse __instance,
                out (JobDriver_LayArtillerySmoke Driver, IntVec3 Cell, int Grace) __state)
            {
                __state = default;
                if (__instance.Caster is not Pawn { Map: { } map } pawn ||
                    pawn.jobs.curDriver is not JobDriver_LayArtillerySmoke driver ||
                    driver.job.verbToUse != __instance ||
                    !SmokeCombat.IsSmokeWeapon(__instance.EquipmentSource?.def)) return;
                IntVec3 cell = driver.job.targetA.Cell;
                ThingDef weapon = __instance.EquipmentSource.def;
                __state = (driver, cell, SmokeCombat.FlightGraceTicks(pawn.Position, cell, map, __instance, weapon));
            }

            private static void Postfix(bool __result,
                (JobDriver_LayArtillerySmoke Driver, IntVec3 Cell, int Grace) __state)
            {
                if (!__result || __state.Driver == null) return;
                __state.Driver.MarkCastLaunched();
                NotifySmokeThrown(__state.Driver.pawn, __state.Cell, __state.Grace);
            }
        }

        [HarmonyPatch(typeof(JobGiver_TakeAndEquip), "TryGiveJob")]
        private static class BlockEquipWhileSmoking
        {
            private static bool Prefix(Pawn pawn) =>
                !ArtilleryWeaponSwitch.InProgress(pawn) && !IsSmokeLocked(pawn);
        }

        [HarmonyPatch(typeof(JobGiver_PickUpOpportunisticWeapon), "TryGiveJob")]
        private static class BlockOpportunisticPickupWhileSmoking
        {
            private static bool Prefix(Pawn pawn) =>
                !ArtilleryWeaponSwitch.InProgress(pawn) && !IsSmokeLocked(pawn);
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
            try
            {
                if (grenade.stackCount > 1) grenade.SplitOff(1).Destroy(DestroyMode.Vanish);
                else grenade.Destroy(DestroyMode.Vanish);
            }
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
