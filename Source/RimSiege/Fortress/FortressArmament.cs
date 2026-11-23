using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using RimWorld;
using Verse;

namespace RimSiege.Fortress
{
    internal readonly record struct ArmedEmplacement(Building Turret, AmmoDef Ammo, int Floor);

    internal static class FortressArmament
    {
        internal static AmmoDef DefaultAmmo(ThingDef def)
        {
            ThingDef weapon = def.building.turretGunDef;
            AmmoSetDef set = weapon?.GetCompProperties<CompProperties_AmmoUser>()?.ammoSet;
            if (set == null || !AmmoUtility.IsAmmoSystemActive(set)) return null;
            ThingDef projectile = weapon.Verbs.First().defaultProjectile;
            return (set.ammoTypes.FirstOrDefault(link => link.projectile == projectile) ??
                set.ammoTypes.FirstOrDefault())?.ammo;
        }

        internal static ThingDef StuffFor(ThingDef def, FortressLayoutKind kind = FortressLayoutKind.SquareKeep) =>
            !def.MadeFromStuff ? null :
            kind == FortressLayoutKind.MechCircularBunker && def == ThingDefOf.Barricade ? ThingDefOf.Plasteel :
            GenStuff.DefaultStuffFor(def);

        internal static bool CanPlacePicketBarricade(IntVec3 cell, Map map) =>
            cell.GetTerrain(map).passability != Traversability.Impassable &&
            GenConstruct.CanBuildOnTerrain(ThingDefOf.Barricade, cell, map, Rot4.North, stuffDef: ThingDefOf.Plasteel) &&
            !cell.GetThingList(map).Any(thing => thing is Building && !thing.def.destroyable);

        internal static List<ArmedEmplacement> Spawn(Map map, FortressLayout layout, List<FortressDefensePiece> pieces)
        {
            var emplacements = new List<ArmedEmplacement>();

            foreach (FortressDefensePiece piece in pieces)
            {
                // Optional DLC or CE Guns may have been removed between visits.
                if (piece.Def == null) continue;
                IntVec3 cell = piece.CellOn(map);
                if (layout.Kind == FortressLayoutKind.MechCircularBunker && piece.Floor == 0 &&
                    piece.Def == ThingDefOf.Barricade && !layout.Geometry.Footprint.Contains(piece.Ground))
                {
                    if (!cell.InBounds(map) || !CanPlacePicketBarricade(cell, map)) continue;
                    foreach (Thing obstacle in cell.GetThingList(map)
                        .Where(thing => thing.def.destroyable && thing is Plant or Building).ToArray())
                        obstacle.Destroy(DestroyMode.Vanish);
                }
                bool unroof = piece.Def.building?.IsMortar == true ||
                    layout.Facilities.Mortar.Contains(cell) || layout.Facilities.SecondMortar.Contains(cell);
                if (unroof)
                {
                    foreach (IntVec3 covered in GenAdj.OccupiedRect(cell, piece.Rotation, piece.Def.Size))
                        if (covered.Roofed(map)) map.roofGrid.SetRoof(covered, null);
                }
                var building = (Building)FortressConstruction.Spawn(piece.Def, cell, piece.Rotation,
                    StuffFor(piece.Def, layout.Kind), map);
                if (building is not Building_TurretGunCE turret) continue;

                AmmoDef ammo = null;
                if (turret.CompAmmo is { UseAmmo: true } user)
                {
                    ammo = piece.Ammo as AmmoDef ?? DefaultAmmo(piece.Def);
                    user.ResetAmmoCount(ammo);
                }
                emplacements.Add(new ArmedEmplacement(building, ammo, piece.Floor));
            }
            return emplacements;
        }
    }
}
