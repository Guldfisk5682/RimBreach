using System;
using System.Collections.Generic;
using System.Linq;
using ProjectZ;
using Verse;

namespace RimSiege.Fortress
{
    internal enum FortressLine { Curtain, Courtyard, Keep }

    internal sealed class FortressDefenseGround
    {
        private readonly Map map;
        private readonly FortressBlueprint blueprint;
        private readonly FortressGeometry geometry;
        private readonly Dictionary<(IntVec3 Ground, int Floor), int> coveredSectors = new();
        private readonly Dictionary<(CellRect Rect, int Floor), IntVec3> anchors = new();

        internal FortressDefenseGround(Map map, FortressBlueprint blueprint)
        {
            this.map = map;
            this.blueprint = blueprint;
            FortressLayout rebuilt = blueprint.Rebuild(map);
            geometry = rebuilt?.Geometry ?? BuildFallbackGeometry(blueprint);
            if (blueprint.Defenses != null)
            {
                foreach (FortressDefensePiece piece in blueprint.Defenses)
                    if (piece.CoveredSectors != 0)
                        coveredSectors[(piece.Ground, piece.Floor)] = piece.CoveredSectors;
            }
        }

        internal IReadOnlyList<FortressGate> Gates => geometry.Gates;
        internal IntVec3 RestPost => Anchor(blueprint.Facilities.Living.Rect, 0);
        internal IntVec3 MedicalPost => Anchor(blueprint.Facilities.Supply.Rect, 1);
        internal bool Inside(IntVec3 cell) => geometry.Interior.Contains(Api.GroundCellOf(cell, map));
        internal bool InDefenseArea(IntVec3 cell) => Inside(cell) || OnCurtain(cell);
        internal bool InKeep(IntVec3 cell) => geometry.Core.Contains(Api.GroundCellOf(cell, map));
        internal bool Outside(IntVec3 cell) => !blueprint.ContainsGround(Api.GroundCellOf(cell, map));
        internal bool OnCurtain(IntVec3 cell) => Api.FloorOf(cell, map) > 0 &&
            geometry.RampartDeckGround.Contains(Api.GroundCellOf(cell, map));
        internal bool IsCurtain(IntVec3 ground) => geometry.Perimeter.Contains(ground);

        internal int CoveredSectors(IntVec3 cell) =>
            coveredSectors.TryGetValue((Api.GroundCellOf(cell, map), Api.FloorOf(cell, map)), out int mask) ? mask : 0;

        internal IntVec3 GateCell(int sector)
        {
            foreach (FortressGate gate in geometry.Gates)
                if (gate.Side.AsInt == sector) return gate.Cell;
            return geometry.CurtainPosts[sector];
        }

        internal bool TowerMiddle(IntVec3 cell)
        {
            if (blueprint.Top != 2 || Api.FloorOf(cell, map) != 1) return false;
            IntVec3 g = Api.GroundCellOf(cell, map);
            if (geometry.Core.Contains(g)) return true;
            foreach (FortressBastionZone zone in geometry.Zones)
                if (zone.Ground.Contains(g)) return true;
            return false;
        }

        internal bool TowerFaces(IntVec3 cell, int sector)
        {
            IntVec3 g = Api.GroundCellOf(cell, map);
            int mask = FortressGeometry.SectorBit(sector);
            foreach (FortressBastionZone zone in geometry.Zones)
                if (zone.Ground.Contains(g)) return (zone.SectorMask & mask) != 0;
            return false;
        }

        internal int Sector(IntVec3 cell)
        {
            IntVec3 delta = Api.GroundCellOf(cell, map) - blueprint.Outer.CenterCell;
            return Math.Abs(delta.x) > Math.Abs(delta.z) ? (delta.x >= 0 ? 1 : 3) : (delta.z >= 0 ? 0 : 2);
        }

        internal IntVec3 CurtainPost(int sector)
        {
            IntVec3 center = geometry.CurtainPosts[sector];
            return Anchor(CellRect.CenteredOn(center, 5, 5), 1, geometry.RampartDeckGround.Contains);
        }

        internal IntVec3 Post(FortressLine line, int sector, int index)
        {
            Rot4 side = new(sector);
            if (line == FortressLine.Keep)
                return Anchor(geometry.Core, blueprint.Top == 2 && index % 2 == 0 ? 1 : blueprint.Top);

            int sectorBit = FortressGeometry.SectorBit(sector);
            FortressBastionZone[] adjacentZones = geometry.Zones
                .Where(zone => (zone.SectorMask & sectorBit) != 0).ToArray();
            if (adjacentZones.Length == 0) adjacentZones = geometry.Zones.ToArray();
            FortressBastionZone zone = adjacentZones[index / 2 % adjacentZones.Length];

            if (line == FortressLine.Courtyard)
            {
                if (blueprint.Top == 2 && index % 2 == 0)
                {
                    CellRect interior = zone.Ground.ContractedBy(1);
                    IntVec3 firingSide = interior.GetCenterCellOnEdge(side.Opposite);
                    IntVec3 post = Anchor(CellRect.CenteredOn(firingSide, 3, 3).ClipInsideRect(interior), 1);
                    if (post.IsValid) return post;
                }
                IntVec3 inner = geometry.InteriorPosts[sector];
                return ((index + sector) % 3) switch
                {
                    0 => ZoneDeckPost(zone),
                    1 => Anchor(geometry.Core, blueprint.Top),
                    _ => Anchor(CellRect.CenteredOn(inner, 5, 5), 0, geometry.Interior.Contains)
                };
            }
            if (index % 2 == 1) return ZoneDeckPost(zone);
            return CurtainPost(sector);
        }

        private IntVec3 ZoneDeckPost(FortressBastionZone zone)
        {
            if (geometry.Kind == FortressLayoutKind.HumanBastion)
                return Anchor(CellRect.CenteredOn(zone.DeckAnchor, 5, 5), zone.Top, geometry.RampartDeckGround.Contains);
            return Anchor(zone.Ground, zone.Top);
        }

        internal bool PassableBreach(int sector)
        {
            var visited = new HashSet<IntVec3>();
            var queue = new Queue<IntVec3>();

            foreach (IntVec3 edge in geometry.PerimeterOuterEdge)
            {
                if (Sector(edge) != sector) continue;
                bool exposed = false;
                foreach (IntVec3 dir in GenAdj.CardinalDirections)
                {
                    IntVec3 outside = edge + dir;
                    if (!geometry.Footprint.Contains(outside) && outside.InBounds(map) && outside.Walkable(map))
                    {
                        exposed = true;
                        break;
                    }
                }
                if (exposed) Add(edge);
            }

            while (queue.TryDequeue(out IntVec3 cell))
            {
                if (geometry.Interior.Contains(cell)) return true;
                foreach (IntVec3 neighbor in GenAdj.CardinalDirections) Add(cell + neighbor);
            }
            return false;

            void Add(IntVec3 cell)
            {
                if (!geometry.Footprint.Contains(cell) || !visited.Add(cell) ||
                    !cell.Walkable(map) || cell.GetDoor(map) != null) return;
                queue.Enqueue(cell);
            }
        }

        private IntVec3 Anchor(CellRect rect, int floor, Func<IntVec3, bool> allowed = null)
        {
            if (anchors.TryGetValue((rect, floor), out IntVec3 cached) && cached.IsValid && cached.Standable(map))
                return cached;
            IntVec3 center = rect.CenterCell;
            return anchors[(rect, floor)] = rect.Where(cell => allowed == null || allowed(cell))
                .OrderBy(cell => (cell - center).LengthHorizontalSquared)
                .Select(cell => Api.CellOnFloor(cell, floor, map))
                .FirstOrDefault(cell => cell.IsValid && cell.Standable(map), IntVec3.Invalid);
        }

        private static FortressGeometry BuildFallbackGeometry(FortressBlueprint blueprint)
        {
            FortressDimensions dim = FortressDimensions.For(blueprint.Size);
            CellRect outer = blueprint.Outer;
            CellRect courtyard = outer.ContractedBy(FortressLayout.CurtainThickness);
            CellRect keep = FortressLayout.KeepFootprint(outer, dim.KeepSide);
            var footprint = new HashSet<IntVec3>(outer.Cells);
            var interior = new HashSet<IntVec3>(courtyard.Cells);
            var perimeter = new HashSet<IntVec3>(outer.Cells.Where(c => !interior.Contains(c)));
            var edge = new HashSet<IntVec3>(outer.EdgeCells);
            var gates = FortressLayout.GateSides(blueprint.GateSide, blueprint.FourGates)
                .Select(side =>
                {
                    IntVec3 cell = outer.GetCenterCellOnEdge(side);
                    return new FortressGate(cell, cell + side.FacingCell, side, FortressLayout.CurtainThickness);
                }).ToList();
            var zones = FortressLayout.TowerFootprints(outer, dim.TowerSide).Select((tower, index) =>
            {
                Rot4 xSide = tower.CenterCell.x < outer.CenterCell.x ? Rot4.West : Rot4.East;
                Rot4 zSide = tower.CenterCell.z < outer.CenterCell.z ? Rot4.South : Rot4.North;
                int mask = FortressGeometry.SectorBit(xSide) | FortressGeometry.SectorBit(zSide);
                return new FortressBastionZone(index, tower, blueprint.Top, mask, tower.CenterCell, xSide, zSide);
            }).ToList();
            var curtainPosts = Enumerable.Range(0, 4).Select(s =>
            {
                Rot4 side = new(s);
                return outer.GetCenterCellOnEdge(side) - side.FacingCell * (FortressLayout.CurtainThickness - 1);
            }).ToArray();
            var interiorPosts = Enumerable.Range(0, 4).Select(s =>
            {
                Rot4 side = new(s);
                return courtyard.GetCenterCellOnEdge(side) - side.FacingCell * 2;
            }).ToArray();

            return new FortressGeometry(blueprint.LayoutKind, blueprint.Size, outer, footprint, interior,
                perimeter, edge, new HashSet<IntVec3>(perimeter), courtyard, keep, gates, zones,
                new List<FortressEmplacementSlot>(), curtainPosts, interiorPosts);
        }
    }
}
