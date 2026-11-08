using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.BaseGen;
using Verse;

namespace RimSiege.Fortress
{
    internal static class FortressStronghold
    {
        internal static void Furnish(Map map, FortressLayout layout, ThingDef stuff)
        {
            CellRect keep = layout.Modules.First(module => module.Kind == FortressModuleKind.Keep).Ground;
            CellRect room = keep.ContractedBy(1);
            HashSet<IntVec3> access = Approaches(room, map);
            foreach (var stair in layout.Tiers[0].Stairs)
                if (keep.Contains(stair.center)) access.UnionWith(stair.footprint.AdjacentCellsCardinal.Where(room.Contains));

            bool powered = !layout.Facilities.Power.IsEmpty;
            if (powered) AddBatteries(map, room, access);

            BaseGen.globalSettings.map = map;
            if (powered)
                BaseGen.symbolStack.Push("chargeBatteries", new ResolveParams { rect = room, faction = map.ParentFaction });
            PlanBeds(map, room, access, keep.Width >= 13 ? 2 : 1);
            BaseGen.Generate();
            FortressStorage.PlaceShelves(map, room, stuff, access, limit: 2);

            foreach (FortressModule module in layout.Modules.Where(module => module.Kind == FortressModuleKind.Tower))
            {
                CellRect tower = module.Ground.ContractedBy(1);
                HashSet<IntVec3> towerAccess = Approaches(tower, map);
                foreach (FortressLadder ladder in layout.Ladders)
                {
                    if (!tower.Contains(ladder.Foot)) continue;
                    towerAccess.Add(ladder.Foot);
                    towerAccess.UnionWith(GenAdj.CardinalDirections.Select(direction => ladder.Foot + direction).Where(tower.Contains));
                }
                FortressStorage.PlaceShelves(map, tower, stuff, towerAccess, limit: 2);
            }
        }

        private static HashSet<IntVec3> Approaches(CellRect room, Map map) => new(room.Where(cell =>
            cell.x == room.CenterCell.x || cell.z == room.CenterCell.z ||
            BaseGenUtility.AnyDoorAdjacentCardinalTo(cell, map)));

        private static void AddBatteries(Map map, CellRect room, HashSet<IntVec3> access)
        {
            ThingDef def = ThingDefOf.Battery;
            int count = 0;
            foreach (IntVec3 root in room.EdgeCells.OrderByDescending(cell => cell.z))
            {
                Rot4 rotation = root.z == room.maxZ || root.z == room.minZ ? Rot4.East : Rot4.North;
                CellRect footprint = GenAdj.OccupiedRect(root, rotation, def.Size);
                if (!Fits(map, room, footprint, access) || !footprint.All(cell => cell.Roofed(map))) continue;
                FortressConstruction.Spawn(def, root, rotation, null, map);
                access.UnionWith(footprint);
                if (++count == 2) break;
            }
        }

        private static void PlanBeds(Map map, CellRect room, HashSet<IntVec3> access, int pairs)
        {
            Faction faction = map.ParentFaction;
            ThingDef bed = FortressBarracksResolver.BedFor(faction);
            int axis = room.CenterCell.x;
            var occupied = new HashSet<IntVec3>();
            foreach (IntVec3 left in room.Where(cell => cell.x < axis)
                .OrderBy(cell => System.Math.Abs(cell.z - room.CenterCell.z)).ThenBy(cell => cell.x))
            {
                IntVec3 right = new(2 * axis - left.x, 0, left.z);
                CellRect west = GenAdj.OccupiedRect(left, Rot4.North, bed.Size);
                CellRect east = GenAdj.OccupiedRect(right, Rot4.North, bed.Size);
                if (!Fits(map, room, west, access) || !Fits(map, room, east, access) ||
                    west.Any(occupied.Contains) || east.Any(occupied.Contains)) continue;

                PushBed(west);
                PushBed(east);
                occupied.UnionWith(west);
                occupied.UnionWith(east);
                if (--pairs == 0) break;
            }

            void PushBed(CellRect footprint) =>
                BaseGen.symbolStack.Push("bed", new ResolveParams
                {
                    rect = footprint,
                    faction = faction,
                    thingRot = Rot4.North,
                    singleThingDef = bed
                });
        }

        private static bool Fits(Map map, CellRect room, CellRect footprint, HashSet<IntVec3> access) =>
            footprint.FullyContainedWithin(room) &&
            footprint.All(cell => !access.Contains(cell) && cell.Standable(map) && cell.GetFirstBuilding(map) == null) &&
            footprint.AdjacentCellsCardinal.Any(cell => access.Contains(cell) && cell.Standable(map));
    }
}
