using System.Collections.Generic;
using System.Linq;
using RimSiege.Integration;
using RimSiege.Raids.Artillery;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Raids.Encirclement
{
    public sealed partial class QuestPart_Encirclement
    {
        private readonly Dictionary<Pawn, int> forwardBuilders = new();
        private readonly Dictionary<Pawn, Pawn> mechEscorts = new();
        private HashSet<Pawn> restingCrew = new();

        internal bool UsesForwardCrews => Phase == 2 || Phase == 3 && Mechanoid;
        internal int BuilderBattery(Pawn pawn) => UsesForwardCrews ? forwardBuilders.GetValueOrDefault(pawn) : 0;
        internal Pawn Escortee(Pawn pawn) => mechEscorts.GetValueOrDefault(pawn);
        internal bool IsRestingCrew(Pawn pawn) => Phase == 2 && restingCrew.Contains(pawn);
        internal int ForwardBuilderCount => forwardBuilders.Count;

        internal IntVec3 RearStation(Pawn pawn)
        {
            var stations = Plan.RearStations;
            return stations.Count == 0 ? Plan.Rear : stations[pawn.thingIDNumber % stations.Count];
        }

        internal bool NearRear(IntVec3 position, float margin)
        {
            IntVec3 ground = IsoFloor.GroundCellOf(position, Map);
            return ground.InHorDistOf(Plan.Rear, margin) ||
                Plan.Rooms.Any(r => r.Footprint.ClosestDistSquaredTo(ground) <= margin * margin);
        }

        internal void AlertAt(IntVec3 position)
        {
            // Contact at a forward battery is handled by its own crew's combat duties.
            if (Phase != 2 || NearRear(position, 12f)) Alert();
        }

        internal int ClearingBattery(Pawn pawn)
        {
            if (Phase is not (1 or 2) || pawn.CurJobDef != JobDefOf.CutPlant ||
                pawn.CurJob.workGiverDef?.workType != WorkTypeDefOf.Construction ||
                pawn.CurJob.targetA.Thing is not Plant { Spawned: true } plant ||
                !LordJob_EncirclementCamp.Builder(pawn) || plant.Map != pawn.Map ||
                pawn.health.summaryHealth.SummaryHealthPercent < 0.78f ||
                pawn.needs?.rest?.CurLevelPercentage < 0.15f || pawn.needs?.food?.CurLevelPercentage < 0.15f)
                return -1;
            return Work.GunBatteryBlockedBy(plant, Faction);
        }

        private bool CrewAvailable(Pawn pawn) => InBattle(pawn) && !pawn.InMentalState && !WaitingForDoctor(pawn) &&
            ClearingBattery(pawn) < 0 &&
            (Phase != 2 || !restingCrew.Contains(pawn) && !NeedsRest(pawn) &&
                pawn.CurJobDef != JobDefOf.LayDown && pawn.CurJobDef != JobDefOf.Ingest);

        private void ReviewRestingCrew()
        {
            if (Phase != 2 || CampLord == null)
            {
                restingCrew.Clear();
                return;
            }
            restingCrew.RemoveWhere(p => !InBattle(p) || p.GetLord() != CampLord || ClearingBattery(p) >= 0 ||
                !NeedsRest(p) && p.CurJobDef != JobDefOf.LayDown && p.CurJobDef != JobDefOf.Ingest &&
                NearRear(p.Position, 12f));
            foreach (Pawn pawn in CampLord.ownedPawns)
                if (InBattle(pawn) && NeedsRest(pawn) && ClearingBattery(pawn) < 0) restingCrew.Add(pawn);
        }

        private void RefreshForwardBuilders()
        {
            if (!Mechanoid || !UsesForwardCrews || CampLord == null)
            {
                forwardBuilders.Clear();
                return;
            }

            var incumbents = new Dictionary<Pawn, int>(forwardBuilders);
            forwardBuilders.Clear();
            // Keep the cutter in the crew until the native CutPlant job completes or an urgent need interrupts it.
            foreach (Pawn pawn in CampLord.ownedPawns)
            {
                int clearing = ClearingBattery(pawn);
                if (clearing > 0) forwardBuilders.Add(pawn, clearing);
            }
            var candidates = CampLord.ownedPawns.Where(p => CrewAvailable(p) && LordJob_EncirclementCamp.Builder(p) &&
                !IsMedic(p) && GunFor(p, out _) == null).ToList();
            int last = System.Math.Min(ActiveCluster, Plan.BatteryKeys.Count - 1);
            for (int index = last; index >= 1; index--)
            {
                if (Battery(index) is not { } battery) continue;
                bool active = Phase == 2 && index == ActiveCluster;
                if (!active && !Work.ActiveProjects.Any(p => p.Battery == index) &&
                    !battery.Camp.Guns.Any(g => g.Building is { Spawned: true } b && b.Faction == Faction))
                    continue;
                Assign(index, battery.Camp.Center, 1);
            }

            // Staff the older batteries before giving spare constructoids to the current site.
            if (Phase == 2 && ActiveCluster > 0 && Battery(ActiveCluster) is { } front)
            {
                int spare = candidates.Count(p => !forwardBuilders.ContainsKey(p)) - 1; // Keep a rear worker if available.
                int assigned = forwardBuilders.Values.Count(b => b == ActiveCluster);
                Assign(ActiveCluster, front.Camp.Center, System.Math.Min(MaxBuilders(front), assigned + spare));
            }

            void Assign(int index, IntVec3 center, int slots)
            {
                int vacancies = slots - forwardBuilders.Values.Count(b => b == index);
                if (vacancies <= 0) return;
                foreach (Pawn pawn in candidates.Where(p => !forwardBuilders.ContainsKey(p))
                    .OrderBy(p => incumbents.GetValueOrDefault(p) != index)
                    .ThenBy(incumbents.ContainsKey)
                    .ThenBy(p => IsoFloor.GroundCellOf(p.Position, Map).DistanceToSquared(center))
                    .Where(p => p.CanReach(center, PathEndMode.OnCell, Danger.Deadly))
                    .Take(vacancies))
                    forwardBuilders.Add(pawn, index);
            }

            static int MaxBuilders(ArtilleryRaid raid) => System.Math.Min(5, raid.Camp.Guns.Count + 1);
        }

        private void RefreshMechEscorts()
        {
            mechEscorts.Clear();
            if (!Mechanoid || CampLord == null) return;
            var available = CampLord.ownedPawns.Where(EncirclementMechEscort.Eligible).ToList();
            var previous = available.Select(EncirclementMechEscort.CurrentEscortee).OfType<Pawn>().ToHashSet();
            int active = Phase == 2 ? ActiveCluster : -1;
            var builders = CampLord.ownedPawns.Where(p => InBattle(p) && LordJob_EncirclementCamp.Builder(p))
                .GroupBy(BuilderBattery)
                .Where(group => Battery(group.Key)?.Camp.Guns.Count > 0)
                .OrderByDescending(group => group.Key == active).ThenByDescending(group => group.Key)
                .Select(group => group.OrderByDescending(previous.Contains)
                    .ThenBy(p => IsoFloor.GroundCellOf(p.Position, Map).DistanceToSquared(Battery(group.Key).Camp.Center))
                    .First()).ToList();
            int limit = EncirclementMechEscort.Limit(available.Count);
            // Cover each occupied site before assigning a second escort to its constructor.
            foreach (Pawn builder in builders.Concat(builders))
            {
                if (mechEscorts.Count >= limit) break;
                if (EncirclementMechEscort.Closest(builder, available) is not { } guard) continue;
                mechEscorts.Add(guard, builder);
                available.Remove(guard);
            }
        }

        private void ExposeCrews()
        {
            Scribe_Collections.Look(ref restingCrew, "restingEncirclementCrew", LookMode.Reference);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) restingCrew ??= new();
        }
    }

    internal static class EncirclementMechEscort
    {
        internal const float Radius = 8f;

        internal static bool Eligible(Pawn pawn) => pawn is { Spawned: true, Dead: false, Downed: false, InMentalState: false } &&
            pawn.RaceProps.IsMechanoid && pawn.kindDef.isFighter && !MechBattery.IsConstructoid(pawn);

        internal static int Limit(int combatants) => System.Math.Min(combatants, System.Math.Clamp(combatants / 8, 2, 6));

        internal static Pawn CurrentEscortee(Pawn pawn) => pawn.mindState.duty is { } duty && duty.def == DutyDefOf.Escort
            ? duty.focus.Thing as Pawn : null;

        internal static Pawn Closest(Pawn builder, List<Pawn> candidates) => candidates
            .OrderBy(p => CurrentEscortee(p) == builder ? 0 : CurrentEscortee(p) == null ? 1 : 2)
            .ThenBy(p => IsoFloor.Distance3D(p.Position, builder.Position, builder.Map))
            .FirstOrDefault(p => p.Faction == builder.Faction && p.CanReach(builder, PathEndMode.OnCell, Danger.Deadly));
    }
}
