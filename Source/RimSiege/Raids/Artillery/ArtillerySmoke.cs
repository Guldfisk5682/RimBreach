using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using HarmonyLib;
using RimSiege.Integration;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Raids.Artillery
{
    [DefOf]
    internal static class ArtillerySmokeDefOf
    {
        public static DutyDef RS_ArtillerySmoke = null;
        public static DutyDef RS_ArtilleryMechSmokeEscort = null;
        public static DutyDef RS_ArtilleryInfantryHold = null;
        public static JobDef RS_LayArtillerySmoke = null;
        public static JobDef RS_ApproachSmokeThrow = null;
        public static ThingDef CE_Weapon_GrenadeSmoke = null;
        [MayRequire("CETeam.CombatExtendedArmory")]
        public static ThingDef CE_Weapon_SmokeBomb = null;

        static ArtillerySmokeDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(ArtillerySmokeDefOf));
    }

    internal sealed class SmokeAssignment : IExposable
    {
        internal Pawn Pawn;
        internal ThingWithComps Grenade, OriginalWeapon;
        internal bool Fighting;
        internal bool LoadoutRestored;
        internal IntVec3 Stand, Target;
        internal int RetryAt;

        public void ExposeData()
        {
            Scribe_References.Look(ref Pawn, "pawn");
            Scribe_References.Look(ref Grenade, "grenade");
            Scribe_References.Look(ref OriginalWeapon, "originalWeapon");
            Scribe_Values.Look(ref Fighting, "fighting");
            Scribe_Values.Look(ref LoadoutRestored, "loadoutRestored");
            Scribe_Values.Look(ref RetryAt, "retryAt");
            Scribe_Values.Look(ref Stand, "stand");
            Scribe_Values.Look(ref Target, "target");
        }
    }

    internal sealed class SmokeTarget : IExposable
    {
        internal IntVec3 Cell = IntVec3.Invalid;
        internal IntVec3 Stand = IntVec3.Invalid;
        internal int Order;
        internal bool Required = true;
        internal bool Covered;
        internal Pawn ClaimedBy;
        internal int ClaimUntil;
        internal int RetryAt;

        internal bool IsDone(Map map) => (!Required && Covered) || ArtillerySmoke.Screened(Cell, map);

        internal bool CanClaim(Pawn pawn, Map map, int now) =>
            !IsDone(map) && now >= RetryAt &&
            (ClaimedBy == null || ClaimedBy == pawn || now >= ClaimUntil);

        internal void Claim(Pawn pawn, int until)
        {
            ClaimedBy = pawn;
            ClaimUntil = until;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref Cell, "cell", IntVec3.Invalid);
            Scribe_Values.Look(ref Stand, "stand", IntVec3.Invalid);
            Scribe_Values.Look(ref Order, "order");
            Scribe_Values.Look(ref Required, "required", true);
            Scribe_Values.Look(ref Covered, "covered");
            Scribe_References.Look(ref ClaimedBy, "claimedBy");
            Scribe_Values.Look(ref ClaimUntil, "claimUntil");
            Scribe_Values.Look(ref RetryAt, "retryAt");
        }
    }

    internal sealed class ArtillerySmoke : IExposable
    {
        private const int RallyDistance = 20;
        private const int PreparationTicks = 1500;
        private const int MaxScreenTicks = 3600;
        private const int MaxApproachStations = 6;
        private const int MinTargetSpacingSq = 10;
        private const int CampOrder = 10;
        private const int OuterOrder = 12;
        private const float CampStopRadiusSq = 121f;
        private const float CampRingRadius = 5.5f;
        private const float OuterPerimeterRadius = 9.5f;
        internal const int GrenadesPerCarrier = 5;
        internal const float DefaultThrowRange = 8.8f;

        internal IntVec3 Rally = IntVec3.Invalid;
        internal int Deadline;
        internal int InitialPawns;
        internal int LastThrowTick;
        internal int InFlightUntil;
        internal int NextEvalTick;
        internal float ThrowRange = DefaultThrowRange;
        internal bool StartedScreen;
        internal bool IsLocal;
        internal IntVec3 LastFollowCell = IntVec3.Invalid;
        internal List<SmokeAssignment> Assignments = new();
        internal List<SmokeTarget> Targets = new();
        internal List<IntVec3> ApproachRoute = new();
        private IntVec3 followCell = IntVec3.Invalid;
        private IntVec3 mechFollowCell = IntVec3.Invalid;
        private int nextFollowUpdate;
        private Pawn[] followingBehind = Array.Empty<Pawn>();

        internal bool CanFollowInto(Pawn follower, IntVec3 cell)
        {
            float distance = SiegeRoles.IsHumanRaidEscortMech(follower) ? 1f : 15f;
            return followingBehind.All(carrier => !carrier.Spawned ||
                IsoFloor.Distance3D(carrier.Position, cell, follower.Map) >= distance ||
                IsoFloor.Distance3D(carrier.Position, cell, follower.Map) >=
                    IsoFloor.Distance3D(carrier.Position, follower.Position, follower.Map));
        }

        internal IntVec3 FollowCell(Lord lord, IntVec3 campCenter, bool mechEscort = false)
        {
            int now = GenTicks.TicksGame;
            if (now >= nextFollowUpdate)
            {
                nextFollowUpdate = now + 45;
                UpdateFollowCells(lord, campCenter);
            }
            return mechEscort ? mechFollowCell : followCell;
        }

        private void UpdateFollowCells(Lord lord, IntVec3 campCenter)
        {
            followCell = IntVec3.Invalid;
            mechFollowCell = IntVec3.Invalid;

            Pawn[] carriers = followingBehind = Assignments.Select(assignment => assignment.Pawn)
                .Where(pawn => Ready(pawn) && HasCarrier(pawn) && pawn.GetLord() == lord &&
                    (FindSmokeWeapon(pawn) != null || pawn.CurJobDef == ArtillerySmokeDefOf.RS_LayArtillerySmoke))
                .ToArray();
            if (carriers.Length == 0) return;

            Map map = lord.Map;
            if (ApproachRoute.Count == 0)
            {
                using PawnPath path = map.pathFinder.FindPathNow(Rally, campCenter, TraverseParms.For(carriers[0]),
                    null, PathEndMode.OnCell);
                if (path == null || !path.Found) return;
                ApproachRoute.AddRange(path.NodesReversed.AsEnumerable().Reverse());
            }

            // Use the back of the smoke team so the main force cannot overtake a carrier still throwing.
            int rear = carriers.Min(pawn => NearestRouteIndex(pawn.Position, map));
            for (int i = rear - 1; i >= 0; i--)
            {
                IntVec3 cell = ApproachRoute[i];
                float distance = carriers.Min(pawn => IsoFloor.Distance3D(pawn.Position, cell, map));
                if (distance < (mechFollowCell.IsValid ? 17f : 1f) || !CanAdvance(cell, map)) continue;
                if (!mechFollowCell.IsValid) mechFollowCell = cell;
                if (distance < 17f) continue;
                LastFollowCell = followCell = cell;
                return;
            }
        }

        private int NearestRouteIndex(IntVec3 position, Map map)
        {
            int closest = 0;
            float distance = float.MaxValue;
            for (int i = 0; i < ApproachRoute.Count; i++)
            {
                float candidate = IsoFloor.Distance3D(position, ApproachRoute[i], map);
                if (candidate >= distance) continue;
                distance = candidate;
                closest = i;
            }
            return closest;
        }
        internal ThingDef SmokeWeapon;
        internal bool TurretsOnly;
        internal Faction ThreatFaction;
        private SmokeThreatCache threatCache;
        private SmokeThreatCache ThreatCache =>
            threatCache ??= new SmokeThreatCache(includeColonists: !TurretsOnly, ThreatFaction);

        internal ThingWithComps FindSmokeWeapon(Pawn pawn) =>
            SmokeCombat.FindSmokeWeapon(pawn, SmokeWeapon ?? ArtillerySmokeDefOf.CE_Weapon_GrenadeSmoke);

        internal bool CanAdvance(IntVec3 cell, Map map) => ThreatCache.CanAdvance(cell, map);

        internal bool CanThrowFrom(IntVec3 cell, Map map) => IsLocal || CanAdvance(cell, map);

        internal ArtillerySmoke LocalScreen(IntVec3 focus) => new()
        {
            Rally = focus, ThrowRange = ThrowRange, StartedScreen = true, IsLocal = true,
            ThreatFaction = ThreatFaction, TurretsOnly = TurretsOnly,
            threatCache = ThreatCache
        };

        internal void AddLocalTarget(Map map, IntVec3 anchor, Predicate<IntVec3> allowed = null)
        {
            bool Stands(IntVec3 cell) => cell.InBounds(map) && IsoFloor.SameFloor(anchor, cell, map) &&
                cell.Standable(map) && (allowed == null || allowed(cell));
            foreach (IntVec3 stand in GenRadial.RadialCellsAround(anchor, 3, true))
            {
                if (!Stands(stand)) continue;
                foreach (IntVec3 cell in GenRadial.RadialCellsAround(anchor, 3, false))
                {
                    if (!Stands(cell) || Targets.Any(t => t.Cell.DistanceToSquared(cell) < MinTargetSpacingSq) ||
                        !SmokeCombat.CanThrowNow(stand, cell, map, ThrowRange)) continue;
                    Targets.Add(new SmokeTarget { Cell = cell, Stand = stand });
                    return;
                }
            }
        }

        internal static bool Eligible(ArtilleryRaid raid) => raid.Faction.def.humanlikeFaction && raid.RaidPoints >= 2000f;

        internal static bool Ready(Pawn pawn) => SmokeCombat.Ready(pawn);

        internal static ArtillerySmoke Plan(ArtilleryRaid raid, out IntVec3 fallbackRally, out string outcome)
        {
            Lord lord = raid.CampLord;
            Map map = lord.Map;
            fallbackRally = IntVec3.Invalid;
            outcome = "insufficient eligible carriers";

            var specialists = ArtilleryDeployment.Specialists(raid.Strategy, lord.ownedPawns);
            var siege = lord.Graph.lordToils.OfType<LordToil_Siege>().Single();
            Pawn builder = lord.ownedPawns.FirstOrDefault(p => ArtilleryCrew.BuilderCandidate(siege, p));
            var eligible = lord.ownedPawns
                .Where(p => p != builder && !specialists.Contains(p) && Ready(p) &&
                    p.equipment != null && p.TryGetComp<CompInventory>() != null)
                .ToList();
            if (eligible.Count < 2) return null;

            Pawn lead = eligible[0];
            IntVec3 entry = lead.Position;
            TraverseParms traverse = TraverseParms.For(lead);
            Verb[] threats = SmokeThreatCache.CollectThreats(map, includeColonists: true, raid.TargetFaction);
            bool Exposed(IntVec3 cell) => IsExposed(cell, map, threats);

            outcome = "no sheltered rally within 20 cells";
            IntVec3 rally = fallbackRally = FindRally(entry, map, traverse, Exposed);
            if (!rally.IsValid) return null;
            outcome = "no route from rally to camp";
            using PawnPath route = map.pathFinder.FindPathNow(rally, raid.Camp.Center, traverse, null, PathEndMode.OnCell);
            if (route == null || !route.Found) return null;

            var nodes = route.NodesReversed.AsEnumerable().Reverse().ToList();
            float throwRange = SafeThrowRange();
            var targets = new List<SmokeTarget>();
            AssessCampThreats(raid.Camp, rally, map, threats, out bool campExposed, out float threatAngle);
            int maxApproach = Math.Clamp(eligible.Count, 2, MaxApproachStations);
            bool routeExposed = AddApproachTargets(targets, nodes, rally, raid.Camp, campExposed, threatAngle,
                maxApproach, map, traverse, throwRange, Exposed);
            if (campExposed)
                AddCampScreen(targets, rally, raid.Camp, threatAngle, map, traverse, throwRange, Exposed);
            if (targets.Count == 0)
            {
                outcome = $"no smoke stations (routeExposed={routeExposed}, campExposed={campExposed})";
                return null;
            }

            int desiredCarriers = Math.Min(eligible.Count, Math.Max(2, targets.Count));
            var carriers = new List<Pawn>(desiredCarriers);
            var grenades = new Dictionary<Pawn, ThingWithComps>(desiredCarriers);
            outcome = "fewer than two carriers accepted the smoke kit";
            foreach (Pawn carrier in eligible.OrderBy(pawn => SiegeRoles.HasDemolitionWeapon(pawn))
                .ThenBy(pawn => pawn.Position.DistanceToSquared(rally)))
            {
                // The issued mission kit is separate from CE's faction loadout.
                // Full inventories cannot reduce the smoke team to two pawns.
                if (GiveSmoke(carrier, GrenadesPerCarrier) is not { } grenade) continue;
                grenades.Add(carrier, grenade);
                carriers.Add(carrier);
                if (carriers.Count == desiredCarriers) break;
            }
            if (carriers.Count < 2)
            {
                DiscardGrantedSmoke(grenades);
                return null;
            }
            outcome = "screen planned";

            int now = GenTicks.TicksGame;
            int budget = ComputeBudgetTicks(nodes, map, targets.Count, carriers.Count);
            var screen = new ArtillerySmoke
            {
                Rally = rally,
                Deadline = now + budget,
                InitialPawns = lord.ownedPawns.Count(Ready),
                ThreatFaction = raid.TargetFaction,
                ThrowRange = throwRange,
                StartedScreen = true,
                Targets = targets,
                ApproachRoute = nodes
            };
            for (int i = 0; i < carriers.Count; i++)
            {
                SmokeTarget initial = i < targets.Count ? targets[i] : null;
                screen.Assignments.Add(new SmokeAssignment
                {
                    Pawn = carriers[i],
                    Grenade = grenades[carriers[i]],
                    OriginalWeapon = carriers[i].equipment.Primary,
                    Stand = initial?.Stand ?? IntVec3.Invalid,
                    Target = initial?.Cell ?? IntVec3.Invalid
                });
            }
            return screen;
        }

        private static bool IsExposed(IntVec3 cell, Map map, Verb[] threats) =>
            SmokeThreatCache.IsExposedToThreats(cell, map, threats);

        private static float SafeThrowRange() =>
            SmokeCombat.ThrowRange(ArtillerySmokeDefOf.CE_Weapon_GrenadeSmoke, margin: 1.2f, fallback: 10f);

        private static IntVec3 FindRally(IntVec3 entry, Map map, TraverseParms traverse, Predicate<IntVec3> exposed)
        {
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(entry, RallyDistance, true)
                .Where(cell => cell == entry || ((cell.x - entry.x) % 4 == 0 && (cell.z - entry.z) % 4 == 0)))
            {
                if (!cell.InBounds(map) || !IsoFloor.SameFloor(cell, entry, map) || !cell.Standable(map) ||
                    exposed(cell) || !map.reachability.CanReach(entry, cell, PathEndMode.OnCell, traverse)) continue;
                if (cell != entry)
                {
                    using PawnPath approach = map.pathFinder.FindPathNow(entry, cell, traverse, null, PathEndMode.OnCell);
                    if (approach == null || !approach.Found || Length(approach.NodesReversed, map) > RallyDistance) continue;
                }
                return cell;
            }
            return IntVec3.Invalid;
        }

        private static readonly int[] OuterSectorSteps = { 0, 1, -1, 2, -2, 3, -3, 4, -4, 5 };

        private static int ComputeBudgetTicks(List<IntVec3> nodes, Map map, int targetCount, int carrierCount)
        {
            int walkTicks = Mathf.RoundToInt(Length(nodes, map) * 16f);
            int screenTicks = Math.Max(400, targetCount * 110 / Math.Max(2, carrierCount));
            return Math.Clamp(1100 + walkTicks + screenTicks, PreparationTicks, MaxScreenTicks);
        }

        private static void AssessCampThreats(CampPlan camp, IntVec3 rally, Map map, Verb[] threats,
            out bool campExposed, out float threatAngle)
        {
            var probes = CollectCampProbes(camp, map);
            Vector3 threatSum = Vector3.zero;
            campExposed = false;

            foreach (Verb verb in threats)
            {
                if (verb.Caster is not { Spawned: true } caster) continue;
                if (!probes.Any(cell => SmokeThreatCache.CanThreaten(verb, cell, map))) continue;
                campExposed = true;
                Vector3 dir = (IsoFloor.GroundCellOf(caster.Position, map) - IsoFloor.GroundCellOf(camp.Center, map)).ToVector3();
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.01f) threatSum += dir.normalized;
            }

            if (threatSum.sqrMagnitude >= 0.01f)
            {
                threatAngle = Mathf.Atan2(threatSum.z, threatSum.x);
                return;
            }

            IntVec3 fallbackRef = camp.BreachAnchor.IsValid && camp.BreachAnchor != camp.Center
                ? camp.BreachAnchor
                : camp.Goal.IsValid && camp.Goal != camp.Center
                    ? camp.Goal
                    : camp.Center + (camp.Center - rally);
            Vector3 fallback = (IsoFloor.GroundCellOf(fallbackRef, map) - IsoFloor.GroundCellOf(camp.Center, map)).ToVector3();
            threatAngle = fallback.x * fallback.x + fallback.z * fallback.z >= 0.01f
                ? Mathf.Atan2(fallback.z, fallback.x)
                : 0f;
        }

        private static List<IntVec3> CollectCampProbes(CampPlan camp, Map map)
        {
            var probes = new List<IntVec3> { camp.Center };
            foreach (ArtilleryPlacement gun in camp.Guns)
                foreach (IntVec3 cell in PlacementCells(gun, map))
                    if (cell.InBounds(map) && IsoFloor.SameFloor(cell, camp.Center, map))
                        probes.Add(cell);

            for (int i = 0; i < 8; i++)
            {
                float angle = i * (Mathf.PI * 0.25f);
                IntVec3 cell = OffsetCell(camp.Center, angle, 8f);
                if (cell.InBounds(map) && IsoFloor.SameFloor(cell, camp.Center, map) && !cell.Filled(map))
                    probes.Add(cell);
            }
            return probes;
        }

        private static bool AddApproachTargets(List<SmokeTarget> targets, List<IntVec3> nodes, IntVec3 rally,
            CampPlan camp, bool campExposed, float threatAngle, int maxStations, Map map, TraverseParms traverse,
            float throwRange, Predicate<IntVec3> exposed)
        {
            var nodeExposed = new bool[nodes.Count];
            int firstExposed = -1;
            int lastExposed = -1;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (!exposed(nodes[i])) continue;
                nodeExposed[i] = true;
                if (firstExposed < 0) firstExposed = i;
                lastExposed = i;
            }
            if (firstExposed < 0) return false;

            int start = Math.Max(0, firstExposed - 3);
            int approachEnd = lastExposed;
            if (campExposed)
            {
                approachEnd = start - 1;
                for (int i = lastExposed; i >= start; i--)
                {
                    if ((nodes[i] - camp.Center).LengthHorizontalSquared < CampStopRadiusSq) continue;
                    approachEnd = i;
                    break;
                }
                if (approachEnd < start) return true;
            }

            float span = 0f;
            for (int i = start + 1; i <= approachEnd; i++)
                span += IsoFloor.Distance3D(nodes[i - 1], nodes[i], map);
            float sampleStep = Math.Clamp(span / Math.Max(1, maxStations - 1), 6f, 8.5f);

            var stations = new List<int>();
            float sinceSample = sampleStep;
            for (int i = start; i <= approachEnd; i++)
            {
                if (i > start) sinceSample += IsoFloor.Distance3D(nodes[i - 1], nodes[i], map);
                bool endpoint = i == start || i == approachEnd;
                if (sinceSample < (endpoint ? 3f : sampleStep)) continue;
                if (!nodeExposed[i] && i != start) continue;
                stations.Add(i);
                sinceSample = 0f;
                if (stations.Count >= maxStations) break;
            }

            Vector3 threatDir = new Vector3(Mathf.Cos(threatAngle), 0f, Mathf.Sin(threatAngle));
            for (int order = 0; order < stations.Count; order++)
            {
                int idx = stations[order];
                IntVec3 spine = nodes[idx];
                IntVec3 anchor = nodes[Math.Max(0, idx - 6)];
                TryAddTarget(targets, spine, spine, order, false, anchor, rally, map, traverse, throwRange, exposed);

                if (!campExposed || order == stations.Count - 1)
                {
                    Vector3 side = SmokeCombat.ApproachSide(nodes, idx, rally);
                    int preferredSign = Vector3.Dot(side, threatDir) >= 0f ? 1 : -1;
                    IntVec3 flank = FindFlankCell(map, spine, side, preferredSign, exposed);
                    if (!flank.IsValid) flank = FindFlankCell(map, spine, side, -preferredSign, exposed);
                    if (flank.IsValid)
                        TryAddTarget(targets, flank, spine, order, false, anchor, rally, map, traverse, throwRange, exposed);
                }
            }
            return true;
        }

        private static void AddCampScreen(List<SmokeTarget> targets, IntVec3 rally, CampPlan camp, float threatAngle,
            Map map, TraverseParms traverse, float throwRange, Predicate<IntVec3> exposed)
        {
            Vector3 away = new Vector3(-Mathf.Cos(threatAngle), 0f, -Mathf.Sin(threatAngle));
            IntVec3 Anchor(IntVec3 cell) => cell + new IntVec3(Mathf.RoundToInt(away.x * 5f), 0, Mathf.RoundToInt(away.z * 5f));

            // Center + 6-point hex ring covers r=0..9.5 around the camp and clustered inner drop spots.
            TryAddTargetNear(targets, camp.Center, camp.Center, 2, CampOrder, true, Anchor(camp.Center), rally, map, traverse, throwRange, exposed);

            for (int i = 0; i < 6; i++)
            {
                float angle = threatAngle + i * (Mathf.PI / 3f);
                IntVec3 ringCell = OffsetCell(camp.Center, angle, CampRingRadius);
                int ringOrder = (i <= 1 || i == 5) ? CampOrder : CampOrder + 1;
                TryAddTargetNear(targets, ringCell, camp.Center, 2, ringOrder, true, Anchor(ringCell), rally, map, traverse, throwRange, exposed);
            }

            // Outer ring at r=9.5 extends continuous cover out to r~13.5 facing active threats and exposed flanks.
            float stepAngle = 2f * Mathf.PI / 10f;
            for (int i = 0; i < OuterSectorSteps.Length; i++)
            {
                int step = OuterSectorSteps[i];
                float angle = threatAngle + step * stepAngle;
                IntVec3 outerCell = OffsetCell(camp.Center, angle, OuterPerimeterRadius);
                int probeRadius = Math.Abs(step) <= 2 ? 3 : 2;
                if (!HasExposedNear(outerCell, camp.Center, probeRadius, map, exposed)) continue;
                TryAddTargetNear(targets, outerCell, camp.Center, 2, OuterOrder, true, Anchor(outerCell), rally, map, traverse, throwRange, exposed);
            }

            // Cover any exposed gun or embrasure tile left uncovered by terrain nudges.
            IntVec3 coverOffset = new IntVec3(Mathf.RoundToInt(-away.x), 0, Mathf.RoundToInt(-away.z));
            foreach (ArtilleryPlacement gun in camp.Guns)
            {
                TryCoverPoint(gun.Cell + coverOffset);
                TryCoverPoint(gun.Cell);
            }

            void TryCoverPoint(IntVec3 point)
            {
                if (!point.InBounds(map) || !IsoFloor.SameFloor(point, camp.Center, map) || !exposed(point)) return;
                if (targets.Any(t => t.Required && IsoFloor.SameFloor(t.Cell, point, map) &&
                    t.Cell.DistanceToSquared(point) <= 9)) return;
                TryAddTargetNear(targets, point, camp.Center, 1, CampOrder + 1, true, Anchor(point), rally, map, traverse, throwRange, exposed);
            }
        }

        private static IntVec3 OffsetCell(IntVec3 center, float angle, float radius) =>
            center + new IntVec3(Mathf.RoundToInt(Mathf.Cos(angle) * radius), 0, Mathf.RoundToInt(Mathf.Sin(angle) * radius));

        private static bool HasExposedNear(IntVec3 origin, IntVec3 floorRef, int radius, Map map, Predicate<IntVec3> exposed)
        {
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(origin, radius, true))
            {
                if (!cell.InBounds(map) || !IsoFloor.SameFloor(cell, floorRef, map) || cell.Filled(map)) continue;
                if (exposed(cell)) return true;
            }
            return false;
        }

        private static bool TryAddTargetNear(List<SmokeTarget> targets, IntVec3 desired, IntVec3 floorRef,
            int maxNudge, int order, bool required, IntVec3 anchor, IntVec3 reachSeed, Map map,
            TraverseParms traverse, float throwRange, Predicate<IntVec3> exposed)
        {
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(desired, maxNudge, true))
            {
                if (TryAddTarget(targets, cell, floorRef, order, required, anchor, reachSeed, map, traverse, throwRange, exposed))
                    return true;
            }
            return false;
        }

        private static bool TryAddTarget(List<SmokeTarget> targets, IntVec3 cell, IntVec3 floorRef, int order,
            bool required, IntVec3 anchor, IntVec3 reachSeed, Map map, TraverseParms traverse,
            float throwRange, Predicate<IntVec3> exposed)
        {
            if (!cell.InBounds(map) || !IsoFloor.SameFloor(cell, floorRef, map) || cell.Filled(map) || !cell.Walkable(map))
                return false;
            if (targets.Any(t => (!required || t.Required) &&
                IsoFloor.SameFloor(t.Cell, cell, map) &&
                t.Cell.DistanceToSquared(cell) < MinTargetSpacingSq))
                return false;
            IntVec3 stand = FindStand(cell, anchor, reachSeed, map, traverse, throwRange, exposed);
            if (!stand.IsValid) return false;
            targets.Add(new SmokeTarget { Cell = cell, Stand = stand, Order = order, Required = required });
            return true;
        }

        private static IntVec3 FindFlankCell(Map map, IntVec3 spine, Vector3 side, int sign, Predicate<IntVec3> exposed)
        {
            IntVec3 fallback = IntVec3.Invalid;
            foreach (int offset in new[] { 4, 5, 3 })
            {
                IntVec3 candidate = SmokeCombat.FlankCell(spine, side, sign * offset, map);
                if (!candidate.IsValid) continue;
                if (exposed(candidate)) return candidate;
                if (!fallback.IsValid) fallback = candidate;
            }
            return fallback;
        }

        private static IntVec3 FindStand(IntVec3 target, IntVec3 anchor, IntVec3 rally, Map map,
            TraverseParms traverse, float throwRange, Predicate<IntVec3> exposed)
        {
            IntVec3 best = IntVec3.Invalid;
            int bestScore = int.MinValue;
            Vector3 toAnchor = (anchor - target).ToVector3();
            toAnchor.y = 0f;
            if (toAnchor.sqrMagnitude > 0.01f) toAnchor.Normalize();

            foreach (IntVec3 cell in GenRadial.RadialCellsAround(target, Mathf.FloorToInt(throwRange), true))
            {
                if (!cell.InBounds(map) || !IsoFloor.SameFloor(cell, target, map) || !cell.Standable(map)) continue;
                float dist = IsoFloor.RangeDistance(cell, target, map);
                if (dist < 2f || dist > throwRange || !GenSight.LineOfSight(cell, target, map, skipFirstCell: true)) continue;
                if (!map.reachability.CanReach(rally, cell, PathEndMode.OnCell, traverse)) continue;

                int score = !exposed(cell) ? 10000 : 0;
                if (dist >= 4f && dist <= throwRange - 0.5f) score += 100;
                Vector3 dir = (cell - target).ToVector3();
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.01f && toAnchor.sqrMagnitude > 0.01f)
                    score += Mathf.RoundToInt(Vector3.Dot(dir.normalized, toAnchor) * 50f);
                score -= cell.DistanceToSquared(anchor) / 8;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = cell;
                }
            }
            return best;
        }

        private static ThingWithComps GiveSmoke(Pawn pawn, int count) =>
            SmokeCombat.GiveSmoke(pawn, ArtillerySmokeDefOf.CE_Weapon_GrenadeSmoke, count);

        private static void DiscardGrantedSmoke(Dictionary<Pawn, ThingWithComps> grenades)
        {
            foreach (KeyValuePair<Pawn, ThingWithComps> pair in grenades)
                if (pair.Value is { Destroyed: false }) pair.Value.Destroy();
        }

        internal static ThingWithComps FindSmokeGrenade(Pawn pawn) =>
            SmokeCombat.FindSmokeWeapon(pawn, ArtillerySmokeDefOf.CE_Weapon_GrenadeSmoke);

        internal bool HasActiveWork(Map map, int now)
        {
            if (InFlightUntil > now || (InFlightUntil == 0 && LastThrowTick > 0 && now < LastThrowTick + 95))
                return true;
            bool unscreened = Targets.Any(t => !t.IsDone(map));
            if (!unscreened) return false;
            return Assignments.Any(a => Ready(a.Pawn) && !a.Fighting && FindSmokeGrenade(a.Pawn) != null);
        }

        internal bool CampCovered(ArtilleryRaid raid, Map map)
        {
            var required = Targets.Where(target => target.Required).ToList();
            if (required.Count == 0) return Targets.All(target => target.IsDone(map));
            return required.All(target => target.IsDone(map)) &&
                raid.Camp.Guns.All(placement => PlacementCovered(placement, map, ThreatCache.CurrentThreats(map)));
        }

        private static bool PlacementCovered(ArtilleryPlacement placement, Map map, Verb[] threats)
        {
            bool Covered(IntVec3 cell) => cell.InBounds(map) &&
                (Screened(cell, map) || !IsExposed(cell, map, threats));
            return PlacementCells(placement, map).All(Covered);
        }

        private static IEnumerable<IntVec3> PlacementCells(ArtilleryPlacement placement, Map map)
        {
            foreach (IntVec3 cell in GenAdj.OccupiedRect(placement.Cell, placement.Rotation, placement.Def.Size))
                yield return cell;
            if (placement.NeedsOperator)
                yield return ThingUtility.InteractionCellWhenAt(placement.Def, placement.Cell, placement.Rotation, map);
        }

        internal void ApplyOptionalDuties(Lord camp)
        {
            foreach (SmokeAssignment assignment in Assignments)
                if (assignment.Pawn?.RaceProps?.Humanlike == true && assignment.Pawn.GetLord() == camp &&
                    assignment.Pawn.mindState.duty?.def == DutyDefOf.Defend)
                    assignment.Pawn.mindState.duty = new PawnDuty(ArtillerySmokeDefOf.RS_ArtillerySmoke, camp.CurLordToil.FlagLoc);
        }

        internal SmokeAssignment AssignmentFor(Pawn pawn) => Assignments.Find(a => a.Pawn == pawn);

        internal bool HasCarrier(Pawn pawn) => AssignmentFor(pawn) is { Fighting: false };

        internal static bool IsInCombat(Pawn pawn, int now)
        {
            if (pawn?.mindState == null) return false;
            Thing enemy = pawn.mindState.enemyTarget;
            return (enemy is { Spawned: true } && pawn.HostileTo(enemy)) ||
                pawn.mindState.lastHarmTick >= now - 90;
        }

        private static void EndSmokeJob(Pawn pawn)
        {
            if (pawn?.CurJob == null) return;
            if (pawn.CurJobDef == ArtillerySmokeDefOf.RS_LayArtillerySmoke ||
                pawn.CurJob.jobGiver is JobGiver_ArtillerySmoke or JobGiver_ArtillerySmokeFollowers ||
                pawn.CurJobDef == ArtillerySmokeDefOf.RS_ApproachSmokeThrow)
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
        }

        internal bool TryClaim(Pawn pawn, IntVec3 targetCell, int duration)
        {
            int now = GenTicks.TicksGame;
            SmokeTarget matched = Targets.Find(target => target.Cell == targetCell);
            if (matched == null || !matched.CanClaim(pawn, pawn.Map, now)) return false;
            ReleaseClaim(pawn, IntVec3.Invalid, 0);
            matched.Claim(pawn, now + duration);
            return true;
        }

        internal void ReleaseClaim(Pawn pawn, IntVec3 targetCell, int tightenTicks)
        {
            int now = GenTicks.TicksGame;
            foreach (SmokeTarget target in Targets)
            {
                if (target.ClaimedBy != pawn) continue;
                if (targetCell.IsValid && target.Cell != targetCell) continue;
                if (tightenTicks > 0)
                    target.ClaimUntil = Math.Min(target.ClaimUntil, now + tightenTicks);
                else
                    target.ClaimedBy = null;
            }
        }

        internal void OnThrowRejected(Pawn pawn, IntVec3 targetCell)
        {
            int now = GenTicks.TicksGame;
            foreach (SmokeTarget target in Targets)
            {
                if (target.ClaimedBy == pawn) target.ClaimedBy = null;
                if (targetCell.IsValid && target.Cell == targetCell)
                    target.RetryAt = Math.Max(target.RetryAt, now + 20);
            }
            NextEvalTick = now;
        }

        internal void NotifySmokeThrown(Pawn pawn, IntVec3 targetCell, int graceTicks)
        {
            int now = GenTicks.TicksGame;
            int until = now + graceTicks;
            LastThrowTick = now;
            InFlightUntil = Math.Max(InFlightUntil, until);
            foreach (SmokeTarget target in Targets)
            {
                if (target.ClaimedBy == pawn) target.ClaimedBy = null;
                if (targetCell.IsValid && target.Cell == targetCell)
                    target.RetryAt = Math.Max(target.RetryAt, until);
            }
            if (FindSmokeWeapon(pawn) == null)
                NextEvalTick = now;
        }

        internal void OnPawnDowned(Pawn pawn)
        {
            SmokeAssignment assignment = AssignmentFor(pawn);
            if (assignment != null) assignment.Fighting = true;
            ReleaseClaim(pawn, IntVec3.Invalid, 0);
            NextEvalTick = GenTicks.TicksGame;
        }

        internal void OnPawnKilled(Pawn pawn)
        {
            SmokeAssignment assignment = AssignmentFor(pawn);
            if (assignment != null)
            {
                assignment.Fighting = true;
                assignment.LoadoutRestored = true;
                assignment.OriginalWeapon = null;
            }
            ReleaseClaim(pawn, IntVec3.Invalid, 0);
            NextEvalTick = GenTicks.TicksGame;
        }

        internal void EvaluateCarriers(Lord lord, Map map, int now,
            Predicate<Pawn> participates = null, bool keepScreenUnderFire = false)
        {
            NextEvalTick = 0;
            var wake = new List<Pawn>();
            foreach (SmokeAssignment assignment in Assignments)
            {
                if (assignment.Fighting) continue;
                Pawn pawn = assignment.Pawn;
                if (!Ready(pawn) || pawn.GetLord() != lord ||
                    !(participates?.Invoke(pawn) ?? (pawn.mindState?.duty?.def == ArtillerySmokeDefOf.RS_ArtillerySmoke)))
                {
                    assignment.Fighting = true;
                    ReleaseClaim(pawn, IntVec3.Invalid, 0);
                    if (pawn?.Spawned == true && !pawn.Dead && !pawn.Downed)
                    {
                        EndSmokeJob(pawn);
                        SmokeLoadout.Restore(assignment);
                        wake.Add(pawn);
                    }
                    continue;
                }

                if (pawn.CurJobDef == ArtillerySmokeDefOf.RS_LayArtillerySmoke) continue;
                if (FindSmokeWeapon(pawn) == null || !keepScreenUnderFire && IsInCombat(pawn, now))
                {
                    assignment.Fighting = true;
                    ReleaseClaim(pawn, IntVec3.Invalid, 0);
                    EndSmokeJob(pawn);
                    SmokeLoadout.Restore(assignment);
                    wake.Add(pawn);
                }
            }
            for (int i = 0; i < wake.Count; i++)
                if (wake[i] is { Spawned: true, Dead: false, Downed: false })
                    wake[i].jobs.CheckForJobOverride();
        }

        internal void RestoreWeapons()
        {
            foreach (SmokeAssignment assignment in Assignments)
                ReleaseCarrier(assignment);
        }

        internal void ReleaseCarrier(SmokeAssignment assignment)
        {
            Pawn pawn = assignment.Pawn;
            assignment.Fighting = true;
            ReleaseClaim(pawn, IntVec3.Invalid, 0);
            if (pawn is { Spawned: true, Dead: false, Downed: false }) EndSmokeJob(pawn);
            SmokeLoadout.Restore(assignment);
        }

        private static float Length(List<IntVec3> nodes, Map map)
        {
            float total = 0;
            for (int i = 1; i < nodes.Count; i++) total += IsoFloor.Distance3D(nodes[i - 1], nodes[i], map);
            return total;
        }

        internal static bool Screened(IntVec3 cell, Map map) => SmokeCombat.Screened(cell, map);

        public void ExposeData()
        {
            Scribe_Values.Look(ref Rally, "rally", IntVec3.Invalid);
            Scribe_Values.Look(ref Deadline, "deadline");
            Scribe_Values.Look(ref InitialPawns, "initialPawns");
            Scribe_Values.Look(ref LastThrowTick, "lastThrowTick");
            Scribe_Values.Look(ref InFlightUntil, "inFlightUntil");
            Scribe_Values.Look(ref ThrowRange, "throwRange", DefaultThrowRange);
            Scribe_Values.Look(ref StartedScreen, "startedScreen");
            Scribe_Values.Look(ref LastFollowCell, "lastFollowCell", IntVec3.Invalid);
            Scribe_Values.Look(ref IsLocal, "localScreen");
            Scribe_Defs.Look(ref SmokeWeapon, "smokeWeapon");
            Scribe_Values.Look(ref TurretsOnly, "turretsOnly");
            Scribe_References.Look(ref ThreatFaction, "threatFaction");
            Scribe_Collections.Look(ref Assignments, "assignments", LookMode.Deep);
            Scribe_Collections.Look(ref Targets, "targets", LookMode.Deep);
            Scribe_Collections.Look(ref ApproachRoute, "approachRoute", LookMode.Value);
            Assignments ??= new();
            Targets ??= new();
            ApproachRoute ??= new();
        }
    }

    internal sealed class LordToil_ArtillerySmoke : LordToil
    {
        private ArtilleryRaid Raid => ((LordJob_Artillery)lord.LordJob).Raid;
        public override bool ShouldFail => lord.ownedPawns.Count == 0;

        public override void UpdateAllDuties()
        {
            IntVec3 focus = Raid.Smoke is { Rally: { IsValid: true } rally } ? rally : Raid.Camp.Center;
            foreach (Pawn pawn in lord.ownedPawns)
            {
                if (pawn?.mindState == null) continue;
                if (!pawn.RaceProps.Humanlike)
                {
                    if (pawn.RaceProps.IsMechanoid && Raid?.Kind != ArtilleryRaidKind.Mechanoid)
                    {
                        // In-progress saves may still have escorts in the camp lord.
                        LordToil_ArtilleryMechEscort.Assign(pawn, focus);
                    }
                    else
                        SiegeRoles.AssignMainForceEscortDuty(lord, pawn, focus, Raid);
                    continue;
                }
                pawn.mindState.duty = new PawnDuty(ArtillerySmokeDefOf.RS_ArtillerySmoke, focus)
                {
                    focusSecond = focus,
                    radius = 28f,
                    wanderRadius = 12f
                };
            }
        }

        public override void Cleanup()
        {
            if (Raid.Smoke is { } screen)
            {
                screen.RestoreWeapons();
                Raid.Smoke = null;
            }
            base.Cleanup();
        }

        public override void LordToilTick()
        {
            int now = GenTicks.TicksGame;
            if (Raid.Smoke is not ArtillerySmoke smoke) return;
            if (now % 10 != 0 && !(smoke.NextEvalTick > 0 && now >= smoke.NextEvalTick)) return;
            foreach (SmokeTarget target in smoke.Targets)
            {
                if (!target.Required && !target.Covered && ArtillerySmoke.Screened(target.Cell, lord.Map))
                    target.Covered = true;
                if (target.ClaimedBy != null && (!ArtillerySmoke.Ready(target.ClaimedBy) || now > target.ClaimUntil ||
                    ArtillerySmoke.FindSmokeGrenade(target.ClaimedBy) == null &&
                    target.ClaimedBy.CurJobDef != ArtillerySmokeDefOf.RS_LayArtillerySmoke))
                    target.ClaimedBy = null;
            }
            smoke.EvaluateCarriers(lord, lord.Map, now);

            bool allScreened = smoke.Targets.All(target => target.IsDone(lord.Map));
            bool noCarriers = !smoke.Assignments.Any(assignment =>
                ArtillerySmoke.Ready(assignment.Pawn) && !assignment.Fighting &&
                assignment.Pawn.GetLord() == lord);
            if (allScreened || noCarriers || now >= smoke.Deadline || !smoke.HasActiveWork(lord.Map, now))
                ArtilleryDeployment.EndSmoke(Raid);
        }
    }

    public sealed class JobGiver_ArtillerySmoke : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            ArtillerySmoke screen = SmokeLoadout.ActiveScreen(pawn);
            if (screen == null || !screen.StartedScreen || !ArtillerySmoke.Ready(pawn) || !screen.HasCarrier(pawn))
                return null;
            TribalNativeGroup tribal = TribalNativeGroup.For(pawn);
            int now = GenTicks.TicksGame;
            if ((tribal != null || screen.IsLocal) && pawn.CurJobDef == ArtillerySmokeDefOf.RS_LayArtillerySmoke)
                return pawn.CurJob;
            ThingWithComps grenade = screen.FindSmokeWeapon(pawn);
            if (grenade == null || tribal == null && !screen.IsLocal && ArtillerySmoke.IsInCombat(pawn, now))
                return null;

            Map map = pawn.Map;
            float throwRange = screen.ThrowRange > 0f ? screen.ThrowRange : ArtillerySmoke.DefaultThrowRange;
            // Try throws or protected approaches in screen order without mutating state until job start.
            var candidates = screen.Targets.Where(candidate => candidate.CanClaim(pawn, map, now) &&
                    (tribal == null || candidate.Required || pawn != tribal.CenterCarrier))
                .OrderBy(candidate => candidate.Order)
                .ThenBy(candidate => tribal != null && !candidate.Required)
                .ThenBy(candidate => pawn.Position.DistanceToSquared(candidate.Stand)).ToList();
            SmokeTarget throwTarget = screen.CanThrowFrom(pawn.Position, map)
                ? candidates.FirstOrDefault(target => CanThrowNow(pawn.Position, target, map, throwRange)) : null;
            if (throwTarget != null)
            {
                Job throwSmoke = JobMaker.MakeJob(ArtillerySmokeDefOf.RS_LayArtillerySmoke, throwTarget.Cell, grenade);
                throwSmoke.maxNumStaticAttacks = 1;
                return throwSmoke;
            }

            int pathBudget = 3;
            foreach (SmokeTarget target in candidates)
            {
                if (pathBudget == 0) break;
                IntVec3 moveSpot = FindMoveSpot(pawn, target, map, throwRange, screen, ref pathBudget,
                    requireThrowPosition: tribal != null);
                if (!moveSpot.IsValid) continue;
                Job move = JobMaker.MakeJob(ArtillerySmokeDefOf.RS_ApproachSmokeThrow, moveSpot, 90, checkOverrideOnExpiry: true);
                move.targetC = target.Cell;
                return move;
            }

            if (tribal?.Waypoint is { IsValid: true } waypoint && waypoint != pawn.Position &&
                pawn.CanReach(waypoint, PathEndMode.OnCell, Danger.Deadly))
            {
                if (pawn.CurJob?.jobGiver == this && pawn.CurJobDef == JobDefOf.Goto && pawn.pather.Moving)
                    return pawn.CurJob;
                IntVec3 covered = CoveredApproach(pawn, waypoint, screen);
                if (covered.IsValid)
                    return JobMaker.MakeJob(JobDefOf.Goto, covered, 90, checkOverrideOnExpiry: true);
            }
            return screen.IsLocal ? null : WaitForSmoke();
        }

        private static Job WaitForSmoke() => JobMaker.MakeJob(JobDefOf.Wait, 20, checkOverrideOnExpiry: true);

        private static bool CanThrowNow(IntVec3 pos, SmokeTarget target, Map map, float throwRange) =>
            SmokeCombat.CanThrowNow(pos, target.Cell, map, throwRange);

        private static IntVec3 FindMoveSpot(Pawn pawn, SmokeTarget target, Map map, float throwRange,
            ArtillerySmoke screen, ref int pathBudget, bool requireThrowPosition)
        {
            // Reuse the actual native route; a safe destination alone does not make the walk safe.
            // Path and sight queries can refill GenAdjFast's shared result list.
            IntVec3 stand = target.Stand;
            IntVec3[] offsets = GenAdj.AdjacentCells;
            for (int i = -1; i < offsets.Length; i++)
            {
                IntVec3 cell = i < 0 ? stand : stand + offsets[i];
                if (cell == pawn.Position || !cell.InBounds(map) || !cell.Standable(map) ||
                    requireThrowPosition && !screen.CanAdvance(cell, map) ||
                    !CanThrowNow(cell, target, map, throwRange) ||
                    !map.pawnDestinationReservationManager.CanReserve(cell, pawn) ||
                    !pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly)) continue;
                pathBudget--;
                IntVec3 approach = CoveredApproach(pawn, cell, screen, target, throwRange);
                if (!requireThrowPosition || CanThrowNow(approach, target, map, throwRange)) return approach;
                if (pathBudget == 0) break;
            }
            return IntVec3.Invalid;
        }

        internal static IntVec3 CoveredApproach(Pawn pawn, IntVec3 destination, ArtillerySmoke screen,
            SmokeTarget target = null, float throwRange = 0f)
        {
            Map map = pawn.Map;
            using PawnPath path = map.pathFinder.FindPathNow(pawn.Position, destination, TraverseParms.For(pawn),
                null, PathEndMode.OnCell);
            if (path == null || !path.Found) return IntVec3.Invalid;

            bool smokeOnly = pawn.mindState?.duty?.def == ArtillerySmokeDefOf.RS_ArtilleryInfantryHold;
            IntVec3 last = IntVec3.Invalid;
            var nodes = path.NodesReversed;
            for (int i = nodes.Count - 1; i >= 0; i--)
            {
                IntVec3 node = nodes[i];
                if (node == pawn.Position) continue;
                if (smokeOnly ? !ArtillerySmoke.Screened(node, map) : !screen.CanAdvance(node, map)) break;
                last = node;
                if (target != null && CanThrowNow(node, target, map, throwRange)) return node;
            }
            return last;
        }

    }

    public sealed class JobGiver_ArtilleryMechSmokeCombat : JobGiver_AIFightEnemies
    {
        protected override Thing FindAttackTarget(Pawn pawn)
        {
            Verb verb = pawn.TryGetAttackVerb(null, allowManualCastWeapons: true);
            return verb == null || verb.IsMeleeAttack ? null : RaidStationaryCombat.BestShootTarget(pawn, verb);
        }

        protected override bool TryFindShootingPosition(Pawn pawn, out IntVec3 dest, Verb verbToUse = null)
        {
            dest = IntVec3.Invalid;
            return false;
        }

        protected override Job TryGiveJob(Pawn pawn)
        {
            if (!SiegeRaidMap.Enabled || !SiegeRoles.IsHumanRaidEscortMech(pawn) ||
                !RaidStationaryCombat.AppliesTo(pawn, pawn.Map.GetComponent<SiegeRaidMap>()))
                return null;
            if ((pawn.GetLord()?.LordJob as LordJob_Artillery)?.Raid?.InfantryHold is { } hold &&
                hold.UsesMechFlank(pawn, out IntVec3 flank) && flank.IsValid && flank != pawn.Position)
                return null;

            if (pawn.TryGetAttackVerb(null, allowManualCastWeapons: true) is { } verb &&
                verb.verbProps.ai_RangedAlawaysShootGroundBelowTarget)
            {
                Thing target = RaidStationaryCombat.BestShootTarget(pawn, verb);
                if (target == null) return null;
                Job shot = JobMaker.MakeJob(JobDefOf.UseVerbOnThingStatic, RaidStationaryCombat.CastTarget(verb, target));
                shot.verbToUse = verb;
                shot.preventFriendlyFire = true;
                return shot;
            }
            return base.TryGiveJob(pawn);
        }
    }

    public sealed class JobGiver_ArtillerySmokeFollowers : JobGiver_WanderNearDutyLocation
    {
        public JobGiver_ArtillerySmokeFollowers()
        {
            wanderRadius = Region.GridSize;
            wanderDestValidator = (pawn, cell, _) =>
                SmokeLoadout.ActiveScreen(pawn) is { } screen &&
                screen.CanAdvance(cell, pawn.Map) && screen.CanFollowInto(pawn, cell);
        }

        protected override Job TryGiveJob(Pawn pawn)
        {
            if (pawn == null) return null;
            ArtilleryRaid raid = pawn.GetLord()?.LordJob switch
            {
                LordJob_Artillery camp => camp.Raid,
                LordJob_ArtilleryMechEscort escort => escort.Raid,
                _ => null
            };
            if (raid?.CampLord is not Lord campLord || SmokeLoadout.ActiveScreen(pawn) is not { } screen)
                return null;
            bool mech = SiegeRoles.IsHumanRaidEscortMech(pawn);
            IntVec3 target = IntVec3.Invalid;
            if (raid.InfantryHold?.UsesMechFlank(pawn, out target) != true)
            {
                IntVec3 follow = screen.FollowCell(campLord, raid.Camp.Center, mech);
                target = follow.IsValid ? follow
                    : mech ? screen.Rally
                    : screen.LastFollowCell.IsValid && screen.CanAdvance(screen.LastFollowCell, pawn.Map) &&
                      screen.CanFollowInto(pawn, screen.LastFollowCell) ? screen.LastFollowCell
                    : raid.Camp.Center.IsValid && screen.CanAdvance(raid.Camp.Center, pawn.Map) &&
                      screen.CanFollowInto(pawn, raid.Camp.Center) ? raid.Camp.Center
                    : IntVec3.Invalid;
            }
            pawn.mindState.duty.focus = pawn.mindState.duty.focusSecond = target.IsValid ? target : pawn.Position;
            pawn.mindState.duty.wanderRadius = mech ? 4f : wanderRadius;
            if (target.IsValid && !pawn.Position.InHorDistOf(target, mech ? 0.9f : 14f) &&
                pawn.CanReach(target, PathEndMode.OnCell, Danger.Deadly))
            {
                Job move = JobMaker.MakeJob(JobDefOf.Goto, target, 60, checkOverrideOnExpiry: true);
                move.locomotionUrgency = PawnUtility.ResolveLocomotion(pawn, locomotionUrgencyOutsideRadius ?? locomotionUrgency);
                return move;
            }
            if (mech) return JobMaker.MakeJob(JobDefOf.Wait_Wander, 60, checkOverrideOnExpiry: true);
            Job wander = base.TryGiveJob(pawn);
            if (wander != null && wander.def == JobDefOf.Wait_Wander)
            {
                wander.expiryInterval = wander.expiryInterval > 0 ? Math.Min(wander.expiryInterval, 60) : 60;
                wander.checkOverrideOnExpire = true;
            }
            return wander;
        }
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), "TryEnterNextPathCell")]
    internal static class ArtillerySmokeMovement
    {
        private static bool Prefix(Pawn_PathFollower __instance, Pawn ___pawn)
        {
            if (___pawn.mindState?.duty?.def == ArtillerySmokeDefOf.RS_ArtilleryInfantryHold)
            {
                if (ArtillerySmoke.Screened(__instance.nextCell, ___pawn.Map)) return true;
                ___pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                return false;
            }
            ArtillerySmoke screen = SmokeLoadout.ActiveScreen(___pawn);
            if (screen == null) return true;
            if (screen.IsLocal && ___pawn.CurJobDef != ArtillerySmokeDefOf.RS_ApproachSmokeThrow) return true;
            bool tribal = TribalNativeGroup.For(___pawn) != null;
            if (!tribal && screen.AssignmentFor(___pawn) is { Fighting: true } ||
                screen.CanAdvance(__instance.nextCell, ___pawn.Map) &&
                (tribal || screen.HasCarrier(___pawn) || screen.CanFollowInto(___pawn, __instance.nextCell)))
                return true;

            // Native repathing and disappearing smoke can invalidate the route after the job was assigned.
            ___pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
            return false;
        }
    }
}
