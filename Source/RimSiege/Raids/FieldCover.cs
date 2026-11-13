using System;
using System.Linq;
using RimSiege.Integration;
using RimWorld;
using Verse;

namespace RimSiege.Raids
{
    internal static class FieldCover
    {
        internal static IntVec3 TryPlace(Pawn pawn, IntVec3 target, Predicate<IntVec3> allowed = null)
        {
            Map map = pawn.Map;
            IntVec3 heading = IsoFloor.GroundCellOf(target, map) - IsoFloor.GroundCellOf(pawn.Position, map);
            ThingDef barricade = ThingDefOf.Barricade;
            ThingDef steel = ThingDefOf.Steel;
            foreach (IntVec3 direction in GenAdj.CardinalDirections.OrderByDescending(Projection))
            {
                if (Projection(direction) <= 0) break;
                IntVec3 cell = pawn.Position + direction;
                if (!cell.InBounds(map) || cell.InNoBuildEdgeArea(map) || allowed?.Invoke(cell) == false ||
                    !IsoFloor.SameFloor(cell, pawn.Position, map) || !cell.Standable(map) ||
                    cell.GetThingList(map).Any(BlocksPlacement) ||
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

        private static bool BlocksPlacement(Thing thing) => thing is Building or Blueprint or Frame or Pawn ||
            thing.def.category == ThingCategory.Plant && thing.def.Fillage != FillCategory.None;
    }
}
