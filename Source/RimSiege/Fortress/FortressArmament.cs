using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using RimWorld;
using RimWorld.BaseGen;
using Verse;
using Verse.AI.Group;

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

        internal static ThingDef StuffFor(ThingDef def) => def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null;

        internal static List<ArmedEmplacement> Spawn(Map map, FortressLayout layout, List<FortressDefensePiece> pieces)
        {
            var emplacements = new List<ArmedEmplacement>();
            Faction faction = map.ParentFaction;
            BaseGen.globalSettings.map = map;

            foreach (FortressDefensePiece piece in pieces)
            {
                // Optional DLC or CE Guns may have been removed between visits.
                if (piece.Def == null) continue;
                IntVec3 cell = piece.CellOn(map);
                bool courtyardGun = layout.Facilities.Mortar.Contains(cell) || layout.Facilities.SecondMortar.Contains(cell);
                if (courtyardGun)
                {
                    foreach (IntVec3 covered in GenAdj.OccupiedRect(cell, piece.Rotation, piece.Def.Size))
                        if (covered.Roofed(map)) map.roofGrid.SetRoof(covered, null);
                }
                var building = (Building)FortressConstruction.Spawn(piece.Def, cell, piece.Rotation, StuffFor(piece.Def), map);
                if (building is not Building_TurretGunCE turret) continue;

                AmmoDef ammo = null;
                if (turret.CompAmmo is { UseAmmo: true } user)
                {
                    ammo = piece.Ammo as AmmoDef ?? DefaultAmmo(piece.Def);
                    user.ResetAmmoCount(ammo);
                }
                emplacements.Add(new ArmedEmplacement(building, ammo, piece.Floor));

                // Retain the vanilla outpost mortar crew while the wall and tower
                // weapons await the fortress defender duty.
                if (courtyardGun && turret.TryGetComp<CompMannable>() != null)
                {
                    BaseGen.symbolStack.Push("pawn", new ResolveParams
                    {
                        rect = CellRect.SingleCell(turret.InteractionCell),
                        faction = faction,
                        singlePawnLord = LordMaker.MakeNewLord(faction, new LordJob_ManTurrets(), map),
                        singlePawnGenerationRequest = new PawnGenerationRequest(faction.RandomPawnKind(),
                            faction, PawnGenerationContext.NonPlayer, map.Tile, mustBeCapableOfViolence: true, inhabitant: true)
                    });
                }
            }

            BaseGen.Generate();
            return emplacements;
        }
    }
}
