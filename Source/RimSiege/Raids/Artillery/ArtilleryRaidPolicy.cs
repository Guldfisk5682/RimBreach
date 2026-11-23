using System.Collections.Generic;
using System.Linq;
using RimSiege.Integration;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimSiege.Raids.Artillery
{
    internal static class ArtilleryRaidPolicy
    {
        private const int CampCenterAttempts = 200;

        internal static ArtilleryRaid Prepare(IncidentParms parms, float raidPoints, float pawnPoints,
            Faction targetFaction = null, CellRect? objectiveArea = null, bool forceSupport = false,
            ArtilleryGunPoolDef gunPool = null)
        {
            if (!SiegeRaidMap.Enabled || (!forceSupport && RimSiegeMod.Settings.ArtilleryChance == 0) ||
                parms.target is not Map map || parms.faction == null ||
                !parms.faction.HostileTo(targetFaction ?? Faction.OfPlayer))
                return null;

            bool grouped = IsGrouped(parms.raidArrivalMode);
            if ((!grouped && parms.raidArrivalMode != PawnsArrivalModeDefOf.EdgeWalkIn &&
                 parms.raidArrivalMode != PawnsArrivalModeDefOf.EdgeDrop) || (!grouped && !parms.spawnCenter.IsValid) ||
                parms.attackTargets?.Count > 0 || parms.controllerPawn?.Faction == Faction.OfPlayer ||
                // Explicit grouping may omit pawns: vanilla puts unlabelled
                // members in group -1. Decline before reducing the pawn budget.
                (parms.pawnGroups?.Count ?? 0) > 0) return null;

            RaidStrategyWorker worker = parms.raidStrategy.Worker;
            // This worker spawns a mech cluster and never creates a normal raid lord.
            // It inherits Siege, so exclude it before classifying ordinary strategies.
            if (worker is RaidStrategyWorker_SiegeMechanoid) return null;

            string kind;
            if (parms.faction == Faction.OfMechanoids)
            {
                if (!ModsConfig.BiotechActive) return null;
                kind = "Mechanoid";
            }
            else
            {
                if (!parms.faction.def.humanlikeFaction || parms.faction.def.techLevel < TechLevel.Industrial) return null;
                kind = worker is RaidStrategyWorker_ImmediateAttackBreaching ? "Breaching"
                    : worker is RaidStrategyWorker_Siege ? "Siege" : "Ordinary";
            }
            return BuildRaid(parms, raidPoints, pawnPoints, map, kind, targetFaction, objectiveArea, forceSupport, gunPool);
        }

        private static ArtilleryRaid BuildRaid(IncidentParms parms, float raidPoints, float pawnPoints, Map map,
            string kind, Faction targetFaction, CellRect? objectiveArea, bool forceSupport, ArtilleryGunPoolDef gunPool)
        {
            ArtilleryProfileDef profile = DefDatabase<ArtilleryProfileDef>.AllDefsListForReading
                .FirstOrDefault(p => p.kind == kind);
            if (profile == null || (!forceSupport && (profile.BelowGate(raidPoints) ||
                !Rand.Chance(RimSiegeMod.Settings.ArtilleryChance / 10f)))) return null;

            float personnel = profile.PersonnelPoints(raidPoints, pawnPoints);
            bool breaching = parms.raidStrategy.Worker is RaidStrategyWorker_ImmediateAttackBreaching;
            List<ArtilleryPlacement> guns = profile.Configure(raidPoints, personnel, parms.faction, out _, breaching, gunPool);
            if (!guns.Any(g => g.IsSupport)) return null;
            int constructoids = 0;
            List<PawnKindDef> escortKinds = new List<PawnKindDef>();
            if (kind == "Mechanoid")
            {
                constructoids = MechBattery.CountFor(guns, IsGrouped(parms.raidArrivalMode));
                personnel = UnityEngine.Mathf.Max(0f, personnel - MechBattery.ConstructoidCombatPower * constructoids);
            }
            else if (RimSiegeMod.Settings?.HumanMechEscorts == true &&
                     raidPoints >= RimSiegeMod.Settings.HumanMechMinPoints)
            {
                TechLevel tech = parms.faction.def.techLevel;
                float escortBudget = personnel * HumanMechEscorts.BudgetFraction(tech);
                bool allowBoss = tech >= TechLevel.Ultra &&
                    raidPoints >= HumanMechEscorts.BossMinimumPoints(RimSiegeMod.Settings.HumanMechMinPoints);
                escortKinds = HumanMechEscorts.Select(escortBudget, breaching, allowBoss, out float spentEscorts);
                personnel = UnityEngine.Mathf.Max(0f, personnel - spentEscorts);
            }
            var raid = new ArtilleryRaid
            {
                Kind = (ArtilleryRaidKind)System.Enum.Parse(typeof(ArtilleryRaidKind), kind),
                Phase = ArtilleryPhase.Planned, RaidPoints = raidPoints, PawnPoints = personnel,
                Faction = parms.faction, TargetFaction = targetFaction, Strategy = parms.raidStrategy, OriginalParms = parms,
                Camp = new CampPlan { Guns = guns }, Rolled = true, Selected = true,
                IndependentCrew = kind != "Siege", ConstructoidCount = constructoids,
                EscortMechKinds = escortKinds,
                ConstructionSpeedMultiplier = profile.ConstructionSpeed(raidPoints)
            };
            if (IsGrouped(parms.raidArrivalMode)) return raid;
            return Plan(raid, map, parms.spawnCenter, guns, objectiveArea: objectiveArea) ? raid : null;
        }

        internal static bool IsGrouped(PawnsArrivalModeDef arrival) =>
            arrival == PawnsArrivalModeDefOf.EdgeWalkInGroups || arrival?.defName == "EdgeDropGroups";

        internal static bool Plan(ArtilleryRaid raid, Map map, IntVec3 entry, List<ArtilleryPlacement> guns,
            int divisions = 1, IReadOnlyList<CampPlan> otherCamps = null, ArtilleryTargets.Catalog catalog = null,
            CellRect? objectiveArea = null)
        {
            IntVec3 goal = objectiveArea?.CenterCell ?? GenAI.RandomRaidDest(entry, map);
            SupportRoute route = SupportRoute.Build(map, entry, goal, targetFaction: raid.TargetFaction);
            if (route == null) return false;
            var siting = route.PlanningTargets(catalog: catalog);
            if (siting.Targets.Count == 0) return false;
            Dictionary<ArtilleryPlacement, float> ranges = null;
            if (guns.Any(g => g.IsDirectSupport))
                ranges = guns.ToDictionary(g => g, g => !g.IsSupport ? 0f : CeArtillerySight.EffectiveRange(map, raid.Faction,
                    new EmplacementSpec(g.Def, g.Stuff, g.Ammo, entry, Rot4.North, null)));
            IEnumerable<IntVec3> centers = Centers(map, entry, siting, guns, ranges,
                System.Math.Max(1, CampCenterAttempts / divisions), objectiveArea);
            if (objectiveArea is { } bounds)
            {
                CellRect clearance = bounds.ExpandedBy(CampPlanner.SiteSurveyRadius);
                centers = centers.Where(cell => !clearance.Contains(cell));
            }
            if (otherCamps != null)
                centers = centers.Where(center => otherCamps.All(other => IsoFloor.Distance3D(center, other.Center, map) >
                    CampSupplies.Radius * 2));
            if (!CampPlanner.TryPlan(map, raid.Faction, entry, goal, guns, centers, siting, ranges, out CampPlan camp,
                fortified: raid.Kind == ArtilleryRaidKind.Siege, divisions: divisions)) return false;
            raid.Camp = camp;
            SupportRoute.Set(raid, route);
            return true;
        }

        private static IEnumerable<IntVec3> Centers(Map map, IntVec3 entry, CampSiting siting,
            List<ArtilleryPlacement> guns, IReadOnlyDictionary<ArtilleryPlacement, float> ranges, int attempts,
            CellRect? objectiveArea)
        {
            var direct = guns.Where(g => g.IsDirectSupport).ToList();
            IntVec3 native = objectiveArea == null ? CampEntrySearch.Find(entry, map) : IntVec3.Invalid;
            if (direct.Count == 0)
            {
                if (objectiveArea == null) yield return native;
                else
                {
                    var ground = IsoFloor.GroundRect(map).ContractedBy(14);
                    CellRect searchArea = CellRect.CenteredOn(entry, 60).ClipInsideRect(ground);
                    if (searchArea.IsEmpty) yield break;
                    var sampled = new HashSet<IntVec3>();
                    for (int i = 0; i < attempts; i++)
                    {
                        IntVec3 cell = searchArea.RandomCell;
                        if (sampled.Add(cell) && !cell.Roofed(map)) yield return cell;
                    }
                }
                yield break;
            }
            IntVec3 searchTarget = siting.BreachAnchor;
            IntVec3 center = IsoFloor.HasFloors(map)
                ? IsoFloor.CellOnFloor(IsoFloor.GroundCellOf(searchTarget, map), IsoFloor.FloorOf(entry, map), map) : searchTarget;
            float range = direct.Min(g => ranges[g]) + UnityEngine.Mathf.Sqrt(2f) * CampPlanner.GunAreaRadius;
            CellRect search = CellRect.CenteredOn(center, UnityEngine.Mathf.CeilToInt(range));
            search.ClipInsideMap(map);
            if (IsoFloor.HasFloors(map))
            {
                search.minZ = System.Math.Max(search.minZ, IsoFloor.FirstRow(map, IsoFloor.FloorOf(entry, map)));
                search.maxZ = System.Math.Min(search.maxZ, IsoFloor.LastRow(map, IsoFloor.FloorOf(entry, map)));
            }
            // Preserve the 200-centre budget. Deployment feasibility and firepower
            // are ranked by CampPlanner; approach distance orders equivalent sites.
            var candidates = new List<IntVec3>();
            if (native.InBounds(map))
            {
                IntVec3 projected = IsoFloor.HasFloors(map)
                    ? IsoFloor.CellOnFloor(IsoFloor.GroundCellOf(native, map), IsoFloor.FloorOf(entry, map), map) : native;
                if (projected.InBounds(map)) candidates.Add(projected);
            }
            float preferred = direct.Min(g => ranges[g]) - CampSiting.RangeMargin;
            bool floors = IsoFloor.HasFloors(map);
            int sourceFloor = floors ? IsoFloor.FloorOf(entry, map) : 0;
            int targetFloor = floors ? IsoFloor.FloorOf(searchTarget, map) : 0;
            float inner = UnityEngine.Mathf.Max(0f, preferred - CampSiting.CenterTolerance);
            float outer = preferred + CampSiting.CenterTolerance;
            if (floors)
            {
                inner = UnityEngine.Mathf.Max(0f, IsoFloor.HorizontalReach(inner, sourceFloor, targetFloor));
                outer = IsoFloor.HorizontalReach(outer, sourceFloor, targetFloor);
            }
            // Spend the same sample budget in the actual engagement band,
            // rather than relying on a few lucky cells in a large square.
            if (outer >= 0f)
                for (int i = 0; i < attempts; i++)
                {
                    var offset = UnityEngine.Vector3.forward.RotatedBy(Rand.Range(0f, 360f)) * Rand.Range(inner, outer);
                    IntVec3 cell = (center.ToVector3() + offset).ToIntVec3();
                    cell.y = 0;
                    if (search.Contains(cell)) candidates.Add(cell);
                }
            var ordered = candidates.Select(cell =>
            {
                float deviation = UnityEngine.Mathf.Abs(siting.Distance(cell, siting.BreachAnchor) - preferred);
                return (Cell: cell, EntryDistance: siting.Distance(entry, cell), Deviation: deviation);
            }).OrderBy(candidate => candidate.EntryDistance).ThenBy(candidate => candidate.Deviation);
            // Keep the closer alternative first across spatial groups as well.
            var groups = new Dictionary<(int, int, int), Queue<(IntVec3 Cell, float EntryDistance, float Deviation)>>();
            var seen = new HashSet<IntVec3>();
            foreach (var candidate in ordered)
            {
                if (!seen.Add(candidate.Cell)) continue;
                IntVec3 ground = IsoFloor.HasFloors(map) ? IsoFloor.GroundCellOf(candidate.Cell, map) : candidate.Cell;
                int floor = IsoFloor.HasFloors(map) ? IsoFloor.FloorOf(candidate.Cell, map) : 0;
                var key = (ground.x / 3, ground.z / 3, floor);
                if (!groups.TryGetValue(key, out var group)) groups[key] = group = new();
                group.Enqueue(candidate);
            }
            var frontier = new PriorityQueue<Queue<(IntVec3 Cell, float EntryDistance, float Deviation)>, (float, float, int)>();
            int order = 0;
            foreach (var group in groups.Values) Enqueue(group);
            while (frontier.Count > 0)
            {
                var group = frontier.Dequeue();
                yield return group.Dequeue().Cell;
                if (group.Count > 0) Enqueue(group);
            }

            void Enqueue(Queue<(IntVec3 Cell, float EntryDistance, float Deviation)> group)
            {
                var next = group.Peek();
                frontier.Enqueue(group, (next.EntryDistance, next.Deviation, order++));
            }
        }
    }
}
