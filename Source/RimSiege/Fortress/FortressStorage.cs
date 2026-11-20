using System;
using System.Collections.Generic;
using System.Linq;
using ProjectZ;
using RimWorld;
using Verse;

namespace RimSiege.Fortress
{
    internal sealed class FortressStock : IExposable
    {
        private ThingDef def;
        private ThingDef stuff;
        private IntVec3 ground;
        private int floor;
        private int count;

        internal static List<FortressStock> Capture(Map map, FortressLayout layout) =>
            Supplies(map, layout).Select(thing => new FortressStock
            {
                def = thing.def,
                stuff = thing.Stuff,
                ground = Api.GroundCellOf(thing.Position, map),
                floor = Api.FloorOf(thing.Position, map),
                count = thing.stackCount
            }).ToList();

        internal static void Restore(Map map, FortressLayout layout, List<FortressStock> stock)
        {
            foreach (Thing thing in Supplies(map, layout).ToList()) thing.Destroy(DestroyMode.Vanish);
            foreach (FortressStock stack in stock)
            {
                if (stack.def == null) continue;
                Thing thing = ThingMaker.MakeThing(stack.def, stack.stuff);
                thing.stackCount = stack.count;
                GenPlace.TryPlaceThing(thing, Api.CellOnFloor(stack.ground, stack.floor, map), map, ThingPlaceMode.Direct);
            }
        }

        private static IEnumerable<Thing> Supplies(Map map, FortressLayout layout)
        {
            FortressFacilityPlan facilities = layout.Facilities;
            IEnumerable<IntVec3> cells = Enumerable.Empty<IntVec3>();
            if (!facilities.Supply.IsEmpty) cells = cells.Concat(facilities.Supply.Interior(map, 0).Cells);
            if (!facilities.Dining.IsEmpty || !facilities.Living.IsEmpty)
                cells = cells.Concat(facilities.DiningInterior(map).Cells);
            if (!facilities.Power.IsEmpty) cells = cells.Concat(facilities.Power.Interior(map, 0).Cells);
            if (!facilities.AuxSupply.IsEmpty) cells = cells.Concat(facilities.AuxSupply.Interior(map, 0).Cells);
            if (facilities.MortarDef != null) cells = cells.Concat(facilities.Mortar.Cells);
            if (facilities.SecondMortarDef != null) cells = cells.Concat(facilities.SecondMortar.Cells);
            foreach (FortressBastionZone zone in layout.Geometry.Zones)
                if (zone.VaultCells != null) cells = cells.Concat(zone.VaultCells);
            foreach (HashSet<IntVec3> room in layout.Geometry.LogisticsRooms)
                cells = cells.Concat(room);
            return cells.Distinct().SelectMany(cell => cell.GetThingList(map))
                .Where(thing => thing.def.category == ThingCategory.Item);
        }

        public void ExposeData()
        {
            Scribe_Defs.Look(ref def, "def");
            Scribe_Defs.Look(ref stuff, "stuff");
            Scribe_Values.Look(ref ground, "ground");
            Scribe_Values.Look(ref floor, "floor");
            Scribe_Values.Look(ref count, "count");
        }
    }

    internal static class FortressStorage
    {
        internal static List<Building_Storage> PlaceShelves(Map map, CellRect room, ThingDef stuff,
            HashSet<IntVec3> access, int limit = int.MaxValue)
        {
            var shelves = new List<Building_Storage>();
            var keepClear = new HashSet<IntVec3>(access);
            AddAlongWalls(ThingDefOf.Shelf);
            AddAlongWalls(ThingDefOf.ShelfSmall);
            return shelves;

            void AddAlongWalls(ThingDef def)
            {
                foreach (IntVec3 root in room.EdgeCells)
                {
                    if (shelves.Count == limit) return;
                    Rot4 rotation = root.z == room.minZ || root.z == room.maxZ ? Rot4.North : Rot4.East;
                    CellRect footprint = GenAdj.OccupiedRect(root, rotation, def.Size);
                    if (!footprint.FullyContainedWithin(room) || !footprint.All(cell => Free(cell) && !keepClear.Contains(cell)))
                        continue;
                    IntVec3 workingCell = footprint.AdjacentCellsCardinal
                        .Where(cell => room.Contains(cell) && Free(cell))
                        .OrderBy(cell => (cell - room.CenterCell).LengthHorizontalSquared)
                        .FirstOrDefault(IntVec3.Invalid);
                    if (!workingCell.IsValid) continue;
                    keepClear.Add(workingCell);
                    shelves.Add((Building_Storage)FortressConstruction.Spawn(def, root, rotation, stuff, map));
                }
            }

            bool Free(IntVec3 cell) => cell.Standable(map) && cell.GetFirstBuilding(map) == null;
        }

        internal static List<Building_Storage> PlaceShelvesInCells(Map map, HashSet<IntVec3> roomCells,
            IntVec3 center, ThingDef stuff, HashSet<IntVec3> access, int limit = int.MaxValue)
        {
            var shelves = new List<Building_Storage>();
            var keepClear = new HashSet<IntVec3>(access);
            var wallAdjacent = roomCells
                .Where(cell => GenAdj.CardinalDirections.Any(dir => !roomCells.Contains(cell + dir)))
                .OrderByDescending(cell => (cell - center).LengthHorizontalSquared)
                .ToArray();

            AddAlongWalls(ThingDefOf.Shelf);
            AddAlongWalls(ThingDefOf.ShelfSmall);
            return shelves;

            void AddAlongWalls(ThingDef def)
            {
                foreach (IntVec3 root in wallAdjacent)
                {
                    if (shelves.Count == limit) return;
                    bool horizWall = !roomCells.Contains(root + IntVec3.North) || !roomCells.Contains(root + IntVec3.South);
                    Rot4[] rotations = horizWall ? new[] { Rot4.North, Rot4.East } : new[] { Rot4.East, Rot4.North };
                    foreach (Rot4 rotation in rotations)
                    {
                        CellRect footprint = GenAdj.OccupiedRect(root, rotation, def.Size);
                        if (!footprint.All(cell => roomCells.Contains(cell) && Free(cell) && !keepClear.Contains(cell)))
                            continue;
                        IntVec3 workingCell = footprint.AdjacentCellsCardinal
                            .Where(cell => roomCells.Contains(cell) && Free(cell))
                            .OrderBy(cell => (cell - center).LengthHorizontalSquared)
                            .FirstOrDefault(IntVec3.Invalid);
                        if (!workingCell.IsValid) continue;
                        keepClear.Add(workingCell);
                        shelves.Add((Building_Storage)FortressConstruction.Spawn(def, root, rotation, stuff, map));
                        break;
                    }
                }
            }

            bool Free(IntVec3 cell) => cell.Standable(map) && cell.GetFirstBuilding(map) == null;
        }

        internal static void Fill(Map map, IEnumerable<Building_Storage> shelves, Func<int, ThingDef> contents)
        {
            int index = 0;
            foreach (Building_Storage shelf in shelves)
            {
                foreach (IntVec3 cell in shelf.GetSlotGroup().CellsList)
                {
                    int slots = cell.GetMaxItemsAllowedInCell(map) - cell.GetItemCount(map);
                    for (int i = 0; i < slots; i++) Stock(map, contents(index++), cell);
                }
            }
        }

        internal static int Stock(Map map, ThingDef def, IntVec3 cell, int count = int.MaxValue)
        {
            Thing stack = ThingMaker.MakeThing(def);
            stack.stackCount = Math.Min(count, def.stackLimit);
            int requested = stack.stackCount;
            return GenPlace.TryPlaceThing(stack, cell, map, ThingPlaceMode.Direct)
                ? requested : requested - stack.stackCount;
        }
    }
}
