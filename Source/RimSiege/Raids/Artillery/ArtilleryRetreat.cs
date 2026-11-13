using System.Collections.Generic;
using System.Linq;
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

        internal static void Review(ArtilleryMap artillery)
        {
            foreach (ArtilleryRaid raid in artillery.TrackedRaids.Where(raid => raid.IncidentKey == 0).ToArray())
            {
                if (ShouldRetreat(raid.Faction, raid.InitialRaidSize, raid.ViolentLosses.Count, raid.ReachedDefenses, raid.RaidSeed))
                    ReviewWave(raid);
            }
            foreach (ArtilleryIncident incident in artillery.TrackedIncidents.ToArray())
            {
                var waves = artillery.TrackedRaids.Where(raid => raid.IncidentKey == incident.Key).ToArray();
                Faction faction = waves.FirstOrDefault()?.Faction ?? incident.NativeLords.FirstOrDefault()?.faction;
                if (!ShouldRetreat(faction, incident.InitialRaidSize, incident.ViolentLosses.Count,
                    incident.ReachedDefenses, incident.Key)) continue;
                foreach (ArtilleryRaid wave in waves) ReviewWave(wave);
                foreach (Lord lord in incident.NativeLords.ToArray()) Review(lord);
            }
        }

        private static bool ShouldRetreat(Faction faction, int initial, int lost, bool reachedDefenses, int seed)
        {
            if (faction?.def.humanlikeFaction != true || initial == 0 || lost == 0) return false;
            float fraction = reachedDefenses ? BreachedLossFraction :
                faction.def.attackersDownPercentageRangeForAutoFlee.RandomInRangeSeeded(seed);
            return lost >= initial * fraction;
        }

        private static void ReviewWave(ArtilleryRaid raid)
        {
            Review(raid.VanguardLord);
            Review(raid.AssaultLord);
            Review(raid.CampLord);
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
                    (artillery.IncidentForLord(lord) == null && artillery.ForLord(lord) == null);
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
                if (incident?.ReachedDefenses == true || incident == null && (raid == null || raid.ReachedDefenses)) return;

                IntVec3 cell = pawn.Position;
                if (!map.areaManager.Home[cell] && !OnPlayerWall(cell, map)) return;

                if (incident != null) incident.ReachedDefenses = true;
                else raid.ReachedDefenses = true;
            }
        }

        private static bool OnPlayerWall(IntVec3 cell, Map map)
        {
            int floor = IsoFloor.FloorOf(cell, map);
            if (floor <= 0) return false;
            IntVec3 below = IsoFloor.CellOnFloor(IsoFloor.GroundCellOf(cell, map), floor - 1, map);
            return below.InBounds(map) && below.GetEdifice(map) is Building wall &&
                wall.Faction == Faction.OfPlayer && (wall.def.IsWall || wall.def.defName == "CE_Embrasure");
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
                __state = artillery.IncidentForLord(__instance)?.ViolentLosses ?? artillery.ForPawn(pawn)?.ViolentLosses;
            }

            private static void Postfix(Lord __instance, Pawn pawn, HashSet<int> __state)
            {
                if (__state != null && !__instance.ownedPawns.Contains(pawn))
                    __state.Add(pawn.thingIDNumber);
            }
        }
    }
}
