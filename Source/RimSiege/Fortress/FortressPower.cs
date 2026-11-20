using System.Collections.Generic;
using System.Linq;
using ProjectZ;
using RimWorld;
using Verse;

namespace RimSiege.Fortress
{
    internal static class FortressPower
    {
        internal static float FirmCapacity(Map map, FortressLayout layout)
        {
            if (layout.Facilities.Power.IsEmpty) return 0f;
            return layout.Facilities.Power.Interior(map, 0)
                .Select(cell => cell.GetFirstBuilding(map)).OfType<Building>().Distinct()
                .Where(building => building.def == ThingDefOf.ChemfuelPoweredGenerator)
                .Sum(generator => -generator.GetComp<CompPowerPlant>().Props.PowerConsumption);
        }

        internal static void Connect(Map map, FortressLayout layout)
        {
            FortressGeometry geo = layout.Geometry;
            CellRect trunk = geo.CourtyardBounds.ExpandedBy(1);
            var cables = new HashSet<IntVec3>();
            foreach (IntVec3 cell in trunk.EdgeCells)
            {
                if (!geo.Footprint.Contains(cell)) continue;
                cables.Add(cell);
                Add(cell, 1);
            }

            if (layout.Kind == FortressLayoutKind.HumanBastion)
            {
                BastionDimensions dim = BastionDimensions.For(layout.Size);
                IntVec3 center = layout.Outer.CenterCell;
                int tipInset = dim.Half - 2;
                // Run Floor 1 spurs to each bastion tip, Floor 1 parapet rings, and Floor 0 underground vault grids.
                foreach (FortressBastionZone zone in geo.Zones)
                {
                    int sx = zone.XSide == Rot4.East ? 1 : -1;
                    int sz = zone.ZSide == Rot4.North ? 1 : -1;
                    IntVec3 from = zone.Ground.CenterCell;
                    IntVec3 to = new(center.x + sx * tipInset, 0, center.z + sz * tipInset);
                    int xStep = to.x >= from.x ? 1 : -1;
                    int zStep = to.z >= from.z ? 1 : -1;
                    int x = from.x;
                    int z = from.z;
                    Add(new IntVec3(x, 0, z), 1);
                    while (x != to.x || z != to.z)
                    {
                        if (x != to.x)
                        {
                            x += xStep;
                            Add(new IntVec3(x, 0, z), 1);
                        }
                        if (z != to.z)
                        {
                            z += zStep;
                            Add(new IntVec3(x, 0, z), 1);
                        }
                    }

                    if (zone.VaultCells != null)
                    {
                        foreach (IntVec3 cell in zone.VaultCells)
                        {
                            if (cell.x == from.x || cell.z == from.z ||
                                GenAdj.CardinalDirections.Any(dir => !zone.VaultCells.Contains(cell + dir)))
                                cables.Add(cell);
                        }
                    }
                }

                foreach (IntVec3 cell in geo.RampartDeckGround)
                {
                    if (!geo.IsRampartGunCell(cell)) continue;
                    if (System.Math.Abs(cell.x - center.x) <= dim.FlankInner ||
                        System.Math.Abs(cell.z - center.z) <= dim.FlankInner) continue;
                    foreach (IntVec3 dir in GenAdj.AdjacentCells)
                    {
                        if (geo.PerimeterOuterEdge.Contains(cell + dir))
                        {
                            Add(cell, 1);
                            break;
                        }
                    }
                }
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
                    if (geo.Footprint.Contains(new IntVec3(x, 0, center.z)))
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
