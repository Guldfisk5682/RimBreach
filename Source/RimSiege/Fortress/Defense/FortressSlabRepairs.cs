using System.Collections.Generic;
using System.Linq;
using ProjectZ;
using RimWorld;
using Verse;

namespace RimSiege.Fortress
{
    [StaticConstructorOnStartup]
    internal sealed class FortressSlabRepairs
    {
        private readonly TerrainDef slab;
        private readonly TerrainDef openAir = DefDatabase<TerrainDef>.GetNamed("PZ_OpenAir");
        private readonly HashSet<IntVec3> deck = new();
        private readonly HashSet<IntVec3> pending = new();

        static FortressSlabRepairs() => Api.FloorChanged += OnFloorChanged;

        internal FortressSlabRepairs(Map map, FortressBlueprint blueprint)
        {
            slab = blueprint.Slab;
            FortressLayout layout = blueprint.Rebuild(map);
            if (layout == null) return;
            foreach (FortressTier tier in layout.Tiers.Skip(1))
                deck.UnionWith(tier.Deck.Where(cell => !tier.Openings.Contains(cell)));
            pending.UnionWith(deck.Where(cell => cell.GetTerrain(map) == openAir));
        }

        internal bool HasPending => pending.Count != 0;

        internal bool FloorChanged(IntVec3 cell, bool built) =>
            built ? pending.Remove(cell) : deck.Contains(cell) && pending.Add(cell);

        internal void PlacePending(Map map, Faction faction)
        {
            // Work from lower floors upward. ProjectZ checks which plans are supported.
            foreach (IntVec3 cell in pending.OrderBy(cell => Api.FloorOf(cell, map)).ToArray())
            {
                if (cell.GetTerrain(map) != openAir)
                {
                    pending.Remove(cell);
                    continue;
                }
                if (cell.GetThingList(map).Any(thing => thing.def.entityDefToBuild == slab) ||
                    !GenConstruct.CanPlaceBlueprintAt_NewTemp(slab, cell, Rot4.North, map, ignoreEdgeArea: true).Accepted)
                    continue;
                GenConstruct.PlaceBlueprintForBuild(slab, cell, map, Rot4.North, faction, null);
            }
        }

        private static void OnFloorChanged(Map map, IntVec3 cell, bool built)
        {
            if (map.Parent is not FortressSite { Blueprint: { } blueprint, Resolved: false } ||
                !blueprint.ContainsGround(Api.GroundCellOf(cell, map))) return;
            foreach (var lord in map.lordManager.lords)
                if (lord.CurLordToil is LordToil_FortressDefense && lord.LordJob is LordJob_FortressDefense defense)
                    defense.NotifySlabChanged(cell, built);
        }
    }
}
