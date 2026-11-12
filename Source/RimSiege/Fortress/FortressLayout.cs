using System.Collections.Generic;
using System.Linq;
using ProjectZ;
using RimWorld;
using Verse;

namespace RimSiege.Fortress
{
    internal readonly record struct FortressBuilding(ThingDef Def, IntVec3 Cell, Rot4 Rotation);

    internal readonly record struct FortressDimensions(int OuterSide, int TowerSide, int KeepSide)
    {
        internal static readonly FortressDimensions Large = new(49, 7, 13);
        internal static readonly FortressDimensions Compact = new(41, 5, 9);
    }

    internal readonly record struct FortressLadder(IntVec3 Foot, IntVec3 Landing, Rot4 Rotation);
    internal enum FortressModuleKind { Keep, Tower, Facility }
    internal readonly record struct FortressModule(CellRect Ground, int Top, FortressModuleKind Kind);
    internal readonly record struct FortressRoomFloor(CellRect Cells, bool Sterile, bool Roofed);

    internal sealed class FortressTier
    {
        internal readonly List<FortressBuilding> Buildings = new();
        internal readonly List<StairGeometry> Stairs = new();
        internal readonly List<IntVec3> Deck = new();
        internal readonly HashSet<IntVec3> Openings = new();
        internal readonly List<FortressRoomFloor> Interiors = new();
    }

    internal sealed class FortressLayout
    {
        internal const int CurtainThickness = 4;
        private const int TowerClearance = 3;

        internal CellRect Outer { get; }
        internal IntVec3 Gate { get; }
        internal Rot4 GateSide { get; }
        internal bool FourGates { get; }
        internal static IEnumerable<Rot4> GateSides(Rot4 primary, bool fourGates) =>
            fourGates ? GenAdj.CardinalDirections.Select(Rot4.FromIntVec3) : new[] { primary };
        internal HashSet<IntVec3> NaturalBarrier { get; }
        internal FortressTier[] Tiers { get; }
        internal readonly List<FortressLadder> Ladders = new();
        internal readonly List<FortressModule> Modules = new();
        internal FortressFacilityPlan Facilities { get; private set; }

        private FortressLayout(CellRect outer, Rot4 gateSide, HashSet<IntVec3> naturalBarrier, int top, bool fourGates)
        {
            Outer = outer;
            GateSide = gateSide;
            FourGates = fourGates;
            Gate = outer.GetCenterCellOnEdge(gateSide);
            NaturalBarrier = naturalBarrier;
            Tiers = Enumerable.Range(0, top + 1).Select(_ => new FortressTier()).ToArray();
        }

        internal static FortressLayout Plan(Map map, CellRect outer, Rot4 gateSide,
            HashSet<IntVec3> naturalBarrier, FortressDimensions dimensions, int? savedTop = null,
            FortressFacilityPlan? savedFacilities = null, bool fourGates = true)
        {
            int top = savedTop ?? (Api.CellOnFloor(outer.CenterCell, 2, map).IsValid ? 2 : 1);
            if (!Api.CellOnFloor(outer.CenterCell, top, map).IsValid) return null;
            var plan = new FortressLayout(outer, gateSide, naturalBarrier, top, fourGates);
            return plan.Compose(map, gateSide, top, dimensions, savedFacilities) ? plan : null;
        }

        private bool Compose(Map map, Rot4 gateSide, int top, FortressDimensions dimensions,
            FortressFacilityPlan? savedFacilities)
        {
            ThingDef embrasure = DefDatabase<ThingDef>.GetNamed("CE_Embrasure");
            var passages = GateSides(gateSide, FourGates).SelectMany(side => Enumerable.Range(0, CurtainThickness)
                .Select(depth => (Cell: Outer.GetCenterCellOnEdge(side) - side.FacingCell * depth, Side: side)))
                .ToDictionary(entry => entry.Cell, entry => entry.Side);

            foreach (IntVec3 cell in Outer)
            {
                int depth = Depth(Outer, cell);
                if (depth >= CurtainThickness || NaturalBarrier.Contains(cell)) continue;
                IntVec3 upper = Api.CellOnFloor(cell, 1, map);
                Tiers[1].Deck.Add(upper);
                if (depth == 0)
                    Tiers[1].Buildings.Add(new FortressBuilding(Merlon(Outer, cell, embrasure), upper, Rot4.North));
                if (passages.TryGetValue(cell, out Rot4 doorSide))
                {
                    if (depth == 0) Tiers[0].Buildings.Add(new FortressBuilding(ThingDefOf.Door, cell, doorSide));
                    continue;
                }
                Tiers[0].Buildings.Add(new FortressBuilding(ThingDefOf.Wall, cell, Rot4.North));
            }

            CellRect courtyard = Outer.ContractedBy(CurtainThickness);
            foreach (int x in new[] { courtyard.minX, courtyard.maxX })
            {
                if (!AddStair(map, 0, new IntVec3(x, 0, courtyard.minZ + 2), Rot4.South) ||
                    !AddStair(map, 0, new IntVec3(x, 0, courtyard.maxZ - 2), Rot4.North)) return false;
            }

            CellRect keep = KeepFootprint(Outer, dimensions.KeepSide);
            Modules.Add(new FortressModule(keep, top, FortressModuleKind.Keep));
            AddPerimeter(map, keep, 0, gateSide, fourDoors: FourGates);
            AddInterior(map, keep, 0);
            AddDeck(map, keep, 1);
            if (!AddStair(map, 0, new IntVec3(keep.minX + 3, 0, keep.minZ + 3), Rot4.South))
                return false;

            if (top == 2)
            {
                AddPerimeter(map, keep, 1, Rot4.Invalid, embrasure, centerPorts: true);
                AddInterior(map, keep, 1, roofed: true);
                AddDeck(map, keep, 2);
                if (!AddStair(map, 1, new IntVec3(keep.maxX - 3, 0, keep.maxZ - 3), Rot4.North))
                    return false;
            }
            AddParapet(map, keep, top, embrasure);
            AddTowers(map, dimensions.TowerSide, top, embrasure);
            FortressFacilityPlan facilities = savedFacilities.GetValueOrDefault();
            if (!savedFacilities.HasValue && !FortressFacilityPlan.TryPlace(map, this, out facilities)) return false;
            Facilities = facilities;
            AddFacility(map, facilities.Living, upperRoom: true);
            AddFacility(map, facilities.Supply, upperRoom: true, sterile: facilities.Industrial);
            if (!facilities.Power.IsEmpty) AddFacility(map, facilities.Power, upperRoom: false);
            return VerticalLinksFit();
        }

        private bool VerticalLinksFit()
        {
            var reserved = new HashSet<IntVec3>();
            foreach (FortressTier tier in Tiers)
            {
                reserved.UnionWith(tier.Openings);
                foreach (StairGeometry stair in tier.Stairs)
                {
                    reserved.UnionWith(stair.footprint);
                    reserved.Add(stair.exit);
                }
            }

            foreach (FortressLadder ladder in Ladders)
            {
                if (!reserved.Add(ladder.Foot) || !reserved.Add(ladder.Landing)) return false;
            }
            return true;
        }

        internal static CellRect KeepFootprint(CellRect outer, int side) =>
            CellRect.CenteredOn(outer.CenterCell, side, side);

        internal static IEnumerable<CellRect> TowerFootprints(CellRect outer, int side)
        {
            CellRect area = outer.ContractedBy(CurtainThickness + TowerClearance);
            foreach (int x in new[] { area.minX, area.maxX - side + 1 })
                foreach (int z in new[] { area.minZ, area.maxZ - side + 1 })
                    yield return new CellRect(x, z, side, side);
        }

        private void AddTowers(Map map, int side, int top, ThingDef embrasure)
        {
            foreach (CellRect tower in TowerFootprints(Outer, side))
            {
                bool west = tower.CenterCell.x < Outer.CenterCell.x;
                bool south = tower.CenterCell.z < Outer.CenterCell.z;
                Rot4 inward = west ? Rot4.East : Rot4.West;
                Modules.Add(new FortressModule(tower, top, FortressModuleKind.Tower));
                Rot4 inwardZ = south ? Rot4.North : Rot4.South;
                IntVec3 inwardCorner = new IntVec3(west ? tower.maxX : tower.minX, 0,
                    south ? tower.maxZ : tower.minZ);
                IntVec3 lowerFoot = inwardCorner - inward.FacingCell - inwardZ.FacingCell;

                AddTowerBase(tower, inward, inwardZ);
                AddInterior(map, tower, 0);
                AddDeck(map, tower, 1);
                AddLadder(map, 0, lowerFoot, inward.Opposite);
                AddPerimeter(map, tower, 1, Rot4.Invalid, embrasure, centerPorts: top == 2 && side >= 7);

                if (top == 1) continue;
                AddInterior(map, tower, 1);

                // Turn the upper flight toward the corner, keeping both
                // landings inside the shell and the two shaft columns distinct.
                AddLadder(map, 1, lowerFoot - inwardZ.FacingCell, inwardZ);
                AddDeck(map, tower, 2);
                foreach (IntVec3 cell in tower.EdgeCells)
                    Tiers[2].Buildings.Add(new FortressBuilding(ThingDefOf.Barricade,
                        Api.CellOnFloor(cell, 2, map), Rot4.North));
            }
        }

        private void AddFacility(Map map, FortressLot lot, bool upperRoom, bool sterile = false)
        {
            Modules.Add(new FortressModule(lot.Rect, 1, FortressModuleKind.Facility));
            AddPerimeter(map, lot.Rect, 0, lot.Facing);
            AddInterior(map, lot.Rect, 0);
            AddDeck(map, lot.Rect, 1);
            AddLadder(map, 0, lot.Foot, lot.Facing.Opposite);
            if (!upperRoom) return;
            AddPerimeter(map, lot.Rect, 1, lot.Facing);
            AddInterior(map, lot.Rect, 1, sterile, roofed: true);
        }

        private void AddInterior(Map map, CellRect module, int floor, bool sterile = false, bool roofed = false)
        {
            CellRect inside = module.ContractedBy(1);
            CellRect band = CellRect.FromLimits(Api.CellOnFloor(inside.Min, floor, map), Api.CellOnFloor(inside.Max, floor, map));
            Tiers[floor].Interiors.Add(new FortressRoomFloor(band, sterile, roofed));
        }

        private void AddTowerBase(CellRect tower, Rot4 inward, Rot4 inwardZ)
        {
            int doorOffset = tower.Width / 2 - 1;
            IntVec3 sideDoor = tower.GetCenterCellOnEdge(inward) - inwardZ.FacingCell * doorOffset;
            IntVec3 endDoor = tower.GetCenterCellOnEdge(inwardZ) - inward.FacingCell * doorOffset;
            foreach (IntVec3 cell in tower.EdgeCells)
            {
                if (cell == sideDoor) Tiers[0].Buildings.Add(new FortressBuilding(ThingDefOf.Door, cell, inward));
                else if (cell == endDoor) Tiers[0].Buildings.Add(new FortressBuilding(ThingDefOf.Door, cell, inwardZ));
                else Tiers[0].Buildings.Add(new FortressBuilding(ThingDefOf.Wall, cell, Rot4.North));
            }
        }

        private void AddLadder(Map map, int level, IntVec3 groundFoot, Rot4 facing)
        {
            IntVec3 foot = Api.CellOnFloor(groundFoot, level, map);
            IntVec3 landing = Api.CellOnFloor(groundFoot + facing.FacingCell, level + 1, map);
            Tiers[level + 1].Openings.Add(Api.CellOnFloor(groundFoot, level + 1, map));
            Ladders.Add(new FortressLadder(foot, landing, facing));
        }

        private void AddPerimeter(Map map, CellRect rect, int level, Rot4 gateSide, ThingDef embrasure = null,
            bool centerPorts = false, bool fourDoors = false)
        {
            var doors = gateSide.IsValid ? GateSides(gateSide, fourDoors)
                .ToDictionary(side => rect.GetCenterCellOnEdge(side)) : null;
            int portHalfWidth = rect.Width >= 9 ? 1 : 0;
            foreach (IntVec3 cell in rect.EdgeCells)
            {
                bool inPort = System.Math.Abs(cell.x - rect.CenterCell.x) <= portHalfWidth ||
                    System.Math.Abs(cell.z - rect.CenterCell.z) <= portHalfWidth;
                Rot4 rotation = Rot4.North;
                bool isDoor = doors != null && doors.TryGetValue(cell, out rotation);
                ThingDef def = isDoor ? ThingDefOf.Door :
                    embrasure != null && (centerPorts ? inPort : Along(rect, cell) % 2 == 0) ? embrasure : ThingDefOf.Wall;
                Tiers[level].Buildings.Add(new FortressBuilding(def, Api.CellOnFloor(cell, level, map),
                    isDoor ? rotation : Rot4.North));
            }
        }

        private void AddParapet(Map map, CellRect rect, int level, ThingDef embrasure)
        {
            foreach (IntVec3 cell in rect.EdgeCells)
            {
                ThingDef def = Along(rect, cell) % 2 == 0 ? embrasure : ThingDefOf.Barricade;
                Tiers[level].Buildings.Add(new FortressBuilding(def, Api.CellOnFloor(cell, level, map), Rot4.North));
            }
        }

        private void AddDeck(Map map, CellRect rect, int level)
        {
            // Lay the deck from the supported perimeter inwards.
            foreach (IntVec3 cell in rect.OrderBy(cell => Depth(rect, cell)))
                Tiers[level].Deck.Add(Api.CellOnFloor(cell, level, map));
        }

        private bool AddStair(Map map, int level, IntVec3 groundCenter, Rot4 rotation)
        {
            IntVec3 center = Api.CellOnFloor(groundCenter, level, map);
            if (!Api.TryGetStairGeometry(center, rotation, map, out StairGeometry stair)) return false;
            Tiers[level].Stairs.Add(stair);
            Tiers[level + 1].Openings.UnionWith(stair.well);
            return true;
        }

        private static ThingDef Merlon(CellRect rect, IntVec3 cell, ThingDef embrasure)
        {
            int offset = Along(rect, cell) % 4;
            if (offset == 0) return embrasure;
            return offset == 2 ? ThingDefOf.Wall : ThingDefOf.Barricade;
        }

        private static int Along(CellRect rect, IntVec3 cell) =>
            cell.z == rect.minZ || cell.z == rect.maxZ ? cell.x - rect.minX : cell.z - rect.minZ;

        internal static int Depth(CellRect rect, IntVec3 cell) =>
            System.Math.Min(System.Math.Min(cell.x - rect.minX, rect.maxX - cell.x),
                System.Math.Min(cell.z - rect.minZ, rect.maxZ - cell.z));
    }
}
