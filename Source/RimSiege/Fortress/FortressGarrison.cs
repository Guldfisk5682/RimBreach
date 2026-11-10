using System.Collections.Generic;
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
            int size = blueprint.Outer.Width == FortressDimensions.Large.OuterSide
                ? FortressDimensions.Large.KeepSide : FortressDimensions.Compact.KeepSide;
            CellRect keep = CellRect.CenteredOn(blueprint.Outer.CenterCell, size, size).ContractedBy(1);
            BaseGen.globalSettings.map = map;
            BaseGen.symbolStack.Push("lootScatter", new ResolveParams
            {
                rect = keep,
                faction = map.ParentFaction,
                thingSetMakerDef = ThingSetMakerDefOf.MapGen_DefaultStockpile,
                lootMarketValue = value
            });
            BaseGen.Generate();
        }
    }
}
