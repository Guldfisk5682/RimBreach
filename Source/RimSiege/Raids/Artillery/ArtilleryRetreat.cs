using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using CombatExtended.Compatibility;
using HarmonyLib;
using RimSiege.Integration;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace RimSiege.Raids.Artillery
{
    internal static class ArtilleryRetreat
    {
        private const float BreachedLossFraction = 0.70f;
        private const float ActiveBatteryLossFraction = 0.85f;

        internal static void Review(ArtilleryMap artillery)
        {
            foreach (ArtilleryRaid raid in artillery.TrackedRaids.Where(raid => raid.IncidentKey == 0).ToArray())
            {
                if (ShouldRetreat(raid.Faction, raid.InitialRaidSize, raid.ViolentLosses.Count,
                    raid.ReachedDefenses, raid.Breaches, raid.RaidSeed) is { } loss &&
                    (loss >= ActiveBatteryLossFraction || !HasActiveBattery(raid)))
                    ReviewWave(raid);
            }
            foreach (ArtilleryIncident incident in artillery.TrackedIncidents.ToArray())
            {
                var waves = artillery.TrackedRaids.Where(raid => raid.IncidentKey == incident.Key).ToArray();
                Faction faction = waves.FirstOrDefault()?.Faction ?? incident.NativeLords.FirstOrDefault()?.faction;
                bool breaches = incident.Breaches || waves.Any(w => w.Breaches);
                if (ShouldRetreat(faction, incident.InitialRaidSize, incident.ViolentLosses.Count,
                    incident.ReachedDefenses, breaches, incident.Key) is not { } loss ||
                    (loss < ActiveBatteryLossFraction && waves.Any(HasActiveBattery))) continue;
                foreach (ArtilleryRaid wave in waves) ReviewWave(wave);
                foreach (Lord lord in incident.NativeLords.ToArray()) Review(lord);
            }
        }

        private static bool HasActiveBattery(ArtilleryRaid raid)
        {
            Lord camp = raid?.CampLord;
            if (raid == null || raid.Phase == ArtilleryPhase.Abandoned || raid.Camp?.Guns == null ||
                camp?.CurLordToil is not LordToil_Siege) return false;
            foreach (ArtilleryPlacement gun in raid.Camp.Guns)
            {
                if (!gun.IsSupport || gun.Building is not { Spawned: true, Destroyed: false } building ||
                    building.Faction != raid.Faction ||
                    building.TryGetComp<CompBreakdownable>()?.BrokenDown == true) continue;
                Pawn op = null;
                if (gun.NeedsOperator)
                {
                    op = gun.Operator;
                    if (op == null || op.Dead || op.Downed || !op.Spawned || op.Map != building.Map ||
                        op.InMentalState || op.Faction != raid.Faction || op.GetLord() != camp ||
                        !op.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
                        continue;
                }
                if (building is Building_TurretGunCE turret)
                {
                    CompAmmoUser ammo = turret.GetAmmo();
                    if (ammo != null && ammo.UseAmmo && !ammo.HasAmmoOrMagazine &&
                        (op == null || ArtilleryGunnerPatch.FindAmmo(op, turret) == null))
                        continue;
                }
                return true;
            }
            return false;
        }

        private static float? ShouldRetreat(Faction faction, int initial, int lost, bool reachedDefenses,
            bool breaches, int seed)
        {
            if (faction?.def.humanlikeFaction != true || initial == 0 || lost == 0) return null;
            float threshold = reachedDefenses || breaches ? BreachedLossFraction
                : faction.def.attackersDownPercentageRangeForAutoFlee.RandomInRangeSeeded(seed);
            return lost >= initial * threshold ? (float)lost / initial : null;
        }

        private static void ReviewWave(ArtilleryRaid raid)
        {
            Review(raid.VanguardLord);
            Review(raid.AssaultLord);
            Review(raid.CampLord);
        }

        internal static void ReviewTribal(SiegeRaidMap raids)
        {
            foreach (TribalSmokeState raid in raids.TribalSmokeStates)
            {
                Lord origin = raid.Groups.Select(group => group.Lord).FirstOrDefault(lord => lord != null);
                if (origin == null || !origin.faction.HostileTo(Faction.OfPlayer) || ShouldRetreat(origin.faction, raid.InitialRaidSize,
                    raid.ViolentLosses.Count, raid.ReachedDefenses, false, raid.RetreatSeed) == null) continue;

                foreach (TribalNativeGroup group in raid.Groups)
                {
                    Review(group.Lord);
                    if (group.Lord?.CurLordToil is LordToil_PanicFlee or LordToil_ExitMap) group.End();
                }
            }
        }

        private static void Review(Lord lord)
        {
            if (lord == null || lord.CurLordToil is LordToil_PanicFlee or LordToil_ExitMap) return;
            // Execute the existing panic transition, including its message, cleanup and quest signals.
            lord.Graph.transitions.FirstOrDefault(transition => transition.target is LordToil_PanicFlee &&
                transition.sources.Contains(lord.CurLordToil) &&
                transition.triggers.Any(trigger => trigger is Trigger_FractionPawnsLost))?.Execute(lord);
        }

        [HarmonyPatch(typeof(Transition), nameof(Transition.CheckSignal))]
        private static class WaveLossThreshold
        {
            private static bool Prefix(Transition __instance, Lord lord, TriggerSignal signal)
            {
                if (signal.type != TriggerSignalType.PawnLost || __instance.target is not LordToil_PanicFlee ||
                    __instance.triggers.Count != 1 || __instance.triggers[0] is not Trigger_FractionPawnsLost)
                    return true;
                ArtilleryMap artillery = lord.Map.GetComponent<ArtilleryMap>();
                // The coordinated raid owns casualty retreats from arrival,
                // even while its groups have separate native duty lords.
                return lord.faction?.def.humanlikeFaction != true ||
                    (artillery.IncidentForLord(lord) == null && artillery.ForLord(lord) == null &&
                     lord.Map.GetComponent<SiegeRaidMap>().TribalSmokeFor(lord) == null);
            }
        }

        [HarmonyPatch(typeof(Thing), nameof(Thing.Position), MethodType.Setter)]
        private static class ReachedPlayerDefenses
        {
            private static void Postfix(Thing __instance)
            {
                if (__instance is not Pawn { Spawned: true, Downed: false } pawn ||
                    pawn.Faction?.def.humanlikeFaction != true || !pawn.HostileTo(Faction.OfPlayer)) return;
                Map map = pawn.Map;
                if (!pawn.Position.InBounds(map)) return;
                ArtilleryMap artillery = map.GetComponent<ArtilleryMap>();
                ArtilleryRaid raid = artillery.ForPawn(pawn);
                ArtilleryIncident incident = raid == null ? artillery.IncidentForLord(pawn.GetLord()) :
                    artillery.Incident(raid.IncidentKey);
                TribalSmokeState tribal = SiegeRaidMap.Enabled && raid == null && incident == null
                    ? map.GetComponent<SiegeRaidMap>().Party(pawn)?.TribalSmoke
                    : null;
                bool? reached = incident?.ReachedDefenses ?? raid?.ReachedDefenses ?? tribal?.ReachedDefenses;
                if (reached != false) return;

                if (!AtPlayerDefenses(pawn)) return;

                if (incident != null) incident.ReachedDefenses = true;
                else if (raid != null) raid.ReachedDefenses = true;
                else tribal.ReachedDefenses = true;
            }
        }

        internal static bool AtPlayerDefenses(Pawn pawn) =>
            pawn is { Spawned: true, Downed: false } && pawn.Position.InBounds(pawn.Map) &&
            (pawn.Map.areaManager.Home[pawn.Position] || OnPlayerWall(pawn.Position, pawn.Map));

        private static bool OnPlayerWall(IntVec3 cell, Map map)
        {
            int floor = IsoFloor.FloorOf(cell, map);
            if (floor <= 0) return false;
            IntVec3 below = IsoFloor.CellOnFloor(IsoFloor.GroundCellOf(cell, map), floor - 1, map);
            return below.InBounds(map) && below.GetEdifice(map) is Building wall &&
                wall.Faction == Faction.OfPlayer && (wall.def.IsWall || wall.def.defName == "CE_Embrasure");
        }

        private static readonly AccessTools.FieldRef<LordJob_AssaultColony, bool> Breachers =
            AccessTools.FieldRefAccess<LordJob_AssaultColony, bool>("breachers");

        private static readonly AccessTools.FieldRef<Trigger_FractionPawnsLost, float> LossFraction =
            AccessTools.FieldRefAccess<Trigger_FractionPawnsLost, float>("fraction");

        [HarmonyPatch(typeof(Lord), nameof(Lord.SetJob))]
        private static class BreacherLossThreshold
        {
            private static void Postfix(Lord __instance, LordJob lordJob)
            {
                if (!SiegeRaidMap.Enabled || __instance.faction?.IsPlayer != false || !__instance.faction.def.humanlikeFaction ||
                    (Faction.OfPlayer != null && !__instance.faction.HostileTo(Faction.OfPlayer)) ||
                    lordJob is not LordJob_AssaultColony assault || !Breachers(assault) ||
                    __instance.Graph?.transitions == null)
                    return;
                foreach (Transition transition in __instance.Graph.transitions)
                {
                    if (transition.target is not LordToil_PanicFlee) continue;
                    foreach (Trigger trigger in transition.triggers)
                        if (trigger is Trigger_FractionPawnsLost lost)
                            LossFraction(lost) = BreachedLossFraction;
                }
            }
        }

        [HarmonyPatch(typeof(Lord), nameof(Lord.Notify_PawnLost))]
        private static class CountRaidLoss
        {
            private static void Prefix(Lord __instance, Pawn pawn, PawnLostCondition cond,
                out HashSet<int> __state)
            {
                __state = null;
                if (__instance.faction?.def.humanlikeFaction != true ||
                    cond is not (PawnLostCondition.Incapped or PawnLostCondition.Killed or PawnLostCondition.MadePrisoner) ||
                    !__instance.ownedPawns.Contains(pawn)) return;
                ArtilleryMap artillery = __instance.Map.GetComponent<ArtilleryMap>();
                __state = artillery.IncidentForLord(__instance)?.ViolentLosses ?? artillery.ForPawn(pawn)?.ViolentLosses ??
                    __instance.Map.GetComponent<SiegeRaidMap>().TribalSmokeFor(__instance)?.ViolentLosses;
            }

            private static void Postfix(Lord __instance, Pawn pawn, HashSet<int> __state)
            {
                if (__state != null && !__instance.ownedPawns.Contains(pawn))
                    __state.Add(pawn.thingIDNumber);
            }
        }
    }
}
