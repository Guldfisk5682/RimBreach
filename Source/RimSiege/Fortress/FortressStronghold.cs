using System.Collections.Generic;
using System.Linq;
using ProjectZ;
using RimWorld;
using RimWorld.BaseGen;
using Verse;

namespace RimSiege.Fortress
{
    internal static class FortressStronghold
    {
        internal static IntVec3 Furnish(Map map, FortressLayout layout, ThingDef stuff, bool reserveLootShelf)
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
            bool hasUpperRoom = layout.Tiers.Length > 2;
            IntVec3 lootShelf = reserveLootShelf && !hasUpperRoom
                ? PlaceLootShelf(map, room, stuff, access)
                : IntVec3.Invalid;
            FortressStorage.PlaceShelves(map, room, stuff, access, limit: 2);
            if (hasUpperRoom)
            {
                lootShelf = FurnishUpperKeep(map, layout, keep, stuff, reserveLootShelf);
                if (reserveLootShelf && !lootShelf.IsValid) lootShelf = PlaceLootShelf(map, room, stuff, access);
            }

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
                // Large 7x7 towers (5x5 interior) have room for up to 4 edge shelves without blocking doors or ladders.
                FortressStorage.PlaceShelves(map, tower, stuff, towerAccess, limit: tower.Width >= 5 ? 4 : 2);
            }
            return lootShelf;
        }

        private static IEnumerable<IntVec3> KeepLootPositions(CellRect room)
        {
            IntVec3 center = room.CenterCell;
            var preferred = new[]
            {
                center + new IntVec3(1, 0, 1),
                center + new IntVec3(2, 0, 1),
                center + new IntVec3(1, 0, -1),
                center + new IntVec3(2, 0, -1)
            };
            var seen = new HashSet<IntVec3>();
            foreach (IntVec3 cell in preferred)
                if (room.Contains(cell) && seen.Add(cell)) yield return cell;
            foreach (IntVec3 cell in room
                .OrderBy(candidate => room.IsOnEdge(candidate))
                .ThenBy(candidate => (candidate - center).LengthHorizontalSquared))
                if (seen.Add(cell)) yield return cell;
        }

        private static IntVec3 PlaceLootShelf(Map map, CellRect room, ThingDef stuff, HashSet<IntVec3> access)
        {
            ThingDef def = ThingDefOf.ShelfSmall;
            foreach (IntVec3 cell in KeepLootPositions(room))
            {
                if (access.Contains(cell) || !cell.Standable(map) || cell.GetFirstBuilding(map) != null ||
                    !GenConstruct.CanBuildOnTerrain(def, cell, map, Rot4.North, stuffDef: stuff) ||
                    !GenSpawn.CanSpawnAt(def, cell, map, Rot4.North, canWipeEdifices: false))
                    continue;
                IntVec3 workingCell = GenAdj.CardinalDirections.Select(dir => cell + dir).FirstOrDefault(neighbor =>
                    room.Contains(neighbor) && neighbor.Standable(map) && neighbor.GetFirstBuilding(map) == null,
                    IntVec3.Invalid);
                if (!workingCell.IsValid) continue;
                var shelf = (Building_Storage)FortressConstruction.Spawn(def, cell, Rot4.North, stuff, map);
                shelf.GetStoreSettings().filter.SetDisallowAll();
                access.Add(cell);
                access.Add(workingCell);
                return cell;
            }
            return IntVec3.Invalid;
        }

        private static IntVec3 FurnishUpperKeep(Map map, FortressLayout layout, CellRect keep, ThingDef stuff,
            bool reserveLootShelf)
        {
            CellRect inside = keep.ContractedBy(1);
            CellRect room = CellRect.FromLimits(Api.CellOnFloor(inside.Min, 1, map), Api.CellOnFloor(inside.Max, 1, map));
            HashSet<IntVec3> access = Approaches(room, map);
            access.UnionWith(layout.Tiers[1].Openings.Where(room.Contains));
            foreach (Rot4 face in GenAdj.CardinalDirections.Select(Rot4.FromIntVec3))
                access.UnionWith(CellRect.CenteredOn(room.GetCenterCellOnEdge(face) - face.FacingCell, 3, 3).ClipInsideRect(room));
            foreach (var stair in layout.Tiers[0].Stairs)
            {
                if (!keep.Contains(stair.center)) continue;
                access.Add(stair.exit);
                access.UnionWith(GenAdj.CardinalDirections.Select(dir => stair.exit + dir).Where(room.Contains));
            }
            foreach (var stair in layout.Tiers[1].Stairs)
            {
                if (!room.Contains(stair.center)) continue;
                access.UnionWith(stair.footprint);
                access.UnionWith(stair.footprint.AdjacentCellsCardinal.Where(room.Contains));
            }

            IntVec3 lootShelf = reserveLootShelf ? PlaceLootShelf(map, room, stuff, access) : IntVec3.Invalid;
            List<Building_Storage> shelves = FortressStorage.PlaceShelves(map, room, stuff, access,
                limit: keep.Width >= 13 ? 8 : 6);
            foreach (Building_Storage shelf in shelves)
                access.UnionWith(shelf.OccupiedRect());

            if (keep.Width >= 13)
            {
                BaseGen.globalSettings.map = map;
                PlanBeds(map, room, access, 1);
                BaseGen.Generate();
            }
            return lootShelf;
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
