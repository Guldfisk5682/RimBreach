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
        public static JobDef RS_LayArtillerySmoke = null;
        public static ThingDef CE_Weapon_GrenadeSmoke = null;

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
        private const int GrenadesPerCarrier = 5;
        internal const float DefaultThrowRange = 8.8f;

        internal IntVec3 Rally = IntVec3.Invalid;
        internal int Deadline;
        internal int InitialPawns;
        internal int LastThrowTick;
        internal float ThrowRange = DefaultThrowRange;
        internal bool StartedScreen;
        internal List<SmokeAssignment> Assignments = new();
        internal List<SmokeTarget> Targets = new();
        private List<IntVec3> approachRoute = new();
        private IntVec3 followCell = IntVec3.Invalid;
        private int nextFollowUpdate;
        private Pawn[] followingBehind = Array.Empty<Pawn>();

        internal bool CanFollowInto(Pawn follower, IntVec3 cell) =>
            followingBehind.All(carrier => !carrier.Spawned ||
                IsoFloor.Distance3D(carrier.Position, cell, follower.Map) >= 15f ||
                IsoFloor.Distance3D(carrier.Position, cell, follower.Map) >=
                    IsoFloor.Distance3D(carrier.Position, follower.Position, follower.Map));

        internal IntVec3 FollowCell(Lord lord, IntVec3 campCenter)
        {
            int now = GenTicks.TicksGame;
            if (now >= nextFollowUpdate)
            {
                nextFollowUpdate = now + 45;
                UpdateFollowCells(lord, campCenter);
            }
            return followCell;
        }

        private void UpdateFollowCells(Lord lord, IntVec3 campCenter)
        {
            followCell = IntVec3.Invalid;

            Pawn[] carriers = followingBehind = Assignments.Select(assignment => assignment.Pawn)
                .Where(pawn => Ready(pawn) && HasCarrier(pawn) && pawn.GetLord() == lord &&
                    (FindSmokeGrenade(pawn) != null || pawn.CurJobDef == ArtillerySmokeDefOf.RS_LayArtillerySmoke))
                .ToArray();
            if (carriers.Length == 0) return;

            Map map = lord.Map;
            if (approachRoute.Count == 0)
            {
                using PawnPath path = map.pathFinder.FindPathNow(Rally, campCenter, TraverseParms.For(carriers[0]),
                    null, PathEndMode.OnCell);
                if (path == null || !path.Found) return;
                approachRoute.AddRange(path.NodesReversed.AsEnumerable().Reverse());
            }

            // Use the back of the smoke team so the main force cannot overtake a carrier still throwing.
            int rear = carriers.Min(pawn => NearestRouteIndex(pawn.Position, map));
            for (int i = rear - 1; i >= 0; i--)
            {
                IntVec3 cell = approachRoute[i];
                if (carriers.Any(pawn => IsoFloor.Distance3D(pawn.Position, cell, map) < 17f) ||
                    !CanAdvance(cell, map)) continue;
                followCell = cell;
                return;
            }
        }

        private int NearestRouteIndex(IntVec3 position, Map map)
        {
            int closest = 0;
            float distance = float.MaxValue;
            for (int i = 0; i < approachRoute.Count; i++)
            {
                float candidate = IsoFloor.Distance3D(position, approachRoute[i], map);
                if (candidate >= distance) continue;
                distance = candidate;
                closest = i;
            }
            return closest;
        }
        private Verb[] currentThreats;
        private int threatWindow = -1;
        private readonly Dictionary<IntVec3, bool> exposure = new();

        internal bool CanAdvance(IntVec3 cell, Map map)
        {
            if (!cell.InBounds(map)) return false;
            if (Screened(cell, map)) return true;
            int window = GenTicks.TicksGame / 15;
            if (window != threatWindow)
            {
                threatWindow = window;
                currentThreats = CollectThreats(map);
                exposure.Clear();
            }
            if (!exposure.TryGetValue(cell, out bool exposed))
                exposure[cell] = exposed = IsExposed(cell, map, currentThreats);
            return !exposed;
        }

        internal static bool Eligible(ArtilleryRaid raid) => raid.Faction.def.humanlikeFaction && raid.RaidPoints >= 2000f;

        internal static bool Ready(Pawn pawn) => pawn is { Spawned: true, Downed: false, Dead: false, InMentalState: false } &&
            pawn.RaceProps.Humanlike &&
            !pawn.WorkTagIsDisabled(WorkTags.Violent) && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) &&
            pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving);

        internal static ArtillerySmoke Plan(ArtilleryRaid raid, out string outcome)
        {
            Lord lord = raid.CampLord;
            Map map = lord.Map;
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
            Verb[] threats = CollectThreats(map);
            bool Exposed(IntVec3 cell) => IsExposed(cell, map, threats);

            outcome = "no sheltered rally within 20 cells";
            IntVec3 rally = FindRally(entry, map, traverse, Exposed);
            if (!rally.IsValid) return null;
            outcome = "no route from rally to camp";
            using PawnPath route = map.pathFinder.FindPathNow(rally, raid.Camp.Center, traverse, null, PathEndMode.OnCell);
            if (route == null || !route.Found) return null;

            var nodes = route.NodesReversed.AsEnumerable().Reverse().ToList();
            float throwRange = SafeThrowRange();
            List<SmokeTarget> targets = BuildTargets(
                nodes, rally, raid.Camp, map, traverse, throwRange, threats, eligible.Count, Exposed);
            outcome = "no viable smoke targets";
            if (targets.Count == 0) return null;

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
                ThrowRange = throwRange,
                StartedScreen = true,
                Targets = targets,
                approachRoute = nodes
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

        private static Verb[] CollectThreats(Map map) =>
            map.listerBuildings.allBuildingsColonistCombatTargets.OfType<IAttackTargetSearcher>()
                .Concat(map.mapPawns.FreeColonistsSpawned.OfType<IAttackTargetSearcher>())
                .Select(shooter => shooter is Building_Turret turret ? turret.AttackVerb : shooter.CurrentEffectiveVerb)
                .Where(verb => verb != null && !verb.verbProps.IsMeleeAttack &&
                    (verb is Verb_LaunchProjectileCE ce ? ce.Projectile : verb.GetProjectile())
                        ?.projectile?.flyOverhead != true)
                .ToArray();

        private static bool IsExposed(IntVec3 cell, Map map, Verb[] threats) =>
            threats.Any(verb => verb.Caster.Spawned &&
                IsoFloor.RangeDistance(verb.Caster.Position, cell, map) <= verb.EffectiveRange &&
                verb.CanHitTarget(cell));

        private static float SafeThrowRange()
        {
            float range = ArtillerySmokeDefOf.CE_Weapon_GrenadeSmoke?.Verbs?
                .FirstOrDefault(v => !v.IsMeleeAttack)?.range ?? 10f;
            return Math.Max(4f, range - 1.2f);
        }

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

        private static List<SmokeTarget> BuildTargets(List<IntVec3> nodes, IntVec3 rally, CampPlan camp, Map map,
            TraverseParms traverse, float throwRange, Verb[] threats, int eligibleCount, Predicate<IntVec3> exposed)
        {
            var targets = new List<SmokeTarget>();
            AssessCampThreats(camp, rally, map, threats, out bool campExposed, out float threatAngle);

            int maxApproach = Math.Clamp(eligibleCount, 2, MaxApproachStations);
            AddApproachTargets(targets, nodes, rally, camp, campExposed, threatAngle, maxApproach, map, traverse, throwRange, exposed);

            if (campExposed)
                AddCampScreen(targets, rally, camp, threatAngle, map, traverse, throwRange, exposed);

            return targets;
        }

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
                float range = verb.EffectiveRange;
                bool hitsCamp = false;
                for (int i = 0; i < probes.Count; i++)
                {
                    IntVec3 probe = probes[i];
                    if (IsoFloor.RangeDistance(caster.Position, probe, map) <= range && verb.CanHitTarget(probe))
                    {
                        hitsCamp = true;
                        break;
                    }
                }
                if (!hitsCamp) continue;
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
                if (gun.Cell.InBounds(map) && IsoFloor.SameFloor(gun.Cell, camp.Center, map))
                    probes.Add(gun.Cell);

            for (int i = 0; i < 8; i++)
            {
                float angle = i * (Mathf.PI * 0.25f);
                IntVec3 cell = OffsetCell(camp.Center, angle, 8f);
                if (cell.InBounds(map) && IsoFloor.SameFloor(cell, camp.Center, map) && !cell.Filled(map))
                    probes.Add(cell);
            }
            return probes;
        }

        private static void AddApproachTargets(List<SmokeTarget> targets, List<IntVec3> nodes, IntVec3 rally,
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
            if (firstExposed < 0) return;

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
                if (approachEnd < start) return;
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
                    Vector3 side = ComputeApproachSide(nodes, idx, rally);
                    int preferredSign = Vector3.Dot(side, threatDir) >= 0f ? 1 : -1;
                    IntVec3 flank = FindFlankCell(map, spine, side, preferredSign, exposed);
                    if (!flank.IsValid) flank = FindFlankCell(map, spine, side, -preferredSign, exposed);
                    if (flank.IsValid)
                        TryAddTarget(targets, flank, spine, order, false, anchor, rally, map, traverse, throwRange, exposed);
                }
            }
        }

        private static Vector3 ComputeApproachSide(List<IntVec3> nodes, int idx, IntVec3 rally)
        {
            IntVec3 prev = nodes[Math.Max(0, idx - 3)];
            IntVec3 next = nodes[Math.Min(nodes.Count - 1, idx + 3)];
            Vector3 tangent = (next - prev).ToVector3();
            tangent.y = 0f;
            if (tangent.sqrMagnitude < 0.01f) tangent = (nodes[nodes.Count - 1] - rally).ToVector3();
            tangent.y = 0f;
            return tangent.sqrMagnitude >= 0.01f
                ? new Vector3(-tangent.z, 0f, tangent.x).normalized
                : new Vector3(1f, 0f, 0f);
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
                IntVec3 candidate = spine + new IntVec3(
                    Mathf.RoundToInt(side.x * sign * offset), 0, Mathf.RoundToInt(side.z * sign * offset));
                if (!candidate.InBounds(map) || !IsoFloor.SameFloor(candidate, spine, map) ||
                    candidate.Filled(map) || !candidate.Walkable(map) ||
                    !GenSight.LineOfSight(spine, candidate, map, skipFirstCell: true)) continue;
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

        private static ThingWithComps GiveSmoke(Pawn pawn, int count)
        {
            var inventory = pawn.TryGetComp<CompInventory>();
            var grenade = (ThingWithComps)ThingMaker.MakeThing(ArtillerySmokeDefOf.CE_Weapon_GrenadeSmoke);
            grenade.stackCount = count;
            if (inventory.container.TryAdd(grenade, canMergeWithExistingStacks: false)) return grenade;
            grenade.Destroy();
            return null;
        }

        private static void DiscardGrantedSmoke(Dictionary<Pawn, ThingWithComps> grenades)
        {
            foreach (KeyValuePair<Pawn, ThingWithComps> pair in grenades)
                if (pair.Value is { Destroyed: false }) pair.Value.Destroy();
        }

        internal static ThingWithComps FindSmokeGrenade(Pawn pawn) =>
            pawn?.equipment?.Primary is { } equipped && equipped.def == ArtillerySmokeDefOf.CE_Weapon_GrenadeSmoke
                ? equipped : pawn?.inventory?.innerContainer?.FirstOrDefault(t => t.def == ArtillerySmokeDefOf.CE_Weapon_GrenadeSmoke && !t.Destroyed) as ThingWithComps;

        internal bool HasActiveWork(Map map, int now)
        {
            if (LastThrowTick > 0 && now < LastThrowTick + 140) return true;
            bool unscreened = Targets.Any(t => !t.IsDone(map));
            if (!unscreened) return false;
            return Assignments.Any(a => Ready(a.Pawn) && !a.Fighting && FindSmokeGrenade(a.Pawn) != null);
        }

        internal bool CampCovered(ArtilleryRaid raid, Map map)
        {
            var required = Targets.Where(target => target.Required).ToList();
            if (required.Count == 0) return Targets.All(target => target.IsDone(map));
            return required.All(target => target.IsDone(map)) &&
                raid.Camp.Guns.All(placement => PlacementCovered(placement, map, currentThreats ??= CollectThreats(map)));
        }

        private static bool PlacementCovered(ArtilleryPlacement placement, Map map, Verb[] threats)
        {
            bool Covered(IntVec3 cell) => cell.InBounds(map) &&
                (Screened(cell, map) || !IsExposed(cell, map, threats));
            return GenAdj.OccupiedRect(placement.Cell, placement.Rotation, placement.Def.Size).Cells.All(Covered) &&
                (!placement.NeedsOperator || Covered(ThingUtility.InteractionCellWhenAt(
                    placement.Def, placement.Cell, placement.Rotation, map)));
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

        internal void RestoreWeapons()
        {
            foreach (SmokeAssignment assignment in Assignments)
            {
                Pawn pawn = assignment.Pawn;
                if (pawn?.CurJobDef == ArtillerySmokeDefOf.RS_LayArtillerySmoke)
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                SmokeLoadout.Restore(assignment);
            }
        }

        private static float Length(List<IntVec3> nodes, Map map)
        {
            float total = 0;
            for (int i = 1; i < nodes.Count; i++) total += IsoFloor.Distance3D(nodes[i - 1], nodes[i], map);
            return total;
        }

        internal static bool Screened(IntVec3 cell, Map map) => cell.AnyGas(map, GasType.BlindSmoke);

        public void ExposeData()
        {
            Scribe_Values.Look(ref Rally, "rally", IntVec3.Invalid);
            Scribe_Values.Look(ref Deadline, "deadline");
            Scribe_Values.Look(ref InitialPawns, "initialPawns");
            Scribe_Values.Look(ref LastThrowTick, "lastThrowTick");
            Scribe_Values.Look(ref ThrowRange, "throwRange", DefaultThrowRange);
            Scribe_Values.Look(ref StartedScreen, "startedScreen");
            Scribe_Collections.Look(ref Assignments, "assignments", LookMode.Deep);
            Scribe_Collections.Look(ref Targets, "targets", LookMode.Deep);
            Scribe_Collections.Look(ref approachRoute, "approachRoute", LookMode.Value);
            Assignments ??= new();
            Targets ??= new();
            approachRoute ??= new();
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
            if (now % 15 != 0 || Raid.Smoke is not ArtillerySmoke smoke) return;
            foreach (SmokeTarget target in smoke.Targets)
            {
                if (!target.Required && !target.Covered && ArtillerySmoke.Screened(target.Cell, lord.Map))
                    target.Covered = true;
                if (target.ClaimedBy != null && (!ArtillerySmoke.Ready(target.ClaimedBy) || now > target.ClaimUntil ||
                    ArtillerySmoke.FindSmokeGrenade(target.ClaimedBy) == null &&
                    target.ClaimedBy.CurJobDef != ArtillerySmokeDefOf.RS_LayArtillerySmoke))
                    target.ClaimedBy = null;
            }

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
            if (pawn?.RaceProps?.Humanlike != true) return null;
            if (pawn.GetLord()?.LordJob is not LordJob_Artillery job || (job.Raid.Smoke ?? job.Raid.OptionalSmoke) is not ArtillerySmoke screen)
                return null;
            if (!screen.StartedScreen || !ArtillerySmoke.Ready(pawn)) return null;
            if (screen.AssignmentFor(pawn) is not { Fighting: false } assignment) return null;
            ThingWithComps grenade = ArtillerySmoke.FindSmokeGrenade(pawn);
            Thing enemy = pawn.mindState.enemyTarget;
            bool fighting = enemy is { Spawned: true } && pawn.HostileTo(enemy) ||
                pawn.mindState.lastHarmTick >= GenTicks.TicksGame - 90;
            if (grenade == null || fighting)
            {
                // An override query can run while the smoke driver is still casting.
                if (pawn.CurJobDef == ArtillerySmokeDefOf.RS_LayArtillerySmoke) return null;
                assignment.Fighting = true;
                SmokeLoadout.Restore(assignment);
                return null;
            }

            int now = GenTicks.TicksGame;
            Map map = pawn.Map;
            float throwRange = screen.ThrowRange > 0f ? screen.ThrowRange : ArtillerySmoke.DefaultThrowRange;
            // Try throws or protected approaches in screen order.
            var candidates = screen.Targets.Where(candidate => !candidate.IsDone(map) && now >= candidate.RetryAt &&
                (candidate.ClaimedBy == null || candidate.ClaimedBy == pawn || now >= candidate.ClaimUntil))
                .OrderBy(candidate => candidate.Order)
                .ThenBy(candidate => pawn.Position.DistanceToSquared(candidate.Stand)).ToList();
            SmokeTarget throwTarget = screen.CanAdvance(pawn.Position, map)
                ? candidates.FirstOrDefault(target => CanThrowNow(pawn.Position, target, map, throwRange)) : null;
            if (throwTarget != null)
            {
                Claim(throwTarget, pawn, now);
                throwTarget.RetryAt = now + 100;
                screen.LastThrowTick = now;
                Job throwSmoke = JobMaker.MakeJob(ArtillerySmokeDefOf.RS_LayArtillerySmoke, throwTarget.Cell, grenade);
                throwSmoke.maxNumStaticAttacks = 1;
                return throwSmoke;
            }

            int pathBudget = 3;
            foreach (SmokeTarget target in candidates)
            {
                if (pathBudget == 0) break;
                IntVec3 moveSpot = FindMoveSpot(pawn, target, map, throwRange, screen, ref pathBudget);
                if (!moveSpot.IsValid) continue;
                Claim(target, pawn, now);
                return JobMaker.MakeJob(JobDefOf.Goto, moveSpot, 90, checkOverrideOnExpiry: true);
            }

            return WaitForSmoke();

            void Claim(SmokeTarget target, Pawn carrier, int tick)
            {
                foreach (SmokeTarget previous in screen.Targets)
                    if (previous.ClaimedBy == carrier) previous.ClaimedBy = null;
                target.ClaimedBy = carrier;
                target.ClaimUntil = tick + 100;
            }
        }

        private static Job WaitForSmoke() => JobMaker.MakeJob(JobDefOf.Wait, 20, checkOverrideOnExpiry: true);

        private static bool CanThrowNow(IntVec3 pos, SmokeTarget target, Map map, float throwRange)
        {
            float dist = IsoFloor.RangeDistance(pos, target.Cell, map);
            return IsoFloor.SameFloor(pos, target.Cell, map) &&
                dist >= 2f && dist <= throwRange &&
                GenSight.LineOfSight(pos, target.Cell, map, skipFirstCell: true);
        }

        private static IntVec3 FindMoveSpot(Pawn pawn, SmokeTarget target, Map map, float throwRange, ArtillerySmoke screen, ref int pathBudget)
        {
            // Reuse the actual native route; a safe destination alone does not make the walk safe.
            foreach (IntVec3 cell in GenAdjFast.AdjacentCells8Way(target.Stand).Prepend(target.Stand))
            {
                if (cell == pawn.Position || !cell.InBounds(map) || !cell.Standable(map) ||
                    !CanThrowNow(cell, target, map, throwRange) ||
                    !map.pawnDestinationReservationManager.CanReserve(cell, pawn) ||
                    !pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly)) continue;
                pathBudget--;
                return CoveredApproach(pawn, cell, screen, target, throwRange);
            }
            return IntVec3.Invalid;
        }

        private static IntVec3 CoveredApproach(Pawn pawn, IntVec3 destination, ArtillerySmoke screen,
            SmokeTarget target = null, float throwRange = 0f)
        {
            Map map = pawn.Map;
            using PawnPath path = map.pathFinder.FindPathNow(pawn.Position, destination, TraverseParms.For(pawn),
                null, PathEndMode.OnCell);
            if (path == null || !path.Found) return IntVec3.Invalid;

            IntVec3 last = IntVec3.Invalid;
            var nodes = path.NodesReversed;
            for (int i = nodes.Count - 1; i >= 0; i--)
            {
                IntVec3 node = nodes[i];
                if (node == pawn.Position) continue;
                if (!screen.CanAdvance(node, map)) break;
                last = node;
                if (target != null && CanThrowNow(node, target, map, throwRange)) return node;
            }
            return last;
        }

    }

    public sealed class JobGiver_ArtillerySmokeFollowers : JobGiver_WanderNearDutyLocation
    {
        public JobGiver_ArtillerySmokeFollowers()
        {
            wanderRadius = Region.GridSize;
            wanderDestValidator = (pawn, _, cell) =>
                pawn.GetLord()?.LordJob is LordJob_Artillery { Raid.Smoke: { } screen } &&
                screen.CanAdvance(cell, pawn.Map) && screen.CanFollowInto(pawn, cell);
        }

        protected override Job TryGiveJob(Pawn pawn)
        {
            if (pawn?.RaceProps?.Humanlike != true) return null;
            Lord lord = pawn.GetLord();
            if (lord?.LordJob is not LordJob_Artillery job || (job.Raid.Smoke ?? job.Raid.OptionalSmoke) is not ArtillerySmoke screen) return null;
            IntVec3 follow = screen.FollowCell(lord, job.Raid.Camp.Center);
            pawn.mindState.duty.focus = pawn.mindState.duty.focusSecond = follow.IsValid ? follow : pawn.Position;
            pawn.mindState.duty.wanderRadius = wanderRadius;
            if (follow.IsValid && !pawn.Position.InHorDistOf(follow, 14f) &&
                pawn.CanReach(follow, PathEndMode.OnCell, Danger.Deadly))
                return JobMaker.MakeJob(JobDefOf.Goto, follow, 60, checkOverrideOnExpiry: true);
            return base.TryGiveJob(pawn);
        }
    }

    [HarmonyPatch(typeof(JobGiver_TakeAndEquip), "TryGiveJob")]
    internal static class ArtillerySmokeEquipmentOwnership
    {
        private static bool Prefix(Pawn pawn) =>
            !ArtilleryWeaponSwitch.InProgress(pawn) && !SmokeLoadout.IsSmokeLocked(pawn);
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), "TryEnterNextPathCell")]
    internal static class ArtillerySmokeMovement
    {
        private static bool Prefix(Pawn_PathFollower __instance, Pawn ___pawn)
        {
            if (___pawn.mindState.duty?.def != ArtillerySmokeDefOf.RS_ArtillerySmoke ||
                ___pawn.GetLord()?.LordJob is not LordJob_Artillery job ||
                (job.Raid.Smoke ?? job.Raid.OptionalSmoke) is not ArtillerySmoke screen ||
                screen.AssignmentFor(___pawn) is { Fighting: true } ||
                screen.CanAdvance(__instance.nextCell, ___pawn.Map) &&
                (screen.HasCarrier(___pawn) || screen.CanFollowInto(___pawn, __instance.nextCell))) return true;

            // Native repathing and disappearing smoke can invalidate the route after the job was assigned.
            ___pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
            return false;
        }
    }
}
