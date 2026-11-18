using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using CombatExtended.AI;
using HarmonyLib;
using RimSiege.Integration;
using RimSiege.Raids.Artillery;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Raids
{
    // Capture the whole incident before MakeLords splits it into squads. Issue
    // kits after duties exist, once for the raid, rather than once per lord.
    [HarmonyPatch(typeof(IncidentWorker_Raid), "TryExecuteWorker")]
    internal static class RaidSpawnPatch
    {
        internal sealed class Spawn
        {
            internal List<Pawn> Pawns;
            internal ArtilleryRaid Artillery;
            internal GroupedArtillery Grouped;
            internal float NativePawnPoints;
            internal float IncidentPoints;
            internal bool EscortMechsInjected;
            internal readonly List<Pawn> InjectedConstructoids = new();
        }
        [ThreadStatic] internal static Spawn Current;

        private static void Prefix(IncidentParms parms, out Spawn __state)
        {
            __state = Current;
            Current = SiegeRaidMap.Enabled ? new Spawn { IncidentPoints = parms.points } : null;
        }

        private static void Postfix(IncidentParms parms, bool __result)
        {
            if (!__result || Current?.Pawns == null || parms.target is not Map map) return;
            Current.Grouped?.CompleteSetup(map);
            if (Current.Artillery != null || parms.raidStrategy.Worker is RaidStrategyWorker_ImmediateAttack ||
                parms.raidStrategy.Worker is RaidStrategyWorker_ImmediateAttackSmart ||
                parms.raidStrategy.Worker is RaidStrategyWorker_StageThenAttack ||
                parms.raidStrategy.Worker is RaidStrategyWorker_ImmediateAttackSappers ||
                parms.raidStrategy.Worker is RaidStrategyWorker_ImmediateAttackBreaching ||
                Current.Pawns.Exists(p => SiegeRoles.Ordinary(p.GetLord()) || SiegeRoles.LadderAssaultDuty(p)))
            {
                bool deferKits = parms.faction.def.humanlikeFaction && parms.faction.def.techLevel >= TechLevel.Industrial &&
                    Current.Grouped == null && Current.Artillery != null;
                SiegeParty party = map.GetComponent<SiegeRaidMap>().Register(Current.Pawns, parms.faction, deferKits);
                if (Current.Artillery == null && Current.Grouped == null)
                    TribalSmokeState.TryInitialize(party, parms, Current.Pawns, Current.IncidentPoints);
                if (Current.Grouped != null)
                {
                    foreach (ArtilleryRaid raid in Current.Grouped.Batteries)
                    {
                        if (party != null) raid.Id = party.Id;
                        ArtilleryDeployment.Begin(raid);
                    }
                }
                else if (Current.Artillery != null)
                {
                    if (party != null) Current.Artillery.Id = party.Id;
                    ArtilleryDeployment.Begin(Current.Artillery);
                }
            }
        }

        private static void Finalizer(Spawn __state) => Current = __state;
    }

    [HarmonyPatch(typeof(IncidentWorker_Raid), nameof(IncidentWorker_Raid.TryGenerateRaidInfo))]
    internal static class RaidRosterPatch
    {
        // Vanilla arrives the roster inside TryGenerateRaidInfo before Postfix.
        // Wrap Arrive so Constructoids are on the list before drop-pods / edge walk-in.
        private static void ArriveWithConstructoids(PawnsArrivalModeWorker worker, List<Pawn> pawns,
            IncidentParms parms)
        {
            ArtilleryRaid raid = RaidSpawnPatch.Current?.Artillery;
            MechBattery.InjectConstructoids(raid, pawns);
            if (raid != null && !ArtilleryRaidPolicy.IsGrouped(parms.raidArrivalMode))
                HumanMechEscorts.InjectUngrouped(raid, pawns, parms.target as Map);
            if (RaidSpawnPatch.Current != null) RaidSpawnPatch.Current.Pawns = pawns;
            worker.Arrive(pawns, parms);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo arrive = AccessTools.Method(typeof(PawnsArrivalModeWorker), nameof(PawnsArrivalModeWorker.Arrive));
            MethodInfo wrap = AccessTools.Method(typeof(RaidRosterPatch), nameof(ArriveWithConstructoids));
            foreach (CodeInstruction code in instructions)
            {
                if (code.Calls(arrive))
                {
                    code.opcode = OpCodes.Call;
                    code.operand = wrap;
                }
                yield return code;
            }
        }

        private static void Postfix(IncidentParms parms, List<Pawn> pawns, bool __result, bool debugTest)
        {
            if (!__result || RaidSpawnPatch.Current == null) return;
            // SpawnThreats path can skip Arrive; inject then land any still-unspawned.
            int before = pawns.Count;
            if (!debugTest)
            {
                MechBattery.InjectConstructoids(RaidSpawnPatch.Current.Artillery, pawns);
                if (!ArtilleryRaidPolicy.IsGrouped(parms?.raidArrivalMode))
                    HumanMechEscorts.InjectUngrouped(RaidSpawnPatch.Current.Artillery, pawns, parms?.target as Map);
            }
            if (pawns.Count > before && parms?.raidArrivalMode != null)
            {
                var late = pawns.Skip(before).Where(p => p != null && !p.Spawned).ToList();
                if (late.Count > 0) parms.raidArrivalMode.Worker.Arrive(late, parms);
            }
            RaidSpawnPatch.Current.Pawns = pawns;
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SpawnSetup))]
    internal static class CarrierSpawnPatch
    {
        private static void Postfix(Pawn __instance, Map map)
        {
            LadderInventory.Restore(__instance);
            SiegeRaidMap raids = map.GetComponent<SiegeRaidMap>();
            SiegeParty party = raids.Party(__instance);
            if (SiegeRaidMap.Enabled && party != null) raids.QueueArrival(__instance);
            if (SiegeRaidMap.Enabled && party?.TribalSmoke is { ReachedDefenses: false } tribal &&
                ArtilleryRetreat.AtPlayerDefenses(__instance))
                tribal.ReachedDefenses = true;
            ArtilleryDeployment.PawnArrived(__instance, map);
            SmokeLoadout.OnPawnRecovered(__instance);
        }
    }

    [HarmonyPatch(typeof(Pawn_HealthTracker), "MakeDowned")]
    internal static class CarrierDownedPatch
    {
        private static void Prefix(Pawn ___pawn, out LadderKit __state) => __state = LadderInventory.Get(___pawn);

        private static void Postfix(Pawn ___pawn, LadderKit __state)
        {
            LadderInventory.Drop(___pawn);
            if (__state?.Spawned == true) __state.SetForbidden(false);
            SmokeLoadout.OnPawnDowned(___pawn);
        }
    }

    [HarmonyPatch(typeof(Pawn_HealthTracker), "MakeUndowned")]
    internal static class CarrierRecoveredPatch
    {
        private static void Postfix(Pawn ___pawn) => SmokeLoadout.OnPawnRecovered(___pawn);
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
    internal static class CarrierDeathPatch
    {
        private static void Prefix(Pawn __instance)
        {
            LadderInventory.Drop(__instance);
            SmokeLoadout.OnPawnKilled(__instance);
        }
    }

    [HarmonyPatch(typeof(Pawn), "get_LabelPrefix")]
    internal static class CarrierLabelPatch
    {
        private static void Postfix(Pawn __instance, ref string __result)
        {
            if (SiegeRaidMap.Enabled && Current.ProgramState == ProgramState.Playing && LadderInventory.Get(__instance) != null &&
                __instance.Faction?.HostileTo(Faction.OfPlayer) == true)
                __result += "RS_LadderCarrier".Translate() + " ";
        }
    }

    [HarmonyPatch(typeof(ThingOwner), "NotifyAdded")]
    internal static class CarrierInventoryAddedPatch
    {
        private static void Postfix(ThingOwner __instance, Thing item)
        {
            if (item is LadderKit kit && __instance.Owner is Pawn_InventoryTracker inventory)
                LadderInventory.Added(inventory.pawn, kit);
        }
    }

    [HarmonyPatch(typeof(ThingOwner), "NotifyRemoved")]
    internal static class CarrierInventoryRemovedPatch
    {
        private static void Postfix(ThingOwner __instance, Thing item)
        {
            if (item is LadderKit kit && __instance.Owner is Pawn_InventoryTracker inventory)
                LadderInventory.Removed(inventory.pawn, kit);
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
    internal static class AssignedGunnerPatch
    {
        private static void Postfix(Pawn ___pawn, Job newJob)
        {
            Pawn pawn = ___pawn;
            if (!SiegeRaidMap.Enabled || pawn.RaceProps?.Humanlike != true || pawn.Map == null || pawn.CurJob != newJob || pawn.CurJobDef != JobDefOf.ManTurret) return;
            ArtilleryMap artillery = pawn.Map.GetComponent<ArtilleryMap>();
            ArtilleryRaid raid = artillery.ForPawn(pawn);
            if (raid != null && pawn.GetLord() == raid.CampLord && pawn.CurJob.targetA.Thing is Building gun && artillery.ForGun(gun) == raid)
            {
                foreach (ArtilleryPlacement slot in raid.Camp.Guns)
                    if (slot.Operator == pawn) slot.Operator = null;
                foreach (ArtilleryPlacement slot in raid.Camp.Guns)
                    if (slot.Building == gun) { slot.Operator = pawn; break; }
            }
            SiegeParty party = pawn.Map.GetComponent<SiegeRaidMap>().Party(pawn);
            if (party != null)
            {
                if (raid == null && !party.Operators.Contains(pawn))
                    party.Operators.Add(pawn);
                LadderInventory.Drop(pawn);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), "CleanupCurrentJob")]
    internal static class ReleasedGunnerPatch
    {
        private static void Prefix(Pawn ___pawn)
        {
            if (___pawn.Map != null && ___pawn.CurJobDef == JobDefOf.ManTurret)
                ___pawn.Map.GetComponent<SiegeRaidMap>().Party(___pawn)?.Operators.Remove(___pawn);
        }
    }

    [HarmonyPatch(typeof(CompTend), nameof(CompTend.TryGiveTacticalJob))]
    internal static class RaidSelfTendSuppressionPatch
    {
        private static bool Prefix(CompTend __instance, ref Job __result)
        {
            if (!IsActiveHostileRaid(__instance.SelPawn)) return true;
            __result = null;
            return false;
        }

        private static bool IsActiveHostileRaid(Pawn pawn)
        {
            if (!SiegeRaidMap.Enabled || pawn is not { Spawned: true, Dead: false, Downed: false, InMentalState: false }) return false;
            if (pawn.Faction == null || !pawn.Faction.HostileTo(Faction.OfPlayer)) return false;
            if (pawn.GetLord() is not { } lord || lord.CurLordToil is not { } toil) return false;
            ArtilleryMap artillery = pawn.Map.GetComponent<ArtilleryMap>();
            ArtilleryRaid raid = artillery.ForPawn(pawn);
            if (toil is LordToil_ArtillerySmoke or LordToil_Siege)
                return raid?.CampLord == lord;
            if (toil is not (LordToil_AssaultColony or LordToil_AssaultColonyBreaching or LordToil_AssaultColonySappers))
                return false;
            return artillery.ForLord(lord) != null || artillery.IncidentForLord(lord) != null ||
                pawn.Map.GetComponent<SiegeRaidMap>().Party(pawn) != null;
        }
    }
}
