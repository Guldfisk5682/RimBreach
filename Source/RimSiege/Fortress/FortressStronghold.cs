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
            if (layout.Kind == FortressLayoutKind.MechCircularBunker)
                return FurnishMechBunker(map, layout, stuff);

            CellRect keep = layout.Modules.First(module => module.Kind == FortressModuleKind.Keep).Ground;
            CellRect room = keep.ContractedBy(1);
            HashSet<IntVec3> access = Approaches(room, map);
            foreach (var stair in layout.Tiers[0].Stairs)
                if (keep.Contains(stair.center)) access.UnionWith(stair.footprint.AdjacentCellsCardinal.Where(room.Contains));
            foreach (FortressLadder ladder in layout.Ladders)
            {
                if (!room.Contains(ladder.Foot)) continue;
                access.Add(ladder.Foot);
                access.UnionWith(GenAdj.CardinalDirections.Select(direction => ladder.Foot + direction).Where(room.Contains));
            }

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

            if (layout.Kind == FortressLayoutKind.HumanBastion)
            {
                FurnishBastionVaults(map, layout, stuff, powered);
            }
            else
            {
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
            }
            return lootShelf;
        }

        private static void FurnishBastionVaults(Map map, FortressLayout layout, ThingDef stuff, bool powered)
        {
            Faction faction = map.ParentFaction;
            bool industrial = layout.Facilities.Industrial;
            ThingDef medicine = ModsConfig.RoyaltyActive && faction.def == FactionDefOf.Empire
                ? ThingDefOf.MedicineUltratech : industrial ? ThingDefOf.MedicineIndustrial : ThingDefOf.MedicineHerbal;
            ThingDef ration = industrial ? ThingDefOf.MealSurvivalPack : ThingDefOf.Pemmican;
            ThingDef lampDef = powered ? ThingDefOf.StandingLamp : ThingDefOf.TorchLamp;
            int shelfLimit = layout.Size == FortressSize.Large ? 10 : 6;
            int componentQuota = layout.Size == FortressSize.Large ? 2 : 1;

            foreach (FortressBastionZone zone in layout.Geometry.Zones)
            {
                if (zone.VaultCells == null || zone.VaultCells.Count == 0) continue;
                HashSet<IntVec3> vault = zone.VaultCells;
                IntVec3 center = zone.Ground.CenterCell;
                var access = new HashSet<IntVec3>(vault.Where(cell =>
                    cell.x == center.x || cell.z == center.z ||
                    BaseGenUtility.AnyDoorAdjacentCardinalTo(cell, map)));
                foreach (FortressLadder ladder in layout.Ladders)
                {
                    if (!vault.Contains(ladder.Foot)) continue;
                    access.Add(ladder.Foot);
                    foreach (IntVec3 dir in GenAdj.CardinalDirections)
                        if (vault.Contains(ladder.Foot + dir)) access.Add(ladder.Foot + dir);
                }

                // Place a standing lamp (or torch) near the vault center without blocking doors or the ladder.
                IntVec3 lampCell = vault
                    .Where(cell => !BaseGenUtility.AnyDoorAdjacentCardinalTo(cell, map) &&
                        !layout.Ladders.Any(ladder => ladder.Foot == cell || ladder.Foot.AdjacentToCardinal(cell)) &&
                        cell.Standable(map) && cell.GetFirstBuilding(map) == null)
                    .OrderBy(cell => (cell - center).LengthHorizontalSquared)
                    .FirstOrDefault(IntVec3.Invalid);
                if (lampCell.IsValid && lampDef != null)
                {
                    ThingDef lampStuff = lampDef.MadeFromStuff ? stuff : null;
                    Thing lamp = FortressConstruction.Spawn(lampDef, lampCell, Rot4.North, lampStuff, map);
                    if (lamp.TryGetComp<CompRefuelable>() is { } refuelable)
                        refuelable.Refuel(refuelable.Props.fuelCapacity);
                    access.Add(lampCell);
                }

                List<Building_Storage> shelves = FortressStorage.PlaceShelvesInCells(
                    map, vault, center, stuff, access, limit: shelfLimit);
                // Reserve half the vault shelves for ammunition; fill the rest with strategic reserves.
                int reserveCount = shelves.Count / 2;
                if (reserveCount <= 0) continue;

                FortressStorage.Fill(map, shelves.Take(reserveCount), index => VaultReserveItem(index));
            }

            ThingDef VaultReserveItem(int index)
            {
                if (!industrial)
                {
                    return (index % 6) switch
                    {
                        0 => ThingDefOf.Steel,
                        1 => stuff,
                        2 => ThingDefOf.WoodLog,
                        3 => medicine,
                        4 => ration,
                        _ => stuff
                    };
                }

                if (index < componentQuota) return ThingDefOf.ComponentIndustrial;
                return ((index - componentQuota) % 6) switch
                {
                    0 => ThingDefOf.Steel,
                    1 => ThingDefOf.Plasteel,
                    2 => stuff,
                    3 => medicine,
                    4 => ration,
                    _ => ThingDefOf.Steel
                };
            }
        }

        private static IntVec3 FurnishMechBunker(Map map, FortressLayout layout, ThingDef stuff)
        {
            FortressGeometry geo = layout.Geometry;
            IntVec3 center = layout.Outer.CenterCell;
            ThingDef gloomlight = DefDatabase<ThingDef>.GetNamedSilentFail("Gloomlight");
            ThingDef stone = FactionMaterial.HardestLocalStone(map);
            ThingDef reinforcedBarrel = DefDatabase<ThingDef>.GetNamedSilentFail("ReinforcedBarrel");
            ThingDef unstableCell = DefDatabase<ThingDef>.GetNamedSilentFail("UnstablePowerCell");
            ThingDef toxifier = DefDatabase<ThingDef>.GetNamedSilentFail("ToxifierGenerator");
            ThingDef gestator = DefDatabase<ThingDef>.GetNamedSilentFail("MechGestator");
            ThingDef largeGestator = DefDatabase<ThingDef>.GetNamedSilentFail("LargeMechGestator");
            ThingDef ripscanner = DefDatabase<ThingDef>.GetNamedSilentFail("SubcoreRipscanner");
            ThingDef softscanner = DefDatabase<ThingDef>.GetNamedSilentFail("SubcoreSoftscanner");
            ThingDef encoder = DefDatabase<ThingDef>.GetNamedSilentFail("SubcoreEncoder");
            ThingDef mechBooster = DefDatabase<ThingDef>.GetNamedSilentFail("MechBooster");
            ThingDef bandNode = DefDatabase<ThingDef>.GetNamedSilentFail("BandNode");
            ThingDef ancientBandNode = DefDatabase<ThingDef>.GetNamedSilentFail("AncientBandNode");
            ThingDef ancientToxifier = DefDatabase<ThingDef>.GetNamedSilentFail("AncientToxifierGenerator");

            bool needsBarrels = reinforcedBarrel != null && !Find.Storyteller.difficulty.classicMortars;
            bool large = layout.Size == FortressSize.Large;
            bool barrelPlaced = false;
            var poweredNodes = new List<IntVec3>();
            float totalDemand = 0f;
            float totalGeneration = 0f;

            var reservedHall = new HashSet<IntVec3>();
            foreach (FortressLadder ladder in layout.Ladders)
            {
                reservedHall.Add(ladder.Foot);
                foreach (IntVec3 dir in GenAdj.CardinalDirections) reservedHall.Add(ladder.Foot + dir);
            }
            foreach (CellRect silo in geo.MortarSilos) reservedHall.UnionWith(silo.ExpandedBy(1).Cells);
            foreach (FortressEmplacementSlot slot in geo.Slots) reservedHall.Add(slot.Ground);
            var reservedInteract = new HashSet<IntVec3>();

            // 1. Core Chamber: multiple mechanoid gestators (Compact: 1 Large + 2 Small; Large: 2 Large + 2 Small) + Gloomlights.
            var coreGestators = new List<(ThingDef Def, IntVec3 Prefer, Rot4 Rot)>();
            if (large)
            {
                if (largeGestator != null)
                {
                    coreGestators.Add((largeGestator, center + new IntVec3(-2, 0, 2), Rot4.South));
                    coreGestators.Add((largeGestator, center + new IntVec3(2, 0, -2), Rot4.North));
                }
                if (gestator != null)
                {
                    coreGestators.Add((gestator, center + new IntVec3(3, 0, 2), Rot4.West));
                    coreGestators.Add((gestator, center + new IntVec3(-3, 0, -2), Rot4.East));
                }
            }
            else
            {
                if (largeGestator != null)
                    coreGestators.Add((largeGestator, center + new IntVec3(0, 0, 1), Rot4.South));
                if (gestator != null)
                {
                    coreGestators.Add((gestator, center + new IntVec3(-2, 0, -2), Rot4.North));
                    coreGestators.Add((gestator, center + new IntVec3(2, 0, -2), Rot4.North));
                }
            }

            foreach (var (podDef, prefer, rot) in coreGestators)
            {
                if (!TryPlaceBuildingInCells(podDef, geo.CoreInterior, prefer, rot, allowDoorAdjacent: false, out _) &&
                    podDef != gestator && gestator != null)
                    TryPlaceBuildingInCells(gestator, geo.CoreInterior, prefer, rot, allowDoorAdjacent: false, out _);
            }

            if (gloomlight != null)
            {
                foreach (IntVec3 offset in new[]
                {
                    new IntVec3(-2, 0, 2), new IntVec3(2, 0, 2),
                    new IntVec3(2, 0, -2), new IntVec3(-2, 0, -2),
                    new IntVec3(-4, 0, 1), new IntVec3(4, 0, -1),
                    new IntVec3(-1, 0, 4), new IntVec3(1, 0, -4)
                })
                {
                    IntVec3 cell = center + offset;
                    if (geo.CoreInterior.Contains(cell) && cell.Standable(map) && cell.GetFirstBuilding(map) == null &&
                        !reservedInteract.Contains(cell) && !BaseGenUtility.AnyDoorAdjacentCardinalTo(cell, map))
                        FortressConstruction.Spawn(gloomlight, cell, Rot4.North, null, map);
                }
            }

            // 2. Unstable Power Cell isolated blast rooms (Compact 2-3, Large 4-6; Royalty optional).
            foreach (HashSet<IntVec3> pcRoom in geo.PowerCellRooms)
            {
                IntVec3 cell = pcRoom.FirstOrDefault(IntVec3.Invalid);
                if (!cell.IsValid || !cell.Standable(map) || cell.GetFirstBuilding(map) != null) continue;
                if (unstableCell != null)
                {
                    FortressConstruction.Spawn(unstableCell, cell, Rot4.North, null, map);
                    RecordPower(unstableCell, cell);
                }
                else if (gloomlight != null)
                {
                    FortressConstruction.Spawn(gloomlight, cell, Rot4.North, null, map);
                }
            }

            // 3. Dedicated enclosed Ammo Vaults (empty stone shelves populated strictly by FortressAmmunition).
            int ammoShelfLimit = large ? 12 : 8;
            foreach (HashSet<IntVec3> ammoRoom in geo.AmmoRooms)
            {
                if (ammoRoom.Count == 0) continue;
                IntVec3 roomCenter = CenterOf(ammoRoom);
                var access = DoorAccess(ammoRoom);
                PlaceRoomLight(ammoRoom, roomCenter, access);
                FortressStorage.PlaceShelvesInCells(map, ammoRoom, roomCenter, stone, access, limit: ammoShelfLimit);
            }

            // 4. Dedicated Logistics / Materials Vaults for Constructoids & Lifters.
            int matShelfLimit = large ? 8 : 6;
            foreach (HashSet<IntVec3> logRoom in geo.LogisticsRooms)
            {
                if (logRoom.Count == 0) continue;
                IntVec3 roomCenter = CenterOf(logRoom);
                var access = DoorAccess(logRoom);
                PlaceRoomLight(logRoom, roomCenter, access);
                List<Building_Storage> shelves = FortressStorage.PlaceShelvesInCells(
                    map, logRoom, roomCenter, stone, access, limit: matShelfLimit);
                if (shelves.Count > 0)
                    FortressStorage.Fill(map, shelves, index => MechMaterialItem(index));
            }

            // 5. Generator & Biotech Mech Facility Rooms:
            //    Always place at least 1 ToxifierGenerator, add iconic Biotech mech facilities, then scale
            //    additional ToxifierGenerators until net grid power covers all consumers with safety margin.
            int toxifiersPlaced = 0;
            if (toxifier != null)
            {
                foreach (HashSet<IntVec3> room in geo.FacilityRooms)
                {
                    if (TryPlaceBuildingInCells(toxifier, room, CenterOf(room), Rot4.North, allowDoorAdjacent: false, out _))
                    {
                        toxifiersPlaced++;
                        break;
                    }
                }
            }

            var facilityPlan = new List<ThingDef>();
            if (ripscanner != null) facilityPlan.Add(ripscanner);
            if (mechBooster != null) facilityPlan.Add(mechBooster);
            if (bandNode != null) facilityPlan.Add(bandNode);
            if (softscanner != null) facilityPlan.Add(softscanner);
            else if (encoder != null) facilityPlan.Add(encoder);
            if (ancientBandNode != null) facilityPlan.Add(ancientBandNode);
            if (ancientToxifier != null) facilityPlan.Add(ancientToxifier);

            int roomCursor = 0;
            foreach (ThingDef facilityDef in facilityPlan)
            {
                if (geo.FacilityRooms.Count == 0) break;
                for (int attempt = 0; attempt < geo.FacilityRooms.Count; attempt++)
                {
                    HashSet<IntVec3> room = geo.FacilityRooms[(roomCursor + attempt) % geo.FacilityRooms.Count];
                    if (TryPlaceBuildingInCells(facilityDef, room, CenterOf(room), Rot4.South, allowDoorAdjacent: false, out _))
                    {
                        roomCursor = (roomCursor + attempt + 1) % geo.FacilityRooms.Count;
                        break;
                    }
                }
            }

            foreach (HashSet<IntVec3> room in geo.FacilityRooms)
                PlaceRoomLight(room, CenterOf(room), DoorAccess(room));

            // Ensure at least 1 ToxifierGenerator and positive net power surplus (+200W margin).
            if (toxifier != null)
            {
                while (toxifiersPlaced == 0 || totalGeneration < totalDemand + 200f)
                {
                    bool placed = false;
                    foreach (HashSet<IntVec3> room in geo.FacilityRooms.Concat(geo.LogisticsRooms))
                    {
                        if (TryPlaceBuildingInCells(toxifier, room, CenterOf(room), Rot4.North, allowDoorAdjacent: false, out _))
                        {
                            placed = true;
                            break;
                        }
                    }
                    if (!placed)
                    {
                        // Overflow into quiet open interior hall spots away from doors, lifts, silos, and core.
                        var openHall = new HashSet<IntVec3>(geo.Interior.Where(cell =>
                            !geo.CoreInterior.Contains(cell) && !reservedHall.Contains(cell) &&
                            cell.Standable(map) && cell.GetFirstBuilding(map) == null));
                        placed = TryPlaceBuildingInCells(toxifier, openHall, center, Rot4.North, allowDoorAdjacent: false, out _);
                    }
                    if (!placed) break;
                    toxifiersPlaced++;
                }
            }

            // 6. Connect all generators, unstable power cells, and Biotech buildings via HiddenConduit.
            ConnectMechPowerGrid(map, geo, center, poweredNodes);

            // 7. Return a clear off-center Core Chamber floor cell for harassment loot (leaving center for gestator).
            return geo.CoreInterior
                .Where(cell => cell != center && cell.Standable(map) && cell.GetFirstBuilding(map) == null &&
                    !BaseGenUtility.AnyDoorAdjacentCardinalTo(cell, map))
                .OrderBy(cell => (cell - center).LengthHorizontalSquared)
                .FirstOrDefault(center);

            ThingDef MechMaterialItem(int index)
            {
                if (needsBarrels && !barrelPlaced && index == 0)
                {
                    barrelPlaced = true;
                    return reinforcedBarrel;
                }
                return (index % 7) switch
                {
                    0 or 1 => ThingDefOf.Steel,
                    2 => ThingDefOf.ComponentIndustrial,
                    3 => ThingDefOf.Plasteel,
                    4 => ThingDefOf.Uranium,
                    5 => stone,
                    _ => ThingDefOf.Steel
                };
            }

            HashSet<IntVec3> DoorAccess(HashSet<IntVec3> room) =>
                new(room.Where(cell => BaseGenUtility.AnyDoorAdjacentCardinalTo(cell, map)));

            void PlaceRoomLight(HashSet<IntVec3> room, IntVec3 roomCenter, HashSet<IntVec3> access)
            {
                if (gloomlight == null) return;
                IntVec3 lampCell = room
                    .Where(cell => !access.Contains(cell) && cell.Standable(map) && cell.GetFirstBuilding(map) == null)
                    .OrderBy(cell => (cell - roomCenter).LengthHorizontalSquared)
                    .FirstOrDefault(IntVec3.Invalid);
                if (!lampCell.IsValid) return;
                FortressConstruction.Spawn(gloomlight, lampCell, Rot4.North, null, map);
                access.Add(lampCell);
            }

            void RecordPower(ThingDef def, IntVec3 pos)
            {
                CompProperties_Power powerProps = def.GetCompProperties<CompProperties_Power>();
                if (powerProps == null) return;
                float draw = powerProps.PowerConsumption;
                if (draw > 0f) totalDemand += draw;
                else if (draw < 0f) totalGeneration += -draw;
                poweredNodes.Add(pos);
            }

            bool TryPlaceBuildingInCells(ThingDef def, HashSet<IntVec3> room, IntVec3 prefer, Rot4 rot,
                bool allowDoorAdjacent, out IntVec3 placedPos)
            {
                placedPos = IntVec3.Invalid;
                if (def == null || room == null || room.Count == 0) return false;
                Rot4[] rotations = def.rotatable
                    ? new[] { rot, rot.Rotated(RotationDirection.Clockwise), rot.Opposite, rot.Rotated(RotationDirection.Counterclockwise) }
                    : new[] { Rot4.North };

                foreach (IntVec3 anchor in room.OrderBy(cell => (cell - prefer).LengthHorizontalSquared))
                {
                    foreach (Rot4 candidateRot in rotations)
                    {
                        CellRect occupied = GenAdj.OccupiedRect(anchor, candidateRot, def.size);
                        bool fits = true;
                        foreach (IntVec3 cell in occupied)
                        {
                            if (!room.Contains(cell) || !cell.Standable(map) || cell.GetFirstBuilding(map) != null ||
                                reservedInteract.Contains(cell) ||
                                !allowDoorAdjacent && BaseGenUtility.AnyDoorAdjacentCardinalTo(cell, map))
                            {
                                fits = false;
                                break;
                            }
                        }
                        if (!fits) continue;
                        IntVec3 interact = IntVec3.Invalid;
                        if (def.hasInteractionCell)
                        {
                            interact = ThingUtility.InteractionCellWhenAt(def, anchor, candidateRot, map);
                            if (!room.Contains(interact) || !interact.Standable(map) || interact.GetFirstBuilding(map) != null)
                                continue;
                        }
                        if (!GenSpawn.CanSpawnAt(def, anchor, map, candidateRot, canWipeEdifices: false)) continue;

                        ThingDef buildStuff = def.MadeFromStuff ? stone : null;
                        FortressConstruction.Spawn(def, anchor, candidateRot, buildStuff, map);
                        if (interact.IsValid) reservedInteract.Add(interact);
                        RecordPower(def, anchor);
                        placedPos = anchor;
                        return true;
                    }
                }
                return false;
            }
        }

        private static IntVec3 CenterOf(HashSet<IntVec3> cells)
        {
            int sx = 0, sz = 0;
            foreach (IntVec3 c in cells) { sx += c.x; sz += c.z; }
            return new IntVec3(sx / cells.Count, 0, sz / cells.Count);
        }

        private static void ConnectMechPowerGrid(Map map, FortressGeometry geo, IntVec3 center, List<IntVec3> nodes)
        {
            if (nodes.Count == 0) return;
            var cables = new HashSet<IntVec3>();
            foreach (IntVec3 cell in geo.Footprint)
                if (cell.x == center.x || cell.z == center.z)
                    cables.Add(cell);

            foreach (IntVec3 node in nodes)
            {
                int xStep = center.x >= node.x ? 1 : -1;
                for (int x = node.x; x != center.x; x += xStep)
                {
                    IntVec3 step = new(x, 0, node.z);
                    if (geo.Footprint.Contains(step)) cables.Add(step);
                }
                int zStep = center.z >= node.z ? 1 : -1;
                for (int z = node.z; z != center.z; z += zStep)
                {
                    IntVec3 step = new(node.x, 0, z);
                    if (geo.Footprint.Contains(step)) cables.Add(step);
                }
                if (geo.Footprint.Contains(node)) cables.Add(node);
            }

            ThingDef conduit = ThingDefOf.HiddenConduit;
            foreach (IntVec3 cell in cables)
            {
                if (cell.GetTransmitter(map) == null && GenConstruct.CanBuildOnTerrain(conduit, cell, map, Rot4.North))
                    FortressConstruction.Spawn(conduit, cell, Rot4.North, null, map);
            }
            map.powerNetManager.UpdatePowerNetsAndConnections_First();
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
