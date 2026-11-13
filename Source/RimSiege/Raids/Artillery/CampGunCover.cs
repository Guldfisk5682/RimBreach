using System.Collections.Generic;
using System.Linq;
using RimSiege.Integration;
using RimWorld;
using Verse;

namespace RimSiege.Raids.Artillery
{
    internal static class CampGunCover
    {
        internal static IEnumerable<ArtilleryPlacement> Candidates(Map map, CampPlan plan, IntVec3 target)
        {
            ThingDef wall = DefDatabase<ThingDef>.GetNamed("CE_Embrasure");
            foreach (ArtilleryPlacement gun in plan.Guns)
            {
                IntVec3 facing = IsoFloor.GroundCellOf(target, map) - IsoFloor.GroundCellOf(gun.Cell, map);
                Rot4 front = GenAdj.CardinalDirections.Select(Rot4.FromIntVec3)
                    .OrderByDescending(dir => dir.FacingCell.x * facing.x + dir.FacingCell.z * facing.z).First();
                CellRect footprint = GenAdj.OccupiedRect(gun.Cell, gun.Rotation, gun.Def.Size);
                IntVec3 step = front.FacingCell;
                foreach (IntVec3 edge in footprint)
                {
                    if (footprint.Contains(edge + step)) continue;
                    IntVec3 cell = edge + step;
                    var cover = new ArtilleryPlacement { Cell = cell, Def = wall, Stuff = ThingDefOf.Steel };
                    if (IsoFloor.SameFloor(cell, gun.Cell, map) && CampPlanner.CanPlace(map, cover))
                        yield return cover;
                }
            }
        }
    }
}
