using System;
using System.Collections.Generic;
using System.Linq;
using ProjectZ;
using RimWorld;
using Verse;

namespace RimSiege.Fortress
{
    internal enum FortressSitingResult { Accepted, Ground, Gates, Layout, UpperTerrain, ExteriorRoute }

    internal static class FortressSiting
    {
        private static readonly int[] CandidateOffsets = { -88, -72, -48, -24, -8, 0, 8, 24, 48, 72, 88 };
        internal const int EdgeMargin = 12;
        internal const int PreservedSkin = 2;

        internal static bool IsCoarseCenter(IntVec3 offset) =>
            Array.BinarySearch(CandidateOffsets, offset.x) >= 0 &&
            Array.BinarySearch(CandidateOffsets, offset.z) >= 0;

        internal static List<CellRect> GroundCandidates(Map map, int side)
        {
            CellRect ground = Api.GroundRect(map);
            IntVec3 center = ground.CenterCell;
            CellRect usable = ground.ContractedBy(EdgeMargin);
            var candidates = new List<CellRect>();

            foreach (int z in CandidateOffsets)
            {
                foreach (int x in CandidateOffsets)
                {
                    IntVec3 anchor = center + new IntVec3(x, 0, z);
                    CellRect rect = CellRect.CenteredOn(anchor, side, side);
                    if (usable.Contains(rect.Min) && usable.Contains(rect.Max))
                        candidates.Add(rect);
                }
            }

            return candidates.OrderBy(rect => (rect.CenterCell - center).LengthHorizontalSquared).ToList();
        }

        private static bool GroundFits(Map map, CellRect rect)
        {
            foreach (IntVec3 cell in rect)
            {
                Building edifice = cell.GetEdifice(map);
                if (IsNaturalBarrier(cell, map))
                {
                    if (FortressLayout.Depth(rect, cell) < PreservedSkin) continue;
                    return false;
                }
                if (BlocksGround(cell, map)) return false;

                if (edifice != null)
                {
                    CellRect occupied = edifice.OccupiedRect();
                    if (!rect.Contains(occupied.Min) || !rect.Contains(occupied.Max)) return false;
                }
            }
            return true;
        }

        internal static FortressSitingResult Plan(Map map, CellRect outer, FortressDimensions dimensions,
            out FortressLayout layout)
        {
            layout = null;
            if (!GroundFits(map, outer)) return FortressSitingResult.Ground;
            CellRect ground = Api.GroundRect(map);
            var sides = GenAdj.CardinalDirections.Select(Rot4.FromIntVec3)
                .OrderBy(side => FortressLayout.Depth(ground, outer.GetCenterCellOnEdge(side))).ToArray();
            if (sides.Any(side =>
            {
                IntVec3 gate = outer.GetCenterCellOnEdge(side);
                return !(gate + side.FacingCell).Standable(map) ||
                    Enumerable.Range(0, PreservedSkin)
                        .Any(depth => IsNaturalBarrier(gate - side.FacingCell * depth, map));
            })) return FortressSitingResult.Gates;

            var barrier = new HashSet<IntVec3>(outer.EdgeCells.Where(cell => IsNaturalBarrier(cell, map)));
            barrier.UnionWith(outer.ContractedBy(1).EdgeCells.Where(cell => IsNaturalBarrier(cell, map)));
            FortressSitingResult result = FortressSitingResult.Layout;
            foreach (Rot4 side in sides)
            {
                IntVec3 gate = outer.GetCenterCellOnEdge(side);
                IntVec3 approach = gate + side.FacingCell;

                FortressLayout candidate = FortressLayout.Plan(map, outer, side, barrier, dimensions);
                if (candidate == null) continue;
                if (!FitsUpperTerrain(map, candidate))
                {
                    if (result == FortressSitingResult.Layout) result = FortressSitingResult.UpperTerrain;
                    continue;
                }
                if (ExteriorRoute(map, ground, outer, approach))
                {
                    layout = candidate;
                    return FortressSitingResult.Accepted;
                }
                result = FortressSitingResult.ExteriorRoute;
            }
            return result;
        }

        private static bool ExteriorRoute(Map map, CellRect ground, CellRect outer, IntVec3 approach)
        {
            // Check the exterior of the proposed walls. A path through the empty
            // construction site would no longer exist after the gate was built.
            bool reached = false;
            map.floodFiller.FloodFill(approach,
                cell => ground.Contains(cell) && !outer.Contains(cell) && cell.Standable(map) && !cell.GetTerrain(map).IsWater,
                cell => reached = FortressLayout.Depth(ground, cell) == 0 &&
                    (map.TileInfo.AllowRoofedEdgeWalkIn || !map.roofGrid.Roofed(cell)));
            return reached;
        }

        private static bool FitsUpperTerrain(Map map, FortressLayout plan)
        {
            foreach (FortressTier tier in plan.Tiers.Skip(1))
            {
                if (tier.Deck.Any(cell => !UpperSpaceFree(cell, map)) ||
                    tier.Buildings.Any(part => !UpperSpaceFree(part.Cell, map)) ||
                    tier.Openings.Any(cell => !UpperSpaceFree(cell, map)))
                    return false;
            }

            foreach (StairGeometry stair in plan.Tiers.SelectMany(tier => tier.Stairs))
            {
                if (!UpperSpaceFree(stair.exit, map)) return false;
            }

            return true;
        }

        internal static bool UpperSpaceFree(IntVec3 cell, Map map) =>
            cell.InBounds(map) && cell.GetEdifice(map) == null &&
            !Api.HasFloorAt(Api.GroundCellOf(cell, map), Api.FloorOf(cell, map), map);

        internal static bool HasHeavyGround(IntVec3 cell, Map map) =>
            cell.GetTerrain(map).passability != Traversability.Impassable &&
            cell.GetAffordances(map).Contains(TerrainAffordanceDefOf.Heavy);

        internal static bool BlocksGround(IntVec3 cell, Map map) =>
            map.roofGrid.RoofAt(cell)?.isThickRoof == true || cell.GetEdifice(map)?.def.destroyable == false;

        internal static bool IsNaturalBarrier(IntVec3 cell, Map map)
        {
            TerrainDef terrain = cell.GetTerrain(map);
            return cell.GetEdifice(map)?.def.building.isNaturalRock == true ||
                terrain.IsWater && terrain.passability == Traversability.Impassable;
        }
    }
}
