using System.Collections.Generic;
using System.Linq;
using RimSiege.Integration;
using RimWorld;
using Verse;

namespace RimSiege.Raids.Artillery
{
    internal static class CampGunCover
    {
        internal static IEnumerable<ArtilleryPlacement> Candidates(Map map, ArtilleryRaid raid, CampPlan plan, IntVec3 target)
        {
            ThingDef def;
            if (raid.Kind == ArtilleryRaidKind.Mechanoid) def = ThingDefOf.Barricade;
            else if (ArtillerySmoke.Eligible(raid)) def = DefDatabase<ThingDef>.GetNamed("CE_Embrasure");
            else return Enumerable.Empty<ArtilleryPlacement>();
            return FrontCandidates(map, plan, target, def, ThingDefOf.Steel);
        }

        private static IEnumerable<ArtilleryPlacement> FrontCandidates(Map map, CampPlan plan, IntVec3 target,
            ThingDef def, ThingDef stuff)
        {
            var blocked = new HashSet<IntVec3>();
            foreach (ArtilleryPlacement cover in plan.Cover) blocked.Add(cover.Cell);
            foreach (ArtilleryPlacement gun in plan.Guns)
                foreach (IntVec3 cell in GenAdj.OccupiedRect(gun.Cell, gun.Rotation, gun.Def.Size))
                    blocked.Add(cell);

            foreach (ArtilleryPlacement gun in plan.Guns)
            {
                IntVec3 facing = IsoFloor.GroundCellOf(target, map) - IsoFloor.GroundCellOf(gun.Cell, map);
                Rot4 front = GenAdj.CardinalDirections.Select(Rot4.FromIntVec3)
                    .OrderByDescending(dir => dir.FacingCell.x * facing.x + dir.FacingCell.z * facing.z).First();
                CellRect footprint = GenAdj.OccupiedRect(gun.Cell, gun.Rotation, gun.Def.Size);
                IntVec3 step = front.FacingCell;
                foreach (IntVec3 edge in footprint)
                {
                    IntVec3 cell = edge + step;
                    if (footprint.Contains(cell) || !blocked.Add(cell)) continue;
                    var cover = new ArtilleryPlacement { Cell = cell, Def = def, Stuff = stuff };
                    if (IsoFloor.SameFloor(cell, gun.Cell, map) && CampPlanner.CanPlace(map, cover))
                        yield return cover;
                }
            }
        }
    }
}
