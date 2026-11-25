using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using RimSiege.Integration;
using RimSiege.Raids.Artillery;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimSiege.Raids.Encirclement
{
    internal static class EncirclementSiting
    {
        private static readonly Rot4[] Sides = { Rot4.North, Rot4.East, Rot4.South, Rot4.West };
        private const int RearPerSide = 3;
        private const int FrontCenterSamples = 96;
        private const int FrontPlanningBudget = 16;
        private const int FrontPerCandidateBudget = 6;
        private const int FrontGunClearance = 4;
        private static readonly TraverseParms Traversal = TraverseParms.For(TraverseMode.NoPassClosedDoors, Danger.Deadly);
        private static readonly TraverseParms BreachTraversal = TraverseParms.For(TraverseMode.PassAllDestroyableThings, Danger.Deadly);

        internal static bool HasEntry(Map map)
        {
            CellRect ground = IsoFloor.GroundRect(map);
            IntVec3 goal = ColonyGoal(map, ground);
            return ground.EdgeCellsNoCorners.Any(c => c.Standable(map) && !c.Roofed(map) &&
                CanBreachToward(map, c, goal));
        }

        private static IntVec3 ColonyGoal(Map map, CellRect ground) => map.listerBuildings.allBuildingsColonistCombatTargets
            .MinBy(b => (IsoFloor.GroundCellOf(b.Position, map) - ground.CenterCell).LengthHorizontalSquared)?.Position
            ?? map.mapPawns.FreeColonistsSpawned.FirstOrDefault()?.Position ?? ground.CenterCell;

        private static bool CanBreachToward(Map map, IntVec3 start, IntVec3 goal)
        {
            IntVec3 projected = IsoFloor.GroundCellOf(goal, map);
            return map.reachability.CanReach(start, projected, PathEndMode.Touch, BreachTraversal);
        }

        internal static EncirclementCampPlan Plan(QuestPart_Encirclement siege)
        {
            Map map = siege.Map;
            CellRect ground = IsoFloor.GroundRect(map);
            IntVec3 goal = ColonyGoal(map, ground);
            IntVec3 groundGoal = IsoFloor.GroundCellOf(goal, map);
            var threats = SmokeThreatCache.CollectThreats(map, includeColonists: false);
            var exposure = new Dictionary<IntVec3, bool>();
            var clearCells = new Dictionary<IntVec3, bool>();
            var rearCandidates = new List<(EncirclementCampPlan Plan, int Beds, int ExposedCells, int ExposedPath, float Distance)>();
            var fallback = new List<IntVec3>();
            List<ThingDef> devices = EncirclementDevices.Choose(siege);
            ThingDef stairs = DefDatabase<ThingDef>.GetNamedSilentFail("PZ_Stairs");
            bool upper = stairs != null && IsoFloor.HasLevel(map, 1) && ProjectZ.Api.TryGetFloorFor(ThingDefOf.Steel, out _);
            int plannedHumans = EncirclementWaveSpawner.PlannedHumanCount(siege);
            IntVec2 bedSize = DefDatabase<ThingDef>.GetNamed("Bedroll").Size;
            float displacedPoints = siege.BasePoints - EncirclementWaveSpawner.PointsFor(siege, 1) -
                EncirclementWaveSpawner.PointsFor(siege, 2);
            float forwardBudget = Mathf.Max(0f, displacedPoints) * new FloatRange(0.3f, 0.6f).RandomInRange;
            ThingDef[] humanDefenses = new[] { "Turret_KPV", "Turret_AGSThirty" }
                .Select(DefDatabase<ThingDef>.GetNamedSilentFail).Where(d => d != null).ToArray();
            int humanMortarCount = siege.Mechanoid ? 0 : Rand.RangeInclusive(1, 4);
            int humanDefenseCount = siege.Mechanoid || humanDefenses.Length == 0 ? 0 : Rand.RangeInclusive(3, 5);
            ThingDef[] humanDefenseGuns = Enumerable.Range(0, humanDefenseCount)
                .Select(_ => humanDefenses.RandomElement()).ToArray();

            foreach (Rot4 side in Sides)
            {
                IntVec3[] edge = ground.GetEdgeCells(side).Where(Entry).ToArray();
                if (edge.Length == 0) continue;
                IntVec3 fallbackEntry = edge.Where((_, i) => i % 8 == 0)
                    .OrderBy(Exposed).ThenByDescending(c => (c - groundGoal).LengthHorizontalSquared)
                    .FirstOrDefault(c => CanBreachToward(map, c, goal), IntVec3.Invalid);
                if (fallbackEntry.IsValid) fallback.Add(fallbackEntry);
                if (siege.ForceNoCamp) continue;
                var centers = new List<(IntVec3 Cell, int Rank)>();
                for (int i = 0; i < edge.Length; i += 9)
                    foreach (int depth in new[] { 12, 20, 28 })
                    {
                        IntVec3 c = edge[i] - side.FacingCell * depth;
                        if (!Clear(c) || map.areaManager.Home[c]) continue;
                        int rank = new CellRect(c.x - 4, c.z - 4, 9, 9).Count(Clear);
                        if (rank >= 40) centers.Add((c, rank - (Exposed(c) ? 30 : 0)));
                    }
                foreach (var center in centers.OrderByDescending(x => x.Rank)
                    .ThenByDescending(x => (x.Cell - groundGoal).LengthHorizontalSquared).Take(RearPerSide))
                {
                    IntVec3 rear = center.Cell;
                    if (!CanBreachToward(map, rear, goal)) continue;
                    List<(IntVec3 Cell, int Exposure)> entries = Entries(edge, rear);
                    if (entries.Count == 0) continue;

                    var plan = new EncirclementCampPlan
                    {
                        Rear = rear, Goal = goal, Facing = side.Opposite,
                        Entries = entries.Select(e => e.Cell).ToList(),
                        PlannedHumans = plannedHumans, ForwardBudget = forwardBudget
                    };
                    var reserved = new HashSet<IntVec3> { rear };
                    // Reserve the device room before housing fills the best reachable sites.
                    foreach (ThingDef device in devices)
                        if (!TryRoom(plan, reserved, stations: false, device, false, null)) break;
                    if (plan.Rooms.Count == 0) continue;
                    int remainingBeds = plan.BedrollLimit;
                    int maxBarracks = upper ? 10 : 12;
                    for (int i = 0; remainingBeds > 0 && i < maxBarracks; i++)
                    {
                        bool tryUpper = upper && remainingBeds > EncirclementBarracksBuilder.GroundBedLimit;
                        if (!(tryUpper && TryRoom(plan, reserved, stations: true, device: null, true, stairs)) &&
                            !TryRoom(plan, reserved, stations: true, device: null, false, null)) break;
                        EncirclementRoom room = plan.Rooms[^1];
                        remainingBeds -= EncirclementBarracksBuilder.BedSlots(map, room, false, bedSize).Count();
                        if (room.Stairs.IsValid)
                            remainingBeds -= EncirclementBarracksBuilder.BedSlots(map, room, true, bedSize).Count();
                    }
                    int beds = plan.BedrollLimit - Mathf.Max(0, remainingBeds);
                    if (plan.BedrollLimit > 0 && beds == 0) continue;

                    plan.RearBattery = RearBattery(plan, reserved, minimum: false);
                    if (plan.RearBattery.Guns.Count == 0) continue;
                    if (siege.Mechanoid) PlaceFixtures(rear, 14, siege.BasePoints >= 12000f ? 6 : 4);
                    else foreach (EncirclementRoom room in plan.Rooms.Where(r => r.Device == null))
                        PlaceFixtures(room.Footprint.CenterCell, 10, Rand.RangeInclusive(2, 4));

                    void PlaceFixtures(IntVec3 anchor, float radius, int quantity)
                    {
                        ThingDef def = siege.Mechanoid ? ThingDefOf.Gloomlight : ThingDefOf.Campfire;
                        foreach (IntVec3 c in GenRadial.RadialCellsAround(anchor, radius, true).Where(c =>
                            Clear(c) && !reserved.Contains(c) && !plan.Fixtures.Any(f => f.InHorDistOf(c, 3f)) &&
                            NativeConstruction.CanPlaceSiegeProject(def, c, Rot4.North, map, null,
                                install: siege.Mechanoid)).Take(quantity))
                        {
                            plan.Fixtures.Add(c);
                            reserved.Add(c);
                        }
                    }
                    int exposedCells = reserved.Count(Exposed);
                    int exposedPath = entries.Sum(e => e.Exposure);
                    plan.Exposed = siege.ForceExposed || exposedCells > 0 || exposedPath > 0;
                    float distance = map.mapPawns.FreeColonistsSpawned.Select(p =>
                        (IsoFloor.GroundCellOf(p.Position, map) - rear).LengthHorizontalSquared)
                        .DefaultIfEmpty((rear - groundGoal).LengthHorizontalSquared).Min();
                    rearCandidates.Add((plan, beds, exposedCells, exposedPath, distance));
                }
            }

            var viable = rearCandidates.OrderBy(x => x.ExposedCells > 0).ThenBy(x => x.ExposedPath > 0)
                .ThenByDescending(x => x.Beds).ThenBy(x => x.ExposedCells + x.ExposedPath)
                .ThenBy(x => x.Plan.Rooms.Count).ThenByDescending(x => x.Distance)
                .Take(3).ToList();
            if (viable.Count != 0)
            {
                int batteries = siege.BasePoints >= 10000f ? 3 : 2;
                int passes = 0;
                var catalog = new ArtilleryTargets.Catalog(map);
                foreach (var candidate in viable)
                {
                    PlanFront(candidate.Plan, catalog, batteries, ref passes);
                    if (passes >= FrontPlanningBudget || candidate.Plan.Front.Count == batteries &&
                        candidate.Plan.Front.All(c => c.Guns.Count >= 2)) break;
                }
                EncirclementCampPlan chosen = viable.OrderBy(x => x.ExposedCells > 0).ThenBy(x => x.ExposedPath > 0)
                    .ThenByDescending(x => x.Beds)
                    .ThenByDescending(x => x.Plan.Front.Count).ThenByDescending(x => x.Plan.Front.Sum(c => c.Guns.Count))
                    .ThenBy(x => x.ExposedCells + x.ExposedPath).ThenBy(x => x.Plan.Rooms.Count)
                    .ThenByDescending(x => x.Distance).First().Plan;
                chosen.BatteryPlanningPasses = passes;
                return chosen;
            }

            if (fallback.Count == 0 && ground.EdgeCellsNoCorners.FirstOrDefault(c =>
                Entry(c) && CanBreachToward(map, c, goal), IntVec3.Invalid) is { IsValid: true } alternate)
                fallback.Add(alternate);
            if (fallback.Count == 0) return null;
            IntVec3 entry = fallback.OrderBy(Exposed).ThenByDescending(c => (c - groundGoal).LengthHorizontalSquared).First();
            Rot4 facing = Rot4.FromAngleFlat((groundGoal - entry).AngleFlat);
            var quick = new EncirclementCampPlan
            {
                Rear = entry + facing.FacingCell * 5, Goal = goal, Facing = facing, Exposed = true,
                AssaultFallback = true, Entries = new() { entry }, ForwardBudget = 0f
            };
            if (!Clear(quick.Rear)) quick.Rear = entry;
            quick.RearBattery = RearBattery(quick, new HashSet<IntVec3>(), minimum: true);
            return quick;

            bool Clear(IntVec3 c)
            {
                if (!ground.Contains(c)) return false;
                if (!clearCells.TryGetValue(c, out bool clear))
                    clearCells[c] = clear = c.Standable(map) && !c.Roofed(map) && c.GetEdifice(map) == null &&
                        !map.areaManager.Home[c] && c.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy);
                return clear;
            }
            bool Entry(IntVec3 c) => c.Standable(map) && !c.Roofed(map) && !map.areaManager.Home[c];

            bool Exposed(IntVec3 c)
            {
                if (!exposure.TryGetValue(c, out bool exposed))
                    exposure[c] = exposed = SmokeThreatCache.IsExposedToThreats(c, map, threats);
                return exposed;
            }

            List<(IntVec3 Cell, int Exposure)> Entries(IntVec3[] from, IntVec3 rear)
            {
                var paths = new List<(IntVec3 Cell, int Exposure)>();
                var sampled = new List<IntVec3>();
                foreach (IntVec3 entry in from.OrderBy(c => (c - rear).LengthHorizontalSquared))
                {
                    float distance = (entry - rear).LengthHorizontalSquared;
                    if (distance > 60 * 60 || sampled.Count >= 9) break;
                    if (sampled.Any(c => c.InHorDistOf(entry, 9f))) continue;
                    sampled.Add(entry);
                    if (!map.reachability.CanReach(entry, rear, PathEndMode.OnCell, Traversal)) continue;
                    using PawnPath path = map.pathFinder.FindPathNow(entry, rear, Traversal, null, PathEndMode.OnCell);
                    if (!path.Found || path.NodesReversed.Count > 100) continue;
                    paths.Add((entry, path.NodesReversed.Count(Exposed)));
                }
                var result = new List<(IntVec3 Cell, int Exposure)>();
                foreach (var p in paths.OrderBy(p => p.Exposure).ThenBy(p => (p.Cell - rear).LengthHorizontalSquared))
                    if (result.Count < 3 && !result.Any(other => other.Cell.InHorDistOf(p.Cell, 14f)) &&
                        (result.Count == 0 || result.Any(other => other.Cell.InHorDistOf(p.Cell, 28f)))) result.Add(p);
                return result;
            }

            bool TryRoom(EncirclementCampPlan camp, HashSet<IntVec3> occupied, bool stations,
                ThingDef device, bool twoStoreys, ThingDef stairDef)
            {
                var size = stations ? new IntVec2(7, 9) : new IntVec2(5, 5);
                int clearance = stations ? Rand.RangeInclusive(3, 5) : 0;
                var separation = camp.Rooms.Where(r => stations && r.Device == null)
                    .Select(r => r.Footprint.ExpandedBy(clearance)).ToList();
                foreach (IntVec3 c in GenRadial.RadialCellsAround(camp.Rear, stations ? 44f : 20f, true))
                {
                    if (occupied.Contains(c) || !Clear(c)) continue;
                    CellRect rect = CellRect.CenteredOn(c, size);
                    if (separation.Any(bounds => bounds.Overlaps(rect)) ||
                        !rect.All(cell => Clear(cell) && !occupied.Contains(cell)) ||
                        !map.reachability.CanReach(camp.Rear, c, PathEndMode.OnCell, Traversal)) continue;
                    var room = new EncirclementRoom { Footprint = rect, Facing = camp.Facing, Device = device };
                    if (!Clear(room.Door() + camp.Facing.FacingCell) ||
                        stations && !Clear(room.Door(rear: true) - camp.Facing.FacingCell)) continue;
                    if (twoStoreys)
                    {
                        room.Stairs = new IntVec3(rect.minX + 1, 0, rect.CenterCell.z - 1);
                        if (!ProjectZ.Api.TryGetStairGeometry(room.Stairs, Rot4.North, map, out _) ||
                            !NativeConstruction.CanPlaceSiegeProject(stairDef, room.Stairs, Rot4.North, map, ThingDefOf.Steel))
                            continue;
                    }
                    camp.Rooms.Add(room);
                    occupied.UnionWith(rect.ExpandedBy(1));
                    return true;
                }
                return false;
            }

            CampPlan RearBattery(EncirclementCampPlan camp, HashSet<IntVec3> occupied, bool minimum)
            {
                var result = new CampPlan { Center = camp.Rear, Goal = goal, BreachAnchor = goal };
                ThingDef mortar = DefDatabase<ThingDef>.GetNamedSilentFail(siege.Mechanoid ? "Turret_AutoMortar" : "Turret_Mortar");
                int mortars = minimum ? 1 : siege.Mechanoid ? (siege.BasePoints >= 10000f ? 2 : 1) : humanMortarCount;
                for (int i = 0; i < mortars; i++)
                    Place(mortar, ArtilleryGunRole.Support, GenRadial.RadialCellsAround(camp.Rear, minimum ? 12f : 24f, true));
                if (minimum) return result;

                ThingDef[] defenseGuns = humanDefenseGuns;
                if (siege.Mechanoid)
                {
                    ThingDef miniTurret = DefDatabase<ThingDef>.GetNamedSilentFail("Turret_AutoMiniTurret");
                    int guards = Mathf.Clamp(3 + Mathf.FloorToInt((siege.BasePoints - 8000f) / 2000f), 3, 5);
                    defenseGuns = Enumerable.Repeat(miniTurret, guards).ToArray();
                }

                var forward = new Vector2(groundGoal.x - camp.Rear.x, groundGoal.z - camp.Rear.z).normalized;
                if (forward == Vector2.zero)
                    forward = new Vector2(camp.Facing.FacingCell.x, camp.Facing.FacingCell.z);
                var lateral = new Vector2(-forward.y, forward.x);
                Vector2 Offset(IntVec3 c) => new(c.x - camp.Rear.x, c.z - camp.Rear.z);
                float frontEdge = camp.Rooms.SelectMany(r => r.Footprint.Corners)
                    .Max(c => Vector2.Dot(Offset(c), forward)) + 3f;
                float radius = Mathf.Clamp(frontEdge + 24f, 35f, 75f);
                var perimeter = GenRadial.RadialCellsAround(camp.Rear, radius, true)
                    .Where(c => !c.InHorDistOf(camp.Rear, 10f) && !occupied.Contains(c) && Clear(c))
                    .Select(c => (Cell: c, Depth: Vector2.Dot(Offset(c), forward), Across: Vector2.Dot(Offset(c), lateral)))
                    .ToList();
                for (int i = 0; i < defenseGuns.Length; i++)
                {
                    float station = (i - (defenseGuns.Length - 1) * 0.5f) * 15f;
                    var sites = perimeter.OrderBy(c => c.Depth >= frontEdge ? 0 : c.Depth > 0f ? 1 : 2)
                        .ThenBy(c => result.Guns.Any(g => g.Role == ArtilleryGunRole.CampDefense && c.Cell.InHorDistOf(g.Cell, 15f)))
                        .ThenBy(c => new Vector2(c.Depth - frontEdge, c.Across - station).sqrMagnitude)
                        .Select(c => c.Cell);
                    Place(defenseGuns[i], ArtilleryGunRole.CampDefense, sites);
                }

                AddCover(map, result, camp.Facing, occupied, siege.Mechanoid);
                return result;

                void Place(ThingDef def, ArtilleryGunRole role, IEnumerable<IntVec3> sites)
                {
                    if (def == null) return;
                    ArtilleryPlacement gun = MakeGun(def, siege, role);
                    bool perimeterGun = role == ArtilleryGunRole.CampDefense;
                    foreach (IntVec3 c in sites)
                    {
                        gun.Cell = c;
                        gun.Rotation = perimeterGun ? Rot4.FromAngleFlat((groundGoal - c).AngleFlat) : camp.Facing;
                        if (!Fits(gun, occupied, Clear) || perimeterGun && Enumerable.Range(1, 4).Any(d =>
                            {
                                IntVec3 ahead = c + gun.Rotation.FacingCell * d;
                                return !ground.Contains(ahead) || occupied.Contains(ahead) ||
                                    ahead.GetEdifice(map) is { def.passability: Traversability.Impassable };
                            }) || !map.reachability.CanReach(camp.Rear, c, PathEndMode.Touch, Traversal) ||
                            !NativeConstruction.CanPlaceSiegeProject(gun.Def, c, gun.Rotation, map, gun.Stuff)) continue;
                        Reserve(map, gun, occupied);
                        result.Guns.Add(gun);
                        return;
                    }
                }
            }

            bool Fits(ArtilleryPlacement gun, HashSet<IntVec3> occupied, Predicate<IntVec3> clear)
            {
                bool Available(IntVec3 c) => !occupied.Contains(c) && clear(c);
                return GenAdj.OccupiedRect(gun.Cell, gun.Rotation, gun.Def.Size).All(c => Available(c)) &&
                    (!gun.NeedsOperator || Available(ThingUtility.InteractionCellWhenAt(gun.Def, gun.Cell, gun.Rotation, map)));
            }

            void PlanFront(EncirclementCampPlan camp, ArtilleryTargets.Catalog catalog, int batteries, ref int passes)
            {
                SupportRoute route = SupportRoute.Build(map, camp.Rear, goal);
                if (route == null) return;
                CampSiting siting = route.PlanningTargets(catalog: catalog);
                if (siting.BreachTargets.Count == 0) return;
                var occupied = new HashSet<IntVec3>(camp.Rooms.SelectMany(r => r.Footprint.ExpandedBy(1)));
                occupied.UnionWith(camp.Fixtures);
                occupied.UnionWith(CampSupplies.Reserved(map, camp.RearBattery));
                var weaponSpecs = siege.Mechanoid
                    ? new (string Name, float Weight)[] { ("Turret_AutoInferno", 1f), ("Turret_AutoChargeBlaster", 1f) }
                    : new (string Name, float Weight)[]
                    {
                        ("Turret_Flak", 1f),
                        ("CE_Turret_SPGNine", 1f),
                        ("CE_Turret_TwelvePounder", 0.4f)
                    };
                var weapons = weaponSpecs
                    .Select(spec => (Def: DefDatabase<ThingDef>.GetNamedSilentFail(spec.Name), spec.Weight))
                    .Where(spec => spec.Def != null)
                    .Select(spec => (Gun: MakeGun(spec.Def, siege, ArtilleryGunRole.Support), spec.Weight))
                    .Where(w => w.Gun.IsDirectSupport)
                    .Select(w => (w.Gun, w.Weight, Range: CeArtillerySight.EffectiveRange(map, siege.Faction,
                        new(w.Gun.Def, w.Gun.Stuff, w.Gun.Ammo, camp.Rear, Rot4.North, null))))
                    .Where(w => w.Range > CampSiting.RangeMargin).ToList();
                if (weapons.Count == 0) return;

                int clusterLimit = siege.BasePoints >= 12000f ? 4 : 3;
                float remaining = camp.ForwardBudget;
                int passLimit = Math.Min(FrontPlanningBudget, passes + FrontPerCandidateBudget);
                for (int i = 0; i < batteries && passes < passLimit; i++)
                {
                    CampPlan battery = null;
                    int batteriesLeft = batteries - i;
                    float share = remaining / batteriesLeft;
                    var pool = weapons.Where(w => QuestPart_Encirclement.HeavyValue(w.Gun) <= remaining).ToList();
                    var choices = new List<(ArtilleryPlacement Gun, float Weight, float Range)>(pool.Count);
                    while (pool.Count > 0)
                    {
                        var picked = pool.RandomElementByWeight(w =>
                            w.Weight * Mathf.Pow(0.45f, camp.Front.Count(c => c.Guns.Any(g => g.Def == w.Gun.Def))));
                        choices.Add(picked);
                        pool.Remove(picked);
                    }
                    foreach (var (template, _, range) in choices)
                    {
                        float cost = QuestPart_Encirclement.HeavyValue(template);
                        int affordableTotal = Mathf.FloorToInt(remaining / cost);
                        if (affordableTotal <= 0) continue;
                        int count = Mathf.Clamp(Mathf.Max(1, Mathf.FloorToInt(share / cost)), 1,
                            Mathf.Min(clusterLimit, affordableTotal));
                        int preferredCount = Mathf.Min(2, count);
                        var centers = ArtilleryRaidPolicy.DirectCenters(map, camp.Rear, siting, range, FrontCenterSamples)
                            .Where(c => Clear(c) && !c.InHorDistOf(camp.Rear, CampSupplies.Radius * 2) &&
                                !camp.Front.Any(other => c.InHorDistOf(other.Center, CampSupplies.Radius * 2)) &&
                                !CellRect.CenteredOn(c, CampPlanner.GunAreaRadius).Any(cell => cell.InBounds(map) &&
                                    (occupied.Contains(cell) || map.areaManager.Home[cell])))
                            .ToLookup(Exposed);
                        var guns = Enumerable.Range(0, count).Select(_ => new ArtilleryPlacement
                        {
                            Def = template.Def, Stuff = template.Stuff, Ammo = template.Ammo, Role = template.Role,
                            ConstructionWorkFactor = template.ConstructionWorkFactor
                        }).ToList();
                        var ranges = guns.ToDictionary(g => g, _ => range);
                        // The ordinary raid planner validates the formation, firing corridor, access and supplies.
                        bool hitPreferred = false;
                        foreach (bool exposed in new[] { false, true })
                        {
                            if (passes >= passLimit || !centers.Contains(exposed)) continue;
                            passes++;
                            if (!CampPlanner.TryPlan(map, siege.Faction, camp.Rear, goal, guns, centers[exposed],
                                siting, ranges, out CampPlan planned, fortified: false, divisions: 2,
                                gunClearance: FrontGunClearance)) continue;
                            if (battery == null || planned.Guns.Count > battery.Guns.Count ||
                                planned.Guns.Count == battery.Guns.Count &&
                                planned.Guns.Sum(QuestPart_Encirclement.HeavyValue) >
                                    battery.Guns.Sum(QuestPart_Encirclement.HeavyValue))
                                battery = planned;
                            if (planned.Guns.Count >= preferredCount)
                            {
                                hitPreferred = true;
                                break;
                            }
                        }
                        if (hitPreferred || passes >= passLimit) break;
                    }
                    if (battery == null) break;
                    Rot4 forward = Rot4.FromAngleFlat(
                        (IsoFloor.GroundCellOf(battery.BreachAnchor, map) - battery.Center).AngleFlat);
                    occupied.UnionWith(CampSupplies.Reserved(map, battery));
                    AddCover(map, battery, forward, occupied, siege.Mechanoid);
                    camp.Front.Add(battery);
                    remaining -= battery.Guns.Sum(QuestPart_Encirclement.HeavyValue);
                }
            }
        }

        private static ArtilleryPlacement MakeGun(ThingDef def, QuestPart_Encirclement siege, ArtilleryGunRole role) => new()
        {
            Def = def, Stuff = def.MadeFromStuff ? ThingDefOf.Steel : null, Ammo = SelectAmmo(def, siege), Role = role,
            ConstructionWorkFactor = WorkFactorFor(def)
        };

        private static float WorkFactorFor(ThingDef def) => def.defName switch
        {
            "Turret_Flak" => 0.4f,
            "CE_Turret_SPGNine" => 0.6f,
            _ => 1f
        };

        private static ThingDef SelectAmmo(ThingDef turret, QuestPart_Encirclement siege)
        {
            string preferred = turret.defName switch
            {
                "Turret_Flak" => "Ammo_90mmCannonShell_HE",
                "CE_Turret_SPGNine" => siege.Faction.def.techLevel >= TechLevel.Ultra
                    ? "Ammo_SPG9Rocket_Thermobaric" : "Ammo_SPG9Rocket_HEAT",
                "CE_Turret_TwelvePounder" => "Ammo_CannonBall_Bursting",
                _ => null
            };
            var ammoSet = turret.building.turretGunDef?.GetCompProperties<CompProperties_AmmoUser>()?.ammoSet;
            if (preferred != null && ammoSet != null && AmmoUtility.IsAmmoSystemActive(ammoSet))
            {
                ThingDef ammo = ammoSet.ammoTypes.FirstOrDefault(link => link.ammo?.defName == preferred)?.ammo;
                if (ammo != null) return ammo;
                Log.WarningOnce($"[RimBreach Encirclement] {turret.defName} cannot load {preferred}; using CE ammo selection.",
                    Gen.HashCombineInt(turret.shortHash, GenText.StableStringHash(preferred)));
            }
            return ArtilleryProfileDef.SelectAmmo(turret, siege.Faction, defaultAmmo: siege.Mechanoid && ammoSet != null);
        }

        private static void Reserve(Map map, ArtilleryPlacement gun, HashSet<IntVec3> occupied)
        {
            occupied.UnionWith(GenAdj.OccupiedRect(gun.Cell, gun.Rotation, gun.Def.Size));
            if (gun.NeedsOperator) occupied.Add(ThingUtility.InteractionCellWhenAt(gun.Def, gun.Cell, gun.Rotation, map));
        }

        private static void AddCover(Map map, CampPlan cluster, Rot4 facing, HashSet<IntVec3> occupied, bool mech)
        {
            ThingDef def = mech ? ThingDefOf.Barricade : ThingDefOf.Sandbags;
            ThingDef stuff = mech ? ThingDefOf.Plasteel : ThingDefOf.Steel;
            if (!mech && !GenStuff.AllowedStuffsFor(def).Contains(stuff)) stuff = ThingDefOf.Cloth;
            foreach (ArtilleryPlacement gun in cluster.Guns)
            {
                Rot4 front = gun.Role == ArtilleryGunRole.CampDefense ? gun.Rotation : facing;
                foreach (IntVec3 cell in GenAdj.OccupiedRect(gun.Cell, gun.Rotation, gun.Def.Size)
                    .GetEdgeCells(front).Select(c => c + front.FacingCell))
                {
                    if (occupied.Contains(cell) || !cell.InBounds(map) || map.areaManager.Home[cell] ||
                        cell.GetEdifice(map) != null || !NativeConstruction.CanPlaceSiegeProject(def, cell, facing, map, stuff)) continue;
                    cluster.Cover.Add(new ArtilleryPlacement { Def = def, Stuff = stuff, Cell = cell, Rotation = facing });
                    occupied.Add(cell);
                }
            }
        }
    }
}
