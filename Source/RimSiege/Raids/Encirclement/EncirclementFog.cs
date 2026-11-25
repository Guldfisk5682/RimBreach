using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using RimWorld;
using Verse;

namespace RimSiege.Raids.Encirclement
{
    public sealed class EncirclementFog : MapComponent
    {
        private static readonly ConditionalWeakTable<FogGrid, EncirclementFog> Active = new();
        private List<CellRect> rooms = new();
        private readonly HashSet<IntVec3> cells = new();
        private readonly HashSet<IntVec3> openings = new();
        private readonly HashSet<IntVec3> breaches = new();
        private bool listening;

        public EncirclementFog(Map map) : base(map) { }

        internal static bool TryGet(FogGrid grid, out EncirclementFog fog) => Active.TryGetValue(grid, out fog);
        internal bool Conceals(IntVec3 cell) => cells.Contains(cell);
        internal int RoomCount => rooms.Count;

        public override void FinalizeInit()
        {
            if (rooms.Count == 0) return;
            IndexRooms();
            // An older save may already have a gap or have been saved before
            // the deferred building-despawn event was processed.
            for (int i = rooms.Count - 1; i >= 0; i--)
                if (rooms[i].ExpandedBy(1).EdgeCells.Any(c => !IsShell(c.GetEdifice(map))))
                    Reveal(i);
        }

        public override void ExposeData() => Scribe_Collections.Look(ref rooms, "rooms", LookMode.Value);

        internal void ConcealRoom(CellRect footprint)
        {
            CellRect interior = footprint.ContractedBy(1);
            if (rooms.Contains(interior) || map.mapPawns.AllPawnsSpawned.Any(p => PlayerObserver(p) &&
                CanSeeRoom(p.Position, interior))) return;
            rooms.Add(interior);
            IndexRooms();
            map.fogGrid.Refog(interior);
            ProjectZ.Api.RequestFogRefresh(map);
        }

        private void IndexRooms()
        {
            cells.Clear();
            foreach (CellRect room in rooms) cells.UnionWith(room);
            if (rooms.Count == 0) Active.Remove(map.fogGrid);
            else Active.GetValue(map.fogGrid, _ => this);
            bool watch = rooms.Count != 0;
            if (watch == listening) return;
            listening = watch;
            if (watch)
            {
                map.events.BuildingDespawned += ShellRemoved;
                map.events.DoorOpened += OpeningChanged;
            }
            else
            {
                map.events.BuildingDespawned -= ShellRemoved;
                map.events.DoorOpened -= OpeningChanged;
                openings.Clear();
                breaches.Clear();
            }
        }

        private void OpeningChanged(Building building)
        {
            if (rooms.Any(r => OnBoundary(r, building.Position)))
                openings.Add(building.Position);
        }

        private static bool IsShell(Building building) => building != null &&
            (building.def.IsWall || building.def.IsDoor || building.def.MakeFog);

        private static bool OnBoundary(CellRect interior, IntVec3 cell) =>
            interior.ExpandedBy(1).Contains(cell) && !interior.Contains(cell);

        private void ShellRemoved(Building building)
        {
            if (!IsShell(building)) return;
            foreach (IntVec3 cell in building.OccupiedRect())
                if (rooms.Any(r => OnBoundary(r, cell))) breaches.Add(cell);
        }

        public override void MapComponentTick()
        {
            if (breaches.Count != 0)
            {
                // Cells are actual floor-band coordinates, so the wall only
                // releases its own storey's interior.
                for (int i = rooms.Count - 1; i >= 0; i--)
                    if (breaches.Any(c => OnBoundary(rooms[i], c) && !IsShell(c.GetEdifice(map))))
                        Reveal(i);
                breaches.Clear();
            }
            if (openings.Count == 0) return;
            Pawn[] observers = map.mapPawns.AllPawnsSpawned.Where(p => PlayerObserver(p) &&
                openings.Any(c => c.InHorDistOf(p.Position, 2f))).ToArray();
            openings.Clear();
            foreach (Pawn pawn in observers) PawnArrived(pawn);
        }

        private static bool PlayerObserver(Pawn pawn) =>
            pawn.Spawned && !pawn.Downed && (pawn.Faction == Faction.OfPlayer || pawn.IsPlayerControlled);

        internal static void PawnArrived(Pawn pawn)
        {
            if (!PlayerObserver(pawn) || !TryGet(pawn.Map.fogGrid, out var fog)) return;
            for (int i = fog.rooms.Count - 1; i >= 0; i--)
                if (fog.CanSeeRoom(pawn.Position, fog.rooms[i])) fog.Reveal(i);
        }

        private bool CanSeeRoom(IntVec3 position, CellRect room)
        {
            if (room.Contains(position)) return true;
            if (!room.ExpandedBy(2).Contains(position)) return false;
            foreach (IntVec3 interior in room.EdgeCells)
                if ((position - interior).LengthHorizontalSquared <= 5 &&
                    GenSight.LineOfSight(position, interior, map, skipFirstCell: true))
                    return true;
            return false;
        }

        private void Reveal(int index)
        {
            CellRect room = rooms[index];
            rooms.RemoveAt(index);
            IndexRooms();
            foreach (IntVec3 cell in room)
                map.fogGrid.Unfog(cell);
            map.fogGrid.FloodUnfogAdjacent(room.CenterCell, sendLetters: false);
            map.events.Notify_MapFogged();
            ProjectZ.Api.RequestFogRefresh(map);
        }

        internal void RevealAll()
        {
            while (rooms.Count > 0) Reveal(rooms.Count - 1);
        }
    }
}
