using System.Collections.Generic;
using Verse;

namespace RimSiege.Fortress
{
    // Positions in this recipe are ground cells. Layout.Plan converts each
    // structure to the floor bands of the newly generated map.
    internal sealed class FortressBlueprint : IExposable
    {
        internal CellRect Outer;
        internal Rot4 GateSide;
        internal int Top;
        internal int FurnishingSeed;
        internal ThingDef Stuff;
        internal TerrainDef Slab;
        internal List<IntVec3> NaturalBarrier;
        internal FortressFacilityPlan Facilities;
        internal List<FortressStock> Stock;

        internal FortressLayout Rebuild(Map map) => FortressLayout.Plan(map, Outer, GateSide,
            new HashSet<IntVec3>(NaturalBarrier),
            Outer.Width == FortressDimensions.Large.OuterSide ? FortressDimensions.Large : FortressDimensions.Compact,
            Top, Facilities);

        public void ExposeData()
        {
            Scribe_Values.Look(ref Outer, "outer");
            Scribe_Values.Look(ref GateSide, "gateSide");
            Scribe_Values.Look(ref Top, "top");
            Scribe_Values.Look(ref FurnishingSeed, "furnishingSeed");
            Scribe_Defs.Look(ref Stuff, "stuff");
            Scribe_Defs.Look(ref Slab, "slab");
            Scribe_Collections.Look(ref NaturalBarrier, "naturalBarrier", LookMode.Value);
            Scribe_Collections.Look(ref Stock, "stock", LookMode.Deep);

            FortressLot living = LookLot(Facilities.Living, "living");
            FortressLot supply = LookLot(Facilities.Supply, "supply");
            FortressLot power = LookLot(Facilities.Power, "power");
            CellRect mortar = Facilities.Mortar;
            CellRect secondMortar = Facilities.SecondMortar;
            ThingDef mortarDef = Facilities.MortarDef;
            ThingDef secondMortarDef = Facilities.SecondMortarDef;
            bool industrial = Facilities.Industrial;
            Scribe_Values.Look(ref mortar, "mortar");
            Scribe_Values.Look(ref secondMortar, "secondMortar");
            Scribe_Defs.Look(ref mortarDef, "mortarDef");
            Scribe_Defs.Look(ref secondMortarDef, "secondMortarDef");
            Scribe_Values.Look(ref industrial, "industrial");
            Facilities = new FortressFacilityPlan(living, supply, power, mortar, industrial,
                mortarDef, secondMortar, secondMortarDef);
        }

        private static FortressLot LookLot(FortressLot lot, string label)
        {
            CellRect rect = lot.Rect;
            Rot4 facing = lot.Facing;
            Scribe_Values.Look(ref rect, label);
            Scribe_Values.Look(ref facing, label + "Facing");
            return new FortressLot(rect, facing);
        }
    }
}
