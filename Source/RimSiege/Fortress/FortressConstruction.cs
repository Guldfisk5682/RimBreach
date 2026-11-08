using System.Collections.Generic;
using System.Linq;
using ProjectZ;
using RimWorld;
using RimWorld.BaseGen;
using Verse;

namespace RimSiege.Fortress
{
    internal readonly record struct FactionMaterial(ThingDef Stuff, TerrainDef Slab)
    {
        internal static FactionMaterial For(Map map)
        {
            IEnumerable<ThingDef> stone = Find.World.NaturalRockTypesIn(map.Tile)
                .Select(rock => rock.building.mineableThing)
                .Where(chunk => chunk?.butcherProducts != null)
                .SelectMany(chunk => chunk.butcherProducts)
                .Select(product => product.thingDef)
                .Where(stuff => stuff.stuffProps?.CanMake(ThingDefOf.Wall) == true)
                .OrderByDescending(stuff => ThingDefOf.Wall.GetStatValueAbstract(StatDefOf.MaxHitPoints, stuff))
                .ThenBy(stuff => stuff.defName);
            IEnumerable<ThingDef> preferred = map.ParentFaction?.def.techLevel >= TechLevel.Spacer
                ? new[] { ThingDefOf.Uranium, ThingDefOf.Plasteel }.Concat(stone) : stone;

            foreach (ThingDef stuff in preferred.Append(ThingDefOf.BlocksGranite))
            {
                if (Api.TryGetFloorFor(stuff, out TerrainDef slab)) return new FactionMaterial(stuff, slab);
            }
            return default;
        }
    }

    internal static class FortressConstruction
    {
        internal static bool Build(Map map, FortressLayout layout, FactionMaterial material, bool reinforced)
        {
            PrepareGround(map, layout);
            BaseGen.globalSettings.map = map;
            foreach (FortressRoomFloor room in layout.Tiers[0].Interiors)
                BaseGen.symbolStack.Push("clear", room.Cells);
            BaseGen.Generate();
            ThingDef stairs = DefDatabase<ThingDef>.GetNamed("PZ_Stairs");
            TerrainDef finish = BaseGenUtility.CorrespondingTerrainDef(material.Stuff, beautiful: false, map.ParentFaction);
            if (!finish.layerable) finish = TerrainDefOf.Concrete;
            TerrainDef sterile = DefDatabase<TerrainDef>.GetNamed("SterileTile");
            var refog = new HashSet<IntVec3>();
            ThingDef wall = reinforced ? DefDatabase<ThingDef>.GetNamed("CE_ReinforcedWall") : ThingDefOf.Wall;
            ThingDef door = reinforced ? DefDatabase<ThingDef>.GetNamed("CE_ReinforcedDoor") : ThingDefOf.Door;
            ThingDef embrasure = reinforced ? DefDatabase<ThingDef>.GetNamed("CE_ReinforcedEmbrasure") : null;

            try
            {
                for (int level = 0; level < layout.Tiers.Length; level++)
                {
                    FortressTier tier = layout.Tiers[level];
                    foreach (IntVec3 cell in tier.Deck)
                    {
                        if (tier.Openings.Contains(cell) || cell.GetTerrain(map) == material.Slab) continue;
                        if (!CanPlace(material.Slab, cell, Rot4.North, null, map, refog)) return false;
                        map.terrainGrid.SetTerrain(cell, material.Slab);
                    }

                    foreach (FortressBuilding part in tier.Buildings)
                    {
                        ThingDef def = part.Def == ThingDefOf.Wall ? wall : part.Def == ThingDefOf.Door ? door :
                            embrasure != null && part.Def.defName == "CE_Embrasure" ? embrasure : part.Def;
                        ThingDef stuff = def.MadeFromStuff ? material.Stuff : null;
                        Spawn(def, part.Cell, part.Rotation, stuff, map);
                    }

                    foreach (FortressRoomFloor room in tier.Interiors)
                    {
                        foreach (IntVec3 cell in room.Cells)
                        {
                            if (tier.Openings.Contains(cell)) continue;
                            map.terrainGrid.SetTerrain(cell, room.Sterile ? sterile : finish);
                            if (room.Roofed) map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                        }
                    }

                    foreach (StairGeometry stair in tier.Stairs)
                    {
                        RevealForPlacement(stair.exit, map, refog);
                        if (!CanPlace(stairs, stair.center, stair.rot, material.Stuff, map, refog)) return false;
                        Spawn(stairs, stair.center, stair.rot, material.Stuff, map);
                    }
                }

                ThingDef ladderDef = Raids.SiegeDefOf.RS_AssaultLadder;
                foreach (FortressLadder ladder in layout.Ladders)
                {
                    map.roofGrid.SetRoof(ladder.Foot, null);
                    RevealForPlacement(ladder.Landing, map, refog);
                    if (!CanPlace(ladderDef, ladder.Foot, ladder.Rotation, null, map, refog)) return false;
                    Spawn(ladderDef, ladder.Foot, ladder.Rotation, null, map);
                }
            }
            finally
            {
                foreach (IntVec3 cell in refog)
                    map.fogGrid.Refog(CellRect.SingleCell(cell));
                Api.RequestFogRefresh(map);
            }

            FortressFacilities.Furnish(map, layout, material);
            FortressStronghold.Furnish(map, layout, material.Stuff);
            if (!layout.Facilities.Power.IsEmpty) FortressPower.Connect(map, layout);

            CellRect ground = Api.GroundRect(map);
            Log.Message($"[RimSiege] Fortress generated on tile {map.Tile}: {layout.Outer}, gate {layout.Gate}, " +
                $"ground {ground.Width}x{ground.Height}, {layout.Tiers.Length} levels, {layout.Ladders.Count} ladders, " +
                $"{material.Stuff.defName}, reinforced={reinforced}, {layout.NaturalBarrier.Count} natural wall cells.");
            return true;
        }

        private static void PrepareGround(Map map, FortressLayout layout)
        {
            foreach (IntVec3 cell in layout.Outer)
            {
                if (layout.NaturalBarrier.Contains(cell)) continue;

                Building edifice = cell.GetEdifice(map);
                edifice?.Destroy(DestroyMode.Vanish);
                if (!FortressSiting.HasHeavyGround(cell, map))
                    map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
            }
        }

        private static bool CanPlace(BuildableDef def, IntVec3 cell, Rot4 rotation, ThingDef stuff, Map map,
            HashSet<IntVec3> refog)
        {
            RevealForPlacement(cell, map, refog);
            AcceptanceReport report = GenConstruct.CanPlaceBlueprintAt_NewTemp(def, cell, rotation, map,
                stuffDef: stuff, ignoreEdgeArea: true);
            if (report.Accepted) return true;

            Log.Error($"[RimSiege] Fortress placement failed on tile {map.Tile}: {def.defName} at {cell}: {report.Reason}");
            return false;
        }

        private static void RevealForPlacement(IntVec3 cell, Map map, HashSet<IntVec3> refog)
        {
            // GenStep_Fog already ran, but blueprint validation also checks visibility.
            if (!cell.Fogged(map)) return;
            refog.Add(cell);
            map.fogGrid.Unfog(cell);
        }

        internal static Thing Spawn(ThingDef def, IntVec3 cell, Rot4 rotation, ThingDef stuff, Map map)
        {
            Thing thing = ThingMaker.MakeThing(def, stuff);
            if (def.CanHaveFaction) thing.SetFactionDirect(map.ParentFaction);
            return GenSpawn.Spawn(thing, cell, map, rotation);
        }
    }
}
