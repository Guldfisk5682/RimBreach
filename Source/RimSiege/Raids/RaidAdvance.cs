using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimSiege.Fortress;
using ProjectZ;
using RimSiege.Integration;
using RimSiege.Raids.Artillery;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Raids
{
    internal sealed class RaidAdvance : IExposable
    {
        internal List<Pawn> Members = new();
        internal IntVec3 Approach;
        internal int Start = -1;

        internal static void Assign(SiegeParty party, Lord lord, Pawn lead)
        {
            if (!party.PlannedAdvances.Add(lord)) return;
            List<Pawn> roster = lord.ownedPawns.Where(SiegeRoles.CanParticipateInTacticalAssault).ToList();
            int limit = Math.Min(5 - party.Advances.Count, Math.Clamp(roster.Count / 4, 3, 5));
            if (roster.Count < 9 || limit < 3) return;

            Map map = lord.Map;
            IntVec3 entry = lead.Position;
            IntVec3 goal = map.GetComponent<ArtilleryMap>().ForPawn(lead)?.Camp?.Goal ?? IntVec3.Invalid;
            if (!goal.IsValid)
                goal = FortressSite.SupportSite(lord)?.AssaultGoal ?? GenAI.RandomRaidDest(entry, map);
            if (!goal.IsValid) return;

            var paths = new List<(IntVec3 Cell, HashSet<IntVec3> Cells)>();
            int checks = 0;
            TraverseParms traverse = TraverseParms.For(lead);
            IntVec3 center = IsoFloor.OnFloorOf(goal, entry, map);
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(center, 24, true)
                .Where(c => (c.x - center.x) % 6 == 0 && (c.z - center.z) % 6 == 0 &&
                    (c - center).LengthHorizontalSquared >= 64)
                .OrderBy(c => (c - entry).LengthHorizontalSquared))
            {
                if (paths.Count == limit || checks == 12) break;
                if (!cell.InBounds(map) || !IsoFloor.SameFloor(cell, entry, map) || !cell.Standable(map) ||
                    paths.Any(p => p.Cell.DistanceToSquared(cell) < 64) ||
                    !map.reachability.CanReach(entry, cell, PathEndMode.OnCell, traverse)) continue;
                checks++;
                using PawnPath path = map.pathFinder.FindPathNow(entry, cell, traverse, null, PathEndMode.OnCell);
                if (path == null || !path.Found) continue;
                var approach = path.NodesReversed.Take(Math.Max(1, path.NodesReversed.Count / 2)).ToHashSet();
                if (paths.Any(p => p.Cells.Count(approach.Contains) * 2 > Math.Min(p.Cells.Count, approach.Count))) continue;
                paths.Add((cell, approach));
            }
            if (paths.Count == 0) return;

            var groups = new RaidAdvance[limit];
            int now = GenTicks.TicksGame;
            for (int i = 0; i < limit; i++)
                groups[i] = new RaidAdvance
                {
                    Approach = paths[i % paths.Count].Cell,
                    Start = now
                };
            // Keep separate groups useful if several close-combat raiders enter side by side.
            roster = roster.OrderByDescending(p => SiegeRoles.HasDemolitionWeapon(p)).ToList();
            for (int i = 0; i < roster.Count; i++) groups[i % limit].Members.Add(roster[i]);
            party.Advances.AddRange(groups);
        }

        public void ExposeData()
        {
            Scribe_Collections.Look(ref Members, "members", LookMode.Reference);
            Scribe_Values.Look(ref Approach, "approach");
            Scribe_Values.Look(ref Start, "start", -1);
        }
    }

    public sealed class JobGiver_RaidAdvance : ThinkNode_JobGiver
    {
        internal static bool Eligible(Pawn pawn, SiegeRaidMap map) =>
            SiegeRoles.CanParticipateInTacticalAssault(pawn) &&
            map.Party(pawn) is { TribalSmoke: null, TacticsEnabled: true } party &&
            pawn.mindState.duty?.def == DutyDefOf.AssaultColony &&
            (pawn.GetLord()?.CurLordToil is LordToil_AssaultColony ||
                party.Encirclement && pawn.GetLord()?.CurLordToil is LordToil_AssaultColonyBreaching);

        protected override Job TryGiveJob(Pawn pawn)
        {
            SiegeRaidMap map = pawn.Map.GetComponent<SiegeRaidMap>();
            if (!Eligible(pawn, map) || LadderInventory.Get(pawn) != null) return null;
            SiegeParty party = map.Party(pawn);
            RaidAdvance.Assign(party, pawn.GetLord(), pawn);
            RaidAdvance group = party.AdvanceFor(pawn);
            if (group == null || party.Advanced.Contains(pawn)) return null;

            int now = GenTicks.TicksGame;
            if (group.Start < 0) group.Start = now;
            if (IsoFloor.Distance3D(pawn.Position, group.Approach, pawn.Map) <= 3 ||
                now >= group.Start + 1500 ||
                !pawn.CanReach(group.Approach, PathEndMode.OnCell, Danger.Deadly))
            {
                party.Advanced.Add(pawn);
                return null;
            }
            return JobMaker.MakeJob(JobDefOf.Goto, group.Approach, 75, checkOverrideOnExpiry: true);
        }
    }

    [HarmonyPatch(typeof(Verb), nameof(Verb.TryStartCastOn), typeof(LocalTargetInfo), typeof(LocalTargetInfo),
        typeof(bool), typeof(bool), typeof(bool), typeof(bool))]
    internal static class RaidAdvanceCoverPatch
    {
        private static void Postfix(Verb __instance, LocalTargetInfo __0, bool __result)
        {
            if (!__result || __instance.IsMeleeAttack ||
                __instance.Caster is not Pawn { Spawned: true, Downed: false } pawn ||
                !SiegeRoles.CanParticipateInTacticalAssault(pawn) || SiegeRoles.CloseAssault(pawn) ||
                SmokeCombat.IsSmokeWeapon(__instance.EquipmentSource?.def) || LadderPerch.Get(pawn) != null ||
                __0.Thing is not Thing target || !pawn.HostileTo(target) || pawn.pather.Moving) return;

            if (pawn.GetLord()?.CurLordToil is not
                (LordToil_AssaultColony or LordToil_AssaultColonyBreaching or LordToil_AssaultColonySappers)) return;
            SiegeParty party = pawn.Map.GetComponent<SiegeRaidMap>().Party(pawn);
            if (party?.TacticsEnabled != true) return;
            if (party.AdvanceFor(pawn) != null) party.Advanced.Add(pawn);
            if (party.CoverBuilders.Contains(pawn)) return;
            if (FieldCover.TryPlace(pawn, target.Position).IsValid)
                party.CoverBuilders.Add(pawn);
        }
    }

    [HarmonyPatch(typeof(JobDriver), nameof(JobDriver.Notify_PatherFailed))]
    internal static class RaidAdvancePathFailure
    {
        private static void Prefix(JobDriver __instance)
        {
            if (__instance.job.jobGiver is not JobGiver_RaidAdvance) return;
            Pawn pawn = __instance.pawn;
            pawn.Map.GetComponent<SiegeRaidMap>().Party(pawn)?.Advanced.Add(pawn);
        }
    }
}
