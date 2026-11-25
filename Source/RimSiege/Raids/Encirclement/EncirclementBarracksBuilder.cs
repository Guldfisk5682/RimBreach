using System.Collections.Generic;
using System.Linq;
using RimSiege.Integration;
using RimSiege.Raids.Artillery;
using RimWorld;
using Verse;

namespace RimSiege.Raids.Encirclement
{
    internal static class EncirclementBarracksBuilder
    {
        internal const int GroundBedLimit = 12;

        internal static void LayOut(QuestPart_Encirclement siege)
        {
            Map map = siege.Map;
            EncirclementCampPlan plan = siege.Plan;
            EncirclementWork work = siege.Work;
            ThingDef bedroll = DefDatabase<ThingDef>.GetNamed("Bedroll");
            int bedsPlaced = 0;
            for (int index = 0; index < plan.Rooms.Count; index++)
            {
                EncirclementRoom room = plan.Rooms[index];
                bool barracks = room.Device == null;
                AddShell(room.Footprint, room, index, step: 0, doors: barracks ? 2 : 1);
                if (!barracks)
                {
                    work.Add(new EncirclementProject
                    {
                        Building = room.Device, Cell = room.Footprint.CenterCell, Room = index, Step = 1,
                        Rotation = room.Facing
                    });
                    continue;
                }
                if (room.Stairs.IsValid && ProjectZ.Api.TryGetStairGeometry(room.Stairs, Rot4.North, map, out var stair) &&
                    ProjectZ.Api.TryGetFloorFor(ThingDefOf.Steel, out TerrainDef slab))
                {
                    work.Add(new EncirclementProject
                    {
                        Building = DefDatabase<ThingDef>.GetNamed("PZ_Stairs"), Stuff = ThingDefOf.Steel,
                        Cell = room.Stairs, Rotation = Rot4.North, Room = index
                    });
                    CellRect upper = room.OnFloor(map, 1);
                    foreach (IntVec3 cell in upper.OrderBy(c => c.DistanceToSquared(stair.exit)))
                        if (cell != stair.exit && !stair.well.Contains(cell))
                            work.Add(new EncirclementProject { Slab = slab, Cell = cell, Room = index, Step = 1, Rotation = Rot4.North });
                    AddShell(upper, room, index, step: 2, doors: 0);
                    AddBeds(room, index, upper: false);
                    AddBeds(room, index, upper: true);
                }
                else AddBeds(room, index, upper: false);
            }

            foreach (IntVec3 cell in plan.Fixtures)
                work.Add(new EncirclementProject
                {
                    Building = siege.Mechanoid ? ThingDefOf.Gloomlight : ThingDefOf.Campfire,
                    Cell = cell, Rotation = Rot4.North, Install = siege.Mechanoid
                });

            for (int i = 0; i < plan.BatteryKeys.Count; i++)
            {
                CampPlan battery = siege.Battery(i).Camp;
                for (int gun = 0; gun < battery.Guns.Count; gun++)
                    AddPlacement(battery.Guns[gun], i, gun);
                foreach (ArtilleryPlacement cover in battery.Cover) AddPlacement(cover, i, -1);
            }

            void AddShell(CellRect rect, EncirclementRoom room, int index, int step, int doors)
            {
                foreach (IntVec3 cell in rect.EdgeCells)
                {
                    bool door = doors >= 1 && cell == room.Door() || doors == 2 && cell == room.Door(rear: true);
                    work.Add(new EncirclementProject
                    {
                        Building = door ? ThingDefOf.Door : ThingDefOf.Wall, Stuff = ThingDefOf.Steel,
                        Cell = cell, Rotation = door ? room.Facing : Rot4.North, Room = index, Step = step
                    });
                }
            }

            void AddBeds(EncirclementRoom room, int index, bool upper)
            {
                foreach (var (cell, rot) in BedSlots(map, room, upper, bedroll.Size).Take(plan.BedrollLimit - bedsPlaced))
                {
                    work.Add(new EncirclementProject
                    {
                        Building = bedroll, Stuff = ThingDefOf.Cloth, Cell = cell, Rotation = rot,
                        Room = index, Step = upper ? 2 : 0, Install = true, Medical = ++bedsPlaced % 5 == 0
                    });
                }
            }

            void AddPlacement(ArtilleryPlacement item, int battery, int gun) => work.Add(new EncirclementProject
            {
                Building = item.Def, Stuff = item.Stuff, Cell = item.Cell, Rotation = item.Rotation,
                Battery = battery, Gun = gun
            });
        }

        internal static IEnumerable<(IntVec3 Cell, Rot4 Rotation)> BedSlots(Map map, EncirclementRoom room,
            bool upper, IntVec2 bedSize)
        {
            CellRect rect = upper ? room.OnFloor(map, 1) : room.Footprint;
            CellRect interior = rect.ContractedBy(1);
            var access = new HashSet<IntVec3>();
            if (!upper && room.Facing.IsHorizontal)
            {
                for (int x = interior.minX; x <= interior.maxX; x++)
                    access.Add(new IntVec3(x, 0, interior.minZ));
            }
            else if (!upper)
            {
                for (int z = interior.minZ; z <= interior.maxZ; z++)
                    access.Add(new IntVec3(rect.CenterCell.x, 0, z));
            }
            if (room.Stairs.IsValid && ProjectZ.Api.TryGetStairGeometry(room.Stairs, Rot4.North, map, out var stair))
            {
                access.UnionWith(upper ? stair.well : stair.footprint);
                IntVec3 stairAccess = upper ? stair.exit : stair.low;
                access.Add(stairAccess);
                for (int x = stairAccess.x; x <= rect.CenterCell.x; x++)
                    access.Add(new IntVec3(x, 0, stairAccess.z));
            }
            return PackBeds(interior, access, bedSize).Take(upper ? 14 : GroundBedLimit);
        }

        // Match adjacent free cells to pack 1x2 beds while preserving stair and door aisles.
        internal static IEnumerable<(IntVec3 Cell, Rot4 Rotation)> PackBeds(CellRect interior,
            HashSet<IntVec3> access, IntVec2 bedSize)
        {
            var bySecondCell = new Dictionary<IntVec3, IntVec3>();
            foreach (IntVec3 first in interior.Where(c => ((c.x + c.z) & 1) == 0 && !access.Contains(c)))
                Match(first, new HashSet<IntVec3>());

            foreach (var pair in bySecondCell.OrderBy(p => p.Key.x).ThenBy(p => p.Key.z))
            {
                IntVec3 first = pair.Value;
                IntVec3 second = pair.Key;
                Rot4 rotation = first.x == second.x ? Rot4.North : Rot4.East;
                IntVec3 anchor = GenAdj.OccupiedRect(first, rotation, bedSize).Contains(second) ? first : second;
                yield return (anchor, rotation);
            }

            bool Match(IntVec3 first, HashSet<IntVec3> visited)
            {
                foreach (IntVec3 direction in GenAdj.CardinalDirections)
                {
                    IntVec3 second = first + direction;
                    if (!interior.Contains(second) || access.Contains(second) || !visited.Add(second)) continue;
                    if (bySecondCell.TryGetValue(second, out IntVec3 displaced) && !Match(displaced, visited)) continue;
                    bySecondCell[second] = first;
                    return true;
                }
                return false;
            }
        }

        internal static bool ReviewRoom(QuestPart_Encirclement siege, EncirclementRoom room,
            List<EncirclementProject> projects, bool debugging)
        {
            Map map = siege.Map;
            if (projects.Any(p => !p.Funded)) return false;
            EncirclementWork work = siege.Work;

            bool Complete(int step) => projects.Where(p => p.Step == step && !p.Install).All(p => p.Finished(map));
            bool Roof(CellRect rect, HashSet<IntVec3> openings = null)
            {
                bool roofed = true;
                foreach (IntVec3 cell in rect.ContractedBy(1))
                {
                    if (openings?.Contains(cell) == true || cell.Roofed(map)) continue;
                    if (debugging && RoofCollapseUtility.WithinRangeOfRoofHolder(cell, map))
                        map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                    if (cell.Roofed(map)) continue;
                    roofed = false;
                    work.SetRoofArea(map, cell);
                }
                return roofed;
            }
            void Conceal(CellRect rect, ref bool flag)
            {
                if (flag) return;
                flag = true;
                map.GetComponent<EncirclementFog>().ConcealRoom(rect);
            }

            bool floor0 = Complete(0);
            if (room.Device != null)
            {
                if (!floor0 || !Roof(room.Footprint)) return false;
                Conceal(room.Footprint, ref room.GroundConcealed);
                room.Stage = 1;
                EncirclementProject device = projects.Single(p => p.IsDevice);
                if (device.Current is Building building and not Frame)
                    building.GetComp<CompEncirclementDevice>().Commission();
                return device.Finished(map) || device.DeviceDestroyed(map);
            }
            if (!room.Stairs.IsValid)
            {
                if (!floor0 || !Roof(room.Footprint)) return false;
                Conceal(room.Footprint, ref room.GroundConcealed);
                return true;
            }
            if (!floor0) return false;
            room.Stage = System.Math.Max(1, room.Stage);
            ProjectZ.Api.TryGetStairGeometry(room.Stairs, Rot4.North, map, out var geometry);
            bool coveredGround = Roof(room.Footprint, geometry.ramp?.ToHashSet());
            if (coveredGround) Conceal(room.Footprint, ref room.GroundConcealed);
            if (!Complete(1)) return false;
            room.Stage = 2;
            CellRect upper = room.OnFloor(map, 1);
            if (!Complete(2) || !Roof(upper, geometry.well?.ToHashSet())) return false;
            Conceal(upper, ref room.UpperConcealed);
            return coveredGround;
        }
    }
}
