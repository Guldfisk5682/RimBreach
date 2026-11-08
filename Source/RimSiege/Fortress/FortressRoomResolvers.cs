using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.BaseGen;
using Verse;

namespace RimSiege.Fortress
{
    public sealed class FortressBarracksResolver : SymbolResolver_Interior_Barracks
    {
        internal static ThingDef BedFor(Faction faction) =>
            faction.def.techLevel < TechLevel.Medieval ? ThingDefOf.Bedroll : ThingDefOf.Bed;

        public override void Resolve(ResolveParams rp)
        {
            rp.singleThingDef = BedFor(rp.faction);
            base.Resolve(rp);
        }
    }

    public sealed class FortressDiningResolver : SymbolResolver_Interior_DiningRoom
    {
        public override void Resolve(ResolveParams rp)
        {
            BaseGen.symbolStack.Push("RS_FortressDiningStock", rp);
            base.Resolve(rp);
        }
    }

    public sealed class FortressDiningStockResolver : SymbolResolver
    {
        public override void Resolve(ResolveParams rp)
        {
            Map map = BaseGen.globalSettings.map;
            foreach (Thing meal in rp.rect.SelectMany(cell => cell.GetThingList(map))
                .Where(thing => thing.def == ThingDefOf.MealSimple || thing.def == ThingDefOf.Pemmican).ToList())
                meal.Destroy(DestroyMode.Vanish);

            ThingDef food = rp.faction.def.techLevel >= TechLevel.Industrial
                ? ThingDefOf.MealSurvivalPack : ThingDefOf.Pemmican;
            var access = new HashSet<IntVec3>(rp.rect.Where(cell =>
                BaseGenUtility.AnyDoorAdjacentCardinalTo(cell, map) ||
                cell.x == rp.rect.CenterCell.x || cell.z == rp.rect.CenterCell.z));
            List<Building_Storage> shelves = FortressStorage.PlaceShelves(map, rp.rect, ThingDefOf.WoodLog, access);
            FortressStorage.Fill(map, shelves, _ => food);
            if (shelves.Count == 0 && CellFinder.TryFindRandomCellInsideWith(rp.rect,
                cell => cell.Standable(map) && cell.GetFirstBuilding(map) == null, out IntVec3 stockCell))
                FortressStorage.Stock(map, food, stockCell);
        }
    }

    public sealed class FortressInfirmaryResolver : SymbolResolver_Interior_Barracks
    {
        public override void Resolve(ResolveParams rp)
        {
            rp.singleThingDef = DefDatabase<ThingDef>.GetNamed("HospitalBed");
            base.Resolve(rp);
        }
    }
}
