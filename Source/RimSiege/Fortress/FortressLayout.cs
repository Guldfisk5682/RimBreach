using System;
using System.Collections.Generic;
using System.Linq;
using ProjectZ;
using RimWorld;
using Verse;

namespace RimSiege.Fortress
{
    internal readonly record struct FortressBuilding(ThingDef Def, IntVec3 Cell, Rot4 Rotation,
        ThingDef StuffOverride = null);

    internal readonly record struct FortressDimensions(int OuterSide, int TowerSide, int KeepSide)
    {
        internal static readonly FortressDimensions Large = new(49, 7, 13);
        internal static readonly FortressDimensions Compact = new(41, 5, 9);

        internal static FortressDimensions For(FortressSize size) =>
            size == FortressSize.Large ? Large : Compact;
    }

    internal readonly record struct FortressLadder(IntVec3 Foot, IntVec3 Landing, Rot4 Rotation);
    internal enum FortressModuleKind { Keep, Tower, Facility }
    internal readonly record struct FortressModule(CellRect Ground, int Top, FortressModuleKind Kind);
    internal readonly record struct FortressRoomFloor(CellRect Cells, bool Sterile, bool Roofed,
        IReadOnlyCollection<IntVec3> CustomCells = null, bool KeepFloor = false)
    {
        internal IEnumerable<IntVec3> AllCells => CustomCells ?? Cells.Cells;
    }

    internal sealed class FortressTier
    {
        internal readonly List<FortressBuilding> Buildings = new();
        internal readonly List<StairGeometry> Stairs = new();
        internal readonly List<IntVec3> Deck = new();
        internal readonly HashSet<IntVec3> ArmoredDeck = new();
        internal readonly HashSet<IntVec3> Openings = new();
        internal readonly List<FortressRoomFloor> Interiors = new();
    }

    internal sealed partial class FortressLayout
    {
        internal const int CurtainThickness = 4;
        private const int TowerClearance = 3;

        internal FortressLayoutKind Kind { get; }
        internal FortressSize Size { get; }
        internal CellRect Outer { get; }
        internal IntVec3 Gate { get; private set; }
        internal Rot4 GateSide { get; }
        internal bool FourGates { get; }
        internal static IEnumerable<Rot4> GateSides(Rot4 primary, bool fourGates) =>
            fourGates ? GenAdj.CardinalDirections.Select(Rot4.FromIntVec3) : new[] { primary };
        internal HashSet<IntVec3> NaturalBarrier { get; }
        internal FortressTier[] Tiers { get; }
        internal FortressGeometry Geometry { get; private set; }
        internal readonly List<FortressLadder> Ladders = new();
        internal readonly List<FortressModule> Modules = new();
        internal FortressFacilityPlan Facilities { get; private set; }

        private FortressLayout(FortressLayoutKind kind, FortressSize size, CellRect outer, Rot4 gateSide,
            HashSet<IntVec3> naturalBarrier, int top, bool fourGates)
        {
            Kind = kind;
            Size = size;
            Outer = outer;
            GateSide = gateSide;
            FourGates = fourGates;
            Gate = outer.GetCenterCellOnEdge(gateSide);
            NaturalBarrier = naturalBarrier;
            Tiers = Enumerable.Range(0, top + 1).Select(_ => new FortressTier()).ToArray();
        }

        internal static FortressLayout Plan(Map map, CellRect outer, Rot4 gateSide,
            HashSet<IntVec3> naturalBarrier, FortressSize size,
            FortressLayoutKind kind = FortressLayoutKind.SquareKeep, int? savedTop = null,
            FortressFacilityPlan? savedFacilities = null, bool fourGates = true)
        {
            int defaultTop = kind is FortressLayoutKind.HumanBastion or FortressLayoutKind.MechCircularBunker
                ? 1
                : (Api.CellOnFloor(outer.CenterCell, 2, map).IsValid ? 2 : 1);
            int top = savedTop ?? defaultTop;
            if (top < 1 || !Api.CellOnFloor(outer.CenterCell, top, map).IsValid) return null;
            var plan = new FortressLayout(kind, size, outer, gateSide, naturalBarrier, top, fourGates);
            bool ok = kind switch
            {
                FortressLayoutKind.HumanBastion => plan.ComposeHumanBastion(map, gateSide, top,
                    BastionDimensions.For(size), savedFacilities),
                FortressLayoutKind.MechCircularBunker => plan.ComposeMechCircularBunker(map, gateSide,
                    MechBunkerDimensions.For(size)),
                _ => plan.ComposeSquareKeep(map, gateSide, top, FortressDimensions.For(size), savedFacilities)
            };
            return ok ? plan : null;
        }

        private bool ComposeSquareKeep(Map map, Rot4 gateSide, int top, FortressDimensions dimensions,
            FortressFacilityPlan? savedFacilities)
        {
            ThingDef embrasure = DefDatabase<ThingDef>.GetNamed("CE_Embrasure");
            Gate = Outer.GetCenterCellOnEdge(gateSide);

            var gates = new List<FortressGate>();
            var passages = new Dictionary<IntVec3, Rot4>();
            foreach (Rot4 side in OrderedGateSides(gateSide, FourGates))
            {
                IntVec3 gateCell = Outer.GetCenterCellOnEdge(side);
                gates.Add(new FortressGate(gateCell, gateCell + side.FacingCell, side, CurtainThickness));
                for (int depth = 0; depth < CurtainThickness; depth++)
                    passages[gateCell - side.FacingCell * depth] = side;
            }

            var footprint = new HashSet<IntVec3>();
            var interior = new HashSet<IntVec3>();
            var perimeter = new HashSet<IntVec3>();
            var perimeterOuterEdge = new HashSet<IntVec3>(Outer.EdgeCells);
            var rampartDeckGround = new HashSet<IntVec3>();
            CellRect courtyard = Outer.ContractedBy(CurtainThickness);

            foreach (IntVec3 cell in Outer)
            {
                footprint.Add(cell);
                int depth = Depth(Outer, cell);
                if (depth >= CurtainThickness)
                {
                    interior.Add(cell);
                    continue;
                }
                perimeter.Add(cell);
                if (NaturalBarrier.Contains(cell)) continue;
                IntVec3 upper = Api.CellOnFloor(cell, 1, map);
                Tiers[1].Deck.Add(upper);
                rampartDeckGround.Add(cell);
                if (depth == 0)
                    Tiers[1].Buildings.Add(new FortressBuilding(Merlon(Outer, cell, embrasure), upper, Rot4.North));
                if (passages.TryGetValue(cell, out Rot4 doorSide))
                {
                    if (depth == 0) Tiers[0].Buildings.Add(new FortressBuilding(ThingDefOf.Door, cell, doorSide));
                    continue;
                }
                Tiers[0].Buildings.Add(new FortressBuilding(ThingDefOf.Wall, cell, Rot4.North));
            }

            foreach (int x in new[] { courtyard.minX, courtyard.maxX })
            {
                if (!AddStair(map, 0, new IntVec3(x, 0, courtyard.minZ + 2), Rot4.South) ||
                    !AddStair(map, 0, new IntVec3(x, 0, courtyard.maxZ - 2), Rot4.North)) return false;
            }

            CellRect keep = KeepFootprint(Outer, dimensions.KeepSide);
            Modules.Add(new FortressModule(keep, top, FortressModuleKind.Keep));
            AddPerimeter(map, keep, 0, gateSide, fourDoors: FourGates);
            AddInterior(map, keep, 0);
            AddDeck(map, keep, 1);
            if (!AddStair(map, 0, new IntVec3(keep.minX + 3, 0, keep.minZ + 3), Rot4.South))
                return false;

            if (top == 2)
            {
                AddPerimeter(map, keep, 1, Rot4.Invalid, embrasure, centerPorts: true);
                AddInterior(map, keep, 1, roofed: true);
                AddDeck(map, keep, 2);
                if (!AddStair(map, 1, new IntVec3(keep.maxX - 3, 0, keep.maxZ - 3), Rot4.North))
                    return false;
            }
            AddParapet(map, keep, top, embrasure);

            var zones = new List<FortressBastionZone>(4);
            AddTowers(map, dimensions.TowerSide, top, embrasure, zones);

            var curtainPosts = new IntVec3[4];
            var interiorPosts = new IntVec3[4];
            for (int sector = 0; sector < 4; sector++)
            {
                Rot4 side = new(sector);
                curtainPosts[sector] = Outer.GetCenterCellOnEdge(side) - side.FacingCell * (CurtainThickness - 1);
                interiorPosts[sector] = courtyard.GetCenterCellOnEdge(side) - side.FacingCell * 2;
            }

            Geometry = new FortressGeometry(
                Kind, Size, Outer, footprint, interior, perimeter, perimeterOuterEdge, rampartDeckGround,
                courtyard, keep, gates, zones, new List<FortressEmplacementSlot>(), curtainPosts, interiorPosts);

            FortressFacilityPlan facilities = savedFacilities.GetValueOrDefault();
            if (!savedFacilities.HasValue && !FortressFacilityPlan.TryPlace(map, this, out facilities)) return false;
            Facilities = facilities;
            if (facilities.SingleStoryCourtyard)
            {
                AddSingleStoryFacility(map, facilities.Living);
                AddSingleStoryFacility(map, facilities.Supply);
                if (!facilities.Dining.IsEmpty) AddSingleStoryFacility(map, facilities.Dining);
                if (!facilities.Infirmary.IsEmpty) AddSingleStoryFacility(map, facilities.Infirmary, sterile: facilities.Industrial);
                if (!facilities.AuxBarracks.IsEmpty) AddSingleStoryFacility(map, facilities.AuxBarracks);
                if (!facilities.AuxSupply.IsEmpty) AddSingleStoryFacility(map, facilities.AuxSupply);
            }
            else
            {
                AddFacility(map, facilities.Living, upperRoom: true);
                AddFacility(map, facilities.Supply, upperRoom: true, sterile: facilities.Industrial);
            }
            if (!facilities.Power.IsEmpty) AddFacility(map, facilities.Power, upperRoom: false);
            return VerticalLinksFit();
        }

        private bool ComposeHumanBastion(Map map, Rot4 gateSide, int top, BastionDimensions dim,
            FortressFacilityPlan? savedFacilities)
        {
            ThingDef embrasure = DefDatabase<ThingDef>.GetNamed("CE_Embrasure");
            IntVec3 center = Outer.CenterCell;
            FortressShapeMask mask = FortressGeometry.ShapeFor(Kind, Size);
            var footprint = new HashSet<IntVec3>(mask.Footprint.Select(offset => center + offset));
            var outerEdge = new HashSet<IntVec3>(mask.OuterEdge.Select(offset => center + offset));
            Gate = mask.GateCell(center, gateSide);

            var gates = new List<FortressGate>();
            var passages = new Dictionary<IntVec3, (Rot4 Side, int Depth)>();
            foreach (Rot4 side in OrderedGateSides(gateSide, FourGates))
            {
                IntVec3 gateCell = mask.GateCell(center, side);
                gates.Add(new FortressGate(gateCell, gateCell + side.FacingCell, side, dim.CurtainThickness));
                for (int depth = 0; depth < dim.CurtainThickness; depth++)
                    passages[gateCell - side.FacingCell * depth] = (side, depth);
            }

            int cIn = dim.CourtyardHalf;
            CellRect courtyardBounds = CellRect.CenteredOn(center, cIn * 2 + 1, cIn * 2 + 1);

            // Define the 4 expanded corner bastion underground vaults (>= 3 solid wall cells on all exterior sides).
            Dictionary<IntVec3, int> exteriorDist = FortressGeometry.CardinalExteriorDistance(footprint);
            var casemateInteriors = new HashSet<IntVec3>();
            var casemateDoors = new Dictionary<IntVec3, Rot4>();
            var gorgeCorners = new HashSet<IntVec3>();
            var zones = new List<FortressBastionZone>(4);
            (int Sx, int Sz, Rot4 XSide, Rot4 ZSide)[] corners =
            {
                (-1,  1, Rot4.West, Rot4.North),
                ( 1,  1, Rot4.East, Rot4.North),
                ( 1, -1, Rot4.East, Rot4.South),
                (-1, -1, Rot4.West, Rot4.South)
            };

            for (int i = 0; i < corners.Length; i++)
            {
                var (sx, sz, xSide, zSide) = corners[i];
                var vaultCells = new HashSet<IntVec3>();
                foreach (IntVec3 cell in footprint)
                {
                    int dx = cell.x - center.x;
                    int dz = cell.z - center.z;
                    if (dx * sx < cIn || dz * sz < cIn) continue;
                    if (exteriorDist.TryGetValue(cell, out int dist) && dist >= 4)
                        vaultCells.Add(cell);
                }

                IntVec3 gorgeCorner = new(center.x + sx * (cIn - 1), 0, center.z + sz * (cIn - 1));
                IntVec3 xDoor = new(center.x + sx * (cIn - 1), 0, center.z + sz * cIn);
                IntVec3 zDoor = new(center.x + sx * cIn, 0, center.z + sz * (cIn - 1));
                gorgeCorners.Add(gorgeCorner);
                casemateDoors[xDoor] = xSide.Opposite;
                casemateDoors[zDoor] = zSide.Opposite;
                casemateInteriors.UnionWith(vaultCells);

                int minX = Math.Min(gorgeCorner.x, vaultCells.Min(c => c.x));
                int maxX = Math.Max(gorgeCorner.x, vaultCells.Max(c => c.x));
                int minZ = Math.Min(gorgeCorner.z, vaultCells.Min(c => c.z));
                int maxZ = Math.Max(gorgeCorner.z, vaultCells.Max(c => c.z));
                var casemate = CellRect.FromLimits(new IntVec3(minX, 0, minZ), new IntVec3(maxX, 0, maxZ));
                Modules.Add(new FortressModule(casemate, top, FortressModuleKind.Tower));

                IntVec3 deckAnchor = new(center.x + sx * (dim.CurtainOuter + 1), 0, center.z + sz * (dim.CurtainOuter + 1));
                int sectorMask = FortressGeometry.SectorBit(xSide) | FortressGeometry.SectorBit(zSide);
                zones.Add(new FortressBastionZone(i, casemate, top, sectorMask, deckAnchor, xSide, zSide, vaultCells));

                Tiers[0].Interiors.Add(new FortressRoomFloor(casemate, false, false, vaultCells));
                // Internal ladder up from the underground vault into the Floor 1 bastion platform.
                IntVec3 foot = new(center.x + sx * (cIn + 1), 0, center.z + sz * (cIn + 1));
                AddLadder(map, 0, foot, xSide);
            }

            var interior = new HashSet<IntVec3>();
            var perimeter = new HashSet<IntVec3>();
            var solidGround = new HashSet<IntVec3>(NaturalBarrier);
            var rampartDeckGround = new HashSet<IntVec3>();
            var supportedDeck = new List<IntVec3>();
            var casemateRoofDeck = new List<IntVec3>();

            foreach (IntVec3 cell in footprint)
            {
                int ax = Math.Abs(cell.x - center.x);
                int az = Math.Abs(cell.z - center.z);
                bool inCasemateInterior = casemateInteriors.Contains(cell);
                bool isCasemateDoor = casemateDoors.TryGetValue(cell, out Rot4 casDoorRot);
                bool isGorgeCorner = gorgeCorners.Contains(cell);
                bool inOpenCourtyard = ax <= cIn && az <= cIn && !inCasemateInterior && !isCasemateDoor && !isGorgeCorner;

                if (inOpenCourtyard)
                {
                    interior.Add(cell);
                    continue;
                }

                if (inCasemateInterior)
                {
                    interior.Add(cell);
                    if (!NaturalBarrier.Contains(cell))
                    {
                        rampartDeckGround.Add(cell);
                        casemateRoofDeck.Add(cell);
                    }
                    continue;
                }

                perimeter.Add(cell);
                if (NaturalBarrier.Contains(cell)) continue;

                rampartDeckGround.Add(cell);
                supportedDeck.Add(cell);

                if (isCasemateDoor)
                {
                    Tiers[0].Buildings.Add(new FortressBuilding(ThingDefOf.Door, cell, casDoorRot));
                    solidGround.Add(cell);
                    continue;
                }

                if (passages.TryGetValue(cell, out var passage))
                {
                    if (passage.Depth == 0)
                    {
                        Tiers[0].Buildings.Add(new FortressBuilding(ThingDefOf.Door, cell, passage.Side));
                        solidGround.Add(cell);
                    }
                    continue;
                }

                Tiers[0].Buildings.Add(new FortressBuilding(ThingDefOf.Wall, cell, Rot4.North));
                solidGround.Add(cell);
            }

            // Lay Floor 1 deck from ground-wall-supported cells inward over expanded vault interiors.
            Dictionary<IntVec3, int> vaultWallDepth = ComputeVaultWallDepth(casemateInteriors);
            foreach (IntVec3 cell in supportedDeck)
                Tiers[1].Deck.Add(Api.CellOnFloor(cell, 1, map));
            foreach (IntVec3 cell in casemateRoofDeck.OrderBy(c => vaultWallDepth.GetValueOrDefault(c)))
                Tiers[1].Deck.Add(Api.CellOnFloor(cell, 1, map));

            // Floor 1 outer parapet: curtain merlons, straight flank embrasures, outward stepped barricades.
            foreach (IntVec3 cell in outerEdge)
            {
                if (NaturalBarrier.Contains(cell)) continue;
                ThingDef parapet = BastionParapet(cell, center, dim, embrasure);
                Tiers[1].Buildings.Add(new FortressBuilding(parapet, Api.CellOnFloor(cell, 1, map), Rot4.North));
            }

            // Four courtyard stairs up to the East/West curtain rampart walkway.
            int stairZOffset = dim.FlankInner - 3;
            foreach (int sx in new[] { -1, 1 })
            {
                int stairX = center.x + sx * cIn;
                if (!AddStair(map, 0, new IntVec3(stairX, 0, center.z - stairZOffset), Rot4.South) ||
                    !AddStair(map, 0, new IntVec3(stairX, 0, center.z + stairZOffset), Rot4.North))
                    return false;
            }

            // Central low supply/command Core building (2 levels: ground room + Floor 1 parapet roof).
            CellRect core = KeepFootprint(Outer, dim.CoreSide);
            Modules.Add(new FortressModule(core, top, FortressModuleKind.Keep));
            AddPerimeter(map, core, 0, gateSide, fourDoors: FourGates);
            AddInterior(map, core, 0);
            AddDeck(map, core, 1);
            AddParapet(map, core, 1, embrasure);
            if (dim.CoreSide >= 9)
            {
                if (!AddStair(map, 0, new IntVec3(core.minX + 3, 0, core.minZ + 3), Rot4.South))
                    return false;
            }
            else
            {
                AddLadder(map, 0, new IntVec3(core.minX + 2, 0, core.minZ + 2), Rot4.East);
            }

            var slots = BuildBastionSlots(center, dim, corners);
            var curtainPosts = new IntVec3[4];
            var interiorPosts = new IntVec3[4];
            for (int sector = 0; sector < 4; sector++)
            {
                Rot4 side = new(sector);
                curtainPosts[sector] = center + side.FacingCell * (dim.CurtainOuter - 1);
                interiorPosts[sector] = center + side.FacingCell * (cIn - 2);
            }

            Geometry = new FortressGeometry(
                Kind, Size, Outer, footprint, interior, perimeter, outerEdge, rampartDeckGround,
                courtyardBounds, core, gates, zones, slots, curtainPosts, interiorPosts);

            if (!Geometry.IsAirtight(solidGround)) return false;

            FortressFacilityPlan facilities = savedFacilities.GetValueOrDefault();
            if (!savedFacilities.HasValue && (!BastionFlanksFit(map) ||
                !FortressFacilityPlan.TryPlace(map, this, out facilities))) return false;
            Facilities = facilities;
            if (facilities.SingleStoryCourtyard)
            {
                AddSingleStoryFacility(map, facilities.Living);
                AddSingleStoryFacility(map, facilities.Supply);
                if (!facilities.Dining.IsEmpty) AddSingleStoryFacility(map, facilities.Dining);
                if (!facilities.Infirmary.IsEmpty) AddSingleStoryFacility(map, facilities.Infirmary, sterile: facilities.Industrial);
                if (!facilities.AuxBarracks.IsEmpty) AddSingleStoryFacility(map, facilities.AuxBarracks);
                if (!facilities.AuxSupply.IsEmpty) AddSingleStoryFacility(map, facilities.AuxSupply);
            }
            else
            {
                AddFacility(map, facilities.Living, upperRoom: true);
                AddFacility(map, facilities.Supply, upperRoom: true, sterile: facilities.Industrial);
            }
            if (!facilities.Power.IsEmpty) AddFacility(map, facilities.Power, upperRoom: false);
            return VerticalLinksFit();
        }

        private bool BastionFlanksFit(Map map)
        {
            // Each end of a curtain has its own flank. Preserve a usable slot for every gun in the profile.
            var guns = FortressDefenseProfileDef.For(map.ParentFaction).machineGuns;
            return Geometry.Slots.Where(slot => slot.Role == FortressEmplacementRole.FlankCurtain)
                .GroupBy(slot => (slot.ZoneIndex, slot.Facing))
                .All(flank => guns.All(gun => flank.Any(slot => Fits(slot, gun))));

            bool Fits(FortressEmplacementSlot slot, ThingDef gun)
            {
                IntVec3 root = slot.Ground;
                if (!HasFlankPort(slot) ||
                    !GenAdj.OccupiedRect(root, slot.Facing, gun.Size).All(UsableDeck)) return false;
                return !gun.hasInteractionCell || UsableDeck(ThingUtility.InteractionCellWhenAt(gun, root, slot.Facing, map));
            }

            bool UsableDeck(IntVec3 cell) => Geometry.IsRampartGunCell(cell) &&
                !Tiers[1].Openings.Contains(Api.CellOnFloor(cell, 1, map));
        }

        internal bool HasFlankPort(FortressEmplacementSlot slot) =>
            !NaturalBarrier.Contains(slot.Ground + slot.Facing.FacingCell);

        private static ThingDef BastionParapet(IntVec3 cell, IntVec3 center, BastionDimensions dim, ThingDef embrasure)
        {
            int ax = Math.Abs(cell.x - center.x);
            int az = Math.Abs(cell.z - center.z);

            // Re-entrant corner where curtain meets bastion flank: solid wall merlon.
            if (ax == dim.FlankInner && az == dim.CurtainOuter || az == dim.FlankInner && ax == dim.CurtainOuter)
                return ThingDefOf.Wall;

            // Straight X-flank (vertical short edge protecting North/South curtain):
            // emb at +1 (wall-foot), wall at +2, emb at +3, wall at +4, barricade at shoulder.
            if (ax == dim.FlankInner && az > dim.CurtainOuter && az < dim.Shoulder)
            {
                int offset = az - dim.CurtainOuter;
                return offset % 2 == 1 ? embrasure : ThingDefOf.Wall;
            }

            // Straight Z-flank (horizontal short edge protecting East/West curtain):
            if (az == dim.FlankInner && ax > dim.CurtainOuter && ax < dim.Shoulder)
            {
                int offset = ax - dim.CurtainOuter;
                return offset % 2 == 1 ? embrasure : ThingDefOf.Wall;
            }

            // Recessed curtain outer edge: merlon rhythm matching SquareKeep.
            if (ax == dim.CurtainOuter && az < dim.FlankInner)
            {
                int offset = (az + dim.FlankInner) % 4;
                if (offset == 0) return embrasure;
                return offset == 2 ? ThingDefOf.Wall : ThingDefOf.Barricade;
            }
            if (az == dim.CurtainOuter && ax < dim.FlankInner)
            {
                int offset = (ax + dim.FlankInner) % 4;
                if (offset == 0) return embrasure;
                return offset == 2 ? ThingDefOf.Wall : ThingDefOf.Barricade;
            }

            // Outward stepped bastion faces: low cover for wide 3D firing arcs.
            return ThingDefOf.Barricade;
        }

        private static List<FortressEmplacementSlot> BuildBastionSlots(
            IntVec3 center, BastionDimensions dim, (int Sx, int Sz, Rot4 XSide, Rot4 ZSide)[] corners)
        {
            var slots = new List<FortressEmplacementSlot>();
            int[] flankOffsets = dim.FlankLength >= 5 ? new[] { 1, 3 } : new[] { 1 };

            for (int i = 0; i < corners.Length; i++)
            {
                var (sx, sz, xSide, zSide) = corners[i];
                int outwardMask = FortressGeometry.SectorBit(xSide) | FortressGeometry.SectorBit(zSide);

                // Straight X-flank guns cover the adjacent North/South curtain sector.
                foreach (int offset in flankOffsets)
                {
                    IntVec3 gun = new(center.x + sx * (dim.FlankInner + 1), 0, center.z + sz * (dim.CurtainOuter + offset));
                    slots.Add(new FortressEmplacementSlot(
                        gun, 1, xSide.Opposite, FortressEmplacementRole.FlankCurtain,
                        FortressGeometry.SectorBit(zSide), i));
                }

                // Straight Z-flank guns cover the adjacent East/West curtain sector.
                foreach (int offset in flankOffsets)
                {
                    IntVec3 gun = new(center.x + sx * (dim.CurtainOuter + offset), 0, center.z + sz * (dim.FlankInner + 1));
                    slots.Add(new FortressEmplacementSlot(
                        gun, 1, zSide.Opposite, FortressEmplacementRole.FlankCurtain,
                        FortressGeometry.SectorBit(xSide), i));
                }

                // Salient nose parapet-hugging anchor for 2x2 corner auto-turret.
                int tipInset = dim.Half - 2;
                IntVec3 nose = new(center.x + sx * tipInset, 0, center.z + sz * tipInset);
                slots.Add(new FortressEmplacementSlot(
                    nose, 1, zSide, FortressEmplacementRole.OutwardFace, outwardMask, i));

                // Two outward face step-corner pockets for spaced 1x1 auto-turrets (anti-chain-detonation).
                IntVec3 shoulderZ = new(center.x + sx * dim.CurtainOuter, 0, center.z + sz * dim.Shoulder);
                IntVec3 shoulderX = new(center.x + sx * dim.Shoulder, 0, center.z + sz * dim.CurtainOuter);
                slots.Add(new FortressEmplacementSlot(
                    shoulderZ, 1, zSide, FortressEmplacementRole.OutwardShoulder, outwardMask, i));
                slots.Add(new FortressEmplacementSlot(
                    shoulderX, 1, xSide, FortressEmplacementRole.OutwardShoulder, outwardMask, i));
            }

            // Recessed curtain wall gun slots aligned behind curtain embrasures ((offset + FlankInner) % 4 == 0).
            int curtainOffset = dim.FlankInner >= 13 ? 7 : 5;
            foreach (Rot4 side in GenAdj.CardinalDirections.Select(Rot4.FromIntVec3))
            {
                IntVec3 mid = center + side.FacingCell * (dim.CurtainOuter - 1);
                IntVec3 along = side.Rotated(RotationDirection.Clockwise).FacingCell;
                int sectorMask = FortressGeometry.SectorBit(side);
                foreach (int sign in new[] { -1, 1 })
                {
                    IntVec3 gun = mid + along * (sign * curtainOffset);
                    slots.Add(new FortressEmplacementSlot(
                        gun, 1, side, FortressEmplacementRole.CurtainWall, sectorMask, -1));
                }
            }

            return slots;
        }

        private static Dictionary<IntVec3, int> ComputeVaultWallDepth(HashSet<IntVec3> vaultCells)
        {
            var depth = new Dictionary<IntVec3, int>(vaultCells.Count);
            var queue = new Queue<IntVec3>();
            foreach (IntVec3 cell in vaultCells)
            {
                foreach (IntVec3 dir in GenAdj.CardinalDirections)
                {
                    if (!vaultCells.Contains(cell + dir))
                    {
                        depth[cell] = 1;
                        queue.Enqueue(cell);
                        break;
                    }
                }
            }
            while (queue.Count > 0)
            {
                IntVec3 current = queue.Dequeue();
                int nextDepth = depth[current] + 1;
                foreach (IntVec3 dir in GenAdj.CardinalDirections)
                {
                    IntVec3 next = current + dir;
                    if (!vaultCells.Contains(next) || depth.ContainsKey(next)) continue;
                    depth[next] = nextDepth;
                    queue.Enqueue(next);
                }
            }
            return depth;
        }

        private static IEnumerable<Rot4> OrderedGateSides(Rot4 primary, bool fourGates) =>
            fourGates
                ? GenAdj.CardinalDirections.Select(Rot4.FromIntVec3).OrderByDescending(side => side == primary)
                : new[] { primary };

        private bool VerticalLinksFit()
        {
            var reserved = new HashSet<IntVec3>();
            foreach (FortressTier tier in Tiers)
            {
                reserved.UnionWith(tier.Openings);
                foreach (StairGeometry stair in tier.Stairs)
                {
                    reserved.UnionWith(stair.footprint);
                    reserved.Add(stair.exit);
                }
            }

            foreach (FortressLadder ladder in Ladders)
            {
                if (!reserved.Add(ladder.Foot) || !reserved.Add(ladder.Landing)) return false;
            }
            return true;
        }

        internal static CellRect KeepFootprint(CellRect outer, int side) =>
            CellRect.CenteredOn(outer.CenterCell, side, side);

        internal static IEnumerable<CellRect> TowerFootprints(CellRect outer, int side)
        {
            CellRect area = outer.ContractedBy(CurtainThickness + TowerClearance);
            foreach (int x in new[] { area.minX, area.maxX - side + 1 })
                foreach (int z in new[] { area.minZ, area.maxZ - side + 1 })
                    yield return new CellRect(x, z, side, side);
        }

        private void AddTowers(Map map, int side, int top, ThingDef embrasure, List<FortressBastionZone> zones)
        {
            int index = 0;
            foreach (CellRect tower in TowerFootprints(Outer, side))
            {
                bool west = tower.CenterCell.x < Outer.CenterCell.x;
                bool south = tower.CenterCell.z < Outer.CenterCell.z;
                Rot4 xSide = west ? Rot4.West : Rot4.East;
                Rot4 zSide = south ? Rot4.South : Rot4.North;
                Rot4 inward = xSide.Opposite;
                Modules.Add(new FortressModule(tower, top, FortressModuleKind.Tower));
                Rot4 inwardZ = zSide.Opposite;
                IntVec3 inwardCorner = new IntVec3(west ? tower.maxX : tower.minX, 0,
                    south ? tower.maxZ : tower.minZ);
                IntVec3 lowerFoot = inwardCorner - inward.FacingCell - inwardZ.FacingCell;

                int sectorMask = FortressGeometry.SectorBit(xSide) | FortressGeometry.SectorBit(zSide);
                zones.Add(new FortressBastionZone(index++, tower, top, sectorMask, tower.CenterCell, xSide, zSide));

                AddTowerBase(tower, inward, inwardZ);
                AddInterior(map, tower, 0);
                AddDeck(map, tower, 1);
                AddLadder(map, 0, lowerFoot, inward.Opposite);
                AddPerimeter(map, tower, 1, Rot4.Invalid, embrasure, centerPorts: top == 2 && side >= 7);

                if (top == 1) continue;
                AddInterior(map, tower, 1);

                // Turn the upper flight toward the corner, keeping both
                // landings inside the shell and the two shaft columns distinct.
                AddLadder(map, 1, lowerFoot - inwardZ.FacingCell, inwardZ);
                AddDeck(map, tower, 2);
                foreach (IntVec3 cell in tower.EdgeCells)
                    Tiers[2].Buildings.Add(new FortressBuilding(ThingDefOf.Barricade,
                        Api.CellOnFloor(cell, 2, map), Rot4.North));
            }
        }

        private void AddSingleStoryFacility(Map map, FortressLot lot, bool sterile = false)
        {
            Modules.Add(new FortressModule(lot.Rect, 0, FortressModuleKind.Facility));
            AddPerimeter(map, lot.Rect, 0, lot.Facing);
            AddInterior(map, lot.Rect, 0, sterile, roofed: true);
        }

        private void AddFacility(Map map, FortressLot lot, bool upperRoom, bool sterile = false)
        {
            Modules.Add(new FortressModule(lot.Rect, 1, FortressModuleKind.Facility));
            AddPerimeter(map, lot.Rect, 0, lot.Facing);
            AddInterior(map, lot.Rect, 0);
            AddDeck(map, lot.Rect, 1);
            AddLadder(map, 0, lot.Foot, lot.Facing.Opposite);
            if (!upperRoom) return;
            AddPerimeter(map, lot.Rect, 1, lot.Facing);
            AddInterior(map, lot.Rect, 1, sterile, roofed: true);
        }

        private void AddInterior(Map map, CellRect module, int floor, bool sterile = false, bool roofed = false)
        {
            CellRect inside = module.ContractedBy(1);
            if (inside.IsEmpty) return;
            CellRect band = CellRect.FromLimits(Api.CellOnFloor(inside.Min, floor, map), Api.CellOnFloor(inside.Max, floor, map));
            Tiers[floor].Interiors.Add(new FortressRoomFloor(band, sterile, roofed));
        }

        private void AddTowerBase(CellRect tower, Rot4 inward, Rot4 inwardZ)
        {
            int doorOffset = tower.Width / 2 - 1;
            IntVec3 sideDoor = tower.GetCenterCellOnEdge(inward) - inwardZ.FacingCell * doorOffset;
            IntVec3 endDoor = tower.GetCenterCellOnEdge(inwardZ) - inward.FacingCell * doorOffset;
            foreach (IntVec3 cell in tower.EdgeCells)
            {
                if (cell == sideDoor) Tiers[0].Buildings.Add(new FortressBuilding(ThingDefOf.Door, cell, inward));
                else if (cell == endDoor) Tiers[0].Buildings.Add(new FortressBuilding(ThingDefOf.Door, cell, inwardZ));
                else Tiers[0].Buildings.Add(new FortressBuilding(ThingDefOf.Wall, cell, Rot4.North));
            }
        }

        private void AddLadder(Map map, int level, IntVec3 groundFoot, Rot4 facing)
        {
            IntVec3 foot = Api.CellOnFloor(groundFoot, level, map);
            IntVec3 landing = Api.CellOnFloor(groundFoot + facing.FacingCell, level + 1, map);
            Tiers[level + 1].Openings.Add(Api.CellOnFloor(groundFoot, level + 1, map));
            Ladders.Add(new FortressLadder(foot, landing, facing));
        }

        private void AddPerimeter(Map map, CellRect rect, int level, Rot4 gateSide, ThingDef embrasure = null,
            bool centerPorts = false, bool fourDoors = false)
        {
            var doors = gateSide.IsValid ? GateSides(gateSide, fourDoors)
                .ToDictionary(side => rect.GetCenterCellOnEdge(side)) : null;
            int portHalfWidth = rect.Width >= 9 ? 1 : 0;
            foreach (IntVec3 cell in rect.EdgeCells)
            {
                bool inPort = Math.Abs(cell.x - rect.CenterCell.x) <= portHalfWidth ||
                    Math.Abs(cell.z - rect.CenterCell.z) <= portHalfWidth;
                Rot4 rotation = Rot4.North;
                bool isDoor = doors != null && doors.TryGetValue(cell, out rotation);
                ThingDef def = isDoor ? ThingDefOf.Door :
                    embrasure != null && (centerPorts ? inPort : Along(rect, cell) % 2 == 0) ? embrasure : ThingDefOf.Wall;
                Tiers[level].Buildings.Add(new FortressBuilding(def, Api.CellOnFloor(cell, level, map),
                    isDoor ? rotation : Rot4.North));
            }
        }

        private void AddParapet(Map map, CellRect rect, int level, ThingDef embrasure)
        {
            foreach (IntVec3 cell in rect.EdgeCells)
            {
                ThingDef def = Along(rect, cell) % 2 == 0 ? embrasure : ThingDefOf.Barricade;
                Tiers[level].Buildings.Add(new FortressBuilding(def, Api.CellOnFloor(cell, level, map), Rot4.North));
            }
        }

        private void AddDeck(Map map, CellRect rect, int level)
        {
            // Lay the deck from the supported perimeter inwards.
            foreach (IntVec3 cell in rect.OrderBy(cell => Depth(rect, cell)))
                Tiers[level].Deck.Add(Api.CellOnFloor(cell, level, map));
        }

        private bool AddStair(Map map, int level, IntVec3 groundCenter, Rot4 rotation)
        {
            IntVec3 center = Api.CellOnFloor(groundCenter, level, map);
            if (!Api.TryGetStairGeometry(center, rotation, map, out StairGeometry stair)) return false;
            Tiers[level].Stairs.Add(stair);
            Tiers[level + 1].Openings.UnionWith(stair.well);
            return true;
        }

        private static ThingDef Merlon(CellRect rect, IntVec3 cell, ThingDef embrasure)
        {
            int offset = Along(rect, cell) % 4;
            if (offset == 0) return embrasure;
            return offset == 2 ? ThingDefOf.Wall : ThingDefOf.Barricade;
        }

        private static int Along(CellRect rect, IntVec3 cell) =>
            cell.z == rect.minZ || cell.z == rect.maxZ ? cell.x - rect.minX : cell.z - rect.minZ;

        internal static int Depth(CellRect rect, IntVec3 cell) =>
            Math.Min(Math.Min(cell.x - rect.minX, rect.maxX - cell.x),
                Math.Min(cell.z - rect.minZ, rect.maxZ - cell.z));
    }
}
