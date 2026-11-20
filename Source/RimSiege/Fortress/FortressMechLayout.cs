using System;
using System.Collections.Generic;
using System.Linq;
using ProjectZ;
using RimWorld;
using Verse;

namespace RimSiege.Fortress
{
    internal sealed partial class FortressLayout
    {
        private bool ComposeMechCircularBunker(Map map, Rot4 gateSide, MechBunkerDimensions dim)
        {
            ThingDef embrasure = DefDatabase<ThingDef>.GetNamed("CE_Embrasure");
            ThingDef reinforcedWall = DefDatabase<ThingDef>.GetNamed("CE_ReinforcedWall");
            ThingDef reinforcedDoor = DefDatabase<ThingDef>.GetNamed("CE_ReinforcedDoor");
            ThingDef plasteel = ThingDefOf.Plasteel;
            ThingDef uranium = ThingDefOf.Uranium;
            ThingDef stone = FactionMaterial.HardestLocalStone(map);
            IntVec3 center = Outer.CenterCell;
            bool isLarge = Size == FortressSize.Large;

            FortressShapeMask mask = FortressGeometry.ShapeFor(Kind, Size);
            var footprint = new HashSet<IntVec3>(mask.Footprint.Select(offset => center + offset));
            var outerEdge = new HashSet<IntVec3>(mask.OuterEdge.Select(offset => center + offset));
            Dictionary<IntVec3, int> exteriorDist = FortressGeometry.CardinalExteriorDistance(footprint);
            var innerUsable = new HashSet<IntVec3>(footprint.Where(cell => exteriorDist[cell] > dim.WallThickness));
            Gate = mask.GateCell(center, gateSide);

            var gates = new List<FortressGate>();
            var gatePassages = new Dictionary<IntVec3, (Rot4 Side, int Depth)>();
            foreach (Rot4 side in OrderedGateSides(gateSide, FourGates))
            {
                IntVec3 gateCell = mask.GateCell(center, side);
                gates.Add(new FortressGate(gateCell, gateCell + side.FacingCell, side, dim.WallThickness));
                for (int depth = 0; depth < dim.WallThickness; depth++)
                    gatePassages[gateCell - side.FacingCell * depth] = (side, depth);
            }

            // 1. Outer 3-cell Plasteel armor shell.
            var perimeter = new HashSet<IntVec3>();
            var plasteelWalls = new HashSet<IntVec3>();
            var reinforcedPlasteelWalls = new HashSet<IntVec3>();
            var stoneWalls = new HashSet<IntVec3>();
            var embrasures = new HashSet<IntVec3>();
            var doors = new Dictionary<IntVec3, (Rot4 Rot, ThingDef Stuff, bool Reinforced)>();
            var pillboxCells = new HashSet<IntVec3>();

            foreach (IntVec3 cell in footprint)
            {
                if (exteriorDist[cell] <= dim.WallThickness)
                {
                    perimeter.Add(cell);
                    plasteelWalls.Add(cell);
                }
            }

            // 2. Carve the 4 cardinal gate passages.
            foreach (var (cell, passage) in gatePassages)
            {
                plasteelWalls.Remove(cell);
                if (passage.Depth == 0)
                    doors[cell] = (passage.Side, plasteel, false);
            }

            // 3. Carve 8 axis-aligned frontal firing pillboxes flanking the 4 cardinal gates (3-cell straight embrasures).
            var slots = new List<FortressEmplacementSlot>();
            const int pbStart = 3;
            const int pbEnd = 5;
            Rot4[] cardinals = { Rot4.North, Rot4.East, Rot4.South, Rot4.West };
            for (int sideIndex = 0; sideIndex < cardinals.Length; sideIndex++)
            {
                Rot4 side = cardinals[sideIndex];
                IntVec3 forward = side.FacingCell;
                IntVec3 tangent = side.Rotated(RotationDirection.Clockwise).FacingCell;
                int sectorMask = FortressGeometry.SectorBit(side);

                foreach (int sign in new[] { -1, 1 })
                {
                    int zoneIndex = PillboxZoneIndexC4(side, sign);
                    for (int off = pbStart; off <= pbEnd; off++)
                    {
                        IntVec3 outerPort = center + forward * dim.FlatCap + tangent * (sign * off);
                        if (outerEdge.Contains(outerPort))
                        {
                            plasteelWalls.Remove(outerPort);
                            embrasures.Add(outerPort);
                            pillboxCells.Add(outerPort);
                        }
                        for (int depth = 1; depth < dim.WallThickness; depth++)
                        {
                            IntVec3 bay = center + forward * (dim.FlatCap - depth) + tangent * (sign * off);
                            plasteelWalls.Remove(bay);
                            perimeter.Remove(bay);
                            pillboxCells.Add(bay);
                        }
                    }

                    IntVec3 gunAnchor = center + forward * (dim.FlatCap - 2) + tangent * (sign * (pbStart + 1));
                    slots.Add(new FortressEmplacementSlot(
                        gunAnchor, 0, side, FortressEmplacementRole.Pillbox, sectorMask, zoneIndex));
                }
            }

            // 4. Central circular Core Chamber (2-layer CE Reinforced Plasteel wall + double CE Reinforced Plasteel doors + 4 pillars).
            var coreInterior = new HashSet<IntVec3>();
            var coreWall = new HashSet<IntVec3>();
            var corePillars = new HashSet<IntVec3>();
            double coreInnerR2 = (dim.CoreRadius + 0.35) * (dim.CoreRadius + 0.35);
            double coreOuterR2 = (dim.CoreRadius + 2 + 0.35) * (dim.CoreRadius + 2 + 0.35);
            foreach (IntVec3 cell in footprint)
            {
                int dx = cell.x - center.x;
                int dz = cell.z - center.z;
                double d2 = dx * dx + dz * dz;
                if (d2 <= coreInnerR2)
                {
                    coreInterior.Add(cell);
                }
                else if (d2 <= coreOuterR2)
                {
                    coreWall.Add(cell);
                    reinforcedPlasteelWalls.Add(cell);
                }
            }

            foreach (Rot4 side in cardinals)
            {
                for (int r = dim.CoreRadius; r <= dim.CoreRadius + 3; r++)
                {
                    IntVec3 cell = center + side.FacingCell * r;
                    if (!coreWall.Contains(cell)) continue;
                    reinforcedPlasteelWalls.Remove(cell);
                    doors[cell] = (side, plasteel, true);
                }
            }

            foreach (int sx in new[] { -1, 1 })
            {
                foreach (int sz in new[] { -1, 1 })
                {
                    IntVec3 pillar = new(center.x + sx * 3, 0, center.z + sz * 3);
                    reinforcedPlasteelWalls.Add(pillar);
                    corePillars.Add(pillar);
                    coreInterior.Remove(pillar);
                }
            }

            CellRect coreRect = CellRect.CenteredOn(center, (dim.CoreRadius + 2) * 2 + 1, (dim.CoreRadius + 2) * 2 + 1);
            Modules.Add(new FortressModule(coreRect, 0, FortressModuleKind.Keep));

            // 5. C4-symmetric functional compartments in all 4 quadrants (0:NE, 1:SE, 2:SW, 3:NW).
            var occupiedRooms = new HashSet<IntVec3>();
            var mortarSilos = new List<CellRect>();
            var siloOpenings = new HashSet<IntVec3>();
            var siloRecess = new HashSet<IntVec3>();
            var ammoRooms = new List<HashSet<IntVec3>>();
            var logisticsRooms = new List<HashSet<IntVec3>>();
            var powerCellRooms = new List<HashSet<IntVec3>>();
            var facilityRooms = new List<HashSet<IntVec3>>();
            var quadrantAmmo = new HashSet<IntVec3>[4];
            var uraniumCapGround = new HashSet<IntVec3>(pillboxCells);
            var reinforcedUraniumCapGround = new HashSet<IntVec3>(coreInterior);
            reinforcedUraniumCapGround.UnionWith(coreWall);
            reinforcedUraniumCapGround.UnionWith(corePillars);

            // Deterministic unstable power cell room count: Compact 2-3, Large 4-6.
            int layoutVariant = Math.Abs(center.x * 31 + center.z * 17);
            int extraCompactCell = layoutVariant % 2;       // 0 -> 2 cells, 1 -> 3 cells
            int extraLargeCells = layoutVariant % 3;        // 0 -> 4 cells, 1 -> 5 cells, 2 -> 6 cells

            var liftOpenings = new HashSet<IntVec3>();
            var cupolaAll = new HashSet<IntVec3>();
            var cupolaEmbrasures = new HashSet<IntVec3>();
            var cupolaBackWalls = new HashSet<IntVec3>();
            var cupolaStanding = new HashSet<IntVec3>();
            var cupolaGrounds = new List<HashSet<IntVec3>>(4);

            for (int q = 0; q < 4; q++)
            {
                bool hasSilo = isLarge || q == 0 || q == 2;
                if (!isLarge)
                {
                    // Compact Wing A [3..8] x [9..14] (6x6 -> 4x4 interior)
                    if (hasSilo)
                    {
                        if (!TryPlaceRoom(3, 8, 9, 14, q, usePlasteel: true, new[] { (5, 9) }, null, out var all, out var inner))
                            return false;
                        mortarSilos.Add(RectFromCells(inner));
                        siloOpenings.UnionWith(inner);
                        siloRecess.UnionWith(all);
                    }
                    else
                    {
                        // Dedicated enclosed Ammo Vaults in SE (q=1) and NW (q=3), isolated from mortar silos & power cells.
                        if (!TryPlaceRoom(3, 8, 9, 14, q, usePlasteel: false, new[] { (5, 9) }, null, out var all, out var inner))
                            return false;
                        ammoRooms.Add(inner);
                        quadrantAmmo[q] = inner;
                        uraniumCapGround.UnionWith(all);
                    }

                    // Compact Wing B [8..12] x [3..8] (5x6 -> 3x4 interior)
                    if (!TryPlaceRoom(8, 12, 3, 8, q, usePlasteel: false, new[] { (8, 5) }, null, out var wingAll, out var wingInner))
                        return false;
                    if (q == 0 || q == 2)
                    {
                        facilityRooms.Add(wingInner);
                        uraniumCapGround.UnionWith(wingAll);
                    }
                    else
                    {
                        logisticsRooms.Add(wingInner);
                    }

                    // Compact Interior Avenue Ambush Bunker [13..15] x [2..4] facing the cardinal avenue.
                    if (!TryPlaceRoom(13, 15, 2, 4, q, usePlasteel: true, new[] { (14, 4) }, new[] { (14, 2) }, out _, out _))
                        return false;
                    AddAmbushSlot(14, 3, 14, 2, q);

                    // Compact Unstable Power Cell blast rooms [14..16] x [6..8] (2 or 3 isolated 1x1 rooms).
                    if (q == 0 || q == 2 || (q == 1 && extraCompactCell > 0))
                    {
                        if (!TryPlaceRoom(14, 16, 6, 8, q, usePlasteel: true, new[] { (14, 7) }, null, out var pcAll, out var pcInner))
                            return false;
                        powerCellRooms.Add(pcInner);
                        uraniumCapGround.UnionWith(pcAll);
                    }
                }
                else
                {
                    // Large Wing A [3..8] x [11..16] (6x6 -> 4x4 interior): 4 symmetric Mortar Silos!
                    if (!TryPlaceRoom(3, 8, 11, 16, q, usePlasteel: true, new[] { (5, 11) }, null, out var siloAll, out var siloInner))
                        return false;
                    mortarSilos.Add(RectFromCells(siloInner));
                    siloOpenings.UnionWith(siloInner);
                    siloRecess.UnionWith(siloAll);

                    // Large Wing B [10..16] x [3..9] (7x7 -> 5x5 interior)
                    if (!TryPlaceRoom(10, 16, 3, 9, q, usePlasteel: false, new[] { (10, 6) }, null, out var wingAll, out var wingInner))
                        return false;
                    if (q == 0 || q == 2)
                    {
                        ammoRooms.Add(wingInner);
                        quadrantAmmo[q] = wingInner;
                        uraniumCapGround.UnionWith(wingAll);
                    }
                    else if (q == 1)
                    {
                        facilityRooms.Add(wingInner);
                        uraniumCapGround.UnionWith(wingAll);
                    }
                    else
                    {
                        logisticsRooms.Add(wingInner);
                    }

                    // Large Interior Avenue Ambush Bunker [17..19] x [2..4] facing the cardinal avenue.
                    if (!TryPlaceRoom(17, 19, 2, 4, q, usePlasteel: true, new[] { (18, 4) }, new[] { (18, 2) }, out _, out _))
                        return false;
                    AddAmbushSlot(18, 3, 18, 2, q);

                    // Large Unstable Power Cell blast rooms: Slot 1 in all 4 quadrants [18..20] x [6..8] + Slot 2 in 0..2 quadrants [6..8] x [18..20].
                    if (!TryPlaceRoom(18, 20, 6, 8, q, usePlasteel: true, new[] { (18, 7) }, null, out var pc1All, out var pc1Inner))
                        return false;
                    powerCellRooms.Add(pc1Inner);
                    uraniumCapGround.UnionWith(pc1All);

                    if ((q == 0 && extraLargeCells >= 1) || (q == 2 && extraLargeCells >= 2))
                    {
                        if (!TryPlaceRoom(6, 8, 18, 20, q, usePlasteel: true, new[] { (7, 18) }, null, out var pc2All, out var pc2Inner))
                            return false;
                        powerCellRooms.Add(pc2Inner);
                        uraniumCapGround.UnionWith(pc2All);
                    }
                }

                // 6. Enclosed 2nd-floor Oblique Firing Cupola (all 4 quadrants on both Compact & Large) + RS_MechLift.
                var cBox = new HashSet<IntVec3>();
                for (int lx = dim.CupolaMin; lx <= dim.CupolaMax; lx++)
                {
                    for (int lz = dim.CupolaMin; lz <= dim.CupolaMax; lz++)
                    {
                        IntVec3 cell = RotC4(center, lx, lz, q);
                        if (footprint.Contains(cell)) cBox.Add(cell);
                    }
                }

                IntVec3 footGround = RotC4(center, dim.CupolaMin + 1, dim.CupolaMin + 1, q);
                IntVec3 landingGround = RotC4(center, dim.CupolaMin + 2, dim.CupolaMin + 1, q);
                if (!innerUsable.Contains(footGround) || occupiedRooms.Contains(footGround) ||
                    coreWall.Contains(footGround) || coreInterior.Contains(footGround) ||
                    siloRecess.Contains(footGround) || siloRecess.Contains(landingGround))
                    return false;

                Rot4 liftFacing = Rot4.FromIntVec3(landingGround - footGround);
                AddLadder(map, 0, footGround, liftFacing);
                liftOpenings.Add(footGround);

                var podStanding = new HashSet<IntVec3>();
                for (int lx = dim.CupolaMin; lx <= dim.CupolaMax; lx++)
                {
                    for (int lz = dim.CupolaMin; lz <= dim.CupolaMax; lz++)
                    {
                        IntVec3 cell = RotC4(center, lx, lz, q);
                        if (!cBox.Contains(cell)) continue;
                        cupolaAll.Add(cell);
                        bool isOuter = !cBox.Contains(RotC4(center, lx + 1, lz, q)) ||
                            !cBox.Contains(RotC4(center, lx, lz + 1, q));
                        bool isInner = lx == dim.CupolaMin || lz == dim.CupolaMin;
                        if (isOuter)
                        {
                            cupolaEmbrasures.Add(cell);
                        }
                        else if (isInner)
                        {
                            cupolaBackWalls.Add(cell);
                        }
                        else if (cell != footGround)
                        {
                            cupolaStanding.Add(cell);
                            podStanding.Add(cell);
                        }
                    }
                }
                if (!podStanding.Contains(landingGround)) return false;
                cupolaGrounds.Add(podStanding);

                // Reserve an elevated Floor-1 Charge Blaster emplacement in the outer-diagonal 2x2 corner of this cupola.
                IntVec3 cupolaGunCorner = RotC4(center, dim.CupolaMin + 2, dim.CupolaMin + 2, q);
                Rot4 cupolaFacing = q switch { 0 => Rot4.North, 1 => Rot4.East, 2 => Rot4.South, _ => Rot4.West };
                slots.Add(new FortressEmplacementSlot(
                    cupolaGunCorner, 1, cupolaFacing, FortressEmplacementRole.Cupola,
                    FortressDefenseGround.MechCupolaSectorMask(q), q));
            }

            // 7. Build Floor 0 entities, interior set, and verify airtightness + ProjectZ slab support.
            var interior = new HashSet<IntVec3>();
            var solidGround = new HashSet<IntVec3>(NaturalBarrier);
            var roofHolders = new HashSet<IntVec3>();
            var floorInteriorCells = new HashSet<IntVec3>();

            foreach (IntVec3 cell in footprint)
            {
                if (NaturalBarrier.Contains(cell)) continue;
                bool isReinforcedPlasteel = reinforcedPlasteelWalls.Contains(cell);
                bool isPlasteelWall = !isReinforcedPlasteel && plasteelWalls.Contains(cell);
                bool isStoneWall = !isReinforcedPlasteel && !isPlasteelWall && stoneWalls.Contains(cell);
                bool isEmbrasure = !isReinforcedPlasteel && !isPlasteelWall && !isStoneWall && embrasures.Contains(cell);
                bool isDoor = doors.TryGetValue(cell, out var doorInfo);

                if (isReinforcedPlasteel)
                {
                    Tiers[0].Buildings.Add(new FortressBuilding(reinforcedWall, cell, Rot4.North, plasteel));
                    solidGround.Add(cell);
                    roofHolders.Add(cell);
                    if (!perimeter.Contains(cell)) interior.Add(cell);
                }
                else if (isPlasteelWall)
                {
                    Tiers[0].Buildings.Add(new FortressBuilding(ThingDefOf.Wall, cell, Rot4.North, plasteel));
                    solidGround.Add(cell);
                    roofHolders.Add(cell);
                    if (!perimeter.Contains(cell)) interior.Add(cell);
                }
                else if (isStoneWall)
                {
                    Tiers[0].Buildings.Add(new FortressBuilding(ThingDefOf.Wall, cell, Rot4.North, stone));
                    solidGround.Add(cell);
                    roofHolders.Add(cell);
                    if (!perimeter.Contains(cell)) interior.Add(cell);
                }
                else if (isEmbrasure)
                {
                    Tiers[0].Buildings.Add(new FortressBuilding(embrasure, cell, Rot4.North, plasteel));
                    solidGround.Add(cell);
                    roofHolders.Add(cell);
                    if (!perimeter.Contains(cell)) interior.Add(cell);
                }
                else if (isDoor)
                {
                    ThingDef doorDef = doorInfo.Reinforced ? reinforcedDoor : ThingDefOf.Door;
                    Tiers[0].Buildings.Add(new FortressBuilding(doorDef, cell, doorInfo.Rot, doorInfo.Stuff));
                    solidGround.Add(cell);
                    if (!perimeter.Contains(cell)) interior.Add(cell);
                }
                else
                {
                    if (!perimeter.Contains(cell)) interior.Add(cell);
                    floorInteriorCells.Add(cell);
                }
            }

            Tiers[0].Interiors.Add(new FortressRoomFloor(Outer, false, false, floorInteriorCells));

            // 8. Continuous Floor 1 Plasteel armored slab laid strictly outward (BFS <= 6) from Floor 0 roof-holders.
            var allOpenings = new HashSet<IntVec3>(siloOpenings);
            allOpenings.UnionWith(liftOpenings);
            foreach (IntVec3 cell in siloOpenings)
                Tiers[1].Openings.Add(Api.CellOnFloor(cell, 1, map));

            var armoredCapGround = new HashSet<IntVec3>();
            var supportDist = new Dictionary<IntVec3, int>(footprint.Count);
            var supportQueue = new Queue<IntVec3>();
            foreach (IntVec3 cell in roofHolders)
            {
                if (allOpenings.Contains(cell)) continue;
                supportDist[cell] = 0;
                supportQueue.Enqueue(cell);
            }
            while (supportQueue.Count > 0)
            {
                IntVec3 current = supportQueue.Dequeue();
                int nextDist = supportDist[current] + 1;
                foreach (IntVec3 dir in GenAdj.CardinalDirections)
                {
                    IntVec3 next = current + dir;
                    if (!footprint.Contains(next) || allOpenings.Contains(next) ||
                        NaturalBarrier.Contains(next) || supportDist.ContainsKey(next)) continue;
                    supportDist[next] = nextDist;
                    supportQueue.Enqueue(next);
                }
            }

            foreach (IntVec3 cell in footprint)
            {
                if (allOpenings.Contains(cell) || NaturalBarrier.Contains(cell)) continue;
                if (!supportDist.TryGetValue(cell, out int dist) || dist > 6) return false;
                armoredCapGround.Add(cell);
            }

            foreach (IntVec3 cell in armoredCapGround.OrderBy(c => supportDist[c]))
                Tiers[1].Deck.Add(Api.CellOnFloor(cell, 1, map));

            // 9. Floor 1 structures:
            //    - 4 Enclosed Oblique Cupolas (outer Plasteel embrasures, inner Plasteel back-walls, roofed standing cells)
            //    - Core Chamber covered with CE Reinforced Uranium walls (both Compact & Large)
            //    - Ammo Vaults, Power Cell rooms, Generator/Facility rooms, Frontal Pillboxes covered with Uranium walls
            //      (automatically upgraded to Reinforced Uranium on Large), recessed 6x6 around mortar silos.
            var cupolaRoofFloor1 = new List<IntVec3>(cupolaStanding.Count);
            foreach (IntVec3 cell in armoredCapGround)
            {
                IntVec3 upper = Api.CellOnFloor(cell, 1, map);
                if (cupolaEmbrasures.Contains(cell))
                {
                    Tiers[1].Buildings.Add(new FortressBuilding(embrasure, upper, Rot4.North, plasteel));
                }
                else if (cupolaBackWalls.Contains(cell))
                {
                    Tiers[1].Buildings.Add(new FortressBuilding(ThingDefOf.Wall, upper, Rot4.North, plasteel));
                }
                else if (cupolaStanding.Contains(cell))
                {
                    cupolaRoofFloor1.Add(upper);
                }
                else if (siloRecess.Contains(cell))
                {
                    continue;
                }
                else if (reinforcedUraniumCapGround.Contains(cell))
                {
                    Tiers[1].Buildings.Add(new FortressBuilding(reinforcedWall, upper, Rot4.North, uranium));
                }
                else if (uraniumCapGround.Contains(cell))
                {
                    Tiers[1].Buildings.Add(new FortressBuilding(ThingDefOf.Wall, upper, Rot4.North, uranium));
                }
            }

            if (cupolaRoofFloor1.Count > 0)
                Tiers[1].Interiors.Add(new FortressRoomFloor(Outer, false, true, cupolaRoofFloor1, KeepFloor: true));

            // 10. Quadrant zones & modules (aligned to C4 quadrants 0:NE, 1:SE, 2:SW, 3:NW) + exterior picket slots.
            var zones = new List<FortressBastionZone>(4);
            (Rot4 XSide, Rot4 ZSide, int Sx, int Sz)[] quadMeta =
            {
                (Rot4.East, Rot4.North,  1,  1),
                (Rot4.East, Rot4.South,  1, -1),
                (Rot4.West, Rot4.South, -1, -1),
                (Rot4.West, Rot4.North, -1,  1)
            };
            for (int q = 0; q < 4; q++)
            {
                var (xSide, zSide, sx, sz) = quadMeta[q];
                HashSet<IntVec3> vault = quadrantAmmo[q] ?? new HashSet<IntVec3>();
                CellRect zoneRect = vault.Count > 0
                    ? RectFromCells(vault).ExpandedBy(1)
                    : CellRect.CenteredOn(new IntVec3(center.x + sx * 10, 0, center.z + sz * 10), 7, 7);
                IntVec3 anchor = new(center.x + sx * 11, 0, center.z + sz * 11);
                int sectorMask = FortressGeometry.SectorBit(xSide) | FortressGeometry.SectorBit(zSide);
                zones.Add(new FortressBastionZone(q, zoneRect, 0, sectorMask, anchor, xSide, zSide, vault));
                Modules.Add(new FortressModule(zoneRect, 0, FortressModuleKind.Tower));
            }

            // Exterior picket slots (~7 cells outside outer wall): 4 diagonals (Compact) + 4 cardinals (Large).
            int diagOffset = (int)Math.Round((dim.Radius + 6.5) * 0.7071);
            for (int q = 0; q < 4; q++)
            {
                var (xSide, zSide, sx, sz) = quadMeta[q];
                IntVec3 picketPos = new(center.x + sx * diagOffset, 0, center.z + sz * diagOffset);
                Rot4 facing = Math.Abs(sx) >= Math.Abs(sz) ? xSide : zSide;
                int sectorMask = FortressGeometry.SectorBit(xSide) | FortressGeometry.SectorBit(zSide);
                slots.Add(new FortressEmplacementSlot(
                    picketPos, 0, facing, FortressEmplacementRole.ExteriorPicket, sectorMask, q));
            }

            if (isLarge)
            {
                int cardinalDist = dim.FlatCap + 7;
                for (int sideIndex = 0; sideIndex < cardinals.Length; sideIndex++)
                {
                    Rot4 side = cardinals[sideIndex];
                    IntVec3 tangent = side.Rotated(RotationDirection.Clockwise).FacingCell;
                    IntVec3 picketPos = center + side.FacingCell * cardinalDist + tangent * 4;
                    slots.Add(new FortressEmplacementSlot(
                        picketPos, 0, side, FortressEmplacementRole.ExteriorPicket,
                        FortressGeometry.SectorBit(side), -1));
                }
            }

            // 11. Airtightness verification.
            if (!IsFootprintAirtight(footprint, outerEdge, solidGround)) return false;

            var curtainPosts = Enumerable.Range(0, 4).Select(s =>
            {
                Rot4 side = new(s);
                return center + side.FacingCell * (dim.FlatCap - 2);
            }).ToArray();
            var interiorPosts = Enumerable.Range(0, 4).Select(s =>
            {
                Rot4 side = new(s);
                return center + side.FacingCell * (dim.CoreRadius + 4);
            }).ToArray();

            CellRect courtyardBounds = CellRect.CenteredOn(center, dim.InnerRadius * 2 + 1, dim.InnerRadius * 2 + 1);
            Geometry = new FortressGeometry(
                Kind, Size, Outer, footprint, interior, perimeter, outerEdge,
                new HashSet<IntVec3>(), courtyardBounds, coreRect, gates, zones, slots,
                curtainPosts, interiorPosts, armoredCapGround, coreInterior, mortarSilos,
                logisticsRooms, ammoRooms, powerCellRooms, facilityRooms, cupolaGrounds);
            return true;

            bool TryPlaceRoom(int x0, int x1, int z0, int z1, int q, bool usePlasteel,
                IEnumerable<(int X, int Z)> roomDoors, IEnumerable<(int X, int Z)> roomEmbrasures,
                out HashSet<IntVec3> allCells, out HashSet<IntVec3> innerCells)
            {
                allCells = new HashSet<IntVec3>();
                innerCells = new HashSet<IntVec3>();
                for (int lx = x0; lx <= x1; lx++)
                {
                    for (int lz = z0; lz <= z1; lz++)
                    {
                        IntVec3 cell = RotC4(center, lx, lz, q);
                        if (!innerUsable.Contains(cell) || coreWall.Contains(cell) ||
                            coreInterior.Contains(cell) || occupiedRooms.Contains(cell))
                            return false;
                        allCells.Add(cell);
                        if (lx == x0 || lx == x1 || lz == z0 || lz == z1)
                        {
                            if (usePlasteel) plasteelWalls.Add(cell);
                            else stoneWalls.Add(cell);
                        }
                        else
                        {
                            innerCells.Add(cell);
                        }
                    }
                }
                if (roomDoors != null)
                {
                    foreach (var (dx, dz) in roomDoors)
                    {
                        IntVec3 dc = RotC4(center, dx, dz, q);
                        plasteelWalls.Remove(dc);
                        stoneWalls.Remove(dc);
                        Rot4 doorRot = DoorRotForLocalWall(dx, dz, x0, x1, z0, z1, q);
                        doors[dc] = (doorRot, plasteel, false);
                    }
                }
                if (roomEmbrasures != null)
                {
                    foreach (var (ex, ez) in roomEmbrasures)
                    {
                        IntVec3 ec = RotC4(center, ex, ez, q);
                        plasteelWalls.Remove(ec);
                        stoneWalls.Remove(ec);
                        embrasures.Add(ec);
                    }
                }
                occupiedRooms.UnionWith(allCells);
                return true;
            }

            void AddAmbushSlot(int turretLx, int turretLz, int embrLx, int embrLz, int q)
            {
                IntVec3 pos = RotC4(center, turretLx, turretLz, q);
                IntVec3 port = RotC4(center, embrLx, embrLz, q);
                Rot4 facing = Rot4.FromIntVec3(port - pos);
                slots.Add(new FortressEmplacementSlot(
                    pos, 0, facing, FortressEmplacementRole.InteriorAmbush,
                    FortressGeometry.SectorBit(facing), q));
            }
        }

        private static IntVec3 RotC4(IntVec3 center, int lx, int lz, int q) => q switch
        {
            0 => new IntVec3(center.x + lx, 0, center.z + lz),   // NE
            1 => new IntVec3(center.x + lz, 0, center.z - lx),   // SE
            2 => new IntVec3(center.x - lx, 0, center.z - lz),   // SW
            _ => new IntVec3(center.x - lz, 0, center.z + lx)    // NW
        };

        private static Rot4 DoorRotForLocalWall(int dx, int dz, int x0, int x1, int z0, int z1, int q)
        {
            IntVec3 outwardLocal = dx == x0 ? new IntVec3(-1, 0, 0) :
                dx == x1 ? new IntVec3(1, 0, 0) :
                dz == z0 ? new IntVec3(0, 0, -1) : new IntVec3(0, 0, 1);
            IntVec3 rotated = RotC4(IntVec3.Zero, outwardLocal.x, outwardLocal.z, q);
            return Rot4.FromIntVec3(rotated);
        }

        private static CellRect RectFromCells(HashSet<IntVec3> cells)
        {
            int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
            foreach (IntVec3 c in cells)
            {
                if (c.x < minX) minX = c.x;
                if (c.x > maxX) maxX = c.x;
                if (c.z < minZ) minZ = c.z;
                if (c.z > maxZ) maxZ = c.z;
            }
            return CellRect.FromLimits(new IntVec3(minX, 0, minZ), new IntVec3(maxX, 0, maxZ));
        }

        private static int PillboxZoneIndexC4(Rot4 side, int sign)
        {
            // C4 zones: 0=NE, 1=SE, 2=SW, 3=NW.
            if (side == Rot4.North) return sign > 0 ? 0 : 3;
            if (side == Rot4.East) return sign > 0 ? 1 : 0;
            if (side == Rot4.South) return sign > 0 ? 2 : 1;
            return sign > 0 ? 3 : 2;
        }

        private static bool IsFootprintAirtight(HashSet<IntVec3> footprint, HashSet<IntVec3> outerEdge,
            HashSet<IntVec3> solidGround)
        {
            foreach (IntVec3 cell in outerEdge)
                if (!solidGround.Contains(cell)) return false;

            var visited = new HashSet<IntVec3>();
            var queue = new Queue<IntVec3>();
            foreach (IntVec3 cell in outerEdge)
            {
                if (solidGround.Contains(cell)) continue;
                visited.Add(cell);
                queue.Enqueue(cell);
            }
            while (queue.Count > 0)
            {
                IntVec3 current = queue.Dequeue();
                foreach (IntVec3 dir in GenAdj.CardinalDirections)
                {
                    IntVec3 next = current + dir;
                    if (!footprint.Contains(next) || solidGround.Contains(next) || !visited.Add(next)) continue;
                    return false;
                }
            }
            return true;
        }
    }
}
