using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using RimSiege.Integration;
using RimSiege.Raids.Artillery;
using RimWorld;
using Verse;

namespace RimSiege.Raids.Encirclement
{
    internal sealed class EncirclementProject : IExposable
    {
        internal ThingDef Building, Stuff;
        internal TerrainDef Slab;
        internal IntVec3 Cell;
        internal Rot4 Rotation;
        internal int Room = -1, Step, Battery, Gun = -1;
        internal bool Install, Funded, Placed, BuiltOnce, MaterialCommitted, Medical;
        internal int Rebuilt;
        internal MinifiedThing PackedBuilding;
        internal Thing Current;

        internal BuildableDef Def => (BuildableDef)Building ?? Slab;
        internal bool IsDevice => Building?.GetCompProperties<CompProperties_CausesGameCondition>() != null;
        internal bool Finished(Map map) => Slab != null ? ProjectZ.Api.HasFloorAt(IsoFloor.GroundCellOf(Cell, map), 1, map) :
            Current is Verse.Building and not Frame;
        internal bool DeviceDestroyed(Map map) => IsDevice && Placed && Current == null && !Finished(map);

        public EncirclementProject() { }

        public void ExposeData()
        {
            Scribe_Defs.Look(ref Building, "building");
            Scribe_Defs.Look(ref Stuff, "stuff");
            Scribe_Defs.Look(ref Slab, "slab");
            Scribe_Values.Look(ref Cell, "cell");
            Scribe_Values.Look(ref Rotation, "rotation", Rot4.North);
            Scribe_Values.Look(ref Room, "room", -1);
            Scribe_Values.Look(ref Step, "step");
            Scribe_Values.Look(ref Battery, "battery");
            Scribe_Values.Look(ref Gun, "gun", -1);
            Scribe_Values.Look(ref Install, "install");
            Scribe_Values.Look(ref Medical, "medical");
            Scribe_Values.Look(ref Funded, "funded");
            Scribe_Values.Look(ref Placed, "placed");
            Scribe_Values.Look(ref BuiltOnce, "builtOnce");
            bool hadCommittedTag = Scribe.mode != LoadSaveMode.LoadingVars || Scribe.loader.curXmlParent?["materialCommitted"] != null;
            Scribe_Values.Look(ref MaterialCommitted, "materialCommitted", forceSave: true);
            if (!hadCommittedTag && BuiltOnce) MaterialCommitted = true;
            Scribe_Values.Look(ref Rebuilt, "rebuilt");
            if (Scribe.mode != LoadSaveMode.Saving || PackedBuilding is { Destroyed: false })
                Scribe_References.Look(ref PackedBuilding, "bedroll");
        }
    }

    internal sealed class EncirclementWork : IExposable
    {
        internal const float MaterialRadius = 45f;
        internal const int MaxForwardGunRebuilds = 1;
        private const float AmmunitionRadius = 22f;
        internal List<EncirclementProject> Projects = new();
        internal List<IntVec3> RoofCells = new();
        internal readonly List<EncirclementProject> ActiveProjects = new();
        private Dictionary<ThingDef, int> initialMaterials = new();
        private Dictionary<ThingDef, int> spentOnRepairs = new();
        private readonly Dictionary<IntVec3, List<EncirclementProject>> byCell = new();
        private readonly SmokeThreatCache repairThreats = new(includeColonists: false, threatFaction: null);
        private HashSet<IntVec3> reserved;

        public EncirclementWork() { }

        internal void Add(EncirclementProject project)
        {
            Projects.Add(project);
            if (!byCell.TryGetValue(project.Cell, out var at))
                byCell.Add(project.Cell, at = new());
            at.Add(project);
        }

        internal bool Owns(Thing target) => target != null && byCell.TryGetValue(target.Position, out var at) &&
            at.Any(p => p.Funded && p.Def == target.def.entityDefToBuild);

        internal bool OwnsBuilding(Building building) => byCell.TryGetValue(building.Position, out var at) &&
            at.Any(p => p.Funded && p.Building == (building.def.entityDefToBuild ?? building.def));

        internal bool OwnsSlab(IntVec3 cell) => byCell.TryGetValue(cell, out var at) &&
            at.Any(p => p.Funded && p.Slab != null);

        internal bool NeedsBatteryBuild(Map map, EncirclementProject project) => project.Gun >= 0 &&
            !project.Finished(map) && !project.BuiltOnce && (project.Current is Blueprint or Frame || !project.Placed ||
                !project.MaterialCommitted || project.Rebuilt < MaxForwardGunRebuilds);

        internal ArtilleryPlacement GunPlacementFor(QuestPart_Encirclement siege, Thing target)
        {
            if (siege == null || target == null || !byCell.TryGetValue(target.Position, out var at)) return null;
            EncirclementProject project = at.Find(p => p.Gun >= 0 && p.Funded && p.Def == target.def.entityDefToBuild);
            if (project == null || siege.Battery(project.Battery) is not { Camp.Guns: { } guns } ||
                project.Gun >= guns.Count) return null;
            return guns[project.Gun];
        }

        internal int GunBatteryBlockedBy(Plant plant, Faction faction)
        {
            // Look up the live constructible, including jobs restored before the first work review.
            foreach (Thing target in plant.Position.GetThingList(plant.Map))
                if (target is Blueprint or Frame && target.Faction == faction &&
                    byCell.TryGetValue(target.Position, out var at) &&
                    at.Find(p => p.Gun >= 0 && p.Funded && p.Def == target.def.entityDefToBuild) is { } project &&
                    GenConstruct.BlocksConstruction(target, plant)) return project.Battery;
            return -1;
        }

        internal bool UnderFire(Map map, IntVec3 cell) => repairThreats.IsExposed(cell, map);
        internal Building NearbyNode(IntVec3 cell, Map map) =>
            IsoFloor.FloorOf(cell, map) == 0
                ? GenRadial.RadialCellsAround(cell, 8f, true)
                    .Select(c => byCell.TryGetValue(c, out var at) ? at.Find(p =>
                        (p.Building == ThingDefOf.Gloomlight || p.Building == EncirclementDefs.RS_SiegeMechNode) &&
                        p.Current is Verse.Building { Spawned: true } and not Frame)
                        ?.Current as Building : null).FirstOrDefault(b => b != null)
                : null;

        internal bool Fund(QuestPart_Encirclement siege, ArtilleryRaid battery, List<EncirclementProject> projects,
            int initialPersonnel = 0)
        {
            List<EncirclementProject> unfunded = projects.Where(p => !p.Funded).ToList();
            if (unfunded.Count == 0 && initialPersonnel == 0) return true;
            var materials = new Dictionary<ThingDef, int>();
            var manifest = new Dictionary<ThingDef, int>();
            var installations = new List<(EncirclementProject Project, MinifiedThing Packed)>();
            foreach (EncirclementProject project in unfunded)
            {
                if (project.Install)
                {
                    Thing building = ThingMaker.MakeThing(project.Building, project.Stuff);
                    building.SetFactionDirect(siege.Faction);
                    installations.Add((project, building.MakeMinified()));
                    continue;
                }
                foreach (ThingDefCountClass cost in project.Def.CostListAdjusted(project.Stuff))
                    Count(materials, cost.thingDef, cost.count);
            }

            if (initialPersonnel > 0)
            {
                Count(manifest, ThingDefOf.MealSurvivalPack, initialPersonnel);
                bool ultratech = siege.Faction.def.techLevel >= TechLevel.Ultra;
                int medicine = System.Math.Min(initialPersonnel / 3, ultratech ? 7 : 20);
                if (medicine > 0)
                    Count(manifest, ultratech ? ThingDefOf.MedicineUltratech : ThingDefOf.MedicineIndustrial, medicine);
            }
            List<List<Thing>> groups = Pack(manifest, installations.Select(x => x.Packed));
            if (!Drop(siege, battery, groups)) return false;

            foreach (var (project, packed) in installations) project.PackedBuilding = packed;
            foreach (EncirclementProject project in unfunded) project.Funded = true;
            foreach (var item in materials) Count(initialMaterials, item.Key, item.Value);
            return true;
        }

        private bool Drop(QuestPart_Encirclement siege, ArtilleryRaid battery, List<List<Thing>> groups)
        {
            if (groups.Count == 0) return true;
            reserved ??= siege.Plan.Reservations(siege.Map);
            if (CampSupplies.DropGroups(siege.Map, battery, groups, openDelay: 40, instaDrop: false,
                leaveSlag: false, canRoofPunch: false, forbid: false, allowFogged: true, faction: siege.Faction,
                dropPosition: c => !reserved.Contains(c) && !siege.Map.areaManager.Home[c] &&
                    c.GetEdifice(siege.Map) == null)) return true;
            CampSupplies.Discard(groups);
            return false;
        }

        private static void Count(Dictionary<ThingDef, int> manifest, ThingDef def, int count) =>
            manifest[def] = manifest.GetValueOrDefault(def) + count;

        private static List<List<Thing>> Pack(Dictionary<ThingDef, int> counts, IEnumerable<Thing> extras)
        {
            var things = new List<Thing>(extras);
            foreach (var pair in counts)
                for (int remaining = pair.Value; remaining > 0;)
                {
                    Thing stack = ThingMaker.MakeThing(pair.Key);
                    stack.stackCount = remaining < pair.Key.stackLimit ? remaining : pair.Key.stackLimit;
                    things.Add(stack);
                    remaining -= stack.stackCount;
                }
            // A few pods per delivery leave enough legal work cells in cramped terrain.
            int groupCount = System.Math.Min(4, (things.Count + 17) / 18);
            var groups = Enumerable.Range(0, groupCount).Select(_ => new List<Thing>()).ToList();
            for (int i = 0; i < things.Count; i++) groups[i % groupCount].Add(things[i]);
            return groups;
        }

        internal void Observe(QuestPart_Encirclement siege, EncirclementProject project)
        {
            Map map = siege.Map;
            project.Current = project.Cell.GetThingList(map).FirstOrDefault(t => t.Spawned &&
                t.Faction == siege.Faction && (t is Blueprint or Frame
                    ? t.def.entityDefToBuild == project.Def : t.def == project.Building && t.Position == project.Cell));
            if (project.Current != null || project.Finished(map)) project.Placed = true;
            if (project.Current is Frame || project.Finished(map)) project.MaterialCommitted = true;
            if (project.Finished(map))
            {
                if (!project.BuiltOnce && project.Medical && project.Current is Building_Bed bed) bed.Medical = true;
                project.BuiltOnce = true;
            }

            if (siege.Phase == 4 || project.Gun < 0 || siege.Battery(project.Battery) is not { } raid) return;
            ArtilleryPlacement gun = raid.Camp.Guns[project.Gun];
            if (gun.Building != null && gun.Building != project.Current)
            {
                map.GetComponent<ArtilleryMap>().ForgetGun(gun.Building);
                gun.Building = null;
            }
            gun.Blueprint = project.Current as Blueprint_Build;
            gun.Frame = project.Current as Frame;
            if (project.Current is Building building)
                ArtilleryCamp.ObserveBuilding(raid, building);
        }

        internal bool TryPlace(QuestPart_Encirclement siege, EncirclementProject project, bool debug = false)
        {
            Map map = siege.Map;
            if (project.Current != null || project.Finished(map)) return false;
            bool rebuilding = project.Placed;
            bool forward = project.Battery > 0;
            if (forward && (siege.Phase >= 3 || siege.IsBatteryAbandoned(project.Battery))) return false;
            bool covered = !UnderFire(map, project.Cell) || SmokeCombat.Screened(project.Cell, map) ||
                forward && siege.HasActiveCoverFor(project.Battery);
            if (rebuilding)
            {
                if (project.Install || project.IsDevice) return false;
                if (!debug && !covered) return false;
                if (forward && project.Gun >= 0 && project.BuiltOnce) return false;
                if (forward && project.MaterialCommitted && project.Rebuilt >= MaxForwardGunRebuilds) return false;
            }
            if (project.Install && project.PackedBuilding is not { Spawned: true, Destroyed: false }) return false;
            if (!NativeConstruction.CanPlaceSiegeProject(project.Def, project.Cell, project.Rotation, map, project.Stuff,
                install: project.Install))
                return false;
            if (rebuilding && project.MaterialCommitted)
            {
                if (!forward)
                {
                    var costs = project.Def.CostListAdjusted(project.Stuff).ToDictionary(c => c.thingDef, c => c.count);
                    if (costs.Any(c => spentOnRepairs.GetValueOrDefault(c.Key) + c.Value >
                        initialMaterials.GetValueOrDefault(c.Key) * 0.4f)) return false;
                    foreach (var item in costs) Count(spentOnRepairs, item.Key, item.Value);
                }
                project.Rebuilt++;
                project.MaterialCommitted = false;
            }

            project.Current = project.Install
                ? GenConstruct.PlaceBlueprintForInstall(project.PackedBuilding, project.Cell, map, project.Rotation, siege.Faction)
                : GenConstruct.PlaceBlueprintForBuild(project.Def, project.Cell, map, project.Rotation, siege.Faction, project.Stuff);
            project.Placed = true;
            return true;
        }

        internal void SetRoofArea(Map map, IntVec3 cell)
        {
            if (cell.Roofed(map) || map.areaManager.BuildRoof[cell]) return;
            RoofCells.Add(cell);
            map.areaManager.BuildRoof[cell] = true;
        }

        private static IEnumerable<(Thing Item, IntVec3 Cell)> AvailableCargo(Map map, ArtilleryRaid raid)
        {
            foreach (Thing item in map.GetComponent<ArtilleryMap>().SupplyOwnership.ItemsFor(raid))
                if (!item.Destroyed && item.SpawnedParentOrMe is { } holder && holder.Map == map &&
                    (item.Spawned || holder is DropPodIncoming or ActiveTransporter ||
                        holder is Pawn pawn && pawn.Faction == raid.Faction && pawn.carryTracker.CarriedThing == item))
                    yield return (item, IsoFloor.GroundCellOf(holder.Position, map));
        }

        internal void SupplyConstruction(QuestPart_Encirclement siege, ArtilleryRaid rear)
        {
            var requests = new Dictionary<ArtilleryRaid, Dictionary<ThingDef, int>>();
            foreach (EncirclementProject project in ActiveProjects)
            {
                if (project.Install || project.Current is not IConstructible work || work.IsCompleted() ||
                    siege.Battery(project.Battery) is not { } battery) continue;
                if (!requests.TryGetValue(battery, out var remaining))
                    requests[battery] = remaining = new();
                foreach (var cost in work.TotalMaterialCost())
                {
                    int need = work.ThingCountNeeded(cost.thingDef);
                    if (need > 0) Count(remaining, cost.thingDef, need);
                }
            }
            if (requests.Count == 0) return;

            // Allocate this incident's loose, incoming and carried cargo once.
            // Frame contents are already deducted by ThingCountNeeded.
            foreach (var (item, cell) in AvailableCargo(siege.Map, rear))
            {
                int available = item.stackCount;
                foreach (var request in requests.Where(r => r.Value.GetValueOrDefault(item.def) > 0 &&
                    cell.InHorDistOf(r.Key.Camp.Center, MaterialRadius))
                    .OrderBy(r => cell.DistanceToSquared(r.Key.Camp.Center)))
                {
                    int claimed = System.Math.Min(available, request.Value[item.def]);
                    request.Value[item.def] -= claimed;
                    available -= claimed;
                    if (available == 0) break;
                }
            }
            foreach (var request in requests)
                Drop(siege, request.Key, Pack(request.Value, Enumerable.Empty<Thing>()));
        }

        internal void ConsumeDebugMaterials(QuestPart_Encirclement siege, EncirclementProject project)
        {
            if (project.Install || project.Current is not IConstructible work ||
                siege.Battery(project.Battery) is not { } battery) return;
            var cargo = AvailableCargo(siege.Map, battery).Where(x => x.Item.Spawned &&
                x.Cell.InHorDistOf(battery.Camp.Center, MaterialRadius)).Select(x => x.Item).ToList();
            foreach (var cost in work.TotalMaterialCost())
            {
                int remaining = work.ThingCountNeeded(cost.thingDef);
                foreach (Thing stack in cargo)
                {
                    if (remaining <= 0) break;
                    if (stack.Destroyed || stack.def != cost.thingDef) continue;
                    int used = System.Math.Min(remaining, stack.stackCount);
                    stack.SplitOff(used).Destroy(DestroyMode.Vanish);
                    remaining -= used;
                }
            }
        }

        internal void SupplyAmmunition(QuestPart_Encirclement siege, ArtilleryRaid raid)
        {
            var emptyGuns = raid.Camp.Guns.Where(g => g.Ammo != null && siege.GunReady(g) &&
                g.Building is Building_TurretGunCE { Spawned: true } turret && turret.Faction == siege.Faction &&
                turret.CompAmmo is { UseAmmo: true, HasMagazine: true, CurMagCount: 0 } && !turret.isReloading).ToList();
            if (emptyGuns.Count == 0) return;
            var inStock = AvailableCargo(siege.Map, raid).Where(x => x.Cell.InHorDistOf(raid.Camp.Center, AmmunitionRadius))
                .Select(x => x.Item.def).ToHashSet();
            var order = emptyGuns.Where(g => !inStock.Contains(g.Ammo)).GroupBy(g => g.Ammo)
                .ToDictionary(g => g.Key, g => 5 * g.Sum(gun => ((Building_TurretGunCE)gun.Building).CompAmmo.MagSize));
            Drop(siege, raid, Pack(order, Enumerable.Empty<Thing>()));
        }

        internal void SupplyRations(QuestPart_Encirclement siege, ArtilleryRaid raid, int humans)
        {
            if (humans <= 0) return;
            ThingDef meal = ThingDefOf.MealSurvivalPack;
            int stock = AvailableCargo(siege.Map, raid).Where(x => x.Item.def == meal &&
                x.Cell.InHorDistOf(raid.Camp.Center, MaterialRadius)).Sum(x => x.Item.stackCount);
            if (stock < System.Math.Max(1, humans / 4))
                Drop(siege, raid, Pack(new() { { meal, humans - stock } }, Enumerable.Empty<Thing>()));
        }

        internal void CancelUnbuilt(QuestPart_Encirclement siege, int battery = -1)
        {
            foreach (EncirclementProject project in Projects)
            {
                if (!project.Funded) continue;
                if (battery >= 0 ? project.Battery != battery : project.Battery <= 0) continue;
                Observe(siege, project);
                if (project.Current is not (Blueprint or Frame)) continue;
                project.Current.Destroy(DestroyMode.Cancel);
                project.Current = null;
                if (project.Gun >= 0 && siege.Battery(project.Battery) is { Camp.Guns: { } guns } &&
                    project.Gun < guns.Count)
                {
                    guns[project.Gun].Blueprint = null;
                    guns[project.Gun].Frame = null;
                }
            }
            ActiveProjects.RemoveAll(p => p.Current == null || p.Current.Destroyed);
        }

        internal void Stop(QuestPart_Encirclement siege, bool stopDevices = true)
        {
            Map map = siege.Map;
            foreach (EncirclementProject project in Projects.Where(p => p.Funded))
            {
                Observe(siege, project);
                if (project.Current is Blueprint or Frame) project.Current.Destroy(DestroyMode.Cancel);
                if (stopDevices && project.IsDevice && project.Current is Building building)
                    building.GetComp<CompEncirclementDevice>()?.Stop();
            }
            foreach (IntVec3 cell in RoofCells) map.areaManager.BuildRoof[cell] = false;
            RoofCells.Clear();
        }

        public void ExposeData()
        {
            Scribe_Collections.Look(ref Projects, "projects", LookMode.Deep);
            Scribe_Collections.Look(ref RoofCells, "roofCells", LookMode.Value);
            Scribe_Collections.Look(ref initialMaterials, "initialMaterials", LookMode.Def, LookMode.Value);
            Scribe_Collections.Look(ref spentOnRepairs, "spentOnRepairs", LookMode.Def, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                List<EncirclementProject> loaded = Projects;
                Projects = new List<EncirclementProject>(loaded.Count);
                foreach (EncirclementProject project in loaded) Add(project);
            }
        }
    }
}
