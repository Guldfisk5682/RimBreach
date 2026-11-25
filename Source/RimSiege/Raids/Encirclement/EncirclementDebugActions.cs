using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LudeonTK;
using RimSiege.Integration;
using RimSiege.Raids.Artillery;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimSiege.Raids.Encirclement
{
    public static class EncirclementDebugActions
    {
        [DebugAction("RimBreach", "Encirclement", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static List<DebugActionNode> Menu()
        {
            var devices = new DebugActionNode("Start Encirclement (Specify Harassment)");
            foreach (var (label, def) in new[]
            {
                ("Weather", EncirclementDefs.RS_FieldWeatherController),
                ("Climate", EncirclementDefs.RS_FieldClimateAdjuster),
                ("Toxic", EncirclementDefs.RS_FieldToxicSpewer),
                ("EMI", EncirclementDefs.RS_FieldEMIDynamo)
            })
                devices.AddChild(StartMenu(label, fast: true, device: def));
            devices.AddChild(Action("Mechanoid 3x", () => Start(Faction.OfMechanoids, 10000, fast: true, threeDevices: true)));

            var phases = new DebugActionNode("Phase jump");
            phases.AddChild(Action("T+12h (Veil & Shuttle Blockade)", () => WithSiege(s => s.BeginBlockade(), start: true)));
            phases.AddChild(Action("Phase 1 (Land Batch 1)", () => Jump(1)));
            phases.AddChild(Action("Fast-finish Phase 1 Barracks & Defenses",
                () => WithSiege(s => { s.StartCamp(); FastFinish(s, 10); })));
            phases.AddChild(Action("Phase 2 (Land Batch 2 & Deploy Batteries)", () => Jump(2)));
            phases.AddChild(Action("Fast-finish Active Battery & Advance", () => WithSiege(FinishBattery)));
            phases.AddChild(Action("Phase 3 (Land Batch 3)", () => Jump(3)));
            phases.AddChild(Action("Phase 4 (Grand Assault & Handoff)", () => Jump(4)));

            var cases = new DebugActionNode("Inject test case");
            cases.AddChild(Action("Destroy Harassment Device", () => WithSiege(DestroyDevice)));
            cases.AddChild(Action("Simulate 50% Batch 1+2 Casualties", () => WithSiege(Casualties)));
            cases.AddChild(StartMenu("Force Exposed-Edge Fallback", fast: true, exposed: true));
            cases.AddChild(StartMenu("Force No Buildable Camp", fast: true, exposed: true, noCamp: true));
            cases.AddChild(Action("Trigger Camp Damage (Test Auto-Repair)", () => WithSiege(DamageCamp)));
            cases.AddChild(Action("Force End / Cleanup Encirclement", () => WithSiege(s =>
            {
                Map map = s.Map;
                s.End(false);
                Log.Message($"[RimBreach Encirclement] Cleanup: {EncirclementDebug.GetSnapshot(map)}");
            })));

            return new()
            {
                StartMenu("Start Encirclement (Normal Time)", fast: false),
                StartMenu("Start Encirclement (Fast Warning & Assembly)", fast: true),
                devices, phases, cases,
                Action("Toggle Plan Overlay", () => Find.CurrentMap.GetComponent<EncirclementDebugView>().Toggle()),
                Action("Dump State to Log", () => Log.Message(EncirclementDebug.GetSnapshot(Find.CurrentMap)))
            };
        }

        private static DebugActionNode Action(string label, System.Action action) =>
            new(label, DebugActionType.Action, action);

        private static DebugActionNode StartMenu(string label, bool fast, ThingDef device = null, bool exposed = false,
            bool noCamp = false)
        {
            var menu = new DebugActionNode(label);
            foreach (var (name, matches) in new (string, Func<Faction, bool>)[]
            {
                ("Industrial", f => f.def.techLevel == TechLevel.Industrial && f != Faction.OfMechanoids),
                ("Empire", f => f.def.defName == "Empire"),
                ("Mechanoid", f => f == Faction.OfMechanoids)
            })
            {
                var group = new DebugActionNode(name);
                foreach (int points in new[] { 8000, 10000, 15000 })
                    group.AddChild(Action($"{points} points", () => Start(QuestNode_Root_Encirclement.Factions()
                        .FirstOrDefault(matches), points, fast, device, exposed: exposed, noCamp: noCamp)));
                menu.AddChild(group);
            }
            return menu;
        }

        private static QuestPart_Encirclement Start(Faction faction, int points, bool fast,
            ThingDef device = null, bool threeDevices = false, bool exposed = false, bool noCamp = false)
        {
            Map map = Find.CurrentMap;
            QuestPart_Encirclement siege = QuestNode_Root_Encirclement.Start(map, faction, points, debug: true,
                fast, device, threeDevices, exposed, noCamp);
            if (siege == null)
                Messages.Message("Encirclement needs a surface colony with a legal entry and an eligible hostile faction.",
                    MessageTypeDefOf.RejectInput, historical: false);
            else if (exposed)
                siege.StartCamp();
            return siege;
        }

        private static void WithSiege(Action<QuestPart_Encirclement> action, bool start = false)
        {
            var siege = GameCondition_Encirclement.CampaignOn(Find.CurrentMap);
            if (siege == null && start)
                siege = Start(QuestNode_Root_Encirclement.Factions().OrderBy(f => f == Faction.OfMechanoids)
                    .FirstOrDefault(), 8000, fast: false);
            if (siege != null) action(siege);
        }

        private static void Jump(int phase) => WithSiege(s =>
        {
            if (s.Phase == 0) s.StartCamp();
            if (s.quest.Historical || s.Phase >= phase) return;
            if (s.Phase == 1 && phase >= 2)
            {
                FastFinish(s, 13);
                s.StartBatteries();
                if (s.Phase == 1) s.StartAssembly(); // Small-map fallback.
                s.Review();
            }
            if (s.Phase == 2 && phase >= 3)
            {
                if (s.ParallelBatteries) FastFinish(s, 8);
                else while (s.Battery(s.ActiveCluster) != null) FinishBattery(s);
                s.StartAssembly();
            }
            if (s.Phase == 3 && phase >= 4) s.StartAssault();
        }, start: true);

        private static void FinishBattery(QuestPart_Encirclement siege)
        {
            if (siege.ParallelBatteries)
            {
                FastFinish(siege, 6);
                return;
            }
            if (siege.Phase != 2 || siege.Battery(siege.ActiveCluster) == null) return;
            int index = siege.ActiveCluster;
            for (int n = 0; n < 6 && siege.ActiveCluster == index; n++)
                FastFinish(siege, 1);
            if (siege.ActiveCluster == index) siege.AdvanceBattery();
        }

        private static void FastFinish(QuestPart_Encirclement siege, int passes)
        {
            if (siege.Phase is < 1 or > 3) return;
            for (int i = 0; i < passes; i++)
            {
                OpenSupplies(siege);
                siege.Review(debug: true);
            }
        }

        private static readonly Action<DropPodIncoming> Impact = AccessTools.MethodDelegate<Action<DropPodIncoming>>(
            AccessTools.DeclaredMethod(typeof(DropPodIncoming), "Impact"));
        private static readonly Action<ActiveTransporter> Open = AccessTools.MethodDelegate<Action<ActiveTransporter>>(
            AccessTools.DeclaredMethod(typeof(ActiveTransporter), "PodOpen"));

        private static void OpenSupplies(QuestPart_Encirclement siege)
        {
            ArtilleryRaid rear = siege.Battery(0);
            if (rear == null) return;
            var supply = siege.Map.GetComponent<ArtilleryMap>().SupplyOwnership.ItemsFor(rear);
            var containers = supply.Select(t => t.SpawnedParentOrMe).Distinct().ToArray();
            foreach (Thing thing in containers)
                if (thing is DropPodIncoming { Spawned: true } drop) Impact(drop);
            // The impact creates an ActiveTransporter at the same position.
            foreach (ActiveTransporter pod in supply.Select(t => t.SpawnedParentOrMe).OfType<ActiveTransporter>()
                .Where(t => t.Spawned).Distinct().ToArray())
                Open(pod);
        }

        private static void DestroyDevice(QuestPart_Encirclement siege)
        {
            var device = siege.Work?.Projects.Where(p => p.IsDevice && p.Current is { Spawned: true })
                .OrderByDescending(p => p.Current is Building and not Frame).FirstOrDefault();
            device?.Current.Destroy(DestroyMode.KillFinalize);
            if (siege.Phase is >= 1 and <= 3) siege.Review(debug: false);
        }

        private static void Casualties(QuestPart_Encirclement siege)
        {
            if (siege.Phase == 1) siege.StartBatteries();
            if (siege.Phase != 2) return;
            foreach (EncirclementWave wave in siege.Waves) wave.RecordLosses(siege);
            int count = (siege.Headcount + 1) / 2 - siege.Losses;
            foreach (Pawn pawn in siege.Waves.Take(2).SelectMany(w => w.Pawns).Where(siege.InBattle)
                .OrderBy(LordJob_EncirclementCamp.Builder).Take(Math.Max(0, count)).ToArray())
                HealthUtility.DamageUntilDowned(pawn, allowBleedingWounds: false);
            siege.Review();
        }

        private static void DamageCamp(QuestPart_Encirclement siege)
        {
            if (siege.Phase is < 1 or > 3) return;
            var projects = siege.Work.Projects;
            foreach (var p in projects.Where(p => p.Building == ThingDefOf.Wall && p.Room >= 0 &&
                p.Step == 0 && p.Current is Building and not Frame).InRandomOrder().Take(2))
                p.Current.Destroy(DestroyMode.KillFinalize);
            if (projects.FirstOrDefault(p => p.Slab != null && p.Finished(siege.Map)) is { } slab)
                siege.Map.terrainGrid.SetTerrain(slab.Cell, DefDatabase<TerrainDef>.GetNamed("PZ_OpenAir"));
            var cover = projects.FirstOrDefault(p => p.Building == ThingDefOf.Sandbags && p.Current is Building and not Frame);
            cover?.Current.Destroy(DestroyMode.KillFinalize);
            siege.SignalReview();
        }
    }

    public static class EncirclementDebug
    {
        public static string GetSnapshot(Map map)
        {
            var s = GameCondition_Encirclement.CampaignOn(map);
            if (s == null) return "[RimBreach Encirclement] No active campaign.";
            float personnel = Enumerable.Range(1, 3).Sum(i =>
                i <= s.Waves.Count ? s.Waves[i - 1].Points : EncirclementWaveSpawner.PointsFor(s, i));
            var lines = new List<string>
            {
                $"[RimBreach Encirclement] Quest {s.quest.id}, phase {s.Phase}, elapsed {s.Now - s.PhaseStart}, " +
                $"remaining {Math.Max(0, s.PhaseDeadline - s.Now)} ticks, fast={s.Fast}, blockade={s.Condition.Blockaded}",
                $"Sky glow {map.skyManager.CurSkyGlow:F3}, alert {s.OnAlert}; {s.Condition.Description}",
                $"Faction {s.Faction.Name}; base {s.BasePoints:F0}, personnel {personnel:F0}; " +
                $"waves {string.Join(", ", s.Waves.Select(w => $"{w.Points:F0}pts/{w.Count} pawns/{w.Losses.Count} lost"))}; " +
                $"active {s.Alive}; camp lord {s.CampLord?.loadID}, assault lord {s.AssaultLord?.loadID}, incident {s.IncidentKey}"
            };
            if (s.Plan == null) return string.Join("\n", lines);
            lines.Add($"Rear {s.Plan.Rear}; entries {string.Join(", ", s.Plan.Entries)}; " +
                $"exposed={s.Plan.Exposed}; fallback={s.Plan.AssaultFallback}; fogged rooms {map.GetComponent<EncirclementFog>().RoomCount}");
            if (s.Plan.PlannedHumans >= 0)
                lines.Add($"Housing for {s.Plan.PlannedHumans} planned humanlike raiders: " +
                    $"{s.Work.Projects.Count(p => p.Install && p.Room >= 0)}/{s.Plan.BedrollLimit} bedrolls in {s.Plan.RearStations.Count} barracks.");
            foreach (var (room, index) in s.Plan.Rooms.Select((room, index) => (room, index)))
            {
                var projects = s.Work.Projects.Where(p => p.Room == index).ToList();
                lines.Add($"{room.Footprint}: step {room.Stage}, roofs/fog {room.GroundConcealed}/{room.UpperConcealed}, " +
                    $"slabs {projects.Count(p => p.Slab != null && p.Finished(map))}, beds {projects.Count(p => p.Install && p.Finished(map))}");
                if (projects.FirstOrDefault(p => p.IsDevice) is { } device)
                    lines.Add($"  {device.Building.defName}, HP {device.Current?.HitPoints ?? 0}, rebuilds {device.Rebuilt}, " +
                        $"conditions {string.Join(", ", (device.Current as Building)?.GetComp<CompEncirclementDevice>()?.CausedConditions
                            .Where(c => c.gameConditionManager.ActiveConditions.Contains(c))
                            .Select(c => $"{c.LabelCap} ({c.Description})") ?? Enumerable.Empty<string>())}");
            }
            lines.Add($"Heavy gun points {s.HeavyBuilt:F0}/{s.HeavyTarget:F0}; planned {s.HeavyPlanned:F0}; active battery {s.ActiveCluster}, " +
                $"smoke carriers {s.Screen?.Work.Assignments.Count ?? 0}");
            if (s.Phase == 2)
            {
                int forwardGunners = s.Plan.Batteries(map).Skip(1).Sum(r => r.Camp.Guns.Count(g => g.Operator != null));
                lines.Add($"Forward builders {s.ForwardBuilderCount}, gunners {forwardGunners}, " +
                    $"resting crew {s.CampLord?.ownedPawns.Count(s.IsRestingCrew) ?? 0}");
            }
            if (s.Screen?.Work is { } smoke)
                lines.Add($"Smoke rally {smoke.Rally}, approach cells {smoke.ApproachRoute.Count}, " +
                    $"covered {smoke.Targets.Count(t => t.IsDone(map))}/{smoke.Targets.Count}, last throw {smoke.LastThrowTick}");
            for (int i = 0; i < s.Plan.BatteryKeys.Count; i++)
                if (s.Battery(i) is { } raid)
                    lines.Add($"  {i}: {raid.Camp.Center}; target {raid.Camp.BreachAnchor}, " +
                        $"distance {IsoFloor.RangeDistance(raid.Camp.Center, raid.Camp.BreachAnchor, map):F1}; " +
                        $"{string.Join(", ", raid.Camp.Guns.Select(g => g.Def.defName))}; " +
                        $"blueprints {raid.Camp.Guns.Count(g => g.Blueprint?.Spawned == true)}, " +
                        $"frames {raid.Camp.Guns.Count(g => g.Frame?.Spawned == true)}, built {raid.Camp.Guns.Count(g => g.Building?.Spawned == true)}, " +
                        $"builders {s.CampLord?.ownedPawns.Count(p => LordJob_EncirclementCamp.Builder(p) && s.BuilderBattery(p) == i) ?? 0}, " +
                        $"gunners {raid.Camp.Guns.Count(g => g.Operator != null)}, smoke {s.ScreenAt(i)?.Work.Assignments.Count ?? 0}, " +
                        $"last throw {s.ScreenAt(i)?.Work.LastThrowTick ?? 0}, " +
                        $"lord {raid.CampLord?.loadID}");
            if (s.Battery(0) is { } rear)
                lines.Add($"Rear defenses toward {s.Plan.Goal}: {string.Join(", ", rear.Camp.Guns
                    .Where(g => g.Role == ArtilleryGunRole.CampDefense).Select(g => $"{g.Cell}/{g.Rotation}"))}");
            return string.Join("\n", lines);
        }
    }

    public sealed class EncirclementDebugView : MapComponent
    {
        private bool visible;
        private EncirclementCampPlan plan;
        private List<IntVec3> exposed;
        private List<List<IntVec3>> paths;
        public EncirclementDebugView(Map map) : base(map) { }

        internal void Toggle()
        {
            visible = !visible;
            if (!visible) Reset();
        }

        private void Reset()
        {
            plan = null;
            exposed = null;
            paths = null;
        }

        public override void MapComponentUpdate()
        {
            if (!visible || !Prefs.DevMode || Find.CurrentMap != map || ProjectZ.Api.ViewedFloor != 0) return;
            var siege = GameCondition_Encirclement.CampaignOn(map);
            if (siege?.Plan == null) { Reset(); return; }
            if (plan != siege.Plan)
            {
                plan = siege.Plan;
                var threats = SmokeThreatCache.CollectThreats(map, includeColonists: false);
                CellRect ground = IsoFloor.GroundRect(map);
                var plot = plan.Reservations(map);
                foreach (IntVec3 entry in plan.Entries)
                    plot.UnionWith(GenRadial.RadialCellsAround(entry, 12f, true).Where(ground.Contains));
                exposed = plot.Where(c => c.InBounds(map) && SmokeThreatCache.IsExposedToThreats(c, map, threats)).ToList();
                paths = new();
                foreach (IntVec3 entry in plan.Entries)
                {
                    using PawnPath route = map.pathFinder.FindPathNow(entry, plan.Rear,
                        TraverseParms.For(TraverseMode.NoPassClosedDoors, Danger.Deadly), null, PathEndMode.OnCell);
                    if (route.Found) paths.Add(route.NodesReversed.ToList());
                }
                var main = SupportRoute.Build(map, plan.Rear, plan.Goal);
                if (main != null) paths.AddRange(main.Segments.Select(s => s.Nodes.ToList()));
            }
            foreach (EncirclementRoom room in plan.Rooms)
                GenDraw.DrawFieldEdges(room.Footprint.Cells.ToList(), Color.cyan);
            GenDraw.DrawRadiusRing(plan.Rear, 5f, Color.cyan);
            GenDraw.DrawFieldEdges(exposed, Color.red);
            foreach (List<IntVec3> route in paths)
                for (int i = 1; i < route.Count; i++)
                    GenDraw.DrawLineBetween(route[i - 1].ToVector3Shifted(), route[i].ToVector3Shifted(), SimpleColor.Yellow);
            foreach (ArtilleryRaid raid in plan.Batteries(map).Skip(1))
            {
                GenDraw.DrawRadiusRing(raid.Camp.Center, 5f, Color.green);
                foreach (ArtilleryPlacement gun in raid.Camp.Guns)
                    GenDraw.DrawLineBetween(gun.Cell.ToVector3Shifted(),
                        IsoFloor.GroundCellOf(raid.Camp.BreachAnchor, map).ToVector3Shifted(), SimpleColor.Green);
            }
        }
    }
}
