using System;
using System.Collections.Generic;
using System.Linq;
using ProjectZ;
using RimWorld;
using Verse;

namespace RimSiege.Fortress
{
    internal sealed class FortressDefensePlanner
    {
        private const int WireSetback = 8;
        private static readonly Rot4[] Faces = { Rot4.North, Rot4.East, Rot4.South, Rot4.West };

        private readonly Map map;
        private readonly FortressLayout layout;
        private readonly List<FortressDefensePiece> pieces = new();
        private readonly HashSet<IntVec3> reserved = new();
        private readonly CellRect ground;
        private float powerRemaining;

        private FortressDefensePlanner(Map map, FortressLayout layout)
        {
            this.map = map;
            this.layout = layout;
            ground = Api.GroundRect(map);
            powerRemaining = FortressPower.FirmCapacity(map, layout);
            foreach (FortressTier tier in layout.Tiers)
            {
                reserved.UnionWith(tier.Openings);
                foreach (var stair in tier.Stairs)
                {
                    reserved.UnionWith(stair.footprint);
                    ReserveLanding(stair.exit);
                }
            }
            foreach (FortressLadder ladder in layout.Ladders)
            {
                reserved.Add(ladder.Foot);
                ReserveLanding(ladder.Landing);
            }
        }

        internal static List<FortressDefensePiece> Plan(Map map, FortressLayout layout, float points)
        {
            var plan = new FortressDefensePlanner(map, layout);
            FortressDefenseProfileDef profile = FortressDefenseProfileDef.For(map.ParentFaction);
            ThingDef machineGun = profile.machineGuns.RandomElement();
            ThingDef corner = ModsConfig.RoyaltyActive && map.ParentFaction.def == FactionDefOf.Empire
                ? profile.imperialCorner : profile.cornerTurrets.RandomElementWithFallback();
            bool large = layout.Outer.Width == FortressDimensions.Large.OuterSide;

            plan.PlaceMortar(layout.Facilities.Mortar, layout.Facilities.MortarDef);
            plan.PlaceMortar(layout.Facilities.SecondMortar, layout.Facilities.SecondMortarDef);
            if (corner != null) plan.PlaceCorners(corner);
            plan.PlaceWallGuns(machineGun, points >= profile.grenadePoints ? profile.grenadeLauncher : null, large);
            plan.PlaceTowers(profile, machineGun, large, points);
            plan.PlaceWire();
            if (profile.hunterTrap != null) plan.PlaceTraps(profile.hunterTrap, large ? 2 : 1);
            return plan.pieces;
        }

        private void ReserveLanding(IntVec3 landing)
        {
            reserved.Add(landing);
            foreach (IntVec3 direction in GenAdj.CardinalDirections)
            {
                IntVec3 cell = landing + direction;
                if (cell.InBounds(map) && cell.Standable(map)) reserved.Add(cell);
            }
        }

        private void PlaceMortar(CellRect yard, ThingDef def)
        {
            if (def == null) return;
            FortressDefensePiece piece = yard.OrderBy(cell => (cell - yard.CenterCell).LengthHorizontalSquared)
                .Select(cell => At(def, cell, layout.GateSide))
                .FirstOrDefault(candidate => Fits(candidate, yard));
            if (piece != null) Add(piece);
        }

        private void PlaceCorners(ThingDef def)
        {
            CellRect deck = OnFloor(layout.Outer.ContractedBy(1), 1);
            CellRect interior = OnFloor(layout.Outer.ContractedBy(FortressLayout.CurtainThickness), 1);
            foreach (Rot4 x in new[] { Rot4.West, Rot4.East })
            {
                foreach (Rot4 z in new[] { Rot4.South, Rot4.North })
                {
                    IntVec3 corner = new(x == Rot4.West ? deck.minX : deck.maxX, 0,
                        z == Rot4.South ? deck.minZ : deck.maxZ);
                    var area = CellRect.CenteredOn(corner, 5, 5).ClipInsideMap(map);
                    FortressDefensePiece piece = area.Where(deck.Contains)
                        .OrderBy(cell => (cell - corner).LengthHorizontalSquared)
                        .SelectMany(cell => new[] { At(def, cell, x), At(def, cell, z) })
                        .FirstOrDefault(candidate => Fits(candidate, deck) &&
                            !Footprint(candidate).Any(interior.Contains));
                    if (piece != null) Add(piece);
                }
            }
        }

        private void PlaceWallGuns(ThingDef machineGun, ThingDef grenadeLauncher, bool large)
        {
            CellRect wall = OnFloor(layout.Outer, 1);
            CellRect deck = wall.ContractedBy(1);
            int pairs = large ? 4 : 3;
            foreach (Rot4 face in Faces)
            {
                IntVec3 center = wall.GetCenterCellOnEdge(face) - face.FacingCell;
                IntVec3 along = face.Rotated(RotationDirection.Clockwise).FacingCell;
                int[] preferred = large ? new[] { 5, 7, 13, 15 } : new[] { 1, 5, 7 };
                int grenadeOffset = large ? 13 : 7;
                bool grenadePair = grenadeLauncher != null && TryPair(grenadeLauncher, grenadeOffset);
                int machinePairLimit = grenadeLauncher != null ? pairs - 1 : pairs;
                int machinePairs = 0;
                foreach (int offset in preferred)
                {
                    if (machinePairs == machinePairLimit) break;
                    if (TryPair(machineGun, offset)) machinePairs++;
                }

                // Keep any usable positions when one half of a pair has no deck.
                foreach (int side in new[] { -1, 1 })
                {
                    int placed = machinePairs + (grenadePair ? 1 : 0);
                    if (placed == pairs) break;
                    if (grenadeLauncher != null && !grenadePair && TrySingle(grenadeLauncher, grenadeOffset, side))
                        placed++;
                    foreach (int offset in preferred)
                    {
                        if (placed == pairs) break;
                        if (TrySingle(machineGun, offset, side)) placed++;
                    }
                }

                bool TryPair(ThingDef def, int offset)
                {
                    foreach (int candidate in Offsets(offset))
                    {
                        FortressDefensePiece left = WallGun(def, candidate, -1);
                        FortressDefensePiece right = WallGun(def, candidate, 1);
                        if (!FitsWallGun(left) || !FitsWallGun(right)) continue;
                        Add(left);
                        Add(right);
                        return true;
                    }
                    return false;
                }

                bool TrySingle(ThingDef def, int offset, int side)
                {
                    foreach (int candidate in Offsets(offset))
                    {
                        FortressDefensePiece gun = WallGun(def, candidate, side);
                        if (!FitsWallGun(gun)) continue;
                        Add(gun);
                        return true;
                    }
                    return false;
                }

                IEnumerable<int> Offsets(int preferredOffset) =>
                    Enumerable.Range(1, layout.Outer.Width / 2 - 4)
                        .Where(offset => offset % 2 == 1)
                        .OrderBy(offset => Math.Abs(offset - preferredOffset));

                FortressDefensePiece WallGun(ThingDef def, int offset, int side)
                {
                    // The gun faces along the parapet and its native rear
                    // interaction cell sits beside it behind the solid merlon.
                    IntVec3 facing = along * (offset % 4 == 1 ? -side : side);
                    return At(def, center + along * (offset * side), Rot4.FromIntVec3(facing));
                }

                bool FitsWallGun(FortressDefensePiece gun) => Fits(gun, deck) &&
                    (OperatorCell(gun) + face.FacingCell).GetEdifice(map)?.def.Fillage == FillCategory.Full;
            }
        }

        private void PlaceTowers(FortressDefenseProfileDef profile, ThingDef machineGun, bool large, float points)
        {
            FortressModule[] towers = layout.Modules.Where(module => module.Kind == FortressModuleKind.Tower).ToArray();
            FortressGunOption roof = (large ? profile.largeRoof : profile.smallRoof)
                .Where(option => option.minPoints <= points &&
                    PowerDraw(option.turret) * option.count * towers.Length <= powerRemaining)
                .RandomElementWithFallback();

            foreach (FortressModule tower in towers)
            {
                CellRect top = OnFloor(tower.Ground.ContractedBy(1), tower.Top);
                Rot4 x = tower.Ground.CenterCell.x < layout.Outer.CenterCell.x ? Rot4.West : Rot4.East;
                Rot4 z = tower.Ground.CenterCell.z < layout.Outer.CenterCell.z ? Rot4.South : Rot4.North;

                if (roof != null) PlaceRoof(roof, top, x, z);
                if (!large || !profile.middleMachineGuns || tower.Top < 2) continue;

                CellRect middle = OnFloor(tower.Ground.ContractedBy(1), 1);
                foreach (Rot4 face in Faces)
                {
                    IntVec3 port = middle.GetCenterCellOnEdge(face);
                    FortressDefensePiece gun = At(machineGun, port, face);
                    if (!Fits(gun, middle)) gun = At(machineGun, port - face.FacingCell, face);
                    if (Fits(gun, middle)) Add(gun);
                }
            }
        }

        private void PlaceRoof(FortressGunOption roof, CellRect top, Rot4 x, Rot4 z)
        {
            foreach (int count in Enumerable.Range(1, roof.count).Reverse())
            {
                foreach (Rot4 face in new[] { x, z })
                {
                    IntVec3 along = face.Rotated(RotationDirection.Clockwise).FacingCell;
                    int stride = (face.IsHorizontal ? roof.turret.Size.x : roof.turret.Size.z) + 1;
                    IEnumerable<IntVec3> roots = roof.centered ? new[] { top.CenterCell } : top
                        .OrderByDescending(cell => Projection(cell - top.CenterCell, face.FacingCell))
                        .ThenBy(cell => Math.Abs(2 * Projection(cell - top.CenterCell, along) + stride * (count - 1)));

                    foreach (IntVec3 root in roots)
                    {
                        FortressDefensePiece[] row = Enumerable.Range(0, count)
                            .Select(i => At(roof.turret, root + along * (i * stride), face)).ToArray();
                        if (!row.All(gun => Fits(gun, top))) continue;
                        foreach (FortressDefensePiece gun in row) Add(gun);
                        return;
                    }
                }
            }
        }

        private void PlaceWire()
        {
            ThingDef wire = DefDatabase<ThingDef>.GetNamed("CE_BarbedWire");
            IntVec3 entrance = layout.Gate + layout.GateSide.FacingCell * WireSetback;
            IntVec3 along = layout.GateSide.Rotated(RotationDirection.Clockwise).FacingCell;
            CellRect ring = layout.Outer.ExpandedBy(WireSetback);
            foreach (IntVec3 cell in ring.EdgeCells)
            {
                if (!ground.Contains(cell) || cell.GetTerrain(map).IsWater ||
                    Math.Abs(Projection(cell - entrance, along)) <= 1 &&
                    Projection(cell - entrance, layout.GateSide.FacingCell) == 0) continue;
                FortressDefensePiece piece = At(wire, cell, Rot4.North);
                if (Fits(piece, ground)) Add(piece);
            }
        }

        private void PlaceTraps(ThingDef def, int perSide)
        {
            CellRect courtyard = layout.Outer.ContractedBy(FortressLayout.CurtainThickness);
            var traps = new List<IntVec3>();
            foreach (Rot4 face in Faces)
            {
                IntVec3 center = courtyard.GetCenterCellOnEdge(face);
                IntVec3 along = face.Rotated(RotationDirection.Clockwise).FacingCell;
                int placed = 0;
                foreach (int offset in Enumerable.Range(3, courtyard.Width / 2 - 3).OrderBy(value => Math.Abs(value - 7)))
                {
                    foreach (int sign in new[] { -1, 1 })
                    {
                        IntVec3 cell = center + along * (sign * offset);
                        FortressDefensePiece piece = At(def, cell, Rot4.North);
                        if (traps.Any(other => other.AdjacentTo8WayOrInside(cell)) || !Fits(piece, courtyard) ||
                            def.PlaceWorkers.Any(worker => !worker.AllowsPlacing(def, cell, Rot4.North, map).Accepted))
                            continue;
                        Add(piece);
                        traps.Add(cell);
                        if (++placed == perSide) break;
                    }
                    if (placed == perSide) break;
                }
            }
        }

        private FortressDefensePiece At(ThingDef def, IntVec3 cell, Rot4 rotation) => new()
        {
            Def = def,
            Ammo = FortressArmament.DefaultAmmo(def),
            Ground = Api.GroundCellOf(cell, map),
            Floor = Api.FloorOf(cell, map),
            Rotation = rotation
        };

        private CellRect Footprint(FortressDefensePiece piece) =>
            GenAdj.OccupiedRect(piece.CellOn(map), piece.Rotation, piece.Def.Size);

        private bool Fits(FortressDefensePiece piece, CellRect allowed)
        {
            IntVec3 cell = piece.CellOn(map);
            CellRect footprint = Footprint(piece);
            if (PowerDraw(piece.Def) > powerRemaining || !footprint.FullyContainedWithin(allowed) ||
                footprint.Any(covered => reserved.Contains(covered) || !covered.Standable(map) ||
                    covered.GetFirstBuilding(map) != null)) return false;
            if (piece.Def.hasInteractionCell)
            {
                IntVec3 operatorCell = OperatorCell(piece);
                if (!allowed.Contains(operatorCell) || reserved.Contains(operatorCell) ||
                    !operatorCell.Standable(map) || operatorCell.GetFirstBuilding(map) != null) return false;
            }
            return GenSpawn.CanSpawnAt(piece.Def, cell, map, piece.Rotation, canWipeEdifices: false);
        }

        private void Add(FortressDefensePiece piece)
        {
            pieces.Add(piece);
            reserved.UnionWith(Footprint(piece));
            if (piece.Def.hasInteractionCell)
                reserved.Add(OperatorCell(piece));
            powerRemaining -= PowerDraw(piece.Def);
        }

        private IntVec3 OperatorCell(FortressDefensePiece piece) =>
            ThingUtility.InteractionCellWhenAt(piece.Def, piece.CellOn(map), piece.Rotation, map);

        private static int Projection(IntVec3 offset, IntVec3 direction) =>
            offset.x * direction.x + offset.z * direction.z;

        private static float PowerDraw(ThingDef def) =>
            Math.Max(0f, def.GetCompProperties<CompProperties_Power>()?.PowerConsumption ?? 0f);

        private CellRect OnFloor(CellRect rect, int floor) =>
            CellRect.FromLimits(Api.CellOnFloor(rect.Min, floor, map), Api.CellOnFloor(rect.Max, floor, map));
    }
}
