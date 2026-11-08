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
        private readonly FortressLayout layout;
        private readonly FortressModule[] towers;
        private readonly Dictionary<FortressModule, IntVec3[]> storage;
        private readonly HashSet<IntVec3> landings;

        internal FortressAmmunition(Map map, FortressLayout layout)
        {
            this.map = map;
            this.layout = layout;
            towers = layout.Modules.Where(module => module.Kind == FortressModuleKind.Tower).ToArray();
            storage = towers.ToDictionary(tower => tower, tower => tower.Ground.ContractedBy(1)
                .Select(cell => cell.GetFirstBuilding(map)).OfType<Building_Storage>().Distinct()
                .SelectMany(shelf => shelf.GetSlotGroup().CellsList).ToArray());
            landings = new HashSet<IntVec3>(layout.Ladders.SelectMany(ladder => new[] { ladder.Foot, ladder.Landing }));
            foreach (var stair in layout.Tiers.SelectMany(tier => tier.Stairs))
            {
                landings.Add(stair.exit);
                landings.UnionWith(stair.footprint);
            }
        }

        internal void StockEmplacements(List<ArmedEmplacement> emplacements)
        {
            var reserve = towers.ToDictionary(tower => tower, _ => new HashSet<AmmoDef>());
            foreach (ArmedEmplacement emplacement in emplacements)
            {
                if (emplacement.Ammo == null) continue;
                Building turret = emplacement.Turret;
                IntVec3 ground = Api.GroundCellOf(turret.Position, map);
                FortressModule nearest = towers.MinBy(tower => (tower.Ground.CenterCell - ground).LengthHorizontalSquared);
                reserve[nearest].Add(emplacement.Ammo);
                int magazine = ((Building_TurretGunCE)turret).CompAmmo.MagSize;
                StockReady(emplacement.Ammo, magazine, turret.InteractionCell);
            }

            // Each ammunition type gets one reserve stack per tower sector.
            foreach (FortressModule tower in towers)
            {
                foreach (AmmoDef ammo in reserve[tower].OrderBy(def => def.defName))
                {
                    if (StockShelf(tower, ammo)) continue;
                    foreach (FortressModule alternative in towers.OrderBy(other =>
                        (other.Ground.CenterCell - tower.Ground.CenterCell).LengthHorizontalSquared))
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

            foreach (FortressModule tower in towers)
                foreach (var type in types)
                    if (!StockShelf(tower, type.Ammo)) break;

            // A small supply by the guns is also visible to CE's same-band
            // infantry pickup search. The sheltered stacks remain downstairs.
            var firingPosts = emplacements.Where(gun => gun.Floor > 0 && gun.Turret.def.hasInteractionCell)
                .GroupBy(gun =>
                {
                    IntVec3 ground = Api.GroundCellOf(gun.Turret.Position, map);
                    return (gun.Floor, Tower: towers.MinBy(tower =>
                        (tower.Ground.CenterCell - ground).LengthHorizontalSquared));
                });
            foreach (var sector in firingPosts)
            {
                var type = types[0];
                StockReady(type.Ammo, type.Magazine, sector.First().Turret.InteractionCell);
            }
        }

        private bool StockShelf(FortressModule tower, AmmoDef ammo)
        {
            IntVec3[] slots = storage[tower];
            if (slots.Any(cell => cell.GetFirstThing(map, ammo) != null)) return true;
            IntVec3 cell = slots.FirstOrDefault(candidate =>
                candidate.GetItemCount(map) < candidate.GetMaxItemsAllowedInCell(map), IntVec3.Invalid);
            if (!cell.IsValid) return false;
            FortressStorage.Stock(map, ammo, cell);
            return true;
        }

        private void StockReady(AmmoDef ammo, int count, IntVec3 near)
        {
            if (count <= 0) return;
            IntVec3 cell = CellRect.CenteredOn(near, 5, 5).ClipInsideMap(map)
                .Where(cell => Api.SameFloor(cell, near, map) && cell.Standable(map) && !landings.Contains(cell) &&
                    cell.GetFirstBuilding(map) == null && cell.GetItemCount(map) < cell.GetMaxItemsAllowedInCell(map))
                .OrderBy(cell => (cell - near).LengthHorizontalSquared)
                .FirstOrDefault(IntVec3.Invalid);
            if (cell.IsValid) FortressStorage.Stock(map, ammo, cell, count);
        }
    }
}
