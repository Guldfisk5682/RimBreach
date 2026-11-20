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
        private const float SupplyHorDistLimit = 30f;
        private const float FallbackHorDistLimit = 28f;

        private readonly Map map;
        private readonly FortressGeometry geometry;
        private readonly FortressModule keep;
        private readonly FortressModule[] towers;
        private readonly FortressFacilityPlan facilities;
        private readonly Dictionary<FortressModule, IntVec3[]> storage;
        private readonly IntVec3[] supplySlots;
        private readonly IntVec3 supplyCenter;
        private readonly HashSet<IntVec3> landings;
        private readonly HashSet<IntVec3> operatingCells = new();
        private readonly HashSet<IntVec3> shelfAccess = new();
        private readonly Dictionary<IntVec3, Building_Storage> readyShelves = new();
        private readonly ThingDef shelfStuff;
        private readonly IntVec3 lootShelf;

        internal FortressAmmunition(Map map, FortressLayout layout, ThingDef shelfStuff, IntVec3 lootShelf)
        {
            this.map = map;
            geometry = layout.Geometry;
            this.shelfStuff = shelfStuff;
            this.lootShelf = lootShelf;
            facilities = layout.Facilities;
            keep = layout.Modules.First(module => module.Kind == FortressModuleKind.Keep);
            towers = layout.Modules.Where(module => module.Kind == FortressModuleKind.Tower).ToArray();
            storage = towers.Prepend(keep).ToDictionary(module => module, SlotsFor);
            supplySlots = facilities.Supply.IsEmpty
                ? Array.Empty<IntVec3>()
                : SlotsInRect(facilities.Supply.Interior(map, 0));
            supplyCenter = facilities.Supply.IsEmpty ? IntVec3.Invalid : facilities.Supply.Rect.CenterCell;
            landings = new HashSet<IntVec3>(layout.Ladders.SelectMany(ladder => new[] { ladder.Foot, ladder.Landing }));
            foreach (var stair in layout.Tiers.SelectMany(tier => tier.Stairs))
            {
                landings.Add(stair.exit);
                landings.UnionWith(stair.footprint);
            }
        }

        internal static bool IsExplosiveAmmo(AmmoDef ammo, Building turret)
        {
            if (ammo == null) return false;
            if (turret?.def?.building?.IsMortar == true || ammo.isMortarAmmo) return true;
            if (ammo.GetCompProperties<CompProperties_ExplosiveCE>() != null ||
                ammo.GetCompProperties<CompProperties_Explosive>() != null) return true;
            if (IsExplosiveProjectile(ammo.detonateProjectile) ||
                IsExplosiveProjectile(ammo.cookOffProjectile)) return true;

            AmmoSetDef ammoSet = (turret as Building_TurretGunCE)?.CompAmmo?.CurAmmoSet ??
                turret?.def?.building?.turretGunDef?.GetCompProperties<CompProperties_AmmoUser>()?.ammoSet;
            return ammoSet?.ammoTypes != null &&
                ammoSet.ammoTypes.Any(link => link.ammo == ammo && IsExplosiveProjectile(link.projectile));
        }

        private static bool IsExplosiveProjectile(ThingDef projectile)
        {
            if (projectile == null) return false;
            return projectile.projectile is { } props &&
                (props.explosionRadius > 0f || props.damageDef?.isExplosive == true) ||
                projectile.GetCompProperties<CompProperties_ExplosiveCE>() != null ||
                projectile.GetCompProperties<CompProperties_Explosive>() != null;
        }

        private IntVec3[] SlotsFor(FortressModule module)
        {
            if (module.Kind == FortressModuleKind.Tower && geometry?.Zones != null)
            {
                foreach (FortressBastionZone zone in geometry.Zones)
                {
                    if (zone.Ground == module.Ground && zone.VaultCells != null)
                    {
                        return zone.VaultCells
                            .Where(cell => cell != lootShelf)
                            .Select(cell => cell.GetFirstBuilding(map)).OfType<Building_Storage>().Distinct()
                            .SelectMany(shelf => shelf.GetSlotGroup().CellsList).ToArray();
                    }
                }
            }

            CellRect inside = module.Ground.ContractedBy(1);
            return Enumerable.Range(0, module.Top + 1).Reverse()
                .SelectMany(floor => CellRect.FromLimits(
                    Api.CellOnFloor(inside.Min, floor, map),
                    Api.CellOnFloor(inside.Max, floor, map)).Cells)
                .Where(cell => cell != lootShelf)
                .Select(cell => cell.GetFirstBuilding(map)).OfType<Building_Storage>().Distinct()
                .SelectMany(shelf => shelf.GetSlotGroup().CellsList).ToArray();
        }

        private IntVec3[] SlotsInRect(CellRect rect) => rect.Cells
            .Select(cell => cell.GetFirstBuilding(map)).OfType<Building_Storage>().Distinct()
            .SelectMany(shelf => shelf.GetSlotGroup().CellsList).ToArray();

        internal void StockEmplacements(List<ArmedEmplacement> emplacements)
        {
            operatingCells.UnionWith(emplacements.Where(gun => gun.Turret.def.hasInteractionCell)
                .Select(gun => gun.Turret.InteractionCell));

            var explosiveKeep = new Dictionary<AmmoDef, (int Ready, int Guns)>();
            var explosiveTower = towers.ToDictionary(tower => tower, _ => new Dictionary<AmmoDef, int>());
            var explosiveSupply = new Dictionary<(FortressModule FallbackTower, AmmoDef Ammo), (int Ready, IntVec3 Ground)>();

            var reserve = towers.ToDictionary(tower => tower, _ => new HashSet<AmmoDef>());
            var keepReserve = new Dictionary<AmmoDef, int>();

            foreach (ArmedEmplacement emplacement in emplacements)
            {
                if (emplacement.Ammo == null) continue;
                Building turret = emplacement.Turret;
                AmmoDef ammo = emplacement.Ammo;
                IntVec3 ground = Api.GroundCellOf(turret.Position, map);
                int magazine = ((Building_TurretGunCE)turret).CompAmmo.MagSize;
                int readyCount = magazine <= 2
                    ? Math.Min(ammo.stackLimit, Math.Max(magazine * 8, 8))
                    : magazine;

                if (IsExplosiveAmmo(ammo, turret))
                {
                    if (keep.Ground.Contains(ground))
                    {
                        var current = explosiveKeep.GetValueOrDefault(ammo);
                        explosiveKeep[ammo] = (current.Ready + readyCount, current.Guns + 1);
                    }
                    else if (TryFindTower(ground, out FortressModule homeTower))
                    {
                        explosiveTower[homeTower][ammo] = explosiveTower[homeTower].GetValueOrDefault(ammo) + readyCount;
                    }
                    else
                    {
                        FortressModule nearest = towers.MinBy(tower => (tower.Ground.CenterCell - ground).LengthHorizontalSquared);
                        bool courtyardMortar = turret.def.building.IsMortar ||
                            facilities.Mortar.Contains(ground) || facilities.SecondMortar.Contains(ground);
                        if (supplySlots.Length > 0 && (courtyardMortar || supplyCenter.InHorDistOf(ground, SupplyHorDistLimit)))
                        {
                            var key = (nearest, ammo);
                            int prev = explosiveSupply.TryGetValue(key, out var entry) ? entry.Ready : 0;
                            explosiveSupply[key] = (prev + readyCount, ground);
                        }
                        else
                        {
                            explosiveTower[nearest][ammo] = explosiveTower[nearest].GetValueOrDefault(ammo) + readyCount;
                        }
                    }
                    continue;
                }

                if (keep.Ground.Contains(ground))
                {
                    keepReserve[ammo] = keepReserve.GetValueOrDefault(ammo) + 1;
                }
                else
                {
                    FortressModule nearest = towers.MinBy(tower => (tower.Ground.CenterCell - ground).LengthHorizontalSquared);
                    reserve[nearest].Add(ammo);
                }

                StockReady(ammo, readyCount, turret);
            }

            var ordnance = new List<(AmmoDef Ammo, int Ready, int Reserve, IntVec3[][] Storage)>();
            foreach (var (ammo, budget) in explosiveKeep.OrderBy(pair => pair.Key.defName))
            {
                int reserveStacks = Math.Clamp((budget.Guns + 1) / 2, 2, 4);
                ordnance.Add((ammo, budget.Ready, reserveStacks * ammo.stackLimit, KeepExplosiveTargets().ToArray()));
            }

            foreach (FortressModule tower in towers)
            {
                foreach (var (ammo, readyTotal) in explosiveTower[tower].OrderBy(pair => pair.Key.defName))
                {
                    ordnance.Add((ammo, readyTotal, ammo.stackLimit, TowerExplosiveTargets(tower).ToArray()));
                }
            }

            foreach (var (key, entry) in explosiveSupply
                .OrderBy(pair => pair.Key.Ammo.defName)
                .ThenBy(pair => pair.Key.FallbackTower.Ground.minX)
                .ThenBy(pair => pair.Key.FallbackTower.Ground.minZ))
            {
                ordnance.Add((key.Ammo, entry.Ready, key.Ammo.stackLimit,
                    SupplyExplosiveTargets(entry.Ground, key.FallbackTower).ToArray()));
            }

            var keepAmmo = keepReserve.OrderBy(pair => pair.Key.defName).ToArray();
            // Give each sector and caliber a first indoor stack before any bulk reserves.
            var remaining = new int[ordnance.Count];
            for (int i = 0; i < ordnance.Count; i++)
            {
                var stock = ordnance[i];
                int loaded = StockExplosive(stock.Ammo, Math.Min(stock.Ready, stock.Ammo.stackLimit), stock.Storage);
                remaining[i] = stock.Ready + stock.Reserve - loaded;
            }

            foreach (var (ammo, _) in keepAmmo)
                StockShelf(keep, ammo, minStacks: 1);

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

            for (int i = 0; i < ordnance.Count; i++)
            {
                var stock = ordnance[i];
                StockExplosive(stock.Ammo, remaining[i], stock.Storage);
            }
            foreach (var (ammo, count) in keepAmmo)
                StockShelf(keep, ammo, minStacks: Math.Clamp((count + 1) / 2, 2, 4));
        }

        private bool TryFindTower(IntVec3 ground, out FortressModule homeTower)
        {
            foreach (FortressModule tower in towers)
            {
                if (tower.Ground.Contains(ground))
                {
                    homeTower = tower;
                    return true;
                }
            }
            homeTower = default;
            return false;
        }

        private IEnumerable<IntVec3[]> KeepExplosiveTargets()
        {
            yield return storage[keep];
            if (supplySlots.Length > 0 && supplyCenter.InHorDistOf(keep.Ground.CenterCell, FallbackHorDistLimit))
                yield return supplySlots;
            foreach (FortressModule tower in towers
                .Where(tower => tower.Ground.CenterCell.InHorDistOf(keep.Ground.CenterCell, FallbackHorDistLimit))
                .OrderBy(tower => (tower.Ground.CenterCell - keep.Ground.CenterCell).LengthHorizontalSquared))
                yield return storage[tower];
        }

        private IEnumerable<IntVec3[]> TowerExplosiveTargets(FortressModule tower)
        {
            yield return storage[tower];
            IntVec3 origin = tower.Ground.CenterCell;
            if (keep.Ground.CenterCell.InHorDistOf(origin, FallbackHorDistLimit))
                yield return storage[keep];
            if (supplySlots.Length > 0 && supplyCenter.InHorDistOf(origin, FallbackHorDistLimit))
                yield return supplySlots;
            foreach (FortressModule other in towers
                .Where(candidate => candidate != tower && candidate.Ground.CenterCell.InHorDistOf(origin, FallbackHorDistLimit))
                .OrderBy(candidate => (candidate.Ground.CenterCell - origin).LengthHorizontalSquared))
                yield return storage[other];
        }

        private IEnumerable<IntVec3[]> SupplyExplosiveTargets(IntVec3 origin, FortressModule fallbackTower)
        {
            if (supplySlots.Length > 0)
                yield return supplySlots;
            if (fallbackTower.Ground.CenterCell.InHorDistOf(origin, FallbackHorDistLimit))
                yield return storage[fallbackTower];
            if (keep.Ground.CenterCell.InHorDistOf(origin, FallbackHorDistLimit))
                yield return storage[keep];
            foreach (FortressModule other in towers
                .Where(candidate => candidate != fallbackTower && candidate.Ground.CenterCell.InHorDistOf(origin, FallbackHorDistLimit))
                .OrderBy(candidate => (candidate.Ground.CenterCell - origin).LengthHorizontalSquared))
                yield return storage[other];
        }

        private int StockExplosive(AmmoDef ammo, int totalCount, IntVec3[][] targets)
        {
            int remaining = totalCount;
            if (remaining <= 0) return 0;
            foreach (IntVec3[] slots in targets)
            {
                foreach (IntVec3 cell in slots)
                {
                    int openSlots = cell.GetMaxItemsAllowedInCell(map) - cell.GetItemCount(map);
                    if (openSlots == 0 && cell.GetItems(map).Any(stack => stack.def == ammo && stack.stackCount < ammo.stackLimit))
                        openSlots = 1;
                    for (int i = 0; i < openSlots && remaining > 0; i++)
                    {
                        int stackCount = Math.Min(remaining, ammo.stackLimit);
                        remaining -= FortressStorage.Stock(map, ammo, cell, stackCount);
                    }
                    if (remaining <= 0) return totalCount;
                }
            }
            return totalCount - remaining;
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

            if (IsExplosiveAmmo(types[0].Ammo, null)) return;

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
                ArmedEmplacement post = sector.OrderBy(gun => IsExplosiveAmmo(gun.Ammo, gun.Turret)).First();
                StockReady(type.Ammo, type.Magazine, post.Turret);
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
