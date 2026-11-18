using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using ProjectZ;
using RimWorld;
using Verse;

namespace RimSiege.Fortress
{
    internal sealed class FortressAmmunition
    {
        private readonly Map map;
        private readonly FortressModule keep;
        private readonly FortressModule[] towers;
        private readonly Dictionary<FortressModule, IntVec3[]> storage;
        private readonly HashSet<IntVec3> landings;
        private readonly HashSet<IntVec3> operatingCells = new();
        private readonly HashSet<IntVec3> shelfAccess = new();
        private readonly Dictionary<IntVec3, Building_Storage> readyShelves = new();
        private readonly ThingDef shelfStuff;
        private readonly IntVec3 lootShelf;

        internal FortressAmmunition(Map map, FortressLayout layout, ThingDef shelfStuff, IntVec3 lootShelf)
        {
            this.map = map;
            this.shelfStuff = shelfStuff;
            this.lootShelf = lootShelf;
            keep = layout.Modules.First(module => module.Kind == FortressModuleKind.Keep);
            towers = layout.Modules.Where(module => module.Kind == FortressModuleKind.Tower).ToArray();
            storage = towers.Prepend(keep).ToDictionary(module => module, SlotsFor);
            landings = new HashSet<IntVec3>(layout.Ladders.SelectMany(ladder => new[] { ladder.Foot, ladder.Landing }));
            foreach (var stair in layout.Tiers.SelectMany(tier => tier.Stairs))
            {
                landings.Add(stair.exit);
                landings.UnionWith(stair.footprint);
            }
        }

        private IntVec3[] SlotsFor(FortressModule module)
        {
            CellRect inside = module.Ground.ContractedBy(1);
            return Enumerable.Range(0, module.Top + 1).Reverse()
                .SelectMany(floor => CellRect.FromLimits(
                    Api.CellOnFloor(inside.Min, floor, map),
                    Api.CellOnFloor(inside.Max, floor, map)).Cells)
                .Where(cell => cell != lootShelf)
                .Select(cell => cell.GetFirstBuilding(map)).OfType<Building_Storage>().Distinct()
                .SelectMany(shelf => shelf.GetSlotGroup().CellsList).ToArray();
        }

        internal void StockEmplacements(List<ArmedEmplacement> emplacements)
        {
            operatingCells.UnionWith(emplacements.Where(gun => gun.Turret.def.hasInteractionCell)
                .Select(gun => gun.Turret.InteractionCell));
            var reserve = towers.ToDictionary(tower => tower, _ => new HashSet<AmmoDef>());
            var keepReserve = new Dictionary<AmmoDef, int>();
            foreach (ArmedEmplacement emplacement in emplacements)
            {
                if (emplacement.Ammo == null) continue;
                Building turret = emplacement.Turret;
                IntVec3 ground = Api.GroundCellOf(turret.Position, map);
                if (keep.Ground.Contains(ground))
                {
                    keepReserve[emplacement.Ammo] = keepReserve.GetValueOrDefault(emplacement.Ammo) + 1;
                }
                else
                {
                    FortressModule nearest = towers.MinBy(tower => (tower.Ground.CenterCell - ground).LengthHorizontalSquared);
                    reserve[nearest].Add(emplacement.Ammo);
                }

                int magazine = ((Building_TurretGunCE)turret).CompAmmo.MagSize;
                int readyCount = magazine <= 2
                    ? Math.Min(emplacement.Ammo.stackLimit, Math.Max(magazine * 8, 8))
                    : magazine;
                StockReady(emplacement.Ammo, readyCount, turret);
            }

            // Prioritize Keep shelves with 2nd-floor machine gun and 3rd-floor turret/artillery ammo.
            var keepAmmo = keepReserve.OrderBy(pair => pair.Key.defName).ToArray();
            foreach (var (ammo, _) in keepAmmo)
                StockShelf(keep, ammo, minStacks: 1);
            foreach (var (ammo, count) in keepAmmo)
                StockShelf(keep, ammo, minStacks: Math.Clamp((count + 1) / 2, 2, 4));

            // Each ammunition type gets one reserve stack per tower sector.
            foreach (FortressModule tower in towers)
            {
                foreach (AmmoDef ammo in reserve[tower].OrderBy(def => def.defName))
                {
                    if (StockShelf(tower, ammo)) continue;
                    foreach (FortressModule alternative in towers.OrderBy(other =>
                        (other.Ground.CenterCell - tower.Ground.CenterCell).LengthHorizontalSquared).Append(keep))
                    {
                        if (alternative != tower && StockShelf(alternative, ammo)) break;
                    }
                }
            }
        }

        internal void StockInfantry(IEnumerable<Pawn> guards, List<ArmedEmplacement> emplacements)
        {
            var types = guards.Select(pawn => pawn.equipment?.Primary?.TryGetComp<CompAmmoUser>())
                .Where(user => user is { UseAmmo: true } && (user.CurrentAmmo ?? user.SelectedAmmo) != null)
                .GroupBy(user => user.CurrentAmmo ?? user.SelectedAmmo)
                .OrderByDescending(group => group.Count()).ThenBy(group => group.Key.defName)
                .Select(group => (Ammo: group.Key, Magazine: group.Max(user => user.MagSize))).ToArray();
            if (types.Length == 0) return;

            foreach (var type in types)
                StockShelf(keep, type.Ammo, minStacks: 2);

            foreach (FortressModule tower in towers)
                foreach (var type in types)
                    if (!StockShelf(tower, type.Ammo)) break;

            // A small supply by the guns is also visible to CE's same-band
            // infantry pickup search. The sheltered stacks remain downstairs.
            var firingPosts = emplacements.Where(gun => gun.Floor > 0 && gun.Turret.def.hasInteractionCell)
                .GroupBy(gun =>
                {
                    IntVec3 ground = Api.GroundCellOf(gun.Turret.Position, map);
                    FortressModule module = keep.Ground.Contains(ground)
                        ? keep
                        : towers.MinBy(tower => (tower.Ground.CenterCell - ground).LengthHorizontalSquared);
                    return (gun.Floor, Module: module);
                });
            foreach (var sector in firingPosts)
            {
                var type = types[0];
                StockReady(type.Ammo, type.Magazine, sector.First().Turret);
            }
        }

        private bool StockShelf(FortressModule module, AmmoDef ammo, int minStacks = 1)
        {
            IntVec3[] slots = storage[module];
            int existing = slots.Sum(cell => cell.GetThingList(map).Count(thing => thing.def == ammo));
            while (existing < minStacks)
            {
                IntVec3 cell = slots.FirstOrDefault(candidate =>
                    candidate.GetItemCount(map) < candidate.GetMaxItemsAllowedInCell(map), IntVec3.Invalid);
                if (!cell.IsValid) return existing > 0;
                FortressStorage.Stock(map, ammo, cell);
                existing++;
            }
            return true;
        }

        private void StockReady(AmmoDef ammo, int count, Building turret)
        {
            if (count <= 0) return;
            IntVec3 near = turret.def.hasInteractionCell ? turret.InteractionCell : turret.Position;
            IntVec3 forward = turret.Rotation.FacingCell;
            ThingDef shelfDef = ThingDefOf.ShelfSmall;
            foreach (IntVec3 cell in turret.OccupiedRect().AdjacentCellsCardinal
                .Where(cell => cell.InBounds(map) && Api.SameFloor(cell, turret.Position, map) &&
                    !landings.Contains(cell) && !operatingCells.Contains(cell))
                .OrderBy(cell => (cell.x - turret.Position.x) * forward.x +
                    (cell.z - turret.Position.z) * forward.z > 0)
                .ThenBy(cell => (cell - near).LengthHorizontalSquared))
            {
                if (!readyShelves.TryGetValue(cell, out Building_Storage shelf))
                {
                    if (shelfAccess.Contains(cell) || !cell.Standable(map) || cell.GetFirstBuilding(map) != null ||
                        !GenConstruct.CanBuildOnTerrain(shelfDef, cell, map, Rot4.North, stuffDef: shelfStuff) ||
                        !GenSpawn.CanSpawnAt(shelfDef, cell, map, Rot4.North, canWipeEdifices: false)) continue;
                    IntVec3 access = GenAdj.CardinalDirections.Select(direction => cell + direction)
                        .FirstOrDefault(candidate => WorkingCell(candidate, cell), IntVec3.Invalid);
                    if (!access.IsValid) continue;
                    shelf = (Building_Storage)FortressConstruction.Spawn(shelfDef, cell, Rot4.North, shelfStuff, map);
                    shelf.GetStoreSettings().filter.SetDisallowAll();
                    readyShelves.Add(cell, shelf);
                    shelfAccess.Add(access);
                }
                if (cell.GetItemCount(map) >= cell.GetMaxItemsAllowedInCell(map)) continue;
                shelf.GetStoreSettings().filter.SetAllow(ammo, true);
                FortressStorage.Stock(map, ammo, cell, count);
                return;
            }

            bool WorkingCell(IntVec3 candidate, IntVec3 shelfCell) => candidate.InBounds(map) &&
                Api.SameFloor(candidate, shelfCell, map) && candidate.Standable(map) && !landings.Contains(candidate);
        }
    }
}
