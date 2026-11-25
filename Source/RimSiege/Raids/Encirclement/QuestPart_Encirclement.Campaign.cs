using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using RimSiege.Raids.Artillery;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Raids.Encirclement
{
    public sealed partial class QuestPart_Encirclement
    {
        private const int ReviewInterval = 180;
        private const int Hour = GenDate.TicksPerHour;
        private const int ForwardStallLimit = 4 * Hour;
        internal EncirclementCampPlan Plan;
        internal EncirclementWork Work;
        internal List<EncirclementWave> Waves = new();
        internal Lord CampLord, AssaultLord;
        internal ArtilleryLocalSmoke Screen;
        internal int IncidentKey;
        internal int PersonnelSeed;
        internal int ActiveCluster, OrderedGuns, NextGunTick;
        internal int CampStartedAt = -1;
        internal int AlertUntil;
        internal HashSet<int> AssemblyRoster = new();
        internal HashSet<int> AssemblyLosses = new();
        private int nextReview, nextSupplies, nextShield, shieldedBattery = -1, noGunsSince = -1;
        private bool rearSupplied, openedHarassment, fallbackWaiting;
        private Dictionary<int, int> batteryStalledSince = new();
        private HashSet<int> abandonedBatteries = new();
        private readonly HashSet<Thing> mannedDefensePosts = new();

        internal bool IsBatteryAbandoned(int index) => abandonedBatteries.Contains(index);

        internal float HeavyTarget => Plan is { ForwardBudget: >= 0f } ? Plan.ForwardBudget : BasePoints * 0.3f;
        internal float HeavyBuilt => Plan.Batteries(Map).Skip(1)
            .Sum(r => r.Camp.Guns.Where(g => g.Building is { Spawned: true } b && b.Faction == Faction).Sum(HeavyValue));
        internal float HeavyPlanned => Plan.Batteries(Map).Skip(1).Sum(r => r.Camp.Guns.Sum(HeavyValue));
        internal int WorkBattery => Phase == 2 ? ActiveCluster : 0;
        internal int Alive => Waves.Sum(w => w.Pawns.Count(InBattle));
        internal int Headcount => Waves.Sum(w => w.Count);
        internal int Losses => Waves.Sum(w => w.Losses.Count);
        internal bool OnAlert => Now < AlertUntil;

        internal ArtilleryRaid Battery(int index) => Plan != null && index >= 0 && index < Plan.BatteryKeys.Count
            ? Map.GetComponent<ArtilleryMap>().ForKey(Plan.BatteryKeys[index]) : null;

        internal bool InBattle(Pawn pawn) => pawn is { Dead: false, Downed: false, Spawned: true } &&
            !pawn.IsPrisoner && !pawn.IsSlave && pawn.Map == Map && pawn.Faction == Faction;

        internal static float HeavyValue(ArtilleryPlacement gun) => gun.Def.defName switch
        {
            "Turret_Flak" or "CE_Turret_SPGNine" or "Turret_AutoInferno" => 280f,
            "Turret_AutoChargeBlaster" => 200f,
            "CE_Turret_TwelvePounder" => 120f,
            _ => gun.Def.building.combatPower
        };

        internal ArtilleryPlacement GunFor(Pawn pawn, out ArtilleryRaid owner)
        {
            foreach (ArtilleryRaid raid in Plan.Batteries(Map))
            {
                ArtilleryPlacement slot = raid.Camp.Guns.Find(g => g.Operator == pawn);
                if (slot == null) continue;
                owner = raid;
                return slot;
            }
            owner = null;
            return null;
        }

        internal void SignalReview() => nextReview = Math.Min(nextReview, Now + 1);

        internal void Alert()
        {
            bool quiet = !OnAlert;
            AlertUntil = Now + Hour;
            if (quiet) SignalReview();
        }

        partial void TickCampaign()
        {
            if (Phase == 0)
            {
                if (Now >= PhaseDeadline) StartCamp();
                return;
            }
            if (Now < nextReview && (Phase == 4 || Now < PhaseDeadline)) return;
            Review();
        }

        internal void StartCamp()
        {
            if (Phase != 0) return;
            BeginBlockade();
            if (!Mechanoid) PersonnelSeed = Rand.Range(1, int.MaxValue);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Plan = EncirclementSiting.Plan(this);
            if (Prefs.DevMode)
                Log.Message($"[RimBreach Encirclement] Siting took {timer.ElapsedMilliseconds} ms, " +
                    $"{Plan?.BatteryPlanningPasses ?? 0} battery searches, rear {Plan?.Rear}, batteries {Plan?.Front.Count ?? 0}.");
            if (Plan == null)
            {
                End(false);
                return;
            }

            var registry = Map.GetComponent<ArtilleryMap>();
            var incident = new ArtilleryIncident { Encirclement = true };
            registry.RegisterIncident(incident);
            IncidentKey = incident.Key;
            Plan.RegisterBatteries(this);
            incident.SetupComplete = true;
            Work = new EncirclementWork();
            EncirclementBarracksBuilder.LayOut(this);

            SetPhase(1, 24 * Hour);
            CampStartedAt = Now;
            LandWave(1);
            if (Plan.Exposed) StartScreen(0, opening: true);
            if (Plan.AssaultFallback)
            {
                LandWave(2);
                fallbackWaiting = true;
                PhaseDeadline = Now + Timer(2 * Hour);
            }
            Review();
        }

        private void LandWave(int number)
        {
            if (number <= Waves.Count) return;
            EncirclementWave wave = EncirclementWaveSpawner.Spawn(this, number);
            Waves.Add(wave);
            if (wave.Pawns.Count != 0)
            {
                if (CampLord == null || !Map.lordManager.lords.Contains(CampLord))
                    CampLord = LordMaker.MakeNewLord(Faction, new LordJob_EncirclementCamp(this), Map, wave.Pawns);
                else
                    CampLord.AddPawns(wave.Pawns);
            }
            string title = number switch
            {
                1 => "RS_EncirclementArrivalTitle",
                2 => "RS_EncirclementReinforcementsTitle",
                _ => "RS_EncirclementMainForceTitle"
            };
            string bodyKey = number switch
            {
                1 => "RS_EncirclementWave1",
                2 => "RS_EncirclementWave2",
                _ => "RS_EncirclementWave3"
            };
            Find.LetterStack.ReceiveLetter(title.Translate(),
                bodyKey.Translate(Faction.NameColored, MapParent.LabelCap),
                LetterDefOf.ThreatBig, new LookTargets(Plan.Rear, Map), Faction, quest);
        }

        private void SetPhase(int phase, int length)
        {
            Phase = phase;
            PhaseStart = Now;
            // Compress the wait before landing and the assembly buffer. Keep construction,
            // smoke and weapon timings on the same game clock as their AI.
            PhaseDeadline = Now + (phase is 1 or 2 ? length : Timer(length));
            nextReview = Now + 1;
        }

        internal void StartBatteries()
        {
            if (Phase != 1 || fallbackWaiting) return;
            StopScreen();
            batteryStalledSince.Clear();
            abandonedBatteries.Clear();
            SetPhase(2, 36 * Hour);
            LandWave(2);
            ActiveCluster = 1;
            NextGunTick = Now;
            if (Mechanoid && Battery(ActiveCluster) != null) StartScreen(ActiveCluster, opening: false);
        }

        internal void StartAssembly()
        {
            if (Phase >= 3) return;
            if (Mechanoid) StopScreen();
            else
            {
                Screen?.Work.RestoreWeapons();
                Screen = null;
            }
            noGunsSince = -1;
            batteryStalledSince.Clear();
            Work?.CancelUnbuilt(this);
            SetPhase(3, 10 * Hour);
            LandWave(2);
            LandWave(3);
            AssemblyRoster = Waves.SelectMany(w => w.Pawns).Where(InBattle).Select(p => p.thingIDNumber).ToHashSet();
            if (Mechanoid)
            {
                RefreshForwardBuilders();
                RefreshMechEscorts();
            }
            CampLord?.CurLordToil.UpdateAllDuties();
        }

        internal void StartAssault()
        {
            if (Phase != 3) return;
            StopScreen();
            SetPhase(4, 0);
            EncirclementAssaultHandoff.Transfer(this);
        }

        internal void Review(bool debug = false)
        {
            nextReview = Now + ReviewInterval;
            foreach (EncirclementWave wave in Waves) wave.RecordLosses(this);
            if (Phase == 4)
            {
                EncirclementAssaultHandoff.ResumeLegacyAssault(this);
                EncirclementAssaultHandoff.ReclaimRecovered(this);
                if (EncirclementAssaultHandoff.Defeated(this)) Win();
                return;
            }
            ReclaimRecoveredToCamp();

            if (!rearSupplied && Battery(0) is { } main)
            {
                var initial = Work.Projects.Where(p => p.Battery == 0 &&
                    (p.Room < 0 || Plan.Rooms[p.Room].Device == null)).ToList();
                int humans = CampLord?.ownedPawns.Count(p => p.RaceProps.Humanlike) ?? 0;
                rearSupplied = Work.Fund(this, main, initial, initialPersonnel: humans);
            }

            ReviewConstruction(debug);
            RefreshThreats();
            ReviewRestingCrew();
            RefreshMedics();
            if (UsesHumanBatteryCrews) ReviewHumanBatteries(debug);
            else
            {
                RefreshGunners();
                ReviewScreen();
                RefreshForwardBuilders();
            }
            RefreshMechEscorts();
            CampLord?.CurLordToil.UpdateAllDuties();

            if (Phase == 1)
            {
                // Eight or more barracks must not delay the first device until every upper deck is finished.
                bool firstShelter = Mechanoid || Plan.Rooms.Any(r => r.Device == null && r.GroundConcealed);
                if (!openedHarassment && rearSupplied && (firstShelter || Now >= PhaseStart + 2 * Hour) &&
                    Plan.Rooms.Any(r => r.Device != null) && Battery(0) is { } battery)
                {
                    openedHarassment = Work.Fund(this, battery, Work.Projects.Where(p =>
                        p.Room >= 0 && Plan.Rooms[p.Room].Device != null).ToList());
                }

                if (debug) return;
                bool deviceDestroyed = openedHarassment && Work.Projects.Any(p => p.DeviceDestroyed(Map));
                bool harassmentReady = openedHarassment && Work.Projects.Where(p => p.IsDevice)
                    .All(p => p.Finished(Map) || p.DeviceDestroyed(Map));
                int barracks = Plan.Rooms.Count(r => r.Device == null);
                bool housingReady = Plan.Rooms.Count(r => r.Device == null && r.GroundConcealed) * 3 >= barracks * 2;
                bool rearBuilt = Battery(0)?.Camp.Guns.All(g => g.Building is { Spawned: true }) != false;
                if (fallbackWaiting)
                {
                    if (rearBuilt || Now >= PhaseDeadline || Alive == 0) StartAssembly();
                }
                else if (deviceDestroyed || harassmentReady && housingReady && rearBuilt ||
                    Now >= PhaseDeadline || Alive == 0) StartBatteries();
            }
            else if (Phase == 2)
            {
                ReviewForwardStalls(debug);
                if (Mechanoid) ReviewBatteries(debug);
                float required = Mathf.Min(HeavyTarget, HeavyPlanned);
                bool built = required > 0f && HeavyBuilt >= required;
                bool anyForwardStarted = Work.Projects.Any(p => p.Battery > 0 && p.Funded) ||
                    abandonedBatteries.Count >= Plan.BatteryKeys.Count - 1;
                bool forwardExhausted = Plan.BatteryKeys.Count > 1 &&
                    (!Mechanoid ? anyForwardStarted : Battery(ActiveCluster) == null) &&
                    Work.Projects.Where(p => p.Battery > 0 && !abandonedBatteries.Contains(p.Battery))
                        .All(p => !Work.NeedsBatteryBuild(Map, p));
                bool blocked = Plan.BatteryKeys.Count == 1 && Now >= PhaseStart + 2 * Hour;
                bool collapsing = Now >= PhaseStart + Hour && Losses * 2 >= Headcount;
                if (!debug && (built || forwardExhausted || blocked || Now >= PhaseDeadline || Alive == 0 || collapsing))
                    StartAssembly();
            }
            else if (Phase == 3 && !debug)
            {
                foreach (Pawn pawn in Waves.SelectMany(w => w.Pawns))
                    if (pawn != null && AssemblyRoster.Contains(pawn.thingIDNumber) && !InBattle(pawn))
                        AssemblyLosses.Add(pawn.thingIDNumber);
                bool noLiveGuns = !HasLiveSiegeGuns();
                if (noLiveGuns)
                {
                    if (noGunsSince < 0) noGunsSince = Now;
                }
                else noGunsSince = -1;
                bool gunsLostReady = noLiveGuns &&
                    Now >= PhaseStart + Timer(4 * Hour) &&
                    Now >= noGunsSince + Timer(Hour);
                if (Now >= PhaseDeadline || AssemblyLosses.Count * 4 >= AssemblyRoster.Count || gunsLostReady)
                    StartAssault();
            }
            if (Phase < 4 && !debug) Resupply();
        }

        private void ReclaimRecoveredToCamp()
        {
            var recovered = Waves.SelectMany(w => w.Pawns)
                .Where(p => InBattle(p) && p.GetLord() == null).Distinct().ToList();
            if (recovered.Count == 0) return;
            if (CampLord == null || !Map.lordManager.lords.Contains(CampLord))
                CampLord = LordMaker.MakeNewLord(Faction, new LordJob_EncirclementCamp(this), Map, recovered);
            else
                CampLord.AddPawns(recovered);
        }

        private bool HasLiveSiegeGuns() => Plan.Batteries(Map).Any(r => r.Camp.Guns.Any(g =>
            g.IsSupport && g.Building is { Spawned: true, Destroyed: false } b && b.Faction == Faction &&
            b.TryGetComp<CompBreakdownable>()?.BrokenDown != true));

        internal bool HasActiveCoverFor(int batteryIndex)
        {
            if (batteryIndex <= 0) return false;
            if (Mechanoid) return Battery(batteryIndex) is { } b && MechSupportShield.ActiveNear(Map, Faction, b.Camp.Center);
            return ScreenAt(batteryIndex)?.Work is { } s && s.Assignments.Any(a => InBattle(a.Pawn));
        }

        private void ReviewConstruction(bool debug)
        {
            int repairOrders = 0;
            var rooms = new List<EncirclementProject>[Plan.Rooms.Count];
            Work.ActiveProjects.Clear();
            foreach (EncirclementProject project in Work.Projects)
            {
                if (project.Room >= 0) (rooms[project.Room] ??= new()).Add(project);
                if (!project.Funded) continue;
                Work.Observe(this, project);
                bool available = project.Room < 0 || project.Step <= Plan.Rooms[project.Room].Stage;
                if (available)
                {
                    bool repair = project.Placed && project.Current == null && !project.Finished(Map);
                    bool throttled = repair && project.Battery == 0;
                    if ((!throttled || repairOrders < 4 || debug) && Work.TryPlace(this, project, debug) && throttled)
                        repairOrders++;
                    if (debug) FinishProject(project);
                }
                if (project.Current is { Spawned: true } target && (target is Blueprint or Frame ||
                    target.def.useHitPoints && target.HitPoints < target.MaxHitPoints))
                    Work.ActiveProjects.Add(project);
            }
            for (int i = 0; i < rooms.Length; i++)
                EncirclementBarracksBuilder.ReviewRoom(this, Plan.Rooms[i], rooms[i], debug);
        }

        private void ReviewForwardStalls(bool debug)
        {
            if (debug || Phase != 2 || Plan.BatteryKeys.Count <= 1) return;
            for (int index = 1; index < Plan.BatteryKeys.Count; index++)
            {
                if (abandonedBatteries.Contains(index) || Battery(index) == null)
                {
                    batteryStalledSince.Remove(index);
                    continue;
                }
                var unfinishedGuns = Work.Projects.Where(p => p.Battery == index && p.Gun >= 0 && p.Funded &&
                    Work.NeedsBatteryBuild(Map, p)).ToList();
                if (unfinishedGuns.Count == 0)
                {
                    batteryStalledSince.Remove(index);
                    continue;
                }
                bool stalled = !HasActiveCoverFor(index) && unfinishedGuns.All(p =>
                    !SmokeCombat.Screened(p.Cell, Map) &&
                    (Work.UnderFire(Map, p.Cell) || !Mechanoid && !CellSafeForCamp(p.Cell)));
                if (!stalled)
                {
                    batteryStalledSince.Remove(index);
                    continue;
                }
                if (!batteryStalledSince.TryGetValue(index, out int since))
                    batteryStalledSince[index] = Now;
                else if (Now >= since + ForwardStallLimit)
                {
                    abandonedBatteries.Add(index);
                    batteryStalledSince.Remove(index);
                    Work.CancelUnbuilt(this, index);
                    SignalReview();
                }
            }
        }

        private void ReviewBatteries(bool debug)
        {
            ArtilleryRaid battery = Battery(ActiveCluster);
            if (battery == null) return;
            var projects = Work.Projects.Where(p => p.Battery == ActiveCluster).ToList();
            if (abandonedBatteries.Contains(ActiveCluster) ||
                OrderedGuns >= battery.Camp.Guns.Count && projects.Where(p => p.Gun >= 0).All(p => !Work.NeedsBatteryBuild(Map, p)))
            {
                AdvanceBattery();
                return;
            }
            if (OrderedGuns >= battery.Camp.Guns.Count) return;
            if (!debug && Mechanoid && shieldedBattery != ActiveCluster && Now < NextGunTick + Hour) return;
            if (!Work.Fund(this, battery, projects)) return;
            OrderedGuns = battery.Camp.Guns.Count;
            NextGunTick = Now;
        }

        internal void AdvanceBattery()
        {
            StopScreen();
            ActiveCluster++;
            OrderedGuns = 0;
            NextGunTick = Now;
            if (Battery(ActiveCluster) != null) StartScreen(ActiveCluster, opening: false);
        }

        private void RefreshGunners()
        {
            if (Mechanoid || CampLord == null) return;
            var batteries = Plan.Batteries(Map).ToList();
            var assigned = RetainGunners(batteries, (_, gun) => gun.Building?.Spawned == true && GunReady(gun));
            foreach (ArtilleryRaid raid in batteries)
                foreach (ArtilleryPlacement gun in raid.Camp.Guns)
                {
                    if (!gun.NeedsOperator || gun.Building?.Spawned != true || gun.Operator != null || !GunReady(gun)) continue;
                    gun.Operator = CampLord.ownedPawns.Where(p => CrewAvailable(p) && !assigned.Contains(p) &&
                            ScreenFor(p)?.Work.HasCarrier(p) != true && ArtilleryCamp.OperatorCandidate(p) &&
                            (OnAlert || !NeedsRest(p)))
                        .OrderByDescending(p => p.skills.GetSkill(SkillDefOf.Shooting).Level)
                        .ThenBy(p => p.Position.DistanceToSquared(gun.Cell)).FirstOrDefault(p =>
                            p.CanReach(gun.Building, PathEndMode.InteractionCell, Danger.Deadly));
                    if (gun.Operator != null) assigned.Add(gun.Operator);
                }
        }

        private HashSet<Pawn> RetainGunners(IEnumerable<ArtilleryRaid> batteries,
            Func<ArtilleryRaid, ArtilleryPlacement, bool> canStaff)
        {
            var assigned = new HashSet<Pawn>();
            foreach (ArtilleryRaid raid in batteries)
                foreach (ArtilleryPlacement gun in raid.Camp.Guns)
                {
                    Pawn pawn = gun.Operator;
                    if (pawn == null) continue;
                    if (!CrewAvailable(pawn) || pawn.GetLord() != CampLord || !canStaff(raid, gun) ||
                        !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) ||
                        !OnAlert && NeedsRest(pawn) || !assigned.Add(pawn)) gun.Operator = null;
                }
            return assigned;
        }

        internal static bool NeedsRest(Pawn pawn) => pawn.RaceProps.Humanlike &&
            (pawn.health.summaryHealth.SummaryHealthPercent < 0.78f ||
                pawn.needs?.rest?.CurLevelPercentage < 0.35f || pawn.needs?.food?.CurLevelPercentage < 0.27f);

        internal bool GunReady(ArtilleryPlacement gun)
        {
            if (Phase >= 4) return true;
            if (gun.Role == ArtilleryGunRole.CampDefense) return mannedDefensePosts.Contains(gun.Building);
            int start = CampStartedAt < 0 ? Phase == 1 ? PhaseStart : Now - 2 * Hour : CampStartedAt;
            return gun.IsDirectSupport || Now >= start + 2 * Hour;
        }

        private void RefreshThreats()
        {
            var nearby = Map.mapPawns.SpawnedPawnsInFaction(RimWorld.Faction.OfPlayer).Where(p =>
                !p.Downed && (p.RaceProps.Humanlike || p.RaceProps.IsMechanoid)).ToList();
            mannedDefensePosts.Clear();
            foreach (ArtilleryPlacement gun in Plan.Batteries(Map).SelectMany(r => r.Camp.Guns))
            {
                if (gun.Role != ArtilleryGunRole.CampDefense || gun.Building is not Building_TurretGunCE { Spawned: true } turret) continue;
                if (nearby.Any(p => CanSee(turret.Position, p.Position, 32f) &&
                    (turret.CompFireArc == null || turret.CompFireArc.WithinFireArc(p.Position))))
                    mannedDefensePosts.Add(turret);
            }
            if (mannedDefensePosts.Count != 0 || nearby.Any(p => CanSee(Plan.Rear, p.Position, 25f) ||
                Plan.RearStations.Any(c => CanSee(c, p.Position, 18f)))) Alert();
        }

        private bool CanSee(IntVec3 from, IntVec3 to, float range) =>
            Integration.IsoFloor.Distance3D(from, to, Map) <= range &&
            (Integration.IsoFloor.SameFloor(from, to, Map)
                ? GenSight.LineOfSight(from, to, Map, skipFirstCell: true)
                : Integration.IsoFloor.CanSeeBetweenFloors(from, to, Map));

        private void StartScreen(int index, bool opening)
        {
            ArtilleryRaid raid = Battery(index);
            if (CampLord == null || raid == null) return;
            if (Mechanoid)
            {
                nextShield = Now;
                if (opening && MechSupportShield.TryDropNear(Map, Faction, Plan.Entries[0], Plan.Reservations(Map)))
                    shieldedBattery = index;
                return;
            }

            if (ArtillerySmokeDefOf.CE_Weapon_GrenadeSmoke == null) return;
            Screen = CreateScreen(raid, SmokeCarriers(Plan.Entries[0]).ToList(), opening);
        }

        private ArtilleryLocalSmoke CreateScreen(ArtilleryRaid raid, List<Pawn> carriers, bool opening, bool stationed = false)
        {
            IntVec3 focus = opening ? Plan.Entries[0] : raid.Camp.Center;
            var reserved = Plan.Reservations(Map);
            bool Allowed(IntVec3 cell) => !reserved.Contains(cell);
            ArtillerySmoke work = carriers.Count > 0 && !stationed
                ? ArtillerySmoke.PlanApproach(raid.Camp, carriers[0], raid.TargetFaction, carriers.Count, out _, out _,
                    turretsOnly: true) : null;
            if (work == null)
            {
                work = new ArtillerySmoke { Rally = focus, StartedScreen = true, ThreatFaction = raid.TargetFaction, TurretsOnly = true };
                if (opening)
                    foreach (IntVec3 entry in Plan.Entries)
                        work.AddLocalTarget(Map, entry + Plan.Facing.FacingCell * 3, Allowed);
                else if (carriers.Count > 0)
                    work.PopulateCampTargets(raid.Camp, focus, carriers[0], Map, Allowed);
                else AddBatterySmokeTargets(raid, work, Allowed);
                if (work.Targets.Count == 0) work.AddLocalTarget(Map, focus, Allowed);
            }

            work.Deadline = Now + 2 * Hour;
            var screen = new ArtilleryLocalSmoke(work);
            screen.Review(raid, CampLord, carriers, Now, Allowed, issueInitialKit: true);
            return screen;
        }

        private void AddBatterySmokeTargets(ArtilleryRaid raid, ArtillerySmoke work, Predicate<IntVec3> allowed)
        {
            foreach (ArtilleryPlacement gun in raid.Camp.Guns)
                work.AddLocalTarget(Map, gun.Cell + gun.Rotation.FacingCell * 2, allowed);
            if (work.Targets.Count == 0) work.AddLocalTarget(Map, raid.Camp.Center, allowed);
        }

        private IEnumerable<Pawn> SmokeCarriers(IntVec3 rally) => CampLord.ownedPawns
            .Where(p => CrewAvailable(p) && p.RaceProps.Humanlike && ArtillerySmoke.Ready(p) && GunFor(p, out _) == null)
            .OrderBy(p => Screen?.Work.HasCarrier(p) == true ? 0 : 1)
            .ThenBy(p => p.Position.DistanceToSquared(rally)).Take(3);

        private void ReviewScreen()
        {
            if (CampLord == null || Battery(WorkBattery) is not { } battery) return;
            if (Mechanoid)
            {
                if (Now >= nextShield && shieldedBattery != WorkBattery && (Phase == 2 || Phase == 1 && Plan.Exposed))
                {
                    nextShield = Now + ReviewInterval;
                    if (CampLord.ownedPawns.Any(p => LordJob_EncirclementCamp.Builder(p) &&
                        p.Position.InHorDistOf(battery.Camp.Center, 13f)) &&
                        MechSupportShield.TryDropNear(Map, Faction, battery.Camp.Center, Plan.Reservations(Map)))
                        shieldedBattery = WorkBattery;
                }
                return;
            }
            if (Screen == null) return;
            if (Phase != 2 && Now >= Screen.Work.Deadline)
            {
                StopScreen();
                return;
            }
            if (ArtillerySmokeDefOf.CE_Weapon_GrenadeSmoke == null)
            {
                StopScreen();
                return;
            }
            var reserved = Plan.Reservations(Map);
            Screen.Review(battery, CampLord, SmokeCarriers(Screen.Work.Rally).ToList(), Now, c => !reserved.Contains(c));
        }

        private void StopScreen()
        {
            Screen?.Work.RestoreWeapons();
            Screen = null;
            foreach (ArtilleryLocalSmoke screen in forwardScreens.Values)
                screen.Work.RestoreWeapons();
            forwardScreens.Clear();
            forwardRallies.Clear();
        }

        private void Resupply()
        {
            if (Now < nextSupplies) return;
            nextSupplies = Now + 600;
            if (Battery(0) is { } rear)
            {
                Work.SupplyConstruction(this, rear);
                if (!Mechanoid) Work.SupplyRations(this, rear, Alive);
            }
            foreach (ArtilleryRaid raid in Plan.Batteries(Map))
                Work.SupplyAmmunition(this, raid);
        }

        private void Win()
        {
            foreach (Pawn pawn in Map.mapPawns.FreeColonistsSpawned)
                pawn.needs?.mood?.thoughts?.memories?.TryGainMemory(EncirclementDefs.RS_EncirclementBroken);
            Find.LetterStack.ReceiveLetter("RS_EncirclementVictoryTitle".Translate(),
                "RS_EncirclementVictory".Translate(Faction.NameColored), LetterDefOf.PositiveEvent, MapParent);
            End(true);
        }

        partial void CleanCampaign()
        {
            StopScreen();
            if (Map != null && Plan != null)
            {
                Work?.Stop(this);
                Map.GetComponent<EncirclementFog>().RevealAll();
                EncirclementAssaultHandoff.Cleanup(this);
            }
            CampLord = AssaultLord = null;
            Plan = null;
            Work = null;
            Waves.Clear();
            medics.Clear();
            forwardBuilders.Clear();
            mechEscorts.Clear();
            restingCrew.Clear();
            batteryStalledSince.Clear();
            abandonedBatteries.Clear();
            noGunsSince = -1;
        }

        partial void ExposeCampaign()
        {
            Scribe_Deep.Look(ref Plan, "campPlan");
            Scribe_Deep.Look(ref Work, "work");
            Scribe_Deep.Look(ref Screen, "screen");
            Scribe_Collections.Look(ref forwardScreens, "forwardScreens", LookMode.Value, LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) forwardScreens ??= new();
            Scribe_Collections.Look(ref Waves, "waves", LookMode.Deep);
            Scribe_References.Look(ref CampLord, "campLord");
            Scribe_References.Look(ref AssaultLord, "assaultLord");
            Scribe_Values.Look(ref IncidentKey, "incidentKey");
            Scribe_Values.Look(ref PersonnelSeed, "personnelSeed");
            Scribe_Values.Look(ref ActiveCluster, "activeCluster");
            Scribe_Values.Look(ref CampStartedAt, "campStartedAt", -1);
            Scribe_Values.Look(ref AlertUntil, "alertUntil");
            Scribe_Values.Look(ref OrderedGuns, "orderedGuns");
            Scribe_Values.Look(ref NextGunTick, "nextGunTick");
            Scribe_Collections.Look(ref AssemblyRoster, "assemblyRoster", LookMode.Value);
            Scribe_Collections.Look(ref AssemblyLosses, "assemblyLosses", LookMode.Value);
            Scribe_Values.Look(ref nextSupplies, "nextSupplies");
            Scribe_Values.Look(ref nextShield, "nextShield");
            Scribe_Values.Look(ref shieldedBattery, "shieldedBattery", -1);
            Scribe_Values.Look(ref noGunsSince, "noGunsSince", -1);
            Scribe_Values.Look(ref rearSupplied, "rearSupplied");
            Scribe_Values.Look(ref openedHarassment, "openedHarassment");
            Scribe_Values.Look(ref fallbackWaiting, "fallbackWaiting");
            Scribe_Collections.Look(ref batteryStalledSince, "batteryStalledSince", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref abandonedBatteries, "abandonedBatteries", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                batteryStalledSince ??= new();
                abandonedBatteries ??= new();
            }
            ExposeCrews();
        }

        private void FinishProject(EncirclementProject project)
        {
            if (project.Current is not (Blueprint or Frame)) return;
            Work.ConsumeDebugMaterials(this, project);
            if (project.Slab != null)
            {
                project.Current.Destroy(DestroyMode.Vanish);
                Map.terrainGrid.SetTerrain(project.Cell, project.Slab);
            }
            else
            {
                var building = project.Install ? project.PackedBuilding?.InnerThing : ThingMaker.MakeThing(project.Building, project.Stuff);
                if (building == null) return;
                project.Current.Destroy(DestroyMode.Vanish);
                if (project.Install) building = project.PackedBuilding.GetDirectlyHeldThings().Take(building, 1);
                building.SetFactionDirect(Faction);
                GenSpawn.Spawn(building, project.Cell, Map, project.Rotation, WipeMode.VanishOrMoveAside);
                if (project.Install && !project.PackedBuilding.Destroyed) project.PackedBuilding.Destroy(DestroyMode.Vanish);
            }
            Work.Observe(this, project);
        }
    }

    [StaticConstructorOnStartup]
    internal static class EncirclementFloorChanges
    {
        static EncirclementFloorChanges() => ProjectZ.Api.FloorChanged += Changed;

        private static void Changed(Map map, IntVec3 cell, bool built)
        {
            var campaign = GameCondition_Encirclement.CampaignOn(map);
            if (campaign?.Work?.OwnsSlab(cell) == true) campaign.SignalReview();
        }
    }
}
