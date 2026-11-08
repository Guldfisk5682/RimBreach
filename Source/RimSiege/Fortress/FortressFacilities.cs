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
        internal bool IsEmpty => Rect.IsEmpty;
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
        ThingDef MortarDef, CellRect SecondMortar, ThingDef SecondMortarDef)
    {
        private const int LayoutAttempts = 6;
        private const float MortarPoints = 2000f;
        private const float StandardMortarPoints = 3500f;
        private const float SecondMortarPoints = 6000f;

        internal static bool TryPlace(Map map, FortressLayout layout, out FortressFacilityPlan result)
        {
            bool industrial = map.ParentFaction.def.techLevel >= TechLevel.Industrial;
            bool powerRequired = industrial && map.ParentFaction.def.humanlikeFaction;
            CellRect courtyard = layout.Outer.ContractedBy(FortressLayout.CurtainThickness + 1);
            var reserved = new HashSet<IntVec3>(layout.Modules.SelectMany(module => module.Ground.Cells));
            reserved.UnionWith(CellRect.FromLimits(layout.Gate, layout.Outer.CenterCell).ExpandedBy(1));
            foreach (StairGeometry stair in layout.Tiers[0].Stairs)
                reserved.UnionWith(stair.footprint.ExpandedBy(1));
            foreach (IntVec3 cell in courtyard)
                if (!FortressSiting.UpperSpaceFree(Api.CellOnFloor(cell, 1, map), map)) reserved.Add(cell);

            for (int attempt = 0; attempt < LayoutAttempts; attempt++)
            {
                var used = new HashSet<IntVec3>(reserved);
                FortressLot power = new(CellRect.Empty, Rot4.Invalid);
                if (powerRequired && !ReserveRoom(8, 7, used, out power)) continue;
                if (!ReserveRoom(8, 6, used, out FortressLot living) ||
                    !ReserveRoom(7, 6, used, out FortressLot supply)) continue;

                CellRect mortar = CellRect.Empty;
                CellRect secondMortar = CellRect.Empty;
                ThingDef mortarDef = null;
                ThingDef secondMortarDef = null;
                if (powerRequired)
                {
                    bool FitsMortar(CellRect rect) => Fits(rect, used) && rect.CenterCell.InHorDistOf(supply.Rect.CenterCell, 26f);
                    float points = (map.Parent as FortressSite)?.DefenderPoints ?? StandardMortarPoints;
                    if (points >= MortarPoints &&
                        courtyard.TryFindRandomInnerRect(new IntVec2(6, 6), out mortar, FitsMortar))
                    {
                        mortarDef = points >= StandardMortarPoints ? ThingDefOf.Turret_Mortar :
                            DefDatabase<ThingDef>.GetNamed("CE_Turret_PortableMortar");
                        used.UnionWith(mortar);
                        if (points >= SecondMortarPoints &&
                            courtyard.TryFindRandomInnerRect(new IntVec2(6, 6), out secondMortar, FitsMortar))
                            secondMortarDef = DefDatabase<ThingDef>.GetNamed("CE_Turret_PortableMortar");
                    }
                }
                result = new FortressFacilityPlan(living, supply, power, mortar, industrial, mortarDef,
                    secondMortar, secondMortarDef);
                return true;
            }

            result = default;
            return false;

            bool ReserveRoom(int preferred, int minimum, HashSet<IntVec3> used, out FortressLot lot)
            {
                bool FitsRoom(CellRect rect) => Fits(rect, used) && Facing(rect, used).IsValid;
                bool found = courtyard.TryFindRandomInnerRect(new IntVec2(preferred, preferred), out CellRect rect, FitsRoom) ||
                    courtyard.TryFindRandomInnerRect(new IntVec2(minimum, minimum), out rect, FitsRoom);
                lot = found ? new FortressLot(rect, Facing(rect, used)) : default;
                if (found)
                {
                    used.UnionWith(rect);
                    used.Add(lot.Foot);
                }
                return found;
            }

            bool Fits(CellRect rect, HashSet<IntVec3> used) => rect.ExpandedBy(1).All(cell => !used.Contains(cell));

            Rot4 Facing(CellRect rect, HashSet<IntVec3> used)
            {
                foreach (Rot4 side in GenAdj.CardinalDirections.Select(Rot4.FromIntVec3)
                    .OrderBy(side => (rect.GetCenterCellOnEdge(side) - layout.Outer.CenterCell).LengthHorizontalSquared))
                {
                    IntVec3 foot = rect.GetCenterCellOnEdge(side) + side.FacingCell;
                    if (courtyard.Contains(foot) && !used.Contains(foot))
                        return side;
                }
                return Rot4.Invalid;
            }
        }
    }

    internal static class FortressFacilities
    {
        internal static void Furnish(Map map, FortressLayout layout, FactionMaterial material)
        {
            FortressFacilityPlan plan = layout.Facilities;
            Faction faction = map.ParentFaction;
            var shells = new List<Thing>();

            BaseGen.globalSettings.map = map;
            Push("RS_FortressBarracks", plan.Living.Interior(map, 0));
            Push("RS_FortressDining", plan.Living.Interior(map, 1));
            Push(plan.Industrial ? "RS_FortressInfirmary" : "RS_FortressBarracks", plan.Supply.Interior(map, 1));
            AddMortar(plan.Mortar, plan.MortarDef);
            AddMortar(plan.SecondMortar, plan.SecondMortarDef);
            BaseGen.Generate();

            void AddMortar(CellRect yard, ThingDef def)
            {
                if (def == null) return;
                foreach (IntVec3 cell in yard)
                    if (cell.Roofed(map)) map.roofGrid.SetRoof(cell, null);
                BaseGen.symbolStack.Push("mannedMortar", new ResolveParams
                {
                    rect = yard,
                    faction = faction,
                    mortarDef = def,
                    postThingGenerate = thing =>
                    {
                        if (thing.def.category == ThingCategory.Item) shells.Add(thing);
                    }
                });
            }

            ThingDef medicine = ModsConfig.RoyaltyActive && faction.def == FactionDefOf.Empire
                ? ThingDefOf.MedicineUltratech : plan.Industrial ? ThingDefOf.MedicineIndustrial : ThingDefOf.MedicineHerbal;
            CellRect storage = plan.Supply.Interior(map, 0);
            IntVec3 entry = plan.Supply.Landing - plan.Supply.Facing.FacingCell;
            var storageAccess = new HashSet<IntVec3>(storage.Where(cell =>
                cell.x == storage.CenterCell.x || cell.z == storage.CenterCell.z)) { entry };
            List<Building_Storage> supply = FortressStorage.PlaceShelves(map, storage, material.Stuff, storageAccess);
            List<Thing> rounds = shells.Where(shell => shell.Spawned).ToList();
            Building_Storage ammoShelf = rounds.Count > 0 && supply.Count > 1 ? supply[^1] : null;
            int componentStacks = layout.Outer.Width == FortressDimensions.Large.OuterSide ? 2 : 1;
            FortressStorage.Fill(map, supply.Where(shelf => shelf != ammoShelf), SupplyItem);
            if (ammoShelf != null)
            {
                foreach (Thing shell in rounds)
                {
                    shell.DeSpawn();
                    GenPlace.TryPlaceThing(shell, ammoShelf.Position, map, ThingPlaceMode.Direct);
                }
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
