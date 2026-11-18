using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ProjectZ;
using ProjectZ.Patches;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Integration
{
    // The existing Iso planner, with a separate walk-only connectivity graph.
    // Its site generation, A*, costs, shared plans and job construction stay native.
    internal sealed partial class IsoSiegePlanner
    {
        [ThreadStatic] internal static IsoSiegePlanner Active;
        internal readonly Map Map;
        private readonly RouteGraph routeGraph, independentGraph;
        internal RouteGraph Graph => availableLanding == null ? routeGraph : independentGraph;
        private readonly ClimbPlans placingPlans, existingPlans;
        private bool allowNewLadders = true;
        private readonly ConditionalWeakTable<Pawn, ClimbPlans> independentPlans = new();
        private Predicate<IntVec3> availableLanding;
        internal ClimbPlans Plans => availableLanding != null
            ? independentPlans.GetValue(Traveller, _ => new ClimbPlans(Map))
            : allowNewLadders ? placingPlans : existingPlans;
        internal Pawn Traveller;
        private readonly CacheImage cache = new CacheImage(), independentCache = new CacheImage();
        private readonly Func<int> epoch;
        private RouteGraph main;
        private int synchronizedEpoch = -1;

        internal IsoSiegePlanner(Map map, Func<int> epoch, Func<Thing, bool> canClearTop)
        {
            Map = map;
            this.epoch = epoch;
            routeGraph = new RouteGraph(map);
            independentGraph = new RouteGraph(map);
            placingPlans = new ClimbPlans(map);
            existingPlans = new ClimbPlans(map);
            this.canClearTop = canClearTop;
        }

        internal int Version => RouteGraph.For(Map).anyVersion + epoch();

        private void Synchronize(Pawn pawn)
        {
            if (Map.regionDirtyer.AnyDirty) Map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            main = RouteGraph.For(Map);
            if (synchronizedEpoch != epoch())
            {
                routeGraph.ClearReach2();
                independentGraph.ClearReach2();
                synchronizedEpoch = epoch();
            }
            Graph.anyVersion = main.anyVersion + epoch();
            Graph.stairsVersion = main.stairsVersion;
            Traveller = pawn;
        }

        // Escort's focus is the friendly breacher. Use ProjectZ's hostile target
        // selection for ladder work while preserving the native escort duty.
        private static LocalTargetInfo RaidFocus(PawnDuty duty) =>
            duty == null || duty.def == DutyDefOf.Escort ? LocalTargetInfo.Invalid : duty.focus;

        internal IntVec3 Destination(Pawn pawn, bool withoutClimbing) =>
            Destination(pawn, withoutClimbing, out _);

        internal IntVec3 Destination(Pawn pawn, bool withoutClimbing, out Thing targetThing)
        {
            targetThing = null;
            if (Map.regionDirtyer.AnyDirty) Map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            main = RouteGraph.For(Map);
            if (main == null || pawn == null || !pawn.Spawned) return IntVec3.Invalid;

            TraverseParms traversal = TraverseParms.For(pawn);
            LocalTargetInfo focus = RaidFocus(pawn.mindState?.duty);
            if (focus.IsValid)
            {
                bool focusReached = withoutClimbing
                    ? main.FloorReach(pawn.Position, focus, PathEndMode.Touch, traversal)
                    : Map.reachability.CanReach(pawn.Position, focus, PathEndMode.Touch, traversal);
                if (focusReached)
                {
                    targetThing = focus.Thing;
                    return focus.Cell;
                }
                return IntVec3.Invalid;
            }

            Thing target = PreferredAssaultTarget(pawn);
            if (target == null) return IntVec3.Invalid;
            bool reached = withoutClimbing
                ? main.FloorReach(pawn.Position, target, PathEndMode.Touch, traversal)
                : Map.reachability.CanReach(pawn.Position, target, PathEndMode.Touch, traversal);
            if (!reached) return IntVec3.Invalid;
            targetThing = target;
            return target.Position;
        }

        // Decide what to assault before asking whether we can walk there. A
        // reachable target elsewhere on the map cannot veto a nearby ladder.
        private Thing PreferredAssaultTarget(Pawn pawn)
        {
            Lord lord = pawn.GetLord();
            List<IAttackTarget> potential = Map.attackTargetsCache.GetPotentialTargetsFor(pawn);
            Thing best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < potential.Count; i++)
            {
                IAttackTarget candidate = potential[i];
                Thing thing = candidate?.Thing;
                if (thing == null || !thing.Spawned || thing.Map != Map ||
                    candidate.ThreatDisabled(pawn) || !AttackTargetFinder.IsAutoTargetable(candidate) ||
                    !pawn.HostileTo(thing) || lord?.LordJob?.ValidateAttackTarget(pawn, thing) == false)
                    continue;

                float dist = IsoFloor.Distance3D(pawn.Position, thing.Position, Map);
                if (dist >= bestDist) continue;
                best = thing;
                bestDist = dist;
            }
            return best;
        }

        internal IntVec3 ClimbEntryForBlockedAssault(Pawn pawn, Faction faction)
        {
            if (Map.regionDirtyer.AnyDirty) Map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            main = RouteGraph.For(Map);
            if (main == null || pawn == null || !pawn.Spawned) return IntVec3.Invalid;
            if (StairGrid.For(Map) is not { } grid) return IntVec3.Invalid;

            TraverseParms traversal = TraverseParms.For(pawn);
            TraverseParms digTraversal = TraverseParms.For(pawn, Danger.Deadly, TraverseMode.PassAllDestroyableThings);
            List<IAttackTarget> potential = Map.attackTargetsCache.GetPotentialTargetsFor(pawn);
            Lord lord = pawn.GetLord();

            IntVec3 bestTop = IntVec3.Invalid;
            float bestDist = float.MaxValue;
            foreach (VLink link in grid.links)
            {
                if (link.thing is not Building_SiegeLadder ladder || link.topBlocker ||
                    (faction != null && ladder.Faction != faction) || !StairGrid.UsableIn(link, Map))
                    continue;
                IntVec3 top = link.top;
                if (!top.InBounds(Map) || !top.StandableBy(Map, pawn)) continue;
                if (main.FloorReach(pawn.Position, top, PathEndMode.OnCell, traversal) ||
                    !Map.reachability.CanReach(pawn.Position, top, PathEndMode.OnCell, traversal))
                    continue;

                float dist = IsoFloor.Distance3D(pawn.Position, ladder.foot, Map);
                if (dist >= bestDist) continue;

                bool leadsToTarget = false;
                for (int i = 0; i < potential.Count; i++)
                {
                    IAttackTarget candidate = potential[i];
                    Thing thing = candidate?.Thing;
                    if (thing == null || !thing.Spawned || thing.Map != Map ||
                        candidate.ThreatDisabled(pawn) || !AttackTargetFinder.IsAutoTargetable(candidate) ||
                        !pawn.HostileTo(thing) || lord?.LordJob?.ValidateAttackTarget(pawn, thing) == false)
                        continue;
                    if (Map.reachability.CanReach(top, thing, PathEndMode.Touch, digTraversal))
                    {
                        leadsToTarget = true;
                        break;
                    }
                }
                if (!leadsToTarget) continue;

                bestTop = top;
                bestDist = dist;
            }
            return bestTop;
        }

        internal Building_SiegeLadder ActiveBlockedLadder(Pawn pawn, Faction faction)
        {
            if (StairGrid.For(Map) is not { } grid || pawn == null || !pawn.Spawned) return null;
            if (Map.regionDirtyer.AnyDirty) Map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            main = RouteGraph.For(Map);
            if (main == null) return null;

            TraverseParms traversal = TraverseParms.For(pawn);
            Building_SiegeLadder best = null;
            float bestDist = float.MaxValue;
            foreach (VLink link in grid.links)
            {
                if (link.thing is not Building_SiegeLadder ladder || !link.topBlocker || !ladder.Spawned ||
                    (faction != null && ladder.Faction != faction) || !canClearTop(ladder.top.GetEdifice(Map)))
                    continue;
                if (LadderPerch.For(Map)?.Holder(ladder) == null) continue;
                if (!main.FloorReach(pawn.Position, ladder.foot, PathEndMode.OnCell, traversal)) continue;
                float dist = IsoFloor.Distance3D(pawn.Position, ladder.foot, Map);
                if (dist < bestDist)
                {
                    best = ladder;
                    bestDist = dist;
                }
            }
            return best;
        }

        internal Job JobFor(Pawn pawn, bool placing = true, Predicate<IntVec3> available = null)
        {
            IsoSiegePlanner previous = Active;
            CacheImage native = new CacheImage();
            CacheImage scopedCache = available == null ? cache : independentCache;
            native.Capture();
            scopedCache.Apply();
            bool vertical = ClimbPlanner.NoVertical;
            bool previousPlacing = allowNewLadders;
            var previousLanding = availableLanding;
            PawnDuty duty = pawn.mindState?.duty;
            LocalTargetInfo previousFocus = duty?.focus ?? LocalTargetInfo.Invalid;
            allowNewLadders = placing;
            availableLanding = available;
            // Reuse native blocked-top ladders. Clearance bindings restrict
            // their targets and leave ceiling excavation disabled.
            ClimbPlanner.NoVertical = false;
            try
            {
                Synchronize(pawn);
                Active = this;
                if (duty != null)
                {
                    LocalTargetInfo focus = RaidFocus(duty);
                    if (!focus.IsValid && PreferredAssaultTarget(pawn) is Thing target) focus = target;
                    duty.focus = focus;
                }
                return RaidClimb.TryGive(pawn, false, false);
            }
            finally
            {
                if (duty != null) duty.focus = previousFocus;
                scopedCache.Capture();
                native.Apply();
                ClimbPlanner.NoVertical = vertical;
                allowNewLadders = previousPlacing;
                availableLanding = previousLanding;
                Active = previous;
            }
        }

        // Iso stores two geometry caches statically. Preserve their native
        // contents while the stricter planner uses its own entries. The readonly
        // dictionary objects and the search algorithm themselves are unchanged.
        private sealed class CacheImage
        {
            private static readonly FieldInfo[] Dictionaries = { Field("rims"), Field("compSites") };
            private static readonly FieldInfo[] Values = { Field("rimMap"), Field("compSitesMap"), Field("compSitesFor") };
            private readonly List<DictionaryEntry>[] entries = { new List<DictionaryEntry>(), new List<DictionaryEntry>() };
            private readonly object[] values = { null, null, -1 };
            private static FieldInfo Field(string name) => AccessTools.Field(typeof(ClimbPlanner), name);

            internal void Capture()
            {
                for (int i = 0; i < Dictionaries.Length; i++)
                {
                    entries[i].Clear();
                    foreach (DictionaryEntry pair in (IDictionary)Dictionaries[i].GetValue(null)) entries[i].Add(pair);
                }
                for (int i = 0; i < Values.Length; i++) values[i] = Values[i].GetValue(null);
            }

            internal void Apply()
            {
                for (int i = 0; i < Dictionaries.Length; i++)
                {
                    IDictionary dictionary = (IDictionary)Dictionaries[i].GetValue(null);
                    dictionary.Clear();
                    foreach (DictionaryEntry pair in entries[i]) dictionary.Add(pair.Key, pair.Value);
                }
                for (int i = 0; i < Values.Length; i++) Values[i].SetValue(null, values[i]);
            }
        }

        [HarmonyPatch(typeof(RouteGraph), nameof(RouteGraph.For))]
        private static class GraphBinding
        {
            private static void Postfix(Map map, ref RouteGraph __result)
            {
                if (Active != null && Active.Map == map) __result = Active.Graph;
            }
        }

        [HarmonyPatch(typeof(ClimbPlans), nameof(ClimbPlans.For))]
        private static class PlanBinding
        {
            private static void Postfix(Map map, ref ClimbPlans __result)
            {
                if (Active != null && Active.Map == map) __result = Active.Plans;
            }
        }

        [HarmonyPatch(typeof(RouteGraph), nameof(RouteGraph.LevelVersion))]
        private static class VersionBinding
        {
            private static bool Prefix(RouteGraph __instance, int L, ref int __result)
            {
                if (Active == null || __instance != Active.Graph) return true;
                __result = Active.main.LevelVersion(L) + Active.epoch();
                return false;
            }
        }

        [HarmonyPatch(typeof(RouteGraph), nameof(RouteGraph.Usable))]
        private static class IndependentLinks
        {
            private static void Postfix(RouteGraph __instance, int link, ref bool __result)
            {
                if (__result && Active?.availableLanding != null && __instance == Active.Graph &&
                    __instance.links[link].thing is Building_SiegeLadder)
                    __result = false;
            }
        }

        [HarmonyPatch(typeof(RouteGraph), "DigPassable")]
        private static class WalkableBinding
        {
            private static bool Prefix(RouteGraph __instance, IntVec3 c, PathGrid pg, ref bool __result)
            {
                if (Active == null || __instance != Active.Graph) return true;
                Building_Door door = c.GetEdifice(Active.Map) as Building_Door;
                __result = pg.WalkableFast(c) && (door == null || door.FreePassage || door.PawnCanOpen(Active.Traveller));
                return false;
            }
        }

        [HarmonyPatch]
        private static class TraversalBinding
        {
            private static IEnumerable<MethodBase> TargetMethods() => AccessTools.GetDeclaredMethods(typeof(TraverseParms))
                .Where(m => m.Name == nameof(TraverseParms.For));

            private static void Postfix(ref TraverseParms __result)
            {
                if (Active == null) return;
                __result.pawn = Active.Traveller;
                __result.mode = TraverseMode.ByPawn;
                __result.canBashDoors = false;
                __result.canBashFences = false;
            }
        }
    }
}
