using System;
using System.Collections.Generic;
using System.Linq;
using ProjectZ;
using Verse;

namespace RimSiege.Fortress
{
    internal enum FortressLayoutKind
    {
        SquareKeep = 0,
        HumanBastion = 1
    }

    internal enum FortressSize
    {
        Compact = 0,
        Large = 1
    }

    internal enum FortressEmplacementRole
    {
        OutwardFace = 0,
        FlankCurtain = 1,
        CurtainWall = 2,
        Core = 3,
        Mortar = 4,
        OutwardShoulder = 5
    }

    internal readonly record struct FortressGate(
        IntVec3 Cell,
        IntVec3 Approach,
        Rot4 Side,
        int PassageDepth);

    internal readonly record struct FortressBastionZone(
        int Index,
        CellRect Ground,
        int Top,
        int SectorMask,
        IntVec3 DeckAnchor,
        Rot4 XSide,
        Rot4 ZSide,
        HashSet<IntVec3> VaultCells = null);

    internal readonly record struct FortressEmplacementSlot(
        IntVec3 Ground,
        int Floor,
        Rot4 Facing,
        FortressEmplacementRole Role,
        int CoveredSectors,
        int ZoneIndex);

    internal readonly record struct BastionDimensions(
        int OuterSide,
        int CurtainOuter,
        int CurtainThickness,
        int FlankInner,
        int FlankLength,
        int CasemateMin,
        int CasemateMax,
        int CoreSide)
    {
        internal static readonly BastionDimensions Large = new(
            OuterSide: 55,
            CurtainOuter: 19,
            CurtainThickness: 3,
            FlankInner: 13,
            FlankLength: 5,
            CasemateMin: 15,
            CasemateMax: 21,
            CoreSide: 9);

        internal static readonly BastionDimensions Compact = new(
            OuterSide: 47,
            CurtainOuter: 16,
            CurtainThickness: 3,
            FlankInner: 11,
            FlankLength: 4,
            CasemateMin: 12,
            CasemateMax: 18,
            CoreSide: 7);

        internal int Half => OuterSide / 2;
        internal int CourtyardHalf => CurtainOuter - CurtainThickness;
        internal int Shoulder => CurtainOuter + FlankLength;

        internal static BastionDimensions For(FortressSize size) =>
            size == FortressSize.Large ? Large : Compact;
    }

    internal readonly record struct FortressShapeMask(
        int CurtainOffset,
        HashSet<IntVec3> Footprint,
        HashSet<IntVec3> OuterEdge,
        HashSet<IntVec3> PreservedSkin,
        int InnerSide)
    {
        internal IntVec3 GateCell(IntVec3 center, Rot4 side) => center + side.FacingCell * CurtainOffset;
    }

    internal sealed class FortressGeometry
    {
        internal FortressLayoutKind Kind { get; }
        internal FortressSize Size { get; }
        internal CellRect Outer { get; }
        internal HashSet<IntVec3> Footprint { get; }
        internal HashSet<IntVec3> Interior { get; }
        internal HashSet<IntVec3> Perimeter { get; }
        internal HashSet<IntVec3> PerimeterOuterEdge { get; }
        internal HashSet<IntVec3> RampartDeckGround { get; }
        internal CellRect CourtyardBounds { get; }
        internal CellRect Core { get; }
        internal IReadOnlyList<FortressGate> Gates { get; }
        internal IReadOnlyList<FortressBastionZone> Zones { get; }
        internal IReadOnlyList<FortressEmplacementSlot> Slots { get; }
        internal IntVec3[] CurtainPosts { get; }
        internal IntVec3[] InteriorPosts { get; }

        internal FortressGeometry(
            FortressLayoutKind kind,
            FortressSize size,
            CellRect outer,
            HashSet<IntVec3> footprint,
            HashSet<IntVec3> interior,
            HashSet<IntVec3> perimeter,
            HashSet<IntVec3> perimeterOuterEdge,
            HashSet<IntVec3> rampartDeckGround,
            CellRect courtyardBounds,
            CellRect core,
            List<FortressGate> gates,
            List<FortressBastionZone> zones,
            List<FortressEmplacementSlot> slots,
            IntVec3[] curtainPosts,
            IntVec3[] interiorPosts)
        {
            Kind = kind;
            Size = size;
            Outer = outer;
            Footprint = footprint;
            Interior = interior;
            Perimeter = perimeter;
            PerimeterOuterEdge = perimeterOuterEdge;
            RampartDeckGround = rampartDeckGround;
            CourtyardBounds = courtyardBounds;
            Core = core;
            Gates = gates;
            Zones = zones;
            Slots = slots;
            CurtainPosts = curtainPosts;
            InteriorPosts = interiorPosts;
        }

        internal static int OuterSideFor(FortressLayoutKind kind, FortressSize size) =>
            kind == FortressLayoutKind.HumanBastion
                ? BastionDimensions.For(size).OuterSide
                : (size == FortressSize.Large ? FortressDimensions.Large.OuterSide : FortressDimensions.Compact.OuterSide);

        internal static int SectorBit(Rot4 side) => 1 << side.AsInt;
        internal static int SectorBit(int sector) => 1 << sector;

        internal bool IsRampartGunCell(IntVec3 ground) =>
            RampartDeckGround.Contains(ground) && !PerimeterOuterEdge.Contains(ground);

        private static class Shapes
        {
            internal static readonly FortressShapeMask CompactSquare = BuildShapeMask(FortressLayoutKind.SquareKeep, FortressSize.Compact);
            internal static readonly FortressShapeMask LargeSquare = BuildShapeMask(FortressLayoutKind.SquareKeep, FortressSize.Large);
            internal static readonly FortressShapeMask CompactBastion = BuildShapeMask(FortressLayoutKind.HumanBastion, FortressSize.Compact);
            internal static readonly FortressShapeMask LargeBastion = BuildShapeMask(FortressLayoutKind.HumanBastion, FortressSize.Large);
        }

        internal static FortressShapeMask ShapeFor(FortressLayoutKind kind, FortressSize size) => kind switch
        {
            FortressLayoutKind.HumanBastion => size == FortressSize.Large ? Shapes.LargeBastion : Shapes.CompactBastion,
            _ => size == FortressSize.Large ? Shapes.LargeSquare : Shapes.CompactSquare
        };

        private static FortressShapeMask BuildShapeMask(FortressLayoutKind kind, FortressSize size)
        {
            int side = OuterSideFor(kind, size);
            CellRect outer = CellRect.CenteredOn(IntVec3.Zero, side, side);

            if (kind == FortressLayoutKind.SquareKeep)
            {
                var edge = new HashSet<IntVec3>(outer.EdgeCells);
                var skin = new HashSet<IntVec3>(edge);
                skin.UnionWith(outer.ContractedBy(1).EdgeCells);
                return new FortressShapeMask(side / 2, new HashSet<IntVec3>(outer.Cells), edge, skin,
                    side - FortressSiting.PreservedSkin * 2);
            }

            var footprint = new HashSet<IntVec3>();
            BastionDimensions dim = BastionDimensions.For(size);
            int half = dim.Half;
            int shoulder = dim.Shoulder;
            int rise = half - shoulder;
            int run = half - dim.FlankInner;

            foreach (IntVec3 cell in outer)
            {
                int ax = Math.Abs(cell.x);
                int az = Math.Abs(cell.z);
                bool inBody = ax <= dim.CurtainOuter && az <= dim.CurtainOuter;
                bool inBastion = ax >= dim.FlankInner && az >= dim.FlankInner &&
                    (az - shoulder) * run <= rise * (ax - dim.FlankInner) &&
                    (ax - shoulder) * run <= rise * (az - dim.FlankInner) &&
                    ax + az <= 2 * half - 1;
                if (inBody || inBastion) footprint.Add(cell);
            }

            var outerEdge = new HashSet<IntVec3>();
            foreach (IntVec3 cell in footprint)
            {
                if (HasExteriorNeighbor8(cell, footprint)) outerEdge.Add(cell);
            }

            var preservedSkin = new HashSet<IntVec3>(outerEdge);
            foreach (IntVec3 cell in footprint)
            {
                if (preservedSkin.Contains(cell)) continue;
                foreach (IntVec3 dir in GenAdj.CardinalDirections)
                {
                    if (outerEdge.Contains(cell + dir))
                    {
                        preservedSkin.Add(cell);
                        break;
                    }
                }
            }

            int innerHalf = dim.CurtainOuter - FortressSiting.PreservedSkin;
            return new FortressShapeMask(dim.CurtainOuter, footprint, outerEdge, preservedSkin, innerHalf * 2 + 1);
        }

        // In RimWorld, a diagonal step requires both adjacent cardinal cells to be passable,
        // so any path around closed doors and walls reduces to a 4-way path.
        internal bool IsAirtight(HashSet<IntVec3> solidGround)
        {
            CellRect bounds = Outer.ExpandedBy(1);
            var visited = new HashSet<IntVec3>();
            var queue = new Queue<IntVec3>();

            foreach (IntVec3 cell in bounds)
            {
                if (Footprint.Contains(cell)) continue;
                bool bordersFootprint = false;
                foreach (IntVec3 dir in GenAdj.CardinalDirections)
                {
                    if (Footprint.Contains(cell + dir))
                    {
                        bordersFootprint = true;
                        break;
                    }
                }
                if (!bordersFootprint) continue;
                visited.Add(cell);
                queue.Enqueue(cell);
            }

            while (queue.Count > 0)
            {
                IntVec3 current = queue.Dequeue();
                foreach (IntVec3 dir in GenAdj.CardinalDirections)
                {
                    IntVec3 next = current + dir;
                    if (!bounds.Contains(next) || !visited.Add(next) || solidGround.Contains(next)) continue;
                    if (Interior.Contains(next)) return false;
                    queue.Enqueue(next);
                }
            }

            return true;
        }

        internal static bool HasExteriorNeighbor8(IntVec3 cell, HashSet<IntVec3> footprint)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    if (!footprint.Contains(new IntVec3(cell.x + dx, 0, cell.z + dz))) return true;
                }
            }
            return false;
        }

        // 1-based 4-way distance to exterior (!footprint); cells with distance >= 4 have >= 3 solid wall cells in all cardinal directions.
        internal static Dictionary<IntVec3, int> CardinalExteriorDistance(HashSet<IntVec3> footprint)
        {
            var distance = new Dictionary<IntVec3, int>(footprint.Count);
            var queue = new Queue<IntVec3>();
            foreach (IntVec3 cell in footprint)
            {
                foreach (IntVec3 dir in GenAdj.CardinalDirections)
                {
                    if (!footprint.Contains(cell + dir))
                    {
                        distance[cell] = 1;
                        queue.Enqueue(cell);
                        break;
                    }
                }
            }

            while (queue.Count > 0)
            {
                IntVec3 current = queue.Dequeue();
                int nextDist = distance[current] + 1;
                foreach (IntVec3 dir in GenAdj.CardinalDirections)
                {
                    IntVec3 next = current + dir;
                    if (!footprint.Contains(next) || distance.ContainsKey(next)) continue;
                    distance[next] = nextDist;
                    queue.Enqueue(next);
                }
            }
            return distance;
        }

        internal List<IntVec3> WireRing(int setback, CellRect groundBounds)
        {
            if (Kind == FortressLayoutKind.SquareKeep)
                return Outer.ExpandedBy(setback).EdgeCells.Where(groundBounds.Contains).ToList();

            CellRect search = Outer.ExpandedBy(setback + 1).ClipInsideRect(groundBounds);
            var distance = new Dictionary<IntVec3, int>(search.Area);
            var queue = new Queue<IntVec3>();
            foreach (IntVec3 cell in PerimeterOuterEdge)
            {
                distance[cell] = 0;
                queue.Enqueue(cell);
            }

            var ring = new List<IntVec3>();
            while (queue.Count > 0)
            {
                IntVec3 current = queue.Dequeue();
                int nextDist = distance[current] + 1;
                if (nextDist > setback) continue;

                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        IntVec3 next = new(current.x + dx, 0, current.z + dz);
                        if (!search.Contains(next) || Footprint.Contains(next) || distance.ContainsKey(next)) continue;
                        distance[next] = nextDist;
                        if (nextDist == setback) ring.Add(next);
                        else queue.Enqueue(next);
                    }
                }
            }
            return ring;
        }
    }
}
