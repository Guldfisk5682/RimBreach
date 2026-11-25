using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimSiege.Raids.Artillery
{
    internal static class HumanMechEscorts
    {
        private const float MinCombatPower = 40f;
        private const float StandardWeight = 1f;
        private const float ExplosiveOrBreacherWeight = 3f;

        internal static float BudgetFraction(TechLevel tech) => tech >= TechLevel.Ultra ? 0.30f : 0.20f;

        internal static int BossMinimumPoints(int escortMinimum) => Mathf.Min(10000, escortMinimum + 2000);

        internal static List<PawnKindDef> Select(float budget, bool breaching, bool allowBoss, out float spent)
        {
            spent = 0f;
            var selected = new List<PawnKindDef>();
            if (budget < MinCombatPower) return selected;

            // Reserve at most one per incident before the roster is split into camps.
            if (allowBoss && ModsConfig.BiotechActive &&
                DefDatabase<PawnKindDef>.GetNamed("Mech_Diabolus") is { } boss && boss.combatPower <= budget)
            {
                selected.Add(boss);
                spent = boss.combatPower;
            }

            var pool = Catalog(breaching);
            float remaining = budget - spent;
            while (remaining >= MinCombatPower)
            {
                var affordable = pool.Where(entry => entry.Kind.combatPower <= remaining).ToList();
                if (affordable.Count == 0) break;
                PawnKindDef chosen = affordable.RandomElementByWeight(entry => entry.Weight).Kind;
                selected.Add(chosen);
                remaining -= chosen.combatPower;
                spent += chosen.combatPower;
            }
            return selected;
        }

        internal static bool CarriesVolatileMortar(Pawn pawn) =>
            pawn.equipment?.Primary?.TryGetComp<CompAmmoUser>() is { UseAmmo: true } weapon &&
            weapon.CurAmmoSet.ammoTypes.Any(link => link.ammo.detonateProjectile != null &&
                link.projectile?.projectile?.flyOverhead == true);

        internal static void InjectUngrouped(ArtilleryRaid raid, List<Pawn> pawns, Map map)
        {
            if (raid == null || pawns == null || raid.Kind == ArtilleryRaidKind.Mechanoid ||
                raid.EscortMechKinds.NullOrEmpty())
                return;
            if (RaidSpawnPatch.Current?.Artillery == raid && RaidSpawnPatch.Current.EscortMechsInjected)
                return;

            int tile = map?.Tile ?? -1;
            foreach (PawnKindDef kind in raid.EscortMechKinds)
                pawns.Add(GenerateMech(kind, raid.Faction, tile));
            if (RaidSpawnPatch.Current?.Artillery == raid)
                RaidSpawnPatch.Current.EscortMechsInjected = true;
        }

        internal static void InjectGrouped(ArtilleryRaid reservation,
            List<(List<Pawn> Pawns, float Power, ArtilleryRaid Raid)> armedGroups, List<Pawn> allPawns, Map map)
        {
            if (reservation == null || allPawns == null || reservation.Kind == ArtilleryRaidKind.Mechanoid ||
                reservation.EscortMechKinds.NullOrEmpty() || armedGroups == null || armedGroups.Count == 0)
                return;
            if (RaidSpawnPatch.Current != null && RaidSpawnPatch.Current.EscortMechsInjected)
                return;

            int tile = map?.Tile ?? -1;
            var assignedPower = new float[armedGroups.Count];
            foreach (PawnKindDef kind in reservation.EscortMechKinds)
            {
                int best = 0;
                float bestRatio = float.MaxValue;
                float bestPower = -1f;
                for (int i = 0; i < armedGroups.Count; i++)
                {
                    float groupPower = Mathf.Max(1f, armedGroups[i].Power);
                    float ratio = assignedPower[i] / groupPower;
                    if (ratio < bestRatio || (Mathf.Approximately(ratio, bestRatio) && groupPower > bestPower))
                    {
                        best = i;
                        bestRatio = ratio;
                        bestPower = groupPower;
                    }
                }
                Pawn mech = GenerateMech(kind, reservation.Faction, tile);
                allPawns.Add(mech);
                armedGroups[best].Pawns.Add(mech);
                assignedPower[best] += kind.combatPower;
            }
            if (RaidSpawnPatch.Current != null)
                RaidSpawnPatch.Current.EscortMechsInjected = true;
        }

        internal static List<(PawnKindDef Kind, float Weight)> Catalog(bool breaching)
        {
            var pool = new List<(PawnKindDef, float)>();
            foreach (PawnKindDef kind in DefDatabase<PawnKindDef>.AllDefsListForReading)
            {
                float weight = EvaluateWeight(kind, breaching);
                if (weight > 0f) pool.Add((kind, weight));
            }
            return pool;
        }

        internal static float EvaluateWeight(PawnKindDef kind, bool breaching)
        {
            if (kind == null || kind.isBoss || kind.RaceProps?.IsMechanoid != true || kind.combatPower < MinCombatPower)
                return 0f;
            if (kind.isGoodBreacher && !breaching)
                return 0f;
            if (kind.weaponTags.NullOrEmpty() || kind.weaponMoney.max <= 0f)
                return 0f;

            LoadoutPropertiesExtension loadout = kind.GetModExtension<LoadoutPropertiesExtension>();
            bool anyQualifies = false;
            bool anyHeavy = kind.isGoodBreacher;
            foreach (ThingDef weapon in CandidateWeapons(kind))
            {
                InspectWeapon(weapon, loadout, out bool qualifies, out bool heavy);
                if (!qualifies) continue;
                anyQualifies = true;
                if (heavy) anyHeavy = true;
            }
            if (!anyQualifies) return 0f;
            return anyHeavy ? ExplosiveOrBreacherWeight : StandardWeight;
        }

        private static IEnumerable<ThingDef> CandidateWeapons(PawnKindDef kind)
        {
            var seen = new HashSet<ThingDef>();
            List<ThingStuffPair> pairs = PawnWeaponGenerator.AllWeaponPairs;
            if (pairs != null)
            {
                for (int i = 0; i < pairs.Count; i++)
                {
                    ThingStuffPair pair = pairs[i];
                    ThingDef td = pair.thing;
                    if (td == null || !td.IsRangedWeapon || td.generateAllowChance <= 0f ||
                        pair.Price > kind.weaponMoney.max ||
                        (kind.weaponStuffOverride != null && pair.stuff != kind.weaponStuffOverride) ||
                        (pair.stuff != null && !pair.stuff.stuffProps.allowedInStuffGeneration) ||
                        td.weaponTags.NullOrEmpty() || !td.weaponTags.Any(tag => kind.weaponTags.Contains(tag)))
                        continue;
                    if (seen.Add(td)) yield return td;
                }
                yield break;
            }

            foreach (ThingDef td in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (td.equipmentType != EquipmentType.Primary || !td.IsRangedWeapon ||
                    td.generateAllowChance <= 0f || td.weaponTags.NullOrEmpty() ||
                    !td.weaponTags.Any(tag => kind.weaponTags.Contains(tag)))
                    continue;
                if (seen.Add(td)) yield return td;
            }
        }

        private static void InspectWeapon(ThingDef weapon, LoadoutPropertiesExtension loadout,
            out bool weaponQualifies, out bool weaponHeavy)
        {
            weaponQualifies = false;
            weaponHeavy = false;
            if (weapon.Verbs.NullOrEmpty()) return;

            CompProperties_AmmoUser ammoProps = weapon.GetCompProperties<CompProperties_AmmoUser>();
            bool usesAmmoSet = ammoProps?.ammoSet?.ammoTypes != null &&
                AmmoUtility.IsAmmoSystemActive(ammoProps.ammoSet);

            foreach (VerbProperties verb in weapon.Verbs)
            {
                if (typeof(Verb_ShootBeam).IsAssignableFrom(verb.verbClass))
                {
                    if (!IsDamagingNonSmokeDamage(verb.beamDamageDef)) continue;
                    weaponQualifies = true;
                    if (verb.ai_IsBuildingDestroyer || verb.CausesExplosion) weaponHeavy = true;
                    continue;
                }

                if (!verb.LaunchesProjectile || verb.defaultProjectile == null) continue;

                bool verbQualifies = false;
                if (!usesAmmoSet)
                {
                    if (IsDamagingNonSmokeProjectile(verb.defaultProjectile))
                    {
                        verbQualifies = true;
                        if (IsExplosiveProjectile(verb.defaultProjectile)) weaponHeavy = true;
                    }
                }
                else
                {
                    foreach (AmmoLink link in ammoProps.ammoSet.ammoTypes)
                    {
                        if (!IsGeneratableAmmo(link.ammo, loadout) ||
                            !IsDamagingNonSmokeProjectile(link.projectile))
                            continue;
                        verbQualifies = true;
                        if (IsExplosiveProjectile(link.projectile)) weaponHeavy = true;
                    }
                }

                if (!verbQualifies) continue;
                weaponQualifies = true;
                if (verb.ai_IsBuildingDestroyer) weaponHeavy = true;
            }
        }

        private static bool IsGeneratableAmmo(AmmoDef ammo, LoadoutPropertiesExtension loadout)
        {
            if (ammo == null || !ammo.alwaysHaulable || ammo.menuHidden) return false;
            if (ammo.generateAllowChance > 0f) return true;
            if (loadout == null) return false;
            if (loadout.forcedAmmoCategory != null && ammo.ammoClass == loadout.forcedAmmoCategory) return true;
            return loadout.weightedAmmoCategories != null &&
                loadout.weightedAmmoCategories.Any(w => w.ammoCategory == ammo.ammoClass);
        }

        private static bool IsDamagingNonSmokeDamage(DamageDef damage) =>
            damage != null && damage.harmsHealth && damage != DamageDefOf.Smoke && damage != DamageDefOf.Extinguish;

        private static bool IsDamagingNonSmokeProjectile(ThingDef proj)
        {
            ProjectileProperties props = proj?.projectile;
            if (props == null || !IsDamagingNonSmokeDamage(props.damageDef)) return false;
            if (props.GetDamageAmount(null) > 0) return true;
            if ((proj.GetCompProperties<CompProperties_ExplosiveCE>()?.damageAmountBase ?? 0f) > 0f) return true;
            return (proj.GetCompProperties<CompProperties_Explosive>()?.damageAmountBase ?? 0f) > 0f;
        }

        private static bool IsExplosiveProjectile(ThingDef proj)
        {
            ProjectileProperties props = proj.projectile;
            if (IsBlastDamage(props.damageDef) && (props.explosionRadius > 0f ||
                typeof(Projectile_Explosive).IsAssignableFrom(proj.thingClass) ||
                typeof(Projectile_DoomsdayRocket).IsAssignableFrom(proj.thingClass))) return true;

            return proj.GetCompProperties<CompProperties_ExplosiveCE>() is { explosiveRadius: > 0f } ce &&
                    IsBlastDamage(ce.explosiveDamageType) ||
                proj.GetCompProperties<CompProperties_Explosive>() is { explosiveRadius: > 0f } vanilla &&
                    IsBlastDamage(vanilla.explosiveDamageType);
        }

        // CE also uses small projectile explosions to spread pure flame.
        private static bool IsBlastDamage(DamageDef damage) => IsDamagingNonSmokeDamage(damage) &&
            (damage.isExplosive || (!typeof(DamageWorker_Flame).IsAssignableFrom(damage.workerClass) &&
                damage.hediff != DamageDefOf.Burn.hediff));

        internal static Pawn GenerateMech(PawnKindDef kind, Faction faction, int tile)
        {
            Pawn pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                kind, faction, PawnGenerationContext.NonPlayer, tile,
                forceGenerateNewPawn: true, allowDead: false, allowDowned: false,
                canGeneratePawnRelations: false, mustBeCapableOfViolence: true,
                allowFood: false, allowAddictions: false));
            if (pawn.needs?.energy != null)
                pawn.needs.energy.CurLevel = pawn.needs.energy.MaxLevel;
            return pawn;
        }
    }
}
