using System.Collections.Generic;
using CombatExtended;
using RimSiege.Integration;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimSiege.Raids.Artillery
{
    public sealed class ArtilleryProfileDef : Def
    {
        public string kind;
        public float minPoints;
        public float personnelFactor = 1f;
        public SimpleCurve personnelCurve;
        public float equipmentFraction = 0.3f;
        public float constructionSpeedOffset;
        public float constructionSpeedScale = 1f;
        public float constructionSpeedCap = 10f;
        public FloatRange budgetShare;
        public FloatRange breachingBudgetShare;
        public SimpleCurve totalPieces;
        public int mortarClassLimit = 5;
        public List<ArtilleryGunRule> guns = new List<ArtilleryGunRule>();

        internal float PersonnelPoints(float raidPoints, float pawnPoints) =>
            pawnPoints * (personnelCurve != null ? personnelCurve.Evaluate(raidPoints) : personnelFactor);

        internal float ConstructionSpeed(float raidPoints) =>
            Mathf.Min(constructionSpeedCap, constructionSpeedScale * (Mathf.Log(raidPoints / 100f) + constructionSpeedOffset));

        internal bool BelowGate(float raidPoints) =>
            raidPoints < minPoints || (totalPieces != null && totalPieces.Evaluate(raidPoints) < 1f);

        internal List<ArtilleryPlacement> Configure(float raidPoints, float pawnPoints, Faction faction, out float share,
            bool breaching = false, ArtilleryGunPoolDef gunPool = null)
        {
            var result = new List<ArtilleryPlacement>();
            var used = new Dictionary<ArtilleryGunRule, int>();
            FloatRange activeShare = breaching && (breachingBudgetShare.max > 0f || breachingBudgetShare.min > 0f)
                ? breachingBudgetShare : budgetShare;
            share = activeShare.max > 0f || activeShare.min > 0f
                ? activeShare.RandomInRange : equipmentFraction;
            float remaining = pawnPoints * share;
            int limit = totalPieces == null ? int.MaxValue : Mathf.FloorToInt(totalPieces.Evaluate(raidPoints));
            int mortarCount = 0;
            int nonEmpMortars = 0;
            while (result.Count < limit)
            {
                var choices = new List<ArtilleryGunRule>();
                foreach (ArtilleryGunRule rule in gunPool?.guns ?? guns)
                {
                    if (rule.turret == null || rule.turret.techLevel > faction.def.techLevel ||
                        (rule.onlyFaction != null && rule.onlyFaction != faction.def) ||
                        (rule.excludeFaction != null && rule.excludeFaction == faction.def)) continue;
                    if (rule.role == ArtilleryGunRole.CampDefense && !result.Exists(g => g.IsSupport)) continue;
                    float cost = rule.ResolvedCost;
                    if (cost > remaining) continue;
                    used.TryGetValue(rule, out int count);
                    int typeLimit = rule.count == null ? int.MaxValue : Mathf.FloorToInt(rule.count.Evaluate(raidPoints));
                    if (count >= typeLimit) continue;
                    ThingDef previewAmmo = rule.ammo;
                    if (previewAmmo == null && rule.defaultAmmo)
                        previewAmmo = SelectAmmo(rule.turret, faction, true);
                    bool mortar = rule.role == ArtilleryGunRole.Support && !CeArtillerySight.IsDirectFire(rule.turret, previewAmmo);
                    if (mortar && mortarCount >= mortarClassLimit) continue;
                    choices.Add(rule);
                }
                if (choices.Count == 0) break;
                ArtilleryGunRule selected = choices.RandomElementByWeight(g => g.weight);
                bool selectedIsMortar = selected.role == ArtilleryGunRole.Support &&
                    !CeArtillerySight.IsDirectFire(selected.turret,
                        selected.ammo ?? (selected.defaultAmmo ? SelectAmmo(selected.turret, faction, true) : null));
                bool allowsEmp = selectedIsMortar && nonEmpMortars >= 1;
                ThingDef ammo = selected.ammo ?? SelectAmmo(selected.turret, faction, selected.defaultAmmo, allowsEmp);
                if (!allowsEmp && IsEmpAmmo(selected.turret, ammo))
                    ammo = SelectAmmo(selected.turret, faction, defaultAmmo: false);
                CompProperties_AmmoUser ammoProps = selected.turret.building.turretGunDef.GetCompProperties<CompProperties_AmmoUser>();
                if ((!allowsEmp && IsEmpAmmo(selected.turret, ammo)) ||
                    (ammo == null && ammoProps?.ammoSet != null && AmmoUtility.IsAmmoSystemActive(ammoProps.ammoSet)))
                {
                    used[selected] = selected.count == null
                        ? int.MaxValue : Mathf.FloorToInt(selected.count.Evaluate(raidPoints));
                    continue;
                }
                result.Add(new ArtilleryPlacement
                {
                    Def = selected.turret,
                    Stuff = selected.turret.MadeFromStuff ? ThingDefOf.Steel : null,
                    Ammo = ammo,
                    Role = selected.role,
                    ConstructionWorkFactor = selected.constructionWorkFactor
                });
                used.TryGetValue(selected, out int previous);
                used[selected] = previous + 1;
                remaining -= selected.ResolvedCost;
                if (selected.role == ArtilleryGunRole.Support && !CeArtillerySight.IsDirectFire(selected.turret, ammo))
                {
                    mortarCount++;
                    if (!IsEmpAmmo(selected.turret, ammo)) nonEmpMortars++;
                }
            }
            return result;
        }

        internal static bool IsNonEmpMortar(ArtilleryPlacement gun) =>
            gun != null && gun.IsSupport && !gun.IsDirectSupport && !IsEmpGun(gun);

        internal static bool IsEmpGun(ArtilleryPlacement gun) =>
            gun != null && IsEmpAmmo(gun.Def, gun.Ammo);

        internal static bool IsEmpAmmo(ThingDef turret, ThingDef ammo)
        {
            if (ammo == null) return false;
            ThingDef projectile = null;
            CompProperties_AmmoUser props = turret?.building?.turretGunDef?.GetCompProperties<CompProperties_AmmoUser>();
            if (props?.ammoSet?.ammoTypes != null)
            {
                foreach (AmmoLink link in props.ammoSet.ammoTypes)
                    if (link.ammo == ammo) { projectile = link.projectile; break; }
            }
            projectile ??= ammo.projectileWhenLoaded;
            return CeSiegeAmmo.IsEmpProjectile(projectile);
        }

        internal static ThingDef SelectAmmo(ThingDef turret, Faction faction, bool defaultAmmo, bool allowEMP = false)
        {
            if (defaultAmmo)
            {
                CompProperties_AmmoUser props = turret.building.turretGunDef.GetCompProperties<CompProperties_AmmoUser>();
                ThingDef projectile = turret.building.turretGunDef.Verbs[0].defaultProjectile;
                foreach (AmmoLink link in props.ammoSet.ammoTypes)
                    if (link.projectile == projectile) return link.ammo;
                return null;
            }
            CompProperties_AmmoUser ammo = turret.building.turretGunDef.GetCompProperties<CompProperties_AmmoUser>();
            if (ammo != null && !TurretGunUtility.NeedsShells(turret)) return CeSiegeAmmo.Select(ammo, faction, allowEMP);
            return TurretGunUtility.TryFindRandomShellDef(turret, allowEMP: allowEMP,
                allowToxGas: ModsConfig.BiotechActive && faction.def == FactionDefOf.PirateWaster,
                mustHarmHealth: true, techLevel: faction.def.techLevel,
                allowAntigrainWarhead: false, faction: faction);
        }
    }

    public sealed class ArtilleryGunPoolDef : Def
    {
        public List<ArtilleryGunRule> guns = new List<ArtilleryGunRule>();
    }

    public sealed class ArtilleryGunRule
    {
        public ArtilleryGunRole role = ArtilleryGunRole.Support;
        public ThingDef turret;
        public ThingDef ammo;
        public FactionDef onlyFaction, excludeFaction;
        // cost <= 0 resolves to turret.building.combatPower at configure time.
        public float cost;
        public float weight = 1f;
        public float constructionWorkFactor = 1f;
        public bool defaultAmmo;
        // null = unlimited of this type (subject to budget / total / mortar caps).
        public SimpleCurve count;

        internal float ResolvedCost => cost > 0f ? cost : (turret?.building?.combatPower ?? 0f);
    }
}
