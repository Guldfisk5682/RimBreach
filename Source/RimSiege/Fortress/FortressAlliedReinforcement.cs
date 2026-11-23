using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimSiege.Integration;
using RimSiege.Raids;
using RimSiege.Raids.Artillery;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Fortress
{
    [DefOf]
    internal static class FortressArtilleryDefOf
    {
        public static ArtilleryGunPoolDef RS_MechFortressArtillery = null;

        static FortressArtilleryDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(FortressArtilleryDefOf));
    }

    internal static class FortressAlliedReinforcement
    {
        private const float PointsFactor = 0.9f;
        private const float TribalSupportFactor = 1.5f;
        private const float MechanoidFortressFactor = 1.5f;
        private const float MaximumPoints = 10000f;
        private const float MechanoidFortressMaximumPoints = 14000f;
        private const int PreferredEntryOffset = 24;

        private static float SupportPoints(float defenderPoints, float minimum, bool tribalSupport, bool mechanoidFortress)
        {
            float points = defenderPoints * PointsFactor;
            if (tribalSupport) points *= TribalSupportFactor;
            if (mechanoidFortress) points *= MechanoidFortressFactor;
            float maximum = mechanoidFortress ? MechanoidFortressMaximumPoints : MaximumPoints;
            return Mathf.Min(maximum, Mathf.Max(points, minimum));
        }

        internal static bool TryDispatch(FortressSite site, Map map)
        {
            Faction faction = site.AlliedFaction;
            PawnGroupKindDef group = PawnGroupKindDefOf.Combat;
            bool mechanoidFortress = FortressGarrison.IsMechanoidFaction(site.Faction);
            float points = SupportPoints(site.DefenderPoints, faction.def.MinPointsToGeneratePawnGroup(group),
                SiegeRaidMap.IsTribalHumanlike(faction), mechanoidFortress);
            var parms = new IncidentParms
            {
                target = map, faction = faction, points = points, forced = true,
                generateFightersOnly = true, raidArrivalMode = PawnsArrivalModeDefOf.EdgeWalkIn,
                pawnGroupMakerSeed = Rand.Int, biocodeWeaponsChance = 1f, biocodeApparelChance = 1f
            };
            if (!TryResolveEntry(site, map, parms)) return false;

            parms.raidStrategy = SelectStrategy(map, parms, allowSiege: true);
            ArtilleryRaid artillery = ArtilleryRaidPolicy.Prepare(parms, points, points, site.Faction, site.Blueprint.Outer,
                forceSupport: true, gunPool: mechanoidFortress ? FortressArtilleryDefOf.RS_MechFortressArtillery : null);
            if (artillery == null && parms.raidStrategy.Worker is RaidStrategyWorker_Siege)
                parms.raidStrategy = SelectStrategy(map, parms, allowSiege: false);
            List<Pawn> pawns = Generate(parms, artillery?.PawnPoints ?? points);
            if (pawns.Count == 0 && artillery != null)
            {
                artillery = null;
                if (parms.raidStrategy.Worker is RaidStrategyWorker_Siege)
                    parms.raidStrategy = SelectStrategy(map, parms, allowSiege: false);
                pawns = Generate(parms, points);
            }
            if (pawns.Count == 0 && parms.raidStrategy != RaidStrategyDefOf.ImmediateAttack)
            {
                parms.raidStrategy = RaidStrategyDefOf.ImmediateAttack;
                pawns = Generate(parms, points);
            }
            if (pawns.Count == 0)
            {
                Log.Warning($"[RimSiege] Could not muster fortress support from {faction.Name} at {points:F0} points.");
                return true;
            }

            HumanMechEscorts.InjectUngrouped(artillery, pawns, map);
            foreach (Pawn pawn in pawns) BiocodeLoadout(pawn);
            site.RegisterSupport(pawns);
            parms.raidArrivalMode.Worker.Arrive(pawns, parms);

            SiegeRaidMap siege = map.GetComponent<SiegeRaidMap>();
            if (artillery == null)
            {
                LordMaker.MakeNewLord(faction, new LordJob_FortressAlliedAssault(faction, parms.raidStrategy), map, pawns);
                SiegeParty party = siege.Register(pawns, faction);
                TribalSmokeState.TryInitialize(party, parms, pawns, points, site.Faction, forceSupport: true);
            }
            else
            {
                artillery.RaidSeed = parms.pawnGroupMakerSeed.Value;
                map.GetComponent<ArtilleryMap>().Register(artillery, pawns);
                LordMaker.MakeNewLord(faction, new LordJob_FortressAlliedArtillery(artillery), map, pawns);
                SiegeParty party = siege.Register(pawns, faction, deferKits: true);
                if (party != null) artillery.Id = party.Id;
                ArtilleryDeployment.Begin(artillery);
            }
            Find.LetterStack.ReceiveLetter("RS_FortressAlliedArrived".Translate(),
                "RS_FortressAlliedArrivedText".Translate(faction.Name, site.Faction.Name),
                LetterDefOf.PositiveEvent, new TargetInfo(parms.spawnCenter, map));
            return true;
        }

        private static bool TryResolveEntry(FortressSite site, Map map, IncidentParms parms)
        {
            FortressBlueprint plan = site.Blueprint;
            if (plan == null) return false;

            CellRect ground = IsoFloor.GroundRect(map);
            IntVec3 gate = FortressGeometry.ShapeFor(plan.LayoutKind, plan.Size)
                .GateCell(plan.Outer.CenterCell, plan.GateSide) + plan.GateSide.FacingCell;
            bool Suitable(IntVec3 cell) => ground.Contains(cell) && FortressLayout.Depth(ground, cell) == 0 &&
                cell.Standable(map) && (map.TileInfo.AllowRoofedEdgeWalkIn || !cell.Roofed(map));
            bool CanReachGate(IntVec3 cell) => Suitable(cell) &&
                map.reachability.CanReach(cell, gate, PathEndMode.OnCell, TraverseMode.NoPassClosedDoors, Danger.Deadly);
            bool CanReachAssault(IntVec3 cell) => Suitable(cell) && map.reachability.CanReach(cell, site.AssaultGoal,
                PathEndMode.OnCell, TraverseMode.PassAllDestroyableThings, Danger.Deadly);

            if (ground.Contains(site.PlayerEntry))
            {
                parms.spawnCenter = SameSideEntryCells(ground, site.PlayerEntry, site.AssaultGoal)
                    .FirstOrDefault(cell => CanReachGate(cell) || CanReachAssault(cell), IntVec3.Invalid);
                if (!parms.spawnCenter.IsValid)
                    parms.spawnCenter = ground.EdgeCells.OrderBy(cell => cell.DistanceToSquared(site.PlayerEntry))
                        .FirstOrDefault(cell => CanReachGate(cell) || CanReachAssault(cell), IntVec3.Invalid);
                return parms.spawnCenter.IsValid && parms.raidArrivalMode.Worker.TryResolveRaidSpawnCenter(parms);
            }

            if (parms.raidArrivalMode.Worker.TryResolveRaidSpawnCenter(parms) && CanReachGate(parms.spawnCenter))
                return true;
            if (!CellFinder.TryFindRandomEdgeCellWith(CanReachGate, map, CellFinder.EdgeRoadChance_Hostile, out parms.spawnCenter) &&
                !ground.EdgeCells.Where(CanReachGate).TryRandomElement(out parms.spawnCenter) &&
                !ground.EdgeCells.Where(CanReachAssault).TryRandomElement(out parms.spawnCenter)) return false;
            return parms.raidArrivalMode.Worker.TryResolveRaidSpawnCenter(parms);
        }

        private static IEnumerable<IntVec3> SameSideEntryCells(CellRect ground, IntVec3 playerEntry, IntVec3 goal)
        {
            Rot4 side = ground.GetClosestEdge(playerEntry);
            int AlongEdge(IntVec3 cell) => side.IsHorizontal ? cell.z : cell.x;
            return ground.GetEdgeCells(side)
                .OrderBy(cell => System.Math.Abs(System.Math.Abs(AlongEdge(cell) - AlongEdge(playerEntry)) - PreferredEntryOffset))
                .ThenBy(cell => cell.DistanceToSquared(goal));
        }

        [HarmonyPatch(typeof(Pawn), nameof(Pawn.SpawnSetup))]
        private static class PlayerEntry
        {
            private static void Postfix(Pawn __instance, Map map, bool respawningAfterLoad)
            {
                if (!respawningAfterLoad && map.Parent is FortressSite site)
                    site.NotifyPlayerEntry(__instance, map);
            }
        }

        private static RaidStrategyDef SelectStrategy(Map map, IncidentParms parms, bool allowSiege)
        {
            bool industrial = parms.faction.def.techLevel >= TechLevel.Industrial;
            return DefDatabase<RaidStrategyDef>.AllDefsListForReading
                .Where(def => (def.Worker is RaidStrategyWorker_ImmediateAttack or RaidStrategyWorker_ImmediateAttackSmart
                        or RaidStrategyWorker_ImmediateAttackBreaching ||
                    (!industrial && def.Worker is RaidStrategyWorker_ImmediateAttackSappers) ||
                    (industrial && allowSiege && def.Worker is RaidStrategyWorker_Siege and not RaidStrategyWorker_SiegeMechanoid)) &&
                    def.Worker.CanUseWith(parms, PawnGroupKindDefOf.Combat))
                .TryRandomElementByWeight(def => def.Worker.SelectionWeightForFaction(map, parms.faction, parms.points),
                    out RaidStrategyDef chosen) ? chosen : RaidStrategyDefOf.ImmediateAttack;
        }

        private static List<Pawn> Generate(IncidentParms parms, float points)
        {
            PawnGroupMakerParms group = IncidentParmsUtility.GetDefaultPawnGroupMakerParms(PawnGroupKindDefOf.Combat, parms);
            group.points = points;
            return PawnGroupMakerUtility.TryGetRandomPawnGroupMaker(group, out _)
                ? PawnGroupMakerUtility.GeneratePawns(group, warnOnZeroResults: false).ToList() : new List<Pawn>();
        }

        private static void BiocodeLoadout(Pawn pawn)
        {
            if (pawn.equipment != null)
                foreach (Thing item in pawn.equipment.AllEquipmentListForReading) Biocode(item, pawn);
            if (pawn.apparel != null)
                foreach (Apparel item in pawn.apparel.WornApparel) Biocode(item, pawn);
            if (pawn.inventory != null)
                foreach (Thing item in pawn.inventory.innerContainer)
                    if (item.def.IsWeapon || item is Apparel) Biocode(item, pawn);
        }

        internal static void Biocode(Thing item, Pawn pawn)
        {
            if (pawn.Name != null && item.TryGetComp<CompBiocodable>() is { Biocodable: true } comp &&
                comp.CodedPawn != pawn) comp.CodeFor(pawn);
        }
    }

    public sealed class Alert_FortressAlliedReinforcements : Alert
    {
        private FortressSite site;

        public override AlertReport GetReport()
        {
            site = Find.CurrentMap?.Parent as FortressSite;
            return site?.SupportTicksLeft >= 0 && !WorldRendererUtility.WorldSelected
                ? AlertReport.CulpritIs(site) : AlertReport.Inactive;
        }

        public override string GetLabel() => site == null ? string.Empty :
            "RS_FortressAlliedCountdown".Translate(site.SupportTicksLeft.ToStringTicksToPeriod());

        public override TaggedString GetExplanation() => site == null ? default :
            "RS_FortressAlliedOnWayText".Translate(site.AlliedFaction.Name, site.LabelCap,
                site.SupportTicksLeft.ToStringTicksToPeriod());
    }
}
