using System;
using System.Collections.Generic;
using System.Linq;
using ProjectZ;
using RimWorld;
using RimWorld.BaseGen;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace RimSiege.Fortress
{
    internal static class FortressGarrison
    {
        internal const float MechCompactMinPoints = 5000f;
        internal const float MechLargeMinPoints = 8000f;

        internal static bool IsMechanoidFaction(Faction faction) =>
            faction != null && (faction == Faction.OfMechanoids || faction.def == FactionDefOf.Mechanoid);

        internal static PawnGroupKindDef MechGroupKind(Faction faction)
        {
            PawnGroupKindDef custom = DefDatabase<PawnGroupKindDef>.GetNamedSilentFail("RS_MechFortressDefense");
            if (custom != null && faction?.def.pawnGroupMakers?.Any(maker => maker.kindDef == custom) == true)
                return custom;
            return PawnGroupKindDefOf.Combat;
        }

        internal static PawnGroupKindDef GroupKindFor(Faction faction) =>
            IsMechanoidFaction(faction) ? MechGroupKind(faction) : PawnGroupKindDefOf.Settlement;

        internal static void MechPlan(Faction faction, float threatPoints, bool isLarge,
            out float combatPoints, out int tunnelers, out int lifters, out int constructoids)
        {
            combatPoints = threatPoints;
            tunnelers = 0;
            lifters = 0;
            constructoids = 0;
            if (!ModsConfig.BiotechActive) return;

            PawnKindDef tunnelerDef = DefDatabase<PawnKindDef>.GetNamedSilentFail("Mech_Tunneler");
            PawnKindDef lifterDef = DefDatabase<PawnKindDef>.GetNamedSilentFail("Mech_Lifter");
            PawnKindDef constructoidDef = DefDatabase<PawnKindDef>.GetNamedSilentFail("Mech_Constructoid");

            PawnGroupKindDef groupKind = MechGroupKind(faction);
            float minCombat = faction?.def.MinPointsToGeneratePawnGroup(groupKind) ?? 0f;
            float floorPoints = Math.Max(minCombat, threatPoints * 0.65f);
            int desiredTunnelers = isLarge ? 3 : 2;
            if (tunnelerDef != null && tunnelerDef.combatPower > 0f)
            {
                while (tunnelers < desiredTunnelers && combatPoints - tunnelerDef.combatPower >= floorPoints)
                {
                    tunnelers++;
                    combatPoints -= tunnelerDef.combatPower;
                }
            }

            // Minimum logistics floor with uncapped scaling at higher threat points.
            int minLogistics = isLarge ? 10 : 5;
            int scaledLogistics = Math.Max(minLogistics, Mathf.FloorToInt(threatPoints / 800f));
            lifters = lifterDef != null ? scaledLogistics : 0;
            constructoids = constructoidDef != null ? scaledLogistics : 0;
        }

        internal static int EstimateEnemiesCount(Site site, SitePartParams parms, bool isLarge)
        {
            Faction faction = site.Faction;
            if (!IsMechanoidFaction(faction))
            {
                return PawnGroupMakerUtility.GeneratePawnKindsExample(new PawnGroupMakerParms
                {
                    tile = site.Tile,
                    faction = faction,
                    groupKind = PawnGroupKindDefOf.Settlement,
                    points = parms.threatPoints,
                    inhabitants = true,
                    seed = OutpostSitePartUtility.GetPawnGroupMakerSeed(parms)
                }).Count();
            }

            MechPlan(faction, parms.threatPoints, isLarge, out float combatPoints,
                out int tunnelers, out int lifters, out int constructoids);
            int combat = PawnGroupMakerUtility.GeneratePawnKindsExample(new PawnGroupMakerParms
            {
                tile = site.Tile,
                faction = faction,
                groupKind = MechGroupKind(faction),
                points = combatPoints,
                inhabitants = true,
                seed = OutpostSitePartUtility.GetPawnGroupMakerSeed(parms)
            }).Count();
            return combat + tunnelers + lifters + constructoids;
        }

        internal static IReadOnlyList<Pawn> Spawn(Map map, FortressLayout layout, SitePart part)
        {
            Faction faction = map.ParentFaction;
            if (IsMechanoidFaction(faction))
                return SpawnMechanoids(map, layout, part, faction);

            FortressGeometry geo = layout.Geometry;
            CellRect courtyard = geo.CourtyardBounds;
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
                singlePawnSpawnCellExtraPredicate = cell =>
                    geo.Interior.Contains(cell) && map.reachability.CanReachMapEdge(cell, traversal),
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

        private static IReadOnlyList<Pawn> SpawnMechanoids(Map map, FortressLayout layout, SitePart part, Faction faction)
        {
            FortressGeometry geo = layout.Geometry;
            var defense = new LordJob_MechFortressDefense();
            Lord lord = LordMaker.MakeNewLord(faction, defense, map);
            TraverseParms traversal = TraverseParms.For(TraverseMode.PassDoors);

            bool isLarge = layout.Size == FortressSize.Large;
            MechPlan(faction, part.parms.threatPoints, isLarge, out float combatPoints,
                out int tunnelers, out int lifters, out int constructoids);

            PawnGroupKindDef groupKind = MechGroupKind(faction);
            BaseGen.globalSettings.map = map;
            BaseGen.symbolStack.Push("pawnGroup", new ResolveParams
            {
                rect = geo.CourtyardBounds,
                faction = faction,
                singlePawnLord = lord,
                pawnGroupKindDef = groupKind,
                singlePawnSpawnCellExtraPredicate = cell =>
                    Api.FloorOf(cell, map) == 0 && geo.Interior.Contains(cell) &&
                    map.reachability.CanReachMapEdge(cell, traversal),
                pawnGroupMakerParams = new PawnGroupMakerParms
                {
                    tile = map.Tile,
                    faction = faction,
                    groupKind = groupKind,
                    points = combatPoints,
                    inhabitants = true,
                    seed = OutpostSitePartUtility.GetPawnGroupMakerSeed(part.parms)
                }
            });
            BaseGen.Generate();

            SpawnFixedMechs(map, geo, lord, faction, "Mech_Tunneler", tunnelers, preferLogistics: false);
            SpawnFixedMechs(map, geo, lord, faction, "Mech_Lifter", lifters, preferLogistics: true);
            SpawnFixedMechs(map, geo, lord, faction, "Mech_Constructoid", constructoids, preferLogistics: true);

            defense.Start();
            return lord.ownedPawns;
        }

        private static void SpawnFixedMechs(Map map, FortressGeometry geo, Lord lord, Faction faction,
            string kindDefName, int count, bool preferLogistics)
        {
            if (count <= 0) return;
            PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(kindDefName);
            if (kind == null) return;

            IReadOnlyCollection<IntVec3> fallback = geo.CoreInterior.Count > 0 ? geo.CoreInterior : geo.Interior;
            List<IntVec3> allLogistics = preferLogistics && geo.LogisticsRooms.Count > 0
                ? geo.LogisticsRooms.SelectMany(room => room).ToList()
                : null;

            for (int i = 0; i < count; i++)
            {
                IntVec3 cell = IntVec3.Invalid;
                if (preferLogistics && geo.LogisticsRooms.Count > 0)
                {
                    HashSet<IntVec3> room = geo.LogisticsRooms[i % geo.LogisticsRooms.Count];
                    cell = FindSpawnCell(map, room);
                    if (!cell.IsValid) cell = FindSpawnCell(map, allLogistics);
                }
                if (!cell.IsValid) cell = FindSpawnCell(map, fallback);
                if (!cell.IsValid) cell = FindSpawnCell(map, geo.Interior);
                if (!cell.IsValid) continue;

                Pawn mech = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, faction,
                    PawnGenerationContext.NonPlayer, tile: map.Tile, forceGenerateNewPawn: true,
                    allowDead: false, allowDowned: false, canGeneratePawnRelations: false,
                    mustBeCapableOfViolence: !preferLogistics, inhabitant: true));
                GenSpawn.Spawn(mech, cell, map, Rot4.Random);
                lord.AddPawn(mech);
            }
        }

        private static IntVec3 FindSpawnCell(Map map, IReadOnlyCollection<IntVec3> cells)
        {
            var standable = cells.Where(cell => cell.InBounds(map) && cell.Standable(map) &&
                cell.GetFirstPawn(map) == null && cell.GetEdifice(map) == null).ToList();
            if (standable.Count == 0)
                standable = cells.Where(cell => cell.InBounds(map) && cell.Standable(map) &&
                    cell.GetFirstPawn(map) == null).ToList();
            return standable.Count > 0 ? standable.RandomElement() : IntVec3.Invalid;
        }
    }
}
