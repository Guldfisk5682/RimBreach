using System.Collections.Generic;
using System.Linq;
using ProjectZ;
using RimWorld;
using RimWorld.BaseGen;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace RimSiege.Fortress
{
    internal static class FortressGarrison
    {
        internal static IReadOnlyList<Pawn> Spawn(Map map, FortressLayout layout, SitePart part)
        {
            Faction faction = map.ParentFaction;
            CellRect courtyard = layout.Outer.ContractedBy(FortressLayout.CurtainThickness);
            var defense = new LordJob_FortressDefense();
            Lord lord = LordMaker.MakeNewLord(faction, defense, map);
            TraverseParms traversal = TraverseParms.For(TraverseMode.PassDoors);
            BaseGen.globalSettings.map = map;
            BaseGen.symbolStack.Push("pawnGroup", new ResolveParams
            {
                rect = courtyard,
                faction = faction,
                singlePawnLord = lord,
                pawnGroupKindDef = PawnGroupKindDefOf.Settlement,
                singlePawnSpawnCellExtraPredicate = cell => map.reachability.CanReachMapEdge(cell, traversal),
                pawnGroupMakerParams = new PawnGroupMakerParms
                {
                    tile = map.Tile,
                    faction = faction,
                    points = part.parms.threatPoints,
                    inhabitants = true,
                    seed = OutpostSitePartUtility.GetPawnGroupMakerSeed(part.parms)
                }
            });
            BaseGen.Generate();
            defense.Start();
            return lord.ownedPawns;
        }

        internal static void PlaceLoot(Map map, FortressBlueprint blueprint, float value)
        {
            if (value <= 0f) return;
            int size = blueprint.Outer.Width == FortressDimensions.Large.OuterSide
                ? FortressDimensions.Large.KeepSide : FortressDimensions.Compact.KeepSide;
            CellRect keep = CellRect.CenteredOn(blueprint.Outer.CenterCell, size, size).ContractedBy(1);
            Faction faction = map.ParentFaction;
            List<Thing> loot = ThingSetMakerDefOf.MapGen_DefaultStockpile.root.Generate(new ThingSetMakerParams
            {
                countRange = new IntRange(3, 10),
                makingFaction = faction,
                techLevel = faction.def.techLevel,
                totalMarketValueRange = new FloatRange(value, value)
            });

            IntVec3[] cells = keep.OrderByDescending(cell => cell.GetFirstBuilding(map) is Building_Storage)
                .ThenByDescending(cell => cell.Roofed(map))
                .ThenBy(cell => (cell - keep.CenterCell).LengthHorizontalSquared).ToArray();
            foreach (Thing thing in loot)
            {
                if (cells.Any(cell => CanPlace(thing, cell) &&
                    GenPlace.TryPlaceThing(thing, cell, map, ThingPlaceMode.Direct, Forbid))) continue;
                if (GenPlace.TryPlaceThing(thing, keep.CenterCell, map, ThingPlaceMode.Near, Forbid,
                    cell => Api.SameFloor(cell, keep.CenterCell, map) && CanPlace(thing, cell))) continue;
                Log.Warning($"[RimSiege] Could not place fortress victory loot {thing} on tile {map.Tile}.");
            }

            bool CanPlace(Thing thing, IntVec3 cell) =>
                (cell.Standable(map) || cell.GetFirstBuilding(map) is Building_Storage shelf && shelf.Accepts(thing)) &&
                cell.GetItemCount(map) < cell.GetMaxItemsAllowedInCell(map);

            static void Forbid(Thing placed, int count) => placed.TrySetForbidden(value: true);
        }
    }
}
