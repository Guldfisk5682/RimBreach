using System.Collections.Generic;
using System.Linq;
using ProjectZ;
using RimWorld;
using Verse;

namespace RimSiege.Fortress
{
    public sealed class FortressLootDef : Def
    {
        public ThingDef thing;
        public int count = 1;
        public float weight = 12f;
        public TechLevel minTech;
        public TechLevel maxTech = TechLevel.Archotech;
        public float minPoints;
        public bool largeOnly;

        internal bool AppliesTo(TechLevel tech, bool large, float points) =>
            tech >= minTech && tech <= maxTech && points >= minPoints && (!largeOnly || large);

        internal Thing Create()
        {
            Thing reward = ThingMaker.MakeThing(thing);
            reward.stackCount = count;
            return reward;
        }
    }

    internal static class FortressLoot
    {
        internal static void PlaceHarassmentLoot(Map map, FortressSite site, IntVec3 lootShelf)
        {
            FortressBlueprint blueprint = site.Blueprint;
            if (!site.Harassment || site.Resolved || blueprint.HarassmentLootPlaced) return;

            FortressDimensions dimensions = Dimensions(blueprint.Outer);
            TechLevel tech = map.ParentFaction.def.techLevel;
            if (!DefDatabase<FortressLootDef>.AllDefsListForReading
                .Where(def => def.AppliesTo(tech, dimensions == FortressDimensions.Large, site.OriginalPoints))
                .TryRandomElementByWeight(def => def.weight, out FortressLootDef reward)) return;

            Thing item = reward.Create();
            IntVec3 anchor = lootShelf.IsValid ? lootShelf : blueprint.Outer.CenterCell;
            blueprint.HarassmentLootPlaced = PlaceOnLootShelf(map, lootShelf, item) ||
                GenPlace.TryPlaceThing(item, anchor, map, ThingPlaceMode.Near, Forbid,
                    cell => Api.SameFloor(cell, anchor, map) && cell.GetFirstBuilding(map) == null &&
                        CanPlaceAt(map, item, cell));
            if (!blueprint.HarassmentLootPlaced) WarnUnplaced(map, item);
        }

        internal static void PlaceVictoryLoot(FortressSite site)
        {
            if (site?.Map == null || site.Blueprint == null) return;
            Map map = site.Map;
            Faction faction = map.ParentFaction;
            TechLevel tech = faction.def.techLevel;
            FortressBlueprint blueprint = site.Blueprint;
            CellRect keep = FortressLayout.KeepFootprint(blueprint.Outer, Dimensions(blueprint.Outer).KeepSide).ContractedBy(1);
            IntVec3 lootShelf = blueprint.LootShelfGround.IsValid
                ? Api.CellOnFloor(blueprint.LootShelfGround, blueprint.LootShelfFloor, map)
                : IntVec3.Invalid;

            float value = site.parts?.FirstOrDefault()?.parms.lootMarketValue ?? 0f;
            if (value <= 0f) return;
            List<Thing> loot = ThingSetMakerDefOf.MapGen_DefaultStockpile.root.Generate(new ThingSetMakerParams
            {
                countRange = new IntRange(3, 10),
                makingFaction = faction,
                techLevel = tech,
                totalMarketValueRange = new FloatRange(value, value)
            });
            IntVec3[] cells = keep.Where(cell => cell != lootShelf)
                .OrderByDescending(cell => cell.GetFirstBuilding(map) is Building_Storage)
                .ThenByDescending(cell => cell.Roofed(map))
                .ThenBy(cell => (cell - keep.CenterCell).LengthHorizontalSquared).ToArray();
            foreach (Thing thing in loot)
            {
                if (cells.Any(cell => CanPlaceAt(map, thing, cell) &&
                    GenPlace.TryPlaceThing(thing, cell, map, ThingPlaceMode.Direct, Forbid))) continue;
                if (GenPlace.TryPlaceThing(thing, keep.CenterCell, map, ThingPlaceMode.Near, Forbid,
                    cell => cell != lootShelf && Api.SameFloor(cell, keep.CenterCell, map) &&
                        CanPlaceAt(map, thing, cell))) continue;
                WarnUnplaced(map, thing);
            }
        }

        private static FortressDimensions Dimensions(CellRect outer) =>
            outer.Width == FortressDimensions.Large.OuterSide ? FortressDimensions.Large : FortressDimensions.Compact;

        private static bool PlaceOnLootShelf(Map map, IntVec3 cell, Thing thing)
        {
            if (!cell.IsValid || cell.GetFirstBuilding(map) is not Building_Storage shelf ||
                cell.GetItemCount(map) >= cell.GetMaxItemsAllowedInCell(map)) return false;
            shelf.GetStoreSettings().filter.SetAllow(thing.def, true);
            return shelf.Accepts(thing) && GenPlace.TryPlaceThing(thing, cell, map, ThingPlaceMode.Direct, Forbid);
        }

        private static bool CanPlaceAt(Map map, Thing thing, IntVec3 cell) =>
            cell.GetItemCount(map) < cell.GetMaxItemsAllowedInCell(map) &&
            (cell.GetFirstBuilding(map) is Building_Storage shelf ? shelf.Accepts(thing) : cell.Standable(map));

        private static void WarnUnplaced(Map map, Thing thing) =>
            Log.Warning($"[RimSiege] Could not place fortress loot {thing} on tile {map.Tile}.");

        private static void Forbid(Thing placed, int count) => placed.TrySetForbidden(value: true);
    }
}
