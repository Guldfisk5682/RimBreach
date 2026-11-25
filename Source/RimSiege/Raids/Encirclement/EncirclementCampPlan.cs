using System.Collections.Generic;
using System.Linq;
using RimSiege.Integration;
using RimSiege.Raids.Artillery;
using RimWorld;
using Verse;

namespace RimSiege.Raids.Encirclement
{
    internal sealed class EncirclementRoom : IExposable
    {
        internal CellRect Footprint;
        internal Rot4 Facing;
        internal IntVec3 Stairs = IntVec3.Invalid;
        internal ThingDef Device;
        internal int Stage;
        internal bool GroundConcealed, UpperConcealed;

        internal CellRect OnFloor(Map map, int floor)
        {
            IntVec3 min = IsoFloor.CellOnFloor(Footprint.Min, floor, map);
            return new CellRect(min.x, min.z, Footprint.Width, Footprint.Height);
        }

        internal IntVec3 Door(bool rear = false)
        {
            IntVec3 offset = (rear ? Facing.Opposite : Facing).FacingCell *
                (Facing.IsHorizontal ? Footprint.Width / 2 : Footprint.Height / 2);
            IntVec3 center = Footprint.CenterCell;
            if (Device == null && Facing.IsHorizontal) center.z = Footprint.minZ + 1;
            return center + offset;
        }

        public EncirclementRoom() { }

        public void ExposeData()
        {
            Scribe_Values.Look(ref Footprint, "footprint");
            Scribe_Values.Look(ref Facing, "facing", Rot4.North);
            Scribe_Values.Look(ref Stairs, "stairs", IntVec3.Invalid);
            Scribe_Defs.Look(ref Device, "device");
            Scribe_Values.Look(ref Stage, "stage");
            Scribe_Values.Look(ref GroundConcealed, "groundConcealed");
            Scribe_Values.Look(ref UpperConcealed, "upperConcealed");
        }
    }

    internal sealed class EncirclementCampPlan : IExposable
    {
        internal IntVec3 Rear;
        internal IntVec3 Goal;
        internal Rot4 Facing;
        internal bool Exposed, AssaultFallback;
        internal List<IntVec3> Entries = new();
        internal List<EncirclementRoom> Rooms = new();
        internal List<IntVec3> Fixtures = new();
        internal CampPlan RearBattery;
        internal List<CampPlan> Front = new();
        internal List<int> BatteryKeys = new();
        internal int BatteryPlanningPasses;
        internal int PlannedHumans = -1;
        internal float ForwardBudget = -1f;
        internal int BedrollLimit => (PlannedHumans + 1) / 2;
        private List<IntVec3> rearStations;
        internal IReadOnlyList<IntVec3> RearStations => rearStations ??= Rooms.Where(r => r.Device == null)
            .Select(r => r.Door() + r.Facing.FacingCell).ToList();

        public EncirclementCampPlan() { }

        internal IEnumerable<ArtilleryRaid> Batteries(Map map)
        {
            ArtilleryMap artillery = map.GetComponent<ArtilleryMap>();
            foreach (int key in BatteryKeys)
                if (artillery.ForKey(key) is { } raid) yield return raid;
        }

        internal HashSet<IntVec3> Reservations(Map map)
        {
            var reserved = new HashSet<IntVec3>(Rooms.SelectMany(r => r.Footprint));
            reserved.UnionWith(Fixtures);
            foreach (ArtilleryRaid raid in Batteries(map))
                reserved.UnionWith(CampSupplies.Reserved(map, raid.Camp));
            return reserved;
        }

        // The artillery registry owns each battery's save record from this point through the assault.
        internal void RegisterBatteries(QuestPart_Encirclement siege)
        {
            ArtilleryMap artillery = siege.Map.GetComponent<ArtilleryMap>();
            SupportRoute route = SupportRoute.Build(siege.Map, Rear, Goal);
            foreach (CampPlan battery in Front.Prepend(RearBattery))
            {
                var raid = new ArtilleryRaid
                {
                    Camp = battery, Phase = ArtilleryPhase.Supporting, SuppliesDelivered = true,
                    Faction = siege.Faction, RaidPoints = siege.BasePoints,
                    PawnPoints = EncirclementWaveSpawner.PointsFor(siege, battery == RearBattery ? 1 : 2),
                    InfantryReleased = true, Encirclement = true, IncidentKey = siege.IncidentKey,
                    Kind = siege.Mechanoid ? ArtilleryRaidKind.Mechanoid : ArtilleryRaidKind.Breaching,
                    RaidSeed = siege.quest.id
                };
                artillery.Register(raid, Enumerable.Empty<Pawn>());
                BatteryKeys.Add(raid.Key);
                SupportRoute.Set(raid, route);
            }
            RearBattery = null;
            Front.Clear();
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref Rear, "rear");
            Scribe_Values.Look(ref Goal, "goal");
            Scribe_Values.Look(ref Facing, "facing", Rot4.North);
            Scribe_Values.Look(ref Exposed, "exposed");
            Scribe_Values.Look(ref AssaultFallback, "assaultFallback");
            Scribe_Values.Look(ref PlannedHumans, "plannedHumans", -1);
            Scribe_Values.Look(ref ForwardBudget, "forwardBatteryBudget", -1f);
            Scribe_Collections.Look(ref Entries, "entries", LookMode.Value);
            Scribe_Collections.Look(ref Rooms, "rooms", LookMode.Deep);
            Scribe_Collections.Look(ref Fixtures, "fixtures", LookMode.Value);
            Scribe_Collections.Look(ref BatteryKeys, "batteryKeys", LookMode.Value);
        }
    }
}
