using System;
using System.Linq;
using ProjectZ;
using RimSiege.Integration;
using RimWorld;
using Verse;

namespace RimSiege.Raids
{
    internal static class FieldCover
    {
        internal static IntVec3 TryPlace(Pawn pawn, IntVec3 target, Predicate<IntVec3> allowed = null)
        {
            if (pawn is not { Spawned: true } || pawn.Map is not { } map || !target.IsValid)
                return IntVec3.Invalid;
            if (pawn.Position.GetEdifice(map) is Building_Door || IsStairOrLadderTile(pawn.Position, map) ||
                pawn.Position.GetCover(map) != null || AlreadyCoveredFrom(pawn, target, map))
                return IntVec3.Invalid;

            IntVec3 heading = IsoFloor.GroundCellOf(target, map) - IsoFloor.GroundCellOf(pawn.Position, map);
            ThingDef barricade = ThingDefOf.Barricade;
            ThingDef steel = ThingDefOf.Steel;
            foreach (IntVec3 direction in GenAdj.CardinalDirections.OrderByDescending(Projection))
            {
                if (Projection(direction) <= 0) break;
                IntVec3 cell = pawn.Position + direction;
                if (!cell.InBounds(map) || cell.InNoBuildEdgeArea(map) || allowed?.Invoke(cell) == false ||
                    !IsoFloor.SameFloor(cell, pawn.Position, map) || !cell.Standable(map) ||
                    cell.GetCover(map) != null || cell.GetThingList(map).Any(BlocksPlacement) ||
                    IsStairOrLadderTile(cell, map) || HasBlockingNeighbor(cell, map) ||
                    !GenConstruct.CanBuildOnTerrain(barricade, cell, map, Rot4.North, stuffDef: steel) ||
                    !GenSpawn.CanSpawnAt(barricade, cell, map, Rot4.North, canWipeEdifices: false)) continue;

                var cover = (Building)ThingMaker.MakeThing(barricade, steel);
                cover.SetFaction(pawn.Faction);
                GenSpawn.Spawn(cover, cell, map);
                return cell;
            }
            return IntVec3.Invalid;

            int Projection(IntVec3 direction) => heading.x * direction.x + heading.z * direction.z;
        }

        private static bool AlreadyCoveredFrom(Pawn pawn, IntVec3 target, Map map)
        {
            if (CoverUtility.CalculateOverallBlockChance(pawn, target, map) > 0.01f)
                return true;
            IntVec3 heading = IsoFloor.GroundCellOf(target, map) - IsoFloor.GroundCellOf(pawn.Position, map);
            foreach (IntVec3 dir in GenAdj.CardinalDirections)
            {
                if (heading.x * dir.x + heading.z * dir.z <= 0) continue;
                IntVec3 adj = pawn.Position + dir;
                if (adj.InBounds(map) && IsoFloor.SameFloor(adj, pawn.Position, map) && adj.GetCover(map) != null)
                    return true;
            }
            return false;
        }

        private static bool IsStairOrLadderTile(IntVec3 cell, Map map)
        {
            if (!cell.InBounds(map)) return false;
            if (Stairwell.StairCellAt(cell, map) || LadderColumn.Reserved(cell, map)) return true;
            if (cell.GetThingList(map).Any(IsVerticalLinkThing)) return true;
            if (StairGrid.For(map) is { } grid)
                foreach (VLink link in grid.links)
                    if (link.low == cell || link.mid == cell || link.bottom == cell || link.top == cell)
                        return true;
            return false;
        }

        private static bool IsVerticalLinkThing(Thing thing) =>
            thing is Building_Stairs or Building_SiegeLadder or Building_Elevator ||
            Stairwell.IsStairThing(thing) ||
            (thing.def.entityDefToBuild is ThingDef def &&
             (typeof(Building_Stairs).IsAssignableFrom(def.thingClass) ||
              typeof(Building_SiegeLadder).IsAssignableFrom(def.thingClass)));

        private static bool HasBlockingNeighbor(IntVec3 cell, Map map)
        {
            StairGrid grid = StairGrid.For(map);
            foreach (IntVec3 offset in GenAdj.AdjacentCells)
            {
                IntVec3 adj = cell + offset;
                if (!adj.InBounds(map) || !IsoFloor.SameFloor(adj, cell, map)) continue;
                if (adj.GetEdifice(map) is Building_Door) return true;
                if (Stairwell.StairCellAt(adj, map) || adj.GetThingList(map).Any(IsVerticalLinkThing)) return true;
                if (grid != null)
                    foreach (VLink link in grid.links)
                        if (link.low == adj || link.bottom == adj || link.top == adj)
                            return true;
            }
            foreach (IntVec3 dir in GenAdj.CardinalDirections)
            {
                IntVec3 neighbor = cell + dir;
                if (!neighbor.InBounds(map) || !IsoFloor.SameFloor(neighbor, cell, map)) continue;
                if (neighbor.GetCover(map) is { } cover &&
                    (cover is Building || cover.def.Fillage == FillCategory.Full ||
                     cover.def.thingCategories?.Contains(ThingCategoryDefOf.Chunks) == true))
                    return true;
                if (neighbor.GetThingList(map).Any(t =>
                        (t is Building b && (b.def.Fillage != FillCategory.None || b.def.passability == Traversability.Impassable)) ||
                        (t is Blueprint or Frame && t.def.entityDefToBuild is ThingDef td &&
                         (td.Fillage != FillCategory.None || td.passability == Traversability.Impassable))))
                    return true;
            }
            return false;
        }

        private static bool BlocksPlacement(Thing thing) => thing is Building or Blueprint or Frame or Pawn ||
            thing.def.category == ThingCategory.Plant && thing.def.Fillage != FillCategory.None;
    }
}
