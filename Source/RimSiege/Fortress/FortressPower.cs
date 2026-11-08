using System.Collections.Generic;
using ProjectZ;
using RimWorld;
using Verse;

namespace RimSiege.Fortress
{
    internal static class FortressPower
    {
        internal static void Connect(Map map, FortressLayout layout)
        {
            CellRect trunk = layout.Outer.ContractedBy(FortressLayout.CurtainThickness - 1);
            var cables = new HashSet<IntVec3>();
            foreach (IntVec3 cell in trunk.EdgeCells)
            {
                cables.Add(cell);
                cables.Add(Api.CellOnFloor(cell, 1, map));
            }

            foreach (FortressModule module in layout.Modules)
            {
                CellRect room = module.Ground;
                IntVec3 center = room.CenterCell;
                for (int level = 0; level <= module.Top; level++)
                {
                    foreach (IntVec3 cell in room.EdgeCells)
                        Add(cell, level);
                    for (int x = room.minX + 1; x < room.maxX; x++)
                        Add(new IntVec3(x, 0, center.z), level);
                    for (int z = room.minZ + 1; z < room.maxZ; z++)
                        Add(new IntVec3(center.x, 0, z), level);
                }

                // Ground branches meet the curtain trunk, so the stacked perimeter
                // cables on each module form risers without a separate power network.
                bool left = center.x <= trunk.CenterCell.x;
                int wall = left ? trunk.minX : trunk.maxX;
                int edge = left ? room.minX : room.maxX;
                for (int x = System.Math.Min(wall, edge); x <= System.Math.Max(wall, edge); x++)
                    cables.Add(new IntVec3(x, 0, center.z));
            }

            ThingDef conduit = ThingDefOf.HiddenConduit;
            foreach (IntVec3 cell in cables)
            {
                if (cell.GetTransmitter(map) == null && GenConstruct.CanBuildOnTerrain(conduit, cell, map, Rot4.North))
                    FortressConstruction.Spawn(conduit, cell, Rot4.North, null, map);
            }
            map.powerNetManager.UpdatePowerNetsAndConnections_First();

            void Add(IntVec3 ground, int level)
            {
                if (level == 0 || Api.HasFloorAt(ground, level, map))
                    cables.Add(Api.CellOnFloor(ground, level, map));
            }
        }
    }
}
