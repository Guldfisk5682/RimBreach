using System;
using System.Collections.Generic;
using System.Linq;
using RimSiege.Raids.Artillery;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Raids.Encirclement
{
    public sealed partial class QuestPart_Encirclement
    {
        private Dictionary<int, ArtilleryLocalSmoke> forwardScreens = new();
        private readonly Dictionary<int, IntVec3> forwardRallies = new();
        private SmokeThreatCache turretThreats;
        private SmokeThreatCache TurretThreats => turretThreats ??= new SmokeThreatCache(includeColonists: false, RimWorld.Faction.OfPlayer);

        internal bool ParallelBatteries => Phase == 2 && !Mechanoid;
        internal bool UsesHumanBatteryCrews => Phase is 2 or 3 && !Mechanoid;
        internal ArtilleryLocalSmoke ScreenFor(Pawn pawn) =>
            Screen?.Work.AssignmentFor(pawn) != null ? Screen :
            forwardScreens.Values.FirstOrDefault(s => s.Work.AssignmentFor(pawn) != null);

        internal ArtilleryLocalSmoke ScreenAt(int index) => forwardScreens.GetValueOrDefault(index);

        internal bool CellSafeForCamp(IntVec3 cell) => Map == null || !cell.InBounds(Map) ||
            SmokeCombat.Screened(cell, Map) || !TurretThreats.IsExposed(cell, Map);

        internal bool CanCampPawnEnter(Pawn pawn, IntVec3 nextCell)
        {
            if (Mechanoid || Phase is < 1 or > 3 || pawn == null || !pawn.RaceProps.Humanlike ||
                pawn.Dead || pawn.Downed || pawn.InMentalState || Plan == null) return true;
            if (CellSafeForCamp(nextCell)) return true;
            // Already caught in unsmoked turret fire: allow moving out toward safety/smoke or back to rear.
            if (!CellSafeForCamp(pawn.Position))
            {
                IntVec3 dest = pawn.pather?.Destination.Cell ?? IntVec3.Invalid;
                if (dest.IsValid && (CellSafeForCamp(dest) ||
                    Plan.Rear.IsValid && dest.InHorDistOf(Plan.Rear, 14f))) return true;
                if (Plan.Rear.IsValid &&
                    nextCell.DistanceToSquared(Plan.Rear) < pawn.Position.DistanceToSquared(Plan.Rear))
                    return true;
            }
            return false;
        }

        internal bool HasSafePath(Pawn pawn, IntVec3 destination, PathEndMode peMode = PathEndMode.OnCell)
        {
            if (Mechanoid || Phase is < 1 or > 3 || pawn == null || !pawn.RaceProps.Humanlike ||
                Map == null || !destination.InBounds(Map)) return true;
            if (pawn.Position == destination) return CellSafeForCamp(destination);
            using PawnPath path = Map.pathFinder.FindPathNow(pawn.Position, destination, TraverseParms.For(pawn),
                null, peMode);
            if (path == null || !path.Found) return false;
            var nodes = path.NodesReversed;
            for (int i = nodes.Count - 1; i >= 0; i--)
            {
                IntVec3 node = nodes[i];
                if (node == pawn.Position) continue;
                if (!CellSafeForCamp(node)) return false;
            }
            return true;
        }

        internal IntVec3 SafeApproachCell(Pawn pawn, IntVec3 destination)
        {
            if (Map == null || !destination.InBounds(Map)) return IntVec3.Invalid;
            using PawnPath path = Map.pathFinder.FindPathNow(pawn.Position, destination, TraverseParms.For(pawn),
                null, PathEndMode.OnCell);
            if (path == null || !path.Found) return IntVec3.Invalid;
            IntVec3 last = IntVec3.Invalid;
            var nodes = path.NodesReversed;
            for (int i = nodes.Count - 1; i >= 0; i--)
            {
                IntVec3 node = nodes[i];
                if (node == pawn.Position) continue;
                if (!CellSafeForCamp(node)) break;
                last = node;
            }
            return last;
        }

        private bool BatteryNeedsSustainedSmoke(ArtilleryRaid raid)
        {
            if (raid?.Camp?.Guns == null) return false;
            bool anyLive = false;
            foreach (ArtilleryPlacement gun in raid.Camp.Guns)
            {
                if (gun.Building is not { Spawned: true, Destroyed: false } b || b.Faction != Faction) continue;
                anyLive = true;
                if (TurretThreats.IsExposed(gun.Cell, Map)) return true;
                if (gun.NeedsOperator && TurretThreats.IsExposed(b.InteractionCell, Map)) return true;
            }
            return anyLive && TurretThreats.IsExposed(raid.Camp.Center, Map);
        }

        internal IntVec3 BatteryRally(int index) => forwardRallies.TryGetValue(index, out IntVec3 rally)
            ? rally : Battery(index)?.Camp.Center ?? Plan.Rear;

        private bool WaitingForSmoke(ArtillerySmoke screen) => Now < screen.Deadline &&
            screen.Assignments.Any(a => InBattle(a.Pawn)) && screen.Targets.Any(t => !t.IsDone(Map));

        private void ReviewHumanBatteries(bool debug)
        {
            if (CampLord == null) return;
            ArtilleryRaid rear = Battery(0);
            var sites = Enumerable.Range(1, Plan.BatteryKeys.Count - 1).Where(i => Battery(i) != null)
                .ToDictionary(i => i, Battery);
            var projects = Work.Projects.Where(p => p.Battery > 0).ToLookup(p => p.Battery);
            var deploying = Phase == 2
                ? sites.Keys.Where(i => !abandonedBatteries.Contains(i) &&
                    projects[i].Any(p => Work.NeedsBatteryBuild(Map, p))).ToArray()
                : Array.Empty<int>();
            var screened = sites.Keys.Where(i => deploying.Contains(i) || BatteryNeedsSustainedSmoke(sites[i])).ToArray();
            var requiredSkill = sites.ToDictionary(pair => pair.Key, pair => pair.Value.Camp.Guns
                .Select(g => g.Def.constructionSkillPrerequisite).DefaultIfEmpty().Max());
            // An older campaign saved only the screen for the active battery.
            if (Screen != null)
            {
                if (!screened.Contains(ActiveCluster) || !forwardScreens.TryAdd(ActiveCluster, Screen))
                    Screen.Work.RestoreWeapons();
                Screen = null;
            }
            foreach (int index in forwardScreens.Keys.Except(screened).ToArray())
            {
                forwardScreens[index].Work.RestoreWeapons();
                forwardScreens.Remove(index);
            }

            var assigned = RetainGunners(Plan.Batteries(Map), CanStaff);
            var free = CampLord.ownedPawns.Where(p => p.RaceProps.Humanlike && CrewAvailable(p) &&
                !assigned.Contains(p) && !IsMedic(p)).ToHashSet();
            var previousBuilders = new Dictionary<Pawn, int>(forwardBuilders);
            forwardBuilders.Clear();
            foreach (Pawn pawn in CampLord.ownedPawns)
            {
                int index = ClearingBattery(pawn);
                if (index > 0) forwardBuilders.Add(pawn, index);
            }

            var carriers = screened.ToDictionary(i => i, _ => new List<Pawn>());
            var previousCarriers = screened.ToDictionary(i => i, _ => new HashSet<Pawn>());
            bool hasSmoke = ArtillerySmokeDefOf.CE_Weapon_GrenadeSmoke != null;
            if (hasSmoke)
                foreach (int index in screened)
                    if (ScreenAt(index) is { } oldScreen)
                        foreach (SmokeAssignment assignment in oldScreen.Work.Assignments)
                            if (ArtillerySmoke.Ready(assignment.Pawn) && free.Contains(assignment.Pawn))
                                previousCarriers[index].Add(assignment.Pawn);
            var anyPreviousCarrier = previousCarriers.Values.SelectMany(s => s).ToHashSet();

            // Fill one seat in each role at every site before giving a site a larger crew.
            if (Phase == 2)
                foreach (int index in sites.Keys.OrderByDescending(i => requiredSkill[i]))
                    if (deploying.Contains(index) || Work.ActiveProjects.Any(p => p.Battery == index))
                        AssignBuilders(index, 1);
            foreach (ArtilleryRaid raid in sites.Values)
                AssignGunners(raid, Math.Max(0, 1 - raid.Camp.Guns.Count(g => g.Operator != null)));
            if (hasSmoke)
                foreach (int index in screened)
                    AssignSmoke(index, 2);

            foreach (ArtilleryRaid raid in sites.Values)
                AssignGunners(raid, raid.Camp.Guns.Count);
            if (Phase == 2)
                foreach (int index in deploying)
                    AssignBuilders(index, Math.Min(5, sites[index].Camp.Guns.Count + 1));
            if (rear != null) AssignGunners(rear, rear.Camp.Guns.Count);

            forwardRallies.Clear();
            var reserved = hasSmoke && screened.Length > 0 ? Plan.Reservations(Map) : null;
            foreach (int index in screened)
            {
                ArtilleryRaid raid = sites[index];
                bool building = deploying.Contains(index);
                bool released = !building || projects[index].Any(p => p.Gun >= 0 && p.Funded);
                if (hasSmoke)
                {
                    int smokeSlots = building
                        ? Math.Clamp(raid.Camp.Guns.Count + 2, 4, 5)
                        : Math.Clamp(raid.Camp.Guns.Count, 2, 3);
                    AssignSmoke(index, smokeSlots);
                    List<Pawn> team = carriers[index];
                    ArtilleryLocalSmoke screen = ScreenAt(index);
                    if (released && team.Count > 0 && screen != null && (screen.Work.Rally != raid.Camp.Center ||
                        screen.Work.ApproachRoute.Count > 0))
                    {
                        // The opening rally can be at the map edge. Once construction
                        // begins, keep the dense camp smoke ring and move supply to the guns.
                        var inherited = screen.Work.Targets.Where(t => t.Required &&
                            t.Cell.InHorDistOf(raid.Camp.Center, 15f) && t.Stand.InHorDistOf(raid.Camp.Center, 22f) &&
                            !reserved.Contains(t.Cell) && t.Stand.Standable(Map)).ToList();
                        screen.Work.RestoreWeapons();
                        var local = screen.Work.LocalScreen(raid.Camp.Center, turretsOnly: true);
                        foreach (SmokeTarget target in inherited)
                            local.Targets.Add(new SmokeTarget
                            {
                                Cell = target.Cell, Stand = target.Stand, Order = target.Order, Required = true
                            });
                        local.PopulateCampTargets(raid.Camp, raid.Camp.Center, team[0], Map, c => !reserved.Contains(c));
                        screen = forwardScreens[index] = new ArtilleryLocalSmoke(local, screen);
                    }
                    if (screen != null)
                        screen.Review(raid, CampLord, team, Now, c => !reserved.Contains(c), issueInitialKit: true);
                    else if (team.Count != 0)
                    {
                        forwardScreens.Add(index, CreateScreen(raid, team, opening: false, stationed: released));
                        SignalReview();
                    }
                }
                if (building)
                {
                    if (!released && !debug && ScreenAt(index)?.Work is { ApproachRoute.Count: > 0 } smoke &&
                        WaitingForSmoke(smoke))
                    {
                        IntVec3 follow = smoke.FollowCell(CampLord, raid.Camp.Center);
                        forwardRallies.Add(index, follow.IsValid ? follow : smoke.Rally);
                    }
                    else if (projects[index].Any(p => !p.Funded))
                        Work.Fund(this, raid, projects[index].ToList());
                }
            }
            ActiveCluster = deploying.FirstOrDefault(Plan.BatteryKeys.Count);

            bool CanStaff(ArtilleryRaid raid, ArtilleryPlacement gun) => gun.NeedsOperator &&
                (gun.Building?.Spawned == true ? GunReady(gun) : raid != rear &&
                    (gun.Frame?.Spawned == true || gun.Blueprint?.Spawned == true ||
                     deploying.Any(i => sites[i] == raid)));

            void AssignBuilders(int index, int limit)
            {
                int vacancies = limit - forwardBuilders.Values.Count(i => i == index);
                IntVec3 center = sites[index].Camp.Center;
                foreach (Pawn pawn in free.Where(LordJob_EncirclementCamp.Builder)
                    .OrderBy(anyPreviousCarrier.Contains)
                    .ThenByDescending(p => p.skills.GetSkill(SkillDefOf.Construction).Level >= requiredSkill[index])
                    .ThenBy(p => previousBuilders.GetValueOrDefault(p) != index)
                    .ThenBy(p => p.Position.DistanceToSquared(center))
                    .Where(p => p.CanReach(center, PathEndMode.Touch, Danger.Deadly)).Take(vacancies).ToArray())
                {
                    forwardBuilders[pawn] = index;
                    if (requiredSkill[index] > 0)
                        pawn.skills?.GetSkill(SkillDefOf.Construction)?.EnsureMinLevelWithMargin(requiredSkill[index]);
                    free.Remove(pawn);
                }
            }

            void AssignGunners(ArtilleryRaid raid, int count)
            {
                foreach (ArtilleryPlacement gun in raid.Camp.Guns.Where(g => g.Operator == null && CanStaff(raid, g)).Take(count))
                {
                    Pawn pawn = free.Where(ArtilleryCamp.OperatorCandidate)
                        .OrderBy(anyPreviousCarrier.Contains)
                        .ThenBy(p => p.Position.DistanceToSquared(gun.Cell))
                        .ThenByDescending(p => p.skills.GetSkill(SkillDefOf.Shooting).Level)
                        .FirstOrDefault(p => gun.Building?.Spawned == true
                            ? p.CanReach(gun.Building, PathEndMode.InteractionCell, Danger.Deadly)
                            : p.CanReach(raid.Camp.Center, PathEndMode.Touch, Danger.Deadly));
                    if (pawn == null) continue;
                    gun.Operator = pawn;
                    free.Remove(pawn);
                }
            }

            void AssignSmoke(int index, int limit)
            {
                List<Pawn> team = carriers[index];
                IntVec3 center = sites[index].Camp.Center;
                HashSet<Pawn> preferred = previousCarriers[index];
                foreach (Pawn pawn in free.Where(ArtillerySmoke.Ready)
                    .OrderByDescending(preferred.Contains)
                    .ThenBy(p => p.Position.DistanceToSquared(center))
                    .Where(p => p.CanReach(center, PathEndMode.Touch, Danger.Deadly)).Take(limit - team.Count).ToArray())
                {
                    team.Add(pawn);
                    free.Remove(pawn);
                }
            }
        }
    }
}
