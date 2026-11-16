using System;
using System.Collections.Generic;
using System.Linq;
using RimSiege.Integration;
using RimSiege.Raids.Artillery;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Raids
{
    internal sealed class TribalSmokeState : IExposable
    {
        private const int BaseRation = 5;
        private const float MinimumSmokePoints = 2000f;
        private const int AbandonLossPercent = 15;
        internal List<TribalNativeGroup> Groups = new();
        internal HashSet<int> ViolentLosses = new();
        internal int InitialRaidSize;
        internal int RetreatSeed;
        internal bool ReachedDefenses;
        private bool screenAbandoned;

        internal static void TryInitialize(SiegeParty party, IncidentParms parms, List<Pawn> pawns, float points)
        {
            ThingDef bomb = ArtillerySmokeDefOf.CE_Weapon_SmokeBomb;
            if (party == null || bomb == null || points < MinimumSmokePoints || !SiegeRaidMap.IsTribalHumanlike(parms.faction) ||
                !parms.faction.HostileTo(Faction.OfPlayer) || !parms.attackTargets.NullOrEmpty() ||
                parms.controllerPawn?.Faction == Faction.OfPlayer ||
                parms.raidStrategy.Worker is RaidStrategyWorker_ShamblerAssault ||
                parms.raidStrategy.Worker is not (RaidStrategyWorker_ImmediateAttack or RaidStrategyWorker_ImmediateAttackSmart or
                    RaidStrategyWorker_StageThenAttack or RaidStrategyWorker_ImmediateAttackSappers or RaidStrategyWorker_ImmediateAttackBreaching) ||
                !Rand.Chance(RimSiegeMod.Settings.ArtilleryChance / 10f))
                return;

            var state = new TribalSmokeState { InitialRaidSize = pawns.Count };
            foreach (var entry in pawns.Where(p => p.GetLord() != null).GroupBy(p => p.GetLord()))
            {
                var group = new TribalNativeGroup { Lord = entry.Key };
                foreach (Pawn pawn in entry)
                {
                    if (!pawn.RaceProps.Humanlike) continue;
                    group.Members.Add(pawn);
                    SmokeCombat.GiveSmoke(pawn, bomb, BaseRation - SmokeCombat.CountSmokeWeapon(pawn, bomb));
                }
                if (group.Members.Count > 0) state.Groups.Add(group);
            }
            if (state.Groups.Count > 0)
            {
                state.AssignCarrierQuotas();
                state.RetreatSeed = state.Groups[0].Lord.loadID;
                state.ReachedDefenses = pawns.Any(ArtilleryRetreat.AtPlayerDefenses);
                party.TribalSmoke = state;
            }
        }

        private void AssignCarrierQuotas()
        {
            int quota = Math.Max(2, (InitialRaidSize + 9) / 10);
            foreach (TribalNativeGroup group in Groups.OrderByDescending(group => group.Members.Count))
            {
                if (quota == 0) return;
                group.CarrierQuota = 1;
                quota--;
            }
            while (quota > 0)
            {
                TribalNativeGroup group = Groups.Where(group => group.CarrierQuota < group.Members.Count)
                    .OrderByDescending(group => group.Members.Count / (float)(group.CarrierQuota + 1))
                    .FirstOrDefault();
                if (group == null) return;
                group.CarrierQuota++;
                quota--;
            }
        }

        internal bool Owns(Lord lord) => lord != null && Groups.Any(group => group.Lord == lord);

        internal ArtillerySmoke ScreenForAssignedPawn(Pawn pawn) =>
            Groups.Select(group => group.Screen).FirstOrDefault(screen => screen?.AssignmentFor(pawn) != null);

        internal TribalNativeGroup ActiveGroup(Pawn pawn)
        {
            Lord lord = pawn.GetLord();
            return SiegeRaidMap.Enabled && TribalNativeGroup.IsAssaulting(lord)
                ? Groups.Find(group => group.Lord == lord && group.Screen != null)
                : null;
        }

        internal void Tick(int now)
        {
            if (screenAbandoned) return;
            if (InitialRaidSize > 0 && ViolentLosses.Count * 100 >= InitialRaidSize * AbandonLossPercent)
            {
                screenAbandoned = true;
                Stop();
                return;
            }
            foreach (TribalNativeGroup group in Groups)
                group.Tick(now);
        }

        internal void Stop()
        {
            foreach (TribalNativeGroup group in Groups)
                group.End();
        }

        public void ExposeData()
        {
            Scribe_Collections.Look(ref Groups, "groups", LookMode.Deep);
            Scribe_Collections.Look(ref ViolentLosses, "violentLossIds", LookMode.Value);
            Scribe_Values.Look(ref InitialRaidSize, "initialRaidSize");
            Scribe_Values.Look(ref RetreatSeed, "retreatSeed");
            Scribe_Values.Look(ref ReachedDefenses, "reachedDefenses");
            Scribe_Values.Look(ref screenAbandoned, "screenAbandoned");
            Groups ??= new();
            ViolentLosses ??= new();
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (InitialRaidSize == 0) InitialRaidSize = Groups.Sum(group => group.Members.Count);
                if (Groups.All(group => group.CarrierQuota == 0)) AssignCarrierQuotas();
            }
        }
    }

    internal sealed class TribalNativeGroup : IExposable
    {
        private const int ReviewInterval = 15;
        private const int LookAhead = 8;
        private const int FollowerSpacing = 4;
        private const int MaxRation = 20;

        internal Lord Lord;
        internal List<Pawn> Members = new();
        internal ArtillerySmoke Screen;
        internal int CarrierQuota;
        private bool started;
        private IntVec3 goal = IntVec3.Invalid;
        private IntVec3 blocker = IntVec3.Invalid;
        private int progress;
        private Pawn lead;
        private IntVec3 leftFollow = IntVec3.Invalid;
        private IntVec3 rightFollow = IntVec3.Invalid;
        internal IntVec3 Waypoint = IntVec3.Invalid;
        internal IntVec3 FollowWaypoint = IntVec3.Invalid;
        internal Pawn CenterCarrier => lead;

        internal enum FrontResult { Advancing, ReachedGoal, ScreenedBlocker, RouteLost }

        internal static bool IsAssaulting(Lord lord) => lord?.CurLordToil is
            LordToil_AssaultColony or LordToil_AssaultColonySappers or LordToil_AssaultColonyBreaching;

        internal static TribalNativeGroup For(Pawn pawn) =>
            pawn.MapHeld?.GetComponent<SiegeRaidMap>()?.Party(pawn)?.TribalSmoke?.ActiveGroup(pawn);

        internal bool Includes(Pawn pawn) => Screen != null && pawn.GetLord() == Lord;

        internal void Tick(int now)
        {
            if (CarrierQuota == 0 || started && Screen == null) return;
            if (!SiegeRaidMap.Enabled || !IsAssaulting(Lord))
            {
                End();
                return;
            }

            bool opening = !started;
            if (opening)
            {
                if (Members.Any(pawn => pawn != null && !pawn.Dead && !pawn.Spawned && pawn.SpawnedOrAnyParentSpawned))
                    return;
                started = true;
                Start();
            }
            if (Screen == null || !opening && now % ReviewInterval != 0 &&
                (Screen.NextEvalTick == 0 || now < Screen.NextEvalTick))
                return;

            Map map = Lord.Map;
            Screen.EvaluateCarriers(Lord, map, now, Includes, keepScreenUnderFire: true);
            Promote();
            // Keep the route anchored to the foremost carrier. A reserve joining behind does not rewind it.
            lead = Screen.Assignments.Where(a => !a.Fighting)
                .Select(a => a.Pawn)
                .OrderBy(p => IsoFloor.Distance3D(p.Position,
                    Screen.ApproachRoute[Math.Min(progress + LookAhead, Screen.ApproachRoute.Count - 1)], map))
                .FirstOrDefault();
            if (lead == null)
            {
                if (Screen.InFlightUntil <= now) End();
                return;
            }
            if (UpdateFront(map) != FrontResult.Advancing)
            {
                End();
                return;
            }

            if (opening)
            {
                foreach (Pawn member in Members)
                    if (ArtillerySmoke.Ready(member) && Includes(member)) member.jobs.CheckForJobOverride();
            }
            else
            {
                foreach (SmokeAssignment assignment in Screen.Assignments)
                    if (!assignment.Fighting && (assignment.Pawn.CurJob == null ||
                        assignment.Pawn.CurJob.jobGiver is JobGiver_TribalSmokeFollowers))
                        assignment.Pawn.jobs.CheckForJobOverride();
            }
        }

        private void Start()
        {
            Pawn anchor = Members.FirstOrDefault(ArtillerySmoke.Ready);
            if (anchor == null || TribalSmokeRoutes.Plan(Lord, anchor) is not { } route) return;
            var screen = new ArtillerySmoke
            {
                SmokeWeapon = ArtillerySmokeDefOf.CE_Weapon_SmokeBomb,
                TurretsOnly = true,
                StartedScreen = true,
                ThrowRange = SmokeCombat.ThrowRange(ArtillerySmokeDefOf.CE_Weapon_SmokeBomb, margin: 0.8f)
            };
            InstallRoute(screen, route);
            int exposed = screen.ApproachRoute.Count(cell => !screen.CanAdvance(cell, Lord.Map));
            if (exposed == 0) return;
            Screen = screen;
            Promote();
            // Supplied once at the start; replacements use their own reserve.
            ThingDef bomb = screen.SmokeWeapon;
            int desired = Math.Clamp((exposed + 2) / 3, 5, MaxRation);
            foreach (SmokeAssignment carrier in screen.Assignments)
                SmokeCombat.GiveSmoke(carrier.Pawn, bomb, desired - SmokeCombat.CountSmokeWeapon(carrier.Pawn, bomb));
        }

        private void InstallRoute(ArtillerySmoke screen, TribalSmokeRoutes.PlannedRoute route)
        {
            screen.ApproachRoute = route.Nodes;
            screen.Rally = route.Nodes[0];
            screen.Targets.Clear();
            goal = route.Goal;
            blocker = route.BlockerCell;
            progress = 0;
            Waypoint = IntVec3.Invalid;
            FollowWaypoint = IntVec3.Invalid;
            leftFollow = rightFollow = IntVec3.Invalid;
        }

        private void Promote()
        {
            int vacancies = CarrierQuota - Screen.Assignments.Count(assignment => !assignment.Fighting);
            if (vacancies <= 0) return;

            IntVec3 front = Screen.ApproachRoute[Math.Clamp(progress, 0, Screen.ApproachRoute.Count - 1)];
            foreach (Pawn pawn in Members.Where(pawn => ArtillerySmoke.Ready(pawn) && Includes(pawn) &&
                    Screen.FindSmokeWeapon(pawn) != null && Screen.AssignmentFor(pawn) == null)
                .OrderBy(pawn => pawn.mindState.duty?.def == DutyDefOf.Breaching || pawn.mindState.duty?.def == DutyDefOf.Sapper)
                .ThenBy(pawn => LadderInventory.Get(pawn) != null)
                .ThenBy(pawn => IsoFloor.Distance3D(pawn.Position, front, pawn.Map))
                .Take(vacancies))
                Screen.Assignments.Add(new SmokeAssignment { Pawn = pawn });
        }

        private FrontResult UpdateFront(Map map)
        {
            if (blocker.IsValid && TribalSmokeRoutes.BlockingThingAt(blocker, map) == null)
            {
                if (TribalSmokeRoutes.BuildCandidateRoute(lead.Position, goal, TraverseParms.For(lead), map) is not { } route)
                    return FrontResult.RouteLost;
                InstallRoute(Screen, route);
            }

            List<IntVec3> nodes = Screen.ApproachRoute;
            int end = nodes.Count - 1;
            int projected = progress;
            int distance = 3; // Accept a throw position on the route or within one adjacent cell.
            for (int i = progress; i <= Math.Min(end, progress + LookAhead); i++)
            {
                if (TribalSmokeRoutes.BlockingThingAt(nodes[i], map) != null)
                {
                    if (i == 0) return FrontResult.RouteLost;
                    blocker = nodes[i];
                    nodes.RemoveRange(i, nodes.Count - i);
                    end = nodes.Count - 1;
                    Screen.Targets.Clear();
                    break;
                }
                int distanceToLead = nodes[i].DistanceToSquared(lead.Position);
                if (distanceToLead < distance)
                {
                    projected = i;
                    distance = distanceToLead;
                }
            }
            progress = Math.Min(projected, end);

            int gap = progress;
            int limit = Math.Min(end, progress + LookAhead);
            while (gap <= limit && Screen.CanAdvance(nodes[gap], map)) gap++;
            if (gap > end)
            {
                float distanceToEnd = IsoFloor.Distance3D(lead.Position, nodes[end], map);
                if (blocker.IsValid && (distanceToEnd <= 1.5f ||
                    distanceToEnd <= FollowerSpacing && SmokeCombat.Screened(nodes[end], map)))
                    return FrontResult.ScreenedBlocker;
                if (!blocker.IsValid && distanceToEnd <= 1.5f)
                    return FrontResult.ReachedGoal;
            }

            Waypoint = gap > progress ? nodes[gap - 1] : IntVec3.Invalid;
            RefreshFollowPositions(nodes, gap, map);
            RefreshTargets(nodes, gap, limit, map);
            return FrontResult.Advancing;
        }

        private void RefreshFollowPositions(List<IntVec3> nodes, int gap, Map map)
        {
            // Follow the covered route behind the leading position, even while a
            // replacement thrower is still catching up from the rear.
            FollowWaypoint = leftFollow = rightFollow = IntVec3.Invalid;
            for (int i = Math.Max(0, Math.Min(progress, gap - 1) - FollowerSpacing); i >= 0; i--)
            {
                if (!Screen.CanAdvance(nodes[i], map)) continue;
                FollowWaypoint = nodes[i];
                Vector3 side = SmokeCombat.ApproachSide(nodes, i, Screen.Rally);
                leftFollow = CoveredFlank(FollowWaypoint, side, -1, map);
                rightFollow = CoveredFlank(FollowWaypoint, side, 1, map);
                break;
            }
        }

        private IntVec3 CoveredFlank(IntVec3 spine, Vector3 side, int sign, Map map)
        {
            IntVec3 last = IntVec3.Invalid;
            for (int offset = 1; offset <= 3; offset++)
            {
                IntVec3 cell = SmokeCombat.FlankCell(spine, side, sign * offset, map);
                if (!cell.IsValid || !Screen.CanAdvance(cell, map)) break;
                last = cell;
            }
            return last;
        }

        internal IntVec3 FollowPosition(Pawn pawn)
        {
            IntVec3 flank = (pawn.thingIDNumber % 3) switch
            {
                0 => leftFollow,
                1 => rightFollow,
                _ => IntVec3.Invalid
            };
            if (flank.IsValid && Screen.CanAdvance(flank, pawn.Map)) return flank;
            return FollowWaypoint.IsValid ? FollowWaypoint : pawn.Position;
        }

        private void RefreshTargets(List<IntVec3> nodes, int gap, int limit, Map map)
        {
            List<SmokeTarget> targets = Screen.Targets;
            targets.RemoveAll(target => target.Order > limit || (target.Required
                ? target.Order < gap || target.Cell != nodes[target.Order]
                : target.Order < progress));

            float smokeRadius = SmokeCombat.ExplosionRadius(Screen.SmokeWeapon);
            IntVec3 previous = IntVec3.Invalid;
            for (int i = gap; i <= limit; i++)
            {
                IntVec3 cell = nodes[i];
                if (Screen.CanAdvance(cell, map) || previous.IsValid &&
                    IsoFloor.Distance3D(previous, cell, map) <= smokeRadius) continue;
                previous = cell;
                IntVec3 stand = nodes[Math.Max(0, Math.Min(gap - 1, i - 2))];
                SmokeTarget existing = targets.Find(target => target.Required && target.Order == i);
                if (existing != null) existing.Stand = stand;
                else targets.Add(new SmokeTarget { Cell = cell, Stand = stand, Order = i });
            }
            RefreshFlankTargets(nodes, gap, limit, smokeRadius, map);
        }

        private void RefreshFlankTargets(List<IntVec3> nodes, int gap, int limit, float smokeRadius, Map map)
        {
            List<SmokeTarget> targets = Screen.Targets;
            int slots = Math.Min(2, Screen.Assignments.Count(assignment => !assignment.Fighting) - 1);
            if (slots <= 0)
            {
                targets.RemoveAll(target => !target.Required);
                return;
            }
            if (targets.Any(target => !target.Required)) return;

            int station = gap <= limit ? gap : Math.Min(progress + 2, limit);
            IntVec3 spine = nodes[station];
            IntVec3 stand = nodes[Math.Max(0, Math.Min(gap - 1, station - 2))];
            Vector3 side = SmokeCombat.ApproachSide(nodes, station, Screen.Rally);
            int maxOffset = Mathf.Clamp(Mathf.CeilToInt(smokeRadius + 1f), 2, 3);
            int firstSide = station % 2 == 0 ? -1 : 1;
            for (int sign = firstSide, tried = 0; tried < 2 && slots > 0; tried++, sign = -sign)
            {
                for (int offset = maxOffset; offset >= 2; offset--)
                {
                    IntVec3 cell = SmokeCombat.FlankCell(spine, side, sign * offset, map);
                    if (!cell.IsValid || Screen.CanAdvance(cell, map) ||
                        !SmokeCombat.CanThrowNow(stand, cell, map, Screen.ThrowRange) ||
                        nodes.IndexOf(cell, progress, limit - progress + 1) >= 0 ||
                        targets.Exists(target => target.Cell == cell)) continue;
                    targets.Add(new SmokeTarget { Cell = cell, Stand = stand, Order = station, Required = false });
                    slots--;
                    break;
                }
            }
        }

        internal void End()
        {
            if (Screen == null) return;
            ArtillerySmoke old = Screen;
            Screen = null;
            foreach (Pawn pawn in Members)
            {
                if (pawn is not { Spawned: true, Dead: false, Downed: false }) continue;
                if (pawn.CurJob?.jobGiver is JobGiver_ArtillerySmoke or JobGiver_TribalSmokeFollowers ||
                    pawn.CurJobDef == ArtillerySmokeDefOf.RS_ApproachSmokeThrow ||
                    pawn.CurJobDef == ArtillerySmokeDefOf.RS_LayArtillerySmoke)
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
            }
            old.RestoreWeapons();
        }

        public void ExposeData()
        {
            Scribe_References.Look(ref Lord, "lord");
            Scribe_Collections.Look(ref Members, "members", LookMode.Reference);
            Scribe_Deep.Look(ref Screen, "screen");
            Scribe_Values.Look(ref CarrierQuota, "carrierQuota");
            Scribe_Values.Look(ref started, "started");
            Scribe_Values.Look(ref goal, "goal", IntVec3.Invalid);
            Scribe_Values.Look(ref blocker, "blocker", IntVec3.Invalid);
            Scribe_Values.Look(ref progress, "progress");
            Members ??= new();
        }
    }

    public sealed class ThinkNode_ConditionalTribalSmoke : ThinkNode_Conditional
    {
        protected override bool Satisfied(Pawn pawn) =>
            pawn is { Spawned: true, Dead: false, Downed: false, InMentalState: false } &&
            TribalNativeGroup.For(pawn)?.Includes(pawn) == true;
    }

    public sealed class JobGiver_TribalSmokeFollowers : JobGiver_Wander
    {
        public JobGiver_TribalSmokeFollowers()
        {
            wanderRadius = 3f;
            ticksBetweenWandersRange = new IntRange(25, 60);
            wanderDestValidator = (pawn, cell, _) => TribalNativeGroup.For(pawn)?.Screen is { } screen &&
                screen.CanAdvance(cell, pawn.Map);
        }

        protected override IntVec3 GetWanderRoot(Pawn pawn)
        {
            return TribalNativeGroup.For(pawn).FollowPosition(pawn);
        }

        protected override Job TryGiveJob(Pawn pawn)
        {
            IntVec3 follow = GetWanderRoot(pawn);
            if (!pawn.Position.InHorDistOf(follow, wanderRadius))
            {
                ArtillerySmoke screen = TribalNativeGroup.For(pawn).Screen;
                if (pawn.CanReach(follow, PathEndMode.OnCell, Danger.Deadly))
                {
                    IntVec3 covered = JobGiver_ArtillerySmoke.CoveredApproach(pawn, follow, screen);
                    if (covered.IsValid)
                        return JobMaker.MakeJob(JobDefOf.Goto, covered, 90, checkOverrideOnExpiry: true);
                }
                return JobMaker.MakeJob(JobDefOf.Wait, 45, checkOverrideOnExpiry: true);
            }
            Job job = base.TryGiveJob(pawn);
            if (job != null)
            {
                if (!job.targetA.IsValid || wanderDestValidator(pawn, job.targetA.Cell, follow)) return job;
                JobMaker.ReturnToPool(job);
            }
            return JobMaker.MakeJob(JobDefOf.Wait, 20, checkOverrideOnExpiry: true);
        }
    }
}
