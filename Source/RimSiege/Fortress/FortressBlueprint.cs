using System.Collections.Generic;
using System.Linq;
using ProjectZ;
using Verse;

namespace RimSiege.Fortress
{
    // Positions in this recipe are ground cells. Layout.Plan converts each
    // structure to the floor bands of the newly generated map.
    internal sealed class FortressBlueprint : IExposable
    {
        internal FortressLayoutKind LayoutKind = FortressLayoutKind.SquareKeep;
        internal FortressSize Size;
        internal CellRect Outer;
        internal Rot4 GateSide;
        internal bool FourGates;
        internal int Top;
        internal int FurnishingSeed;
        internal ThingDef Stuff;
        internal TerrainDef Slab;
        // Older and uniform-deck sites have no secondary slab material.
        internal TerrainDef UnarmoredSlab;
        internal List<IntVec3> NaturalBarrier;
        internal FortressFacilityPlan Facilities;
        internal List<FortressStock> Stock;
        internal List<FortressDefensePiece> Defenses;
        internal IntVec3 LootShelfGround = IntVec3.Invalid;
        internal int LootShelfFloor;
        internal bool HarassmentLootPlaced;
        internal bool MechEnergyLootRolled;

        internal bool IsLarge => Size == FortressSize.Large;

        internal bool ContainsGround(IntVec3 cell) => LayoutKind == FortressLayoutKind.SquareKeep
            ? Outer.Contains(cell)
            : FortressGeometry.ShapeFor(LayoutKind, Size).Footprint.Contains(cell - Outer.CenterCell);

        internal bool ContainsStructure(IntVec3 cell, Map map)
        {
            IntVec3 ground = Api.GroundCellOf(cell, map);
            if (ContainsGround(ground)) return true;
            if (LayoutKind != FortressLayoutKind.MechCircularBunker || Defenses == null) return false;
            int floor = Api.FloorOf(cell, map);
            return Defenses.Any(piece => piece.Floor == floor &&
                (piece.Ground == ground || piece.Def != null &&
                    GenAdj.OccupiedRect(piece.Ground, piece.Rotation, piece.Def.Size).Contains(ground)));
        }

        internal FortressLayout Rebuild(Map map) => FortressLayout.Plan(map, Outer, GateSide,
            new HashSet<IntVec3>(NaturalBarrier ?? new List<IntVec3>()),
            Size, LayoutKind, Top, Facilities, FourGates);

        internal TerrainDef SlabAt(FortressTier tier, IntVec3 cell) =>
            UnarmoredSlab == null || tier.ArmoredDeck.Contains(cell) ? Slab : UnarmoredSlab;

        public void ExposeData()
        {
            Scribe_Values.Look(ref LayoutKind, "layoutKind", FortressLayoutKind.SquareKeep);
            bool hasSize = Size == FortressSize.Large;
            Scribe_Values.Look(ref Outer, "outer");
            Scribe_Values.Look(ref hasSize, "large",
                Outer.Width == FortressGeometry.OuterSideFor(LayoutKind, FortressSize.Large));
            Size = hasSize ? FortressSize.Large : FortressSize.Compact;
            Scribe_Values.Look(ref GateSide, "gateSide");
            Scribe_Values.Look(ref FourGates, "fourGates");
            Scribe_Values.Look(ref Top, "top");
            Scribe_Values.Look(ref FurnishingSeed, "furnishingSeed");
            Scribe_Defs.Look(ref Stuff, "stuff");
            Scribe_Defs.Look(ref Slab, "slab");
            Scribe_Defs.Look(ref UnarmoredSlab, "unarmoredSlab");
            Scribe_Collections.Look(ref NaturalBarrier, "naturalBarrier", LookMode.Value);
            Scribe_Collections.Look(ref Stock, "stock", LookMode.Deep);
            Scribe_Collections.Look(ref Defenses, "defenses", LookMode.Deep);
            Scribe_Values.Look(ref LootShelfGround, "lootShelfGround", IntVec3.Invalid);
            Scribe_Values.Look(ref LootShelfFloor, "lootShelfFloor");
            Scribe_Values.Look(ref HarassmentLootPlaced, "harassmentLootPlaced");
            Scribe_Values.Look(ref MechEnergyLootRolled, "mechEnergyLootRolled");

            FortressLot living = LookLot(Facilities.Living, "living");
            FortressLot supply = LookLot(Facilities.Supply, "supply");
            FortressLot power = LookLot(Facilities.Power, "power");
            FortressLot dining = LookLot(Facilities.Dining, "dining");
            FortressLot infirmary = LookLot(Facilities.Infirmary, "infirmary");
            FortressLot auxBarracks = LookLot(Facilities.AuxBarracks, "auxBarracks");
            FortressLot auxSupply = LookLot(Facilities.AuxSupply, "auxSupply");
            CellRect mortar = Facilities.Mortar;
            CellRect secondMortar = Facilities.SecondMortar;
            ThingDef mortarDef = Facilities.MortarDef;
            ThingDef secondMortarDef = Facilities.SecondMortarDef;
            bool industrial = Facilities.Industrial;
            Scribe_Values.Look(ref mortar, "mortar", CellRect.Empty);
            Scribe_Values.Look(ref secondMortar, "secondMortar", CellRect.Empty);
            Scribe_Defs.Look(ref mortarDef, "mortarDef");
            Scribe_Defs.Look(ref secondMortarDef, "secondMortarDef");
            Scribe_Values.Look(ref industrial, "industrial");
            Facilities = new FortressFacilityPlan(living, supply, power, mortar, industrial,
                mortarDef, secondMortar, secondMortarDef, dining, infirmary, auxBarracks, auxSupply);
        }

        private static FortressLot LookLot(FortressLot lot, string label)
        {
            CellRect rect = lot.Rect;
            Rot4 facing = lot.Facing;
            Scribe_Values.Look(ref rect, label, CellRect.Empty);
            Scribe_Values.Look(ref facing, label + "Facing", Rot4.Invalid);
            return new FortressLot(rect, facing);
        }
    }
}
