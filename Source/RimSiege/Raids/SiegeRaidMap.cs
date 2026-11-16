using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using ProjectZ;
using RimSiege.Integration;
using RimSiege.Raids.Artillery;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Raids
{
    public sealed class SiegeParty : IExposable
    {
        public int Id;
        public int InitialCount;
        public int Issued;
        public int LadderQuotaPercent = 10;
        public List<Pawn> Members = new List<Pawn>();
        public List<Pawn> Operators = new List<Pawn>();
        internal IsoSiegePlanner Planner;
        internal List<RaidAdvance> Advances = new();
        internal HashSet<Lord> PlannedAdvances = new();
        internal HashSet<Pawn> Advanced = new();
        internal HashSet<Pawn> CoverBuilders = new();
        internal TribalSmokeState TribalSmoke;

        internal RaidAdvance AdvanceFor(Pawn pawn) => Advances.Find(group => group.Members.Contains(pawn));
        private readonly Dictionary<Pawn, PlanStatus> jobs = new Dictionary<Pawn, PlanStatus>();
        private readonly Dictionary<Pawn, RouteStatus> ladderRoutes = new Dictionary<Pawn, RouteStatus>();
        private struct PlanStatus { internal int tick, version; internal bool available, placing; }
        private struct RouteStatus { internal int tick, version; internal bool available; }
        private (Faction Faction, int Version, bool Exists) assaultLadder;

        internal bool HasAssaultLadder(Faction faction)
        {
            int version = Planner.Version;
            if (assaultLadder.Faction != faction || assaultLadder.Version != version)
                assaultLadder = (faction, version, Planner.HasSiegeLadderOrClearance(faction));
            return assaultLadder.Exists;
        }

        internal bool HasLadderRoute(Pawn pawn)
        {
            if (!HasAssaultLadder(pawn.Faction)) return false;
            int now = Find.TickManager.TicksGame, version = Planner.Version;
            if (ladderRoutes.TryGetValue(pawn, out RouteStatus cached) && cached.version == version &&
                now - cached.tick < RaidClimb.MemoTicks)
                return cached.available;
            bool available = !Planner.Destination(pawn, true).IsValid && Planner.Destination(pawn, false).IsValid;
            ladderRoutes[pawn] = new RouteStatus { tick = now, version = version, available = available };
            return available;
        }

        internal Job Plan(Pawn pawn, bool placing = true)
        {
            int now = Find.TickManager.TicksGame, version = Planner.Version;
            if (jobs.TryGetValue(pawn, out PlanStatus cached) && cached.placing == placing && cached.version == version && now - cached.tick < RaidClimb.MemoTicks && !cached.available)
                return null;
            Job job = placing && LadderInventory.Get(pawn) != null
                ? Planner.JobFor(pawn, available: UnusedLanding(pawn))
                : Planner.JobFor(pawn, placing);
            jobs[pawn] = new PlanStatus { tick = now, version = version, available = job != null, placing = placing };
            return job;
        }

        private System.Predicate<IntVec3> UnusedLanding(Pawn pawn)
        {
            Map map = pawn.Map;
            var used = Members.Where(p => p != null && p != pawn && p.Spawned && p.Map == map &&
                !p.Downed && p.CurJobDef == PZJobDefOf.PZ_PlaceSiegeLadder)
                .Select(p => p.CurJob.targetB.Cell).ToList();
            if (StairGrid.For(map) is { } stairs)
                used.AddRange(stairs.links.Where(link => link.thing is Building_SiegeLadder &&
                    link.thing.Faction == pawn.Faction).Select(link => link.top));
            return landing => !used.Any(cell => IsoFloor.SameFloor(cell, landing, map) &&
                cell.DistanceToSquared(landing) < 36);
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref Id, "id");
            Scribe_Values.Look(ref InitialCount, "initialCount");
            Scribe_Values.Look(ref Issued, "issued");
            Scribe_Values.Look(ref LadderQuotaPercent, "ladderQuotaPercent", 10);
            Scribe_Collections.Look(ref Members, "members", LookMode.Reference);
            Scribe_Collections.Look(ref Operators, "operators", LookMode.Reference);
            Scribe_Collections.Look(ref Advances, "advanceGroups", LookMode.Deep);
            Scribe_Collections.Look(ref PlannedAdvances, "plannedAdvances", LookMode.Reference);
            Scribe_Collections.Look(ref Advanced, "advancedPawns", LookMode.Reference);
            Advances ??= new();
            PlannedAdvances ??= new();
            Advanced ??= new();
            Scribe_Collections.Look(ref CoverBuilders, "coverBuilders", LookMode.Reference);
            CoverBuilders ??= new();
            Scribe_Deep.Look(ref TribalSmoke, "tribalSmoke");
        }
    }

    public sealed class SiegeRaidMap : MapComponent
    {
        private List<SiegeParty> parties = new List<SiegeParty>();
        private List<SmokeAssignment> pendingSmokeRestores = new();
        private readonly Dictionary<Pawn, SiegeParty> membership = new Dictionary<Pawn, SiegeParty>();
        private readonly Dictionary<Pawn, LadderKit> receivers = new();
        private readonly HashSet<LadderKit> dropped = new();
        private readonly List<Pawn> arriving = new();
        private int nextId = 1;
        internal int ReachEpoch;

        public SiegeRaidMap(Map map) : base(map) { }

        internal static bool Enabled => RimSiegeMod.Settings?.EnhancedAssault == true;
        internal SiegeParty Party(Pawn pawn) => membership.TryGetValue(pawn, out SiegeParty party) ? party : null;
        internal SiegeParty Party(int id) => parties.FirstOrDefault(p => p.Id == id);

        internal bool Applies(Pawn pawn) => Enabled && SiegeRoles.CanParticipateInTacticalAssault(pawn) &&
            Party(pawn) != null && SiegeRoles.LadderAssaultDuty(pawn);

        internal void WakeForLadder(Faction faction)
        {
            if (!Enabled || faction == null || parties.Count == 0) return;
            var wake = new List<Pawn>();
            foreach (SiegeParty party in parties)
                foreach (Pawn pawn in party.Members)
                    if (pawn != null && pawn.Faction == faction && pawn.Spawned && pawn.Map == map &&
                        Party(pawn) == party && Applies(pawn) && !SiegeRoles.FireSupportMech(pawn) &&
                        SiegeRoles.Eligible(pawn, party) && ShouldWakeForLadder(pawn))
                        wake.Add(pawn);
            foreach (Pawn pawn in wake) pawn.jobs.CheckForJobOverride();
        }

        private static bool ShouldWakeForLadder(Pawn pawn)
        {
            if (LadderPerch.Get(pawn) != null) return false;
            Job cur = pawn.CurJob;
            if (cur == null) return true;
            if (cur.jobGiver is JobGiver_SiegeApproach) return false;
            JobDef def = cur.def;
            return def == JobDefOf.Wait || def == JobDefOf.Wait_Wander || def == JobDefOf.FollowClose ||
                def == JobDefOf.Goto || def == JobDefOf.Wait_Combat;
        }

        private void Index(SiegeParty party)
        {
            party.Planner = new IsoSiegePlanner(map, () => ReachEpoch, LadderTopClearance.Allows);
            foreach (Pawn pawn in party.Members)
                if (pawn != null) membership[pawn] = party;
        }

        internal SiegeParty Register(List<Pawn> pawns, Faction faction, bool deferKits = false)
        {
            if (!Enabled || !IsoFloor.HasFloors(map)) return null;
            for (int i = parties.Count - 1; i >= 0; i--)
            {
                SiegeParty old = parties[i];
                if (map.GetComponent<ArtilleryMap>().HasActiveRaid(old.Id) ||
                    old.Members.Any(p => p != null && !p.Dead && p.SpawnedOrAnyParentSpawned &&
                        (SiegeRoles.Ordinary(p.GetLord()) || SiegeRoles.LadderAssaultDuty(p) ||
                         (old.TribalSmoke != null && p.GetLord() is { } lord && map.lordManager.lords.Contains(lord))))) continue;
                old.TribalSmoke?.Stop();
                foreach (Pawn former in old.Members)
                    if (former != null) membership.Remove(former);
                parties.RemoveAt(i);
            }
            PrunePendingSmoke();
            var tacticalMembers = pawns.Where(p => p != null &&
                (p.RaceProps.Humanlike || (faction == Faction.OfMechanoids && p.RaceProps.IsMechanoid))).ToList();
            SiegeParty party = new SiegeParty
            {
                Id = nextId++, InitialCount = tacticalMembers.Count, Members = tacticalMembers,
                LadderQuotaPercent = LadderQuotaFor(faction)
            };
            parties.Add(party);
            Index(party);
            if (deferKits) return party;
            List<Pawn> eligible = tacticalMembers.Where(p => SiegeRoles.Eligible(p, party)).ToList();
            eligible.Shuffle();
            foreach (Pawn pawn in eligible.OrderBy(SiegeRoles.LadderPriority)) Issue(pawn, party);
            return party;
        }

        internal static bool IsTribalHumanlike(Faction faction) =>
            faction?.def.humanlikeFaction == true && faction.def.categoryTag == "Tribal";

        private static int LadderQuotaFor(Faction faction)
        {
            if (IsTribalHumanlike(faction)) return 35;
            if (faction == Faction.OfMechanoids ||
                faction.def.humanlikeFaction && faction.def.techLevel >= TechLevel.Industrial) return 20;
            return 10;
        }

        internal void IssueInOrder(IEnumerable<Pawn> pawns)
        {
            foreach (Pawn pawn in pawns.Where(pawn => Party(pawn) != null).OrderBy(SiegeRoles.LadderPriority))
                Issue(pawn, Party(pawn));
        }

        internal void QueueArrival(Pawn pawn) => arriving.Add(pawn);

        internal void Issue(Pawn pawn, SiegeParty party)
        {
            if (party.Issued >= (party.InitialCount * party.LadderQuotaPercent + 99) / 100 ||
                LadderInventory.Get(pawn) != null || !SiegeRoles.Eligible(pawn, party)) return;
            GiveLadder(pawn, party);
        }

        private void GiveLadder(Pawn pawn, SiegeParty party)
        {
            var kit = (LadderKit)ThingMaker.MakeThing(SiegeDefOf.RS_AssaultLadder).MakeMinified();
            kit.RaidId = party.Id;
            if (!pawn.inventory.innerContainer.TryAdd(kit, false))
            {
                kit.Destroy(DestroyMode.Vanish);
                return;
            }
            party.Issued++;
        }

        internal Job Recovery(Pawn pawn, SiegeParty party)
        {
            LadderKit kit = RecoveryFor(pawn);
            if (kit == null || kit.RaidId != party.Id) return null;
            if (!CanRecover(pawn, party) || kit.IsForbidden(pawn) ||
                !pawn.CanReserveAndReach(kit, PathEndMode.ClosestTouch, Danger.Deadly))
            {
                Release(kit);
                return null;
            }
            Job job = JobMaker.MakeJob(JobDefOf.TakeInventory, kit);
            job.count = 1;
            return job;
        }

        internal void RegisterDropped(LadderKit kit)
        {
            if (kit.RaidId != 0) dropped.Add(kit);
        }

        internal void DeregisterDropped(LadderKit kit)
        {
            Release(kit);
            dropped.Remove(kit);
        }

        internal LadderKit RecoveryFor(Pawn pawn) => receivers.TryGetValue(pawn, out LadderKit kit) ? kit : null;

        private void Release(LadderKit kit)
        {
            if (kit.Receiver == null) return;
            receivers.Remove(kit.Receiver);
            kit.Receiver = null;
        }

        private bool CanRecover(Pawn pawn, SiegeParty party) => pawn.Map == map && Applies(pawn) &&
            Party(pawn) == party && LadderInventory.Get(pawn) == null && SiegeRoles.Eligible(pawn, party) &&
            pawn.TryGetComp<CompSuppressable>()?.isSuppressed != true;

        private bool CanRecover(Pawn pawn, SiegeParty party, Thing item, Dictionary<Pawn, bool> needsRoute)
        {
            if (!CanRecover(pawn, party) || item.IsForbidden(pawn) ||
                !pawn.CanReserveAndReach(item, PathEndMode.ClosestTouch, Danger.Deadly)) return false;
            if (!needsRoute.TryGetValue(pawn, out bool needs))
            {
                needs = !party.Planner.Destination(pawn, true).IsValid && !party.Planner.Destination(pawn, false).IsValid;
                needsRoute.Add(pawn, needs);
            }
            return needs;
        }

        private static bool Taking(Pawn pawn, Thing item) => pawn.CurJobDef == JobDefOf.TakeInventory &&
            pawn.CurJob.targetA.Thing == item;

        public override void MapComponentTick()
        {
            int now = Find.TickManager.TicksGame;
            if (arriving.Count != 0)
            {
                if (Enabled) IssueInOrder(arriving);
                arriving.Clear();
            }
            if (pendingSmokeRestores.Count != 0 && now % 300 == 0)
                PrunePendingSmoke();
            if (Enabled && now % 60 == 0) ArtilleryRetreat.ReviewTribal(this);
            for (int i = 0; i < parties.Count; i++)
                parties[i].TribalSmoke?.Tick(now);
            bool recover = dropped.Count != 0 && now % 60 == 0;
            bool recheckCarriers = Enabled && parties.Count != 0 && now % RaidClimb.MemoTicks == 0;
            if (!recover && !recheckCarriers) return;
            HashSet<Pawn> wake = new HashSet<Pawn>();
            if (recover)
            {
                Dictionary<Pawn, bool> needsRoute = new Dictionary<Pawn, bool>();
                foreach (LadderKit kit in dropped)
                {
                    SiegeParty party = Party(kit.RaidId);
                    Pawn previous = kit.Receiver;
                    if (previous != null && (party == null || !CanRecover(previous, party, kit, needsRoute)))
                    {
                        Release(kit);
                        if (previous.Map == map && Applies(previous) && Taking(previous, kit)) wake.Add(previous);
                    }
                    if (party == null || !Enabled) continue;
                    if (kit.Receiver == null && now >= kit.RetryAt)
                    {
                        kit.RetryAt = now + 60;
                        Pawn receiver = party.Members.Where(p => p != null && RecoveryFor(p) == null && CanRecover(p, party) &&
                                !(p.CurJobDef == JobDefOf.TakeInventory && p.CurJob.targetA.Thing is LadderKit other && other != kit))
                            .OrderBy(SiegeRoles.LadderPriority).ThenBy(p => IsoFloor.Distance3D(p.Position, kit.Position, map))
                            .FirstOrDefault(p => CanRecover(p, party, kit, needsRoute));
                        if (receiver != null)
                        {
                            kit.Receiver = receiver;
                            receivers.Add(receiver, kit);
                        }
                    }
                    Pawn selected = kit.Receiver;
                    if (selected != null && !Taking(selected, kit)) wake.Add(selected);
                }
            }
            if (recheckCarriers)
                foreach (SiegeParty party in parties)
                    foreach (Pawn pawn in party.Members)
                        if (pawn != null && pawn.Map == map && Party(pawn) == party && LadderInventory.Get(pawn) != null &&
                            (pawn.CurJobDef == JobDefOf.AttackMelee || pawn.CurJobDef == JobDefOf.AttackStatic || pawn.CurJobDef == JobDefOf.Wait_Combat) &&
                            pawn.CurJob.jobGiver is not JobGiver_SiegeApproach && Applies(pawn) &&
                            (SiegeRoles.MeleeAssault(pawn) || !party.HasAssaultLadder(pawn.Faction)) &&
                            SiegeRoles.Eligible(pawn, party))
                            wake.Add(pawn);
            // Vanilla may forbid a kit after SpawnSetup; wake only after the drop has finished and enumeration has ended.
            foreach (Pawn pawn in wake) pawn.jobs.CheckForJobOverride();
        }

        public override void FinalizeInit()
        {
            foreach (SiegeParty party in parties)
            {
                Index(party);
                foreach (Pawn pawn in party.Members)
                    if (pawn != null) LadderInventory.Restore(pawn);
            }
        }

        internal TribalSmokeState TribalSmokeFor(Lord lord) =>
            Enabled && lord != null ? TribalSmokeStates.FirstOrDefault(state => state.Owns(lord)) : null;

        internal IEnumerable<TribalSmokeState> TribalSmokeStates
        {
            get
            {
                for (int i = 0; i < parties.Count; i++)
                    if (parties[i].TribalSmoke != null)
                        yield return parties[i].TribalSmoke;
            }
        }

        internal static bool StillApplicableForRestore(Pawn pawn) =>
            pawn != null && !pawn.Dead && !pawn.Destroyed && !pawn.Discarded &&
            !pawn.IsPrisoner && !pawn.IsSlave &&
            pawn.Faction != Faction.OfPlayer && pawn.HostileTo(Faction.OfPlayer);

        internal SmokeAssignment PendingSmokeFor(Pawn pawn) =>
            pawn == null ? null : pendingSmokeRestores.Find(a => a.Pawn == pawn);

        internal void TrackPendingSmoke(Pawn pawn, ThingWithComps originalWeapon)
        {
            if (!StillApplicableForRestore(pawn) || originalWeapon == null || originalWeapon.Destroyed ||
                SmokeCombat.IsSmokeWeapon(originalWeapon.def))
                return;
            SmokeAssignment existing = PendingSmokeFor(pawn);
            if (existing != null)
            {
                if (existing.OriginalWeapon == null || existing.OriginalWeapon.Destroyed)
                    existing.OriginalWeapon = originalWeapon;
                existing.LoadoutRestored = false;
                return;
            }
            pendingSmokeRestores.Add(new SmokeAssignment
            {
                Pawn = pawn,
                OriginalWeapon = originalWeapon,
                Fighting = true
            });
        }

        internal void ClearPendingSmoke(Pawn pawn)
        {
            if (pawn != null)
                pendingSmokeRestores.RemoveAll(a => a.Pawn == pawn);
        }

        internal void PrunePendingSmoke()
        {
            pendingSmokeRestores.RemoveAll(a =>
                a?.Pawn == null || a.LoadoutRestored || a.OriginalWeapon == null || a.OriginalWeapon.Destroyed ||
                !StillApplicableForRestore(a.Pawn) || (!a.Pawn.SpawnedOrAnyParentSpawned && a.Pawn.MapHeld != map));
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref parties, "rimSiegeParties", LookMode.Deep);
            parties ??= new List<SiegeParty>(); // Saves made before this optional feature have no party records.
            Scribe_Collections.Look(ref pendingSmokeRestores, "pendingSmokeRestores", LookMode.Deep);
            pendingSmokeRestores ??= new List<SmokeAssignment>();
            Scribe_Values.Look(ref nextId, "rimSiegeNextParty", 1);
        }
    }
}
