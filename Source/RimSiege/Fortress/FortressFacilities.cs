using System;
using System.Collections.Generic;
using System.Linq;
using ProjectZ;
using RimWorld;
using RimWorld.BaseGen;
using Verse;

namespace RimSiege.Fortress
{
    internal readonly record struct FortressLot(CellRect Rect, Rot4 Facing)
    {
        internal static readonly FortressLot Empty = new(CellRect.Empty, Rot4.Invalid);

        // default(CellRect) is (0,0)-(0,0) with Width=1; any valid facility room is at least 3x3.
        internal bool IsEmpty => Rect.Width < 3 || Rect.Height < 3 || !Facing.IsValid;
        internal IntVec3 Landing => Rect.GetCenterCellOnEdge(Facing);
        internal IntVec3 Foot => Landing + Facing.FacingCell;

        internal CellRect Interior(Map map, int level)
        {
            CellRect cells = Rect.ContractedBy(1);
            return CellRect.FromLimits(Api.CellOnFloor(cells.Min, level, map), Api.CellOnFloor(cells.Max, level, map));
        }
    }

    internal readonly record struct FortressFacilityPlan(
        FortressLot Living, FortressLot Supply, FortressLot Power, CellRect Mortar, bool Industrial,
        ThingDef MortarDef, CellRect SecondMortar, ThingDef SecondMortarDef,
        FortressLot Dining, FortressLot Infirmary,
        FortressLot AuxBarracks, FortressLot AuxSupply)
    {
        private const int LayoutAttempts = 6;
        private const float MortarPoints = 2000f;
        private const float SecondMortarPoints = 6000f;

        internal bool SingleStoryCourtyard => !Dining.IsEmpty || !Infirmary.IsEmpty;
        internal CellRect MedicalRect => !Infirmary.IsEmpty ? Infirmary.Rect : Supply.Rect;
        internal int MedicalFloor => !Infirmary.IsEmpty ? 0 : 1;

        internal CellRect DiningInterior(Map map) =>
            !Dining.IsEmpty ? Dining.Interior(map, 0) : Living.Interior(map, 1);

        internal IEnumerable<FortressLot> ExtraLots()
        {
            if (!Dining.IsEmpty) yield return Dining;
            if (!Infirmary.IsEmpty) yield return Infirmary;
            if (!AuxBarracks.IsEmpty) yield return AuxBarracks;
            if (!AuxSupply.IsEmpty) yield return AuxSupply;
        }

        internal static bool TryPlace(Map map, FortressLayout layout, out FortressFacilityPlan result)
        {
            if (layout.Kind == FortressLayoutKind.HumanBastion)
                return TryPlaceBastion(map, layout, out result);

            bool industrial = map.ParentFaction.def.techLevel >= TechLevel.Industrial;
            bool powerRequired = industrial && map.ParentFaction.def.humanlikeFaction;
            FortressGeometry geo = layout.Geometry;
            CellRect courtyard = geo.CourtyardBounds.ContractedBy(1);
            var reserved = BaseReserved(map, layout, courtyard, requireUpperClear: true);

            for (int attempt = 0; attempt < LayoutAttempts; attempt++)
            {
                var used = new HashSet<IntVec3>(reserved);
                FortressLot power = FortressLot.Empty;
                // Compact courtyards can fit a 4x4 power room between the gate lanes and towers.
                if (powerRequired && !ReserveRoom(8, 7, used, out power) &&
                    (layout.Size != FortressSize.Compact ||
                        !ReserveRoom(6, 6, used, out power))) continue;
                if (!ReserveRoom(8, 6, used, out FortressLot living) ||
                    !ReserveRoom(7, 6, used, out FortressLot supply)) continue;

                PlaceMortars(map, courtyard, used, supply, out CellRect mortar, out ThingDef mortarDef,
                    out CellRect secondMortar, out ThingDef secondMortarDef, allowCompactMortar: false);

                result = new FortressFacilityPlan(living, supply, power, mortar, industrial, mortarDef,
                    secondMortar, secondMortarDef, FortressLot.Empty, FortressLot.Empty,
                    FortressLot.Empty, FortressLot.Empty);
                return true;
            }

            result = default;
            return false;

            bool ReserveRoom(int preferred, int minimum, HashSet<IntVec3> used, out FortressLot lot)
            {
                bool FitsRoom(CellRect rect) => Fits(rect, used) && Facing(rect, used, courtyard, layout.Outer.CenterCell).IsValid;
                bool found = courtyard.TryFindRandomInnerRect(new IntVec2(preferred, preferred), out CellRect rect, FitsRoom) ||
                    minimum != preferred && courtyard.TryFindRandomInnerRect(new IntVec2(minimum, minimum), out rect, FitsRoom);
                lot = found ? new FortressLot(rect, Facing(rect, used, courtyard, layout.Outer.CenterCell)) : FortressLot.Empty;
                if (found)
                {
                    used.UnionWith(rect);
                    used.Add(lot.Foot);
                }
                return found;
            }
        }

        private static bool TryPlaceBastion(Map map, FortressLayout layout, out FortressFacilityPlan result)
        {
            bool industrial = map.ParentFaction.def.techLevel >= TechLevel.Industrial;
            bool powerRequired = industrial && map.ParentFaction.def.humanlikeFaction;
            bool large = layout.Size == FortressSize.Large;
            FortressGeometry geo = layout.Geometry;
            CellRect courtyard = geo.CourtyardBounds.ContractedBy(1);
            IntVec3 center = layout.Outer.CenterCell;
            var groundReserved = BaseReserved(map, layout, courtyard, requireUpperClear: false);
            var upperBlocked = new HashSet<IntVec3>();
            foreach (IntVec3 cell in courtyard)
                if (!FortressSiting.UpperSpaceFree(Api.CellOnFloor(cell, 1, map), map))
                    upperBlocked.Add(cell);

            IntVec2[] powerSizes = large
                ? new[] { new IntVec2(8, 8), new IntVec2(8, 7), new IntVec2(7, 7), new IntVec2(6, 6) }
                : new[] { new IntVec2(6, 6) };
            IntVec2[] barracksSizes = large
                ? new[] { new IntVec2(7, 6), new IntVec2(6, 7), new IntVec2(6, 6) }
                : new[] { new IntVec2(6, 5), new IntVec2(5, 6), new IntVec2(5, 5) };
            IntVec2[] infraSizes = large
                ? new[] { new IntVec2(6, 6), new IntVec2(6, 5), new IntVec2(5, 6), new IntVec2(5, 5) }
                : new[] { new IntVec2(5, 5) };
            IntVec2[] ordnanceSizes = { new IntVec2(5, 5) };

            for (int attempt = 0; attempt < LayoutAttempts; attempt++)
            {
                var used = new HashSet<IntVec3>(groundReserved);
                FortressLot power = FortressLot.Empty;
                if (powerRequired && !ReservePacked(powerSizes, used, needUpper: true, out power))
                    continue;
                if (!ReservePacked(barracksSizes, used, needUpper: false, out FortressLot living) ||
                    !ReservePacked(infraSizes, used, needUpper: false, out FortressLot dining) ||
                    !ReservePacked(infraSizes, used, needUpper: false, out FortressLot infirmary) ||
                    !ReservePacked(ordnanceSizes, used, needUpper: false, out FortressLot supply))
                    continue;

                PlaceMortars(map, courtyard, used, supply, out CellRect mortar, out ThingDef mortarDef,
                    out CellRect secondMortar, out ThingDef secondMortarDef, allowCompactMortar: true);

                // Additional low-rise garrison barracks to fill courtyard living capacity.
                ReservePacked(infraSizes, used, needUpper: false, out FortressLot auxBarracks);

                result = new FortressFacilityPlan(living, supply, power, mortar, industrial, mortarDef,
                    secondMortar, secondMortarDef, dining, infirmary, auxBarracks, FortressLot.Empty);
                return true;
            }

            result = default;
            return false;

            bool ReservePacked(IntVec2[] sizes, HashSet<IntVec3> used, bool needUpper, out FortressLot lot)
            {
                foreach (IntVec2 size in sizes)
                {
                    var candidates = new List<(int Score, CellRect Rect)>();
                    int bestScore = int.MinValue;
                    for (int z = courtyard.minZ; z <= courtyard.maxZ - size.z + 1; z++)
                    {
                        for (int x = courtyard.minX; x <= courtyard.maxX - size.x + 1; x++)
                        {
                            var rect = new CellRect(x, z, size.x, size.z);
                            if (!Fits(rect, used)) continue;
                            if (needUpper && rect.Any(upperBlocked.Contains)) continue;
                            if (!Facing(rect, used, courtyard, center).IsValid) continue;
                            int score = EdgeScore(rect, courtyard, used);
                            if (score > bestScore) bestScore = score;
                            candidates.Add((score, rect));
                        }
                    }
                    if (candidates.Count == 0) continue;

                    CellRect chosen = candidates.Where(entry => entry.Score >= bestScore - 2)
                        .Select(entry => entry.Rect).RandomElement();
                    Rot4 facing = Facing(chosen, used, courtyard, center);
                    lot = new FortressLot(chosen, facing);
                    used.UnionWith(chosen);
                    used.Add(lot.Foot);
                    return true;
                }
                lot = FortressLot.Empty;
                return false;
            }
        }

        private static HashSet<IntVec3> BaseReserved(Map map, FortressLayout layout, CellRect courtyard,
            bool requireUpperClear)
        {
            FortressGeometry geo = layout.Geometry;
            var reserved = new HashSet<IntVec3>(layout.Modules.SelectMany(module => module.Ground.Cells));
            reserved.UnionWith(geo.Perimeter);
            reserved.UnionWith(CellRect.FromLimits(layout.Gate, layout.Outer.CenterCell).ExpandedBy(1));
            foreach (FortressGate gate in geo.Gates)
                reserved.UnionWith(CellRect.FromLimits(gate.Cell, layout.Outer.CenterCell));
            foreach (StairGeometry stair in layout.Tiers[0].Stairs)
            {
                reserved.UnionWith(stair.footprint.ExpandedBy(1));
                reserved.Add(Api.GroundCellOf(stair.exit, map));
            }
            if (requireUpperClear)
            {
                foreach (IntVec3 cell in courtyard)
                    if (!FortressSiting.UpperSpaceFree(Api.CellOnFloor(cell, 1, map), map))
                        reserved.Add(cell);
            }
            return reserved;
        }

        private static void PlaceMortars(Map map, CellRect courtyard, HashSet<IntVec3> used, FortressLot supply,
            out CellRect mortar, out ThingDef mortarDef, out CellRect secondMortar, out ThingDef secondMortarDef,
            bool allowCompactMortar)
        {
            mortar = CellRect.Empty;
            secondMortar = CellRect.Empty;
            mortarDef = null;
            secondMortarDef = null;
            float points = (map.Parent as FortressSite)?.OriginalPoints ?? 5000f;
            if (points < MortarPoints) return;

            if (!TryFindMortar(out mortar)) return;
            ThingDef[] pool = FortressDefenseProfileDef.For(map.ParentFaction).mortars
                .Where(option => option.minPoints <= points).Select(option => option.turret).ToArray();
            if (pool.Length == 0)
            {
                mortar = CellRect.Empty;
                return;
            }
            ThingDef firstMortarDef = pool.RandomElement();
            mortarDef = firstMortarDef;
            used.UnionWith(mortar);
            if (points >= SecondMortarPoints && TryFindMortar(out secondMortar))
            {
                secondMortarDef = pool.Where(def => def != firstMortarDef).RandomElementWithFallback(firstMortarDef);
                used.UnionWith(secondMortar);
            }

            bool TryFindMortar(out CellRect rect)
            {
                if (courtyard.TryFindRandomInnerRect(new IntVec2(6, 6), out rect, FitsMortar)) return true;
                return allowCompactMortar && courtyard.TryFindRandomInnerRect(new IntVec2(5, 5), out rect, FitsMortar);
            }

            bool FitsMortar(CellRect rect) => Fits(rect, used) && rect.CenterCell.InHorDistOf(supply.Rect.CenterCell, 26f);
        }

        private static int EdgeScore(CellRect rect, CellRect courtyard, HashSet<IntVec3> used)
        {
            int score = 0;
            foreach (IntVec3 cell in rect.ExpandedBy(1).EdgeCells)
                if (!courtyard.Contains(cell) || used.Contains(cell)) score++;
            return score;
        }

        private static bool Fits(CellRect rect, HashSet<IntVec3> used) =>
            rect.ExpandedBy(1).All(cell => !used.Contains(cell));

        private static Rot4 Facing(CellRect rect, HashSet<IntVec3> used, CellRect courtyard, IntVec3 center)
        {
            foreach (Rot4 side in GenAdj.CardinalDirections.Select(Rot4.FromIntVec3)
                .OrderBy(side => (rect.GetCenterCellOnEdge(side) - center).LengthHorizontalSquared))
            {
                IntVec3 foot = rect.GetCenterCellOnEdge(side) + side.FacingCell;
                if (courtyard.Contains(foot) && !used.Contains(foot))
                    return side;
            }
            return Rot4.Invalid;
        }
    }

    internal static class FortressFacilities
    {
        internal static void Furnish(Map map, FortressLayout layout, FactionMaterial material)
        {
            FortressFacilityPlan plan = layout.Facilities;
            Faction faction = map.ParentFaction;

            BaseGen.globalSettings.map = map;
            Push("RS_FortressBarracks", plan.Living.Interior(map, 0));
            Push("RS_FortressDining", plan.DiningInterior(map));
            CellRect medicalInterior = CellRect.FromLimits(
                Api.CellOnFloor(plan.MedicalRect.ContractedBy(1).Min, plan.MedicalFloor, map),
                Api.CellOnFloor(plan.MedicalRect.ContractedBy(1).Max, plan.MedicalFloor, map));
            Push(plan.Industrial ? "RS_FortressInfirmary" : "RS_FortressBarracks", medicalInterior);
            if (!plan.AuxBarracks.IsEmpty)
                Push("RS_FortressBarracks", plan.AuxBarracks.Interior(map, 0));
            BaseGen.Generate();

            ThingDef medicine = ModsConfig.RoyaltyActive && faction.def == FactionDefOf.Empire
                ? ThingDefOf.MedicineUltratech : plan.Industrial ? ThingDefOf.MedicineIndustrial : ThingDefOf.MedicineHerbal;
            CellRect storage = plan.Supply.Interior(map, 0);
            IntVec3 entry = plan.Supply.Landing - plan.Supply.Facing.FacingCell;
            var storageAccess = new HashSet<IntVec3>(storage.Where(cell =>
                cell.x == storage.CenterCell.x || cell.z == storage.CenterCell.z)) { entry };
            List<Building_Storage> supply = FortressStorage.PlaceShelves(map, storage, material.Stuff, storageAccess);
            int componentStacks = layout.Size == FortressSize.Large ? 2 : 1;
            // Reserve shelf capacity in Supply for courtyard mortar and nearby wall-support ordnance.
            int ordnanceShelves = supply.Count >= 3 ? 2 : supply.Count > 1 ? 1 : 0;
            if (ordnanceShelves > 0)
            {
                FortressStorage.Fill(map, supply.Take(supply.Count - ordnanceShelves), SupplyItem);
            }
            else if (supply.Count == 1)
            {
                List<IntVec3> cells = supply[0].GetSlotGroup().CellsList;
                int fillCells = Math.Max(1, cells.Count / 2);
                int index = 0;
                foreach (IntVec3 cell in cells.Take(fillCells))
                {
                    int slots = cell.GetMaxItemsAllowedInCell(map) - cell.GetItemCount(map);
                    for (int i = 0; i < slots; i++) FortressStorage.Stock(map, SupplyItem(index++), cell);
                }
            }

            if (!plan.AuxSupply.IsEmpty)
            {
                CellRect auxRoom = plan.AuxSupply.Interior(map, 0);
                IntVec3 auxEntry = plan.AuxSupply.Landing - plan.AuxSupply.Facing.FacingCell;
                var auxAccess = new HashSet<IntVec3>(auxRoom.Where(cell =>
                    cell.x == auxRoom.CenterCell.x || cell.z == auxRoom.CenterCell.z)) { auxEntry };
                List<Building_Storage> auxShelves = FortressStorage.PlaceShelves(map, auxRoom, material.Stuff, auxAccess);
                FortressStorage.Fill(map, auxShelves, SupplyItem);
            }

            if (!plan.Power.IsEmpty) FurnishPower(map, plan.Power, material.Stuff);

            void Push(string symbol, CellRect rect) =>
                BaseGen.symbolStack.Push(symbol, new ResolveParams { rect = rect, faction = faction });

            ThingDef SupplyItem(int index)
            {
                if (!plan.Industrial)
                {
                    if (index < 4) return ThingDefOf.Steel;
                    if (index == 4) return medicine;
                    return index % 2 == 0 ? material.Stuff : ThingDefOf.WoodLog;
                }

                if (index < componentStacks) return ThingDefOf.ComponentIndustrial;
                if (index == componentStacks) return medicine;
                return (index % 4) switch
                {
                    0 or 2 => ThingDefOf.Steel,
                    1 => ThingDefOf.Plasteel,
                    _ => material.Stuff
                };
            }
        }

        private static void FurnishPower(Map map, FortressLot lot, ThingDef stuff)
        {
            CellRect room = lot.Interior(map, 0);
            IntVec3 entrance = lot.Landing - lot.Facing.FacingCell;
            IntVec3 inward = lot.Facing.Opposite.FacingCell;
            var occupied = new HashSet<IntVec3>();
            for (IntVec3 cell = entrance; room.Contains(cell); cell += inward) occupied.Add(cell);
            var aisle = new HashSet<IntVec3>(occupied);

            Rot4 shelfFacing = lot.Facing.IsHorizontal ? Rot4.East : Rot4.North;
            ThingDef shelfDef = ThingDefOf.Shelf;
            IntVec3 fuelCell = room.Where(cell => Fits(GenAdj.OccupiedRect(cell, shelfFacing, shelfDef.Size)))
                .MaxBy(Depth);
            CellRect fuelFootprint = GenAdj.OccupiedRect(fuelCell, shelfFacing, shelfDef.Size);
            var fuelShelf = (Building_Storage)FortressConstruction.Spawn(shelfDef, fuelCell, shelfFacing, stuff, map);
            FortressStorage.Fill(map, new[] { fuelShelf }, _ => ThingDefOf.Chemfuel);
            occupied.UnionWith(fuelFootprint);

            ThingDef generatorDef = ThingDefOf.ChemfuelPoweredGenerator;
            foreach (IntVec3 cell in room.OrderBy(Depth))
            {
                CellRect footprint = GenAdj.OccupiedRect(cell, Rot4.North, generatorDef.Size);
                if (!Fits(footprint)) continue;
                Thing generator = FortressConstruction.Spawn(generatorDef, cell, Rot4.North, null, map);
                CompRefuelable refuelable = generator.TryGetComp<CompRefuelable>();
                refuelable.Refuel(refuelable.Props.fuelCapacity);
                occupied.UnionWith(footprint);
            }

            // Leave a clear row at the ladder landing before fitting panels to the roof.
            CellRect roof = CellRect.FromLimits(Api.CellOnFloor(lot.Rect.Min, 1, map), Api.CellOnFloor(lot.Rect.Max, 1, map));
            IntVec3 access = Api.CellOnFloor(lot.Landing, 1, map);
            ThingDef solar = ThingDefOf.SolarGenerator;
            int count = lot.Rect.Width >= 8 ? 2 : 1;
            for (int i = 0; i < count; i++)
            {
                if (!CellFinder.TryFindRandomCellInsideWith(roof, cell =>
                {
                    CellRect footprint = GenAdj.OccupiedRect(cell, Rot4.North, solar.size);
                    return footprint.FullyContainedWithin(roof) && !footprint.Contains(access) &&
                        !footprint.ExpandedBy(1).Contains(access) &&
                        footprint.All(covered => covered.GetFirstBuilding(map) == null);
                }, out IntVec3 cell)) break;
                FortressConstruction.Spawn(solar, cell, Rot4.North, null, map);
            }

            int Depth(IntVec3 cell) => (cell.x - entrance.x) * inward.x + (cell.z - entrance.z) * inward.z;
            bool Fits(CellRect footprint) => footprint.FullyContainedWithin(room) &&
                !footprint.Any(occupied.Contains) && footprint.AdjacentCellsCardinal.Any(aisle.Contains);
        }
    }
}
