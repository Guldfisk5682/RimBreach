using System.Linq;
using CombatExtended;
using HarmonyLib;
using RimSiege.Raids.Artillery;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Raids
{
    internal static class SiegeRoles
    {
        private static readonly AccessTools.FieldRef<LordJob_AssaultColony, bool> Sappers =
            AccessTools.FieldRefAccess<LordJob_AssaultColony, bool>("sappers");
        private static readonly AccessTools.FieldRef<LordJob_AssaultColony, bool> Breachers =
            AccessTools.FieldRefAccess<LordJob_AssaultColony, bool>("breachers");

        internal static bool IsHumanlikeRaider(Pawn pawn) =>
            pawn is { Dead: false, Downed: false, InMentalState: false } &&
            pawn.RaceProps?.Humanlike == true;

        internal static bool CanBuildHumanlikeCamp(Pawn pawn) =>
            IsHumanlikeRaider(pawn) && pawn.skills != null && pawn.workSettings != null &&
            !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Construction) &&
            !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Firefighter) &&
            pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation);

        // In humanlike raids, all non-humanlike units (war animals, dryads, accompanying mechs/entities)
        // are main-force-only. In pure Mechanoid raids (Faction.OfMechanoids), combat mechs use assault
        // ladders and Constructoids build batteries, while any non-mech/non-humanlike unit is main-force-only.
        internal static bool CanParticipateInTacticalAssault(Pawn pawn) =>
            pawn is { Dead: false, Downed: false, InMentalState: false } &&
            (pawn.RaceProps.Humanlike || (pawn.Faction == Faction.OfMechanoids && pawn.RaceProps.IsMechanoid));

        internal static bool IsMainForceOnly(Pawn pawn, bool allowConstructoid = false) =>
            pawn == null || (!pawn.RaceProps.Humanlike && (!allowConstructoid || !MechBattery.IsConstructoid(pawn)));

        internal static void AssignMainForceEscortDuty(Lord lord, Pawn pawn, IntVec3 fallbackFocus, ArtilleryRaid raid = null)
        {
            if (pawn?.mindState == null) return;
            bool allowConstructoid = lord?.LordJob is LordJob_MechArtillery;
            Pawn escortee = lord?.ownedPawns
                .Where(p => p != null && p != pawn && p.Spawned && !p.Dead && !p.Downed &&
                    !IsMainForceOnly(p, allowConstructoid))
                .OrderBy(p => raid?.Smoke?.HasCarrier(p) == true ? 1 : 0)
                .ThenBy(p => raid?.Camp?.Guns?.Any(g => g.Operator == p) == true ? 1 : 0)
                .ThenBy(p => pawn.Spawned ? p.Position.DistanceToSquared(pawn.Position) : 0)
                .FirstOrDefault();
            if (escortee != null)
                pawn.mindState.duty = new PawnDuty(DutyDefOf.Escort, escortee, 12f);
            else
                pawn.mindState.duty = new PawnDuty(DutyDefOf.Defend,
                    fallbackFocus.IsValid ? fallbackFocus : pawn.PositionHeld) { radius = 12f };
        }

        internal static bool Ordinary(Lord lord)
        {
            if (lord?.LordJob is LordJob_Artillery artillery) return artillery.IsNativeAssault;
            if (lord?.LordJob is LordJob_StageThenAttack) return true;
            return lord?.LordJob is LordJob_AssaultColony assault && !Sappers(assault) && !Breachers(assault);
        }

        internal static bool LadderAssaultDuty(Pawn pawn)
        {
            Lord lord = pawn.GetLord();
            if (Ordinary(lord)) return pawn.mindState?.duty?.def == DutyDefOf.AssaultColony;
            return lord?.CurLordToil is LordToil_AssaultColonyBreaching &&
                (pawn.mindState?.duty?.def == DutyDefOf.AssaultColony || pawn.mindState?.duty?.def == DutyDefOf.Escort);
        }

        internal static bool Eligible(Pawn pawn, SiegeParty party) =>
            CanParticipateInTacticalAssault(pawn) && pawn.Spawned &&
            ProjectZ.RaidClimb.Climber(pawn) &&
            (Ordinary(pawn.GetLord()) || LadderAssaultDuty(pawn)) &&
            ProjectZ.RaidClimb.MayClimb(pawn, false) &&
            !party.Operators.Contains(pawn) && !pawn.Map.GetComponent<ArtilleryMap>().IsOperator(pawn) &&
            pawn.CurJobDef != JobDefOf.ManTurret && !Demolition(pawn);

        internal static bool FireSupportMech(Pawn pawn) =>
            pawn.RaceProps.IsMechanoid && pawn.BodySize >= 3f &&
            pawn.equipment?.Primary?.def.IsRangedWeapon == true;

        internal static bool MeleeAssault(Pawn pawn) =>
            pawn.equipment?.Primary is { } weapon
                ? weapon.def.IsMeleeWeapon
                : pawn.kindDef.canMeleeAttack;

        internal static int LadderPriority(Pawn pawn)
        {
            if (pawn.RaceProps.IsMechanoid)
            {
                if (FireSupportMech(pawn) || MechBattery.IsConstructoid(pawn)) return 2;
                return MeleeAssault(pawn) ? 0 : 1;
            }
            if (pawn.mindState.duty?.def == DutyDefOf.Build ||
                pawn.CurJobDef == JobDefOf.ManTurret) return 3;
            if (MeleeAssault(pawn)) return 0;
            if (pawn.GetLord()?.LordJob is LordJob_Artillery) return 3;
            return pawn.Faction.def.techLevel >= TechLevel.Industrial &&
                ArtilleryDeployment.CarriesLadderPriorityEquipment(pawn) ? 1 : 2;
        }

        private static bool Demolition(Pawn pawn)
        {
            if (pawn.mindState?.duty?.def == DutyDefOf.Breaching || pawn.mindState?.duty?.def == DutyDefOf.Sapper)
                return true;
            if (ArtilleryDeployment.HasMeleePrimary(pawn)) return false;
            return !(pawn.Faction.def.techLevel >= TechLevel.Industrial &&
                ArtilleryDeployment.CarriesLadderPriorityEquipment(pawn) &&
                (pawn.equipment?.Primary == null || !DemolitionWeapon(pawn.equipment.Primary) ||
                    pawn.equipment.Primary.def.weaponTags?.Contains("CE_AI_Grenade") == true)) && HasDemolitionWeapon(pawn);
        }

        internal static bool HasDemolitionWeapon(Pawn pawn)
        {
            if (pawn?.RaceProps?.Humanlike != true) return false;
            if (pawn.equipment?.Primary != null && DemolitionWeapon(pawn.equipment.Primary)) return true;
            CompInventory inventory = pawn.TryGetComp<CompInventory>();
            if (inventory != null)
                foreach (ThingWithComps weapon in inventory.weapons)
                    if (DemolitionWeapon(weapon)) return true;
            return false;
        }

        internal static bool DemolitionWeapon(ThingWithComps weapon)
        {
            Verb verb = weapon.TryGetComp<CompEquippable>()?.PrimaryVerb;
            if (verb == null || verb.IsMeleeAttack || verb.IsIncendiary_Ranged()) return false;
            CompAmmoUser ammo = weapon.TryGetComp<CompAmmoUser>();
            if (ammo != null && ammo.UseAmmo && !ammo.HasAmmoOrMagazine) return false;
            ThingDef projectile = ammo?.CurAmmoProjectile ?? (verb is Verb_LaunchProjectileCE ce ? ce.Projectile : verb.GetProjectile());
            ProjectileProperties props = projectile?.projectile;
            return props != null && (verb.verbProps.ai_IsBuildingDestroyer || props.explosionRadius > 0f) && props.damageDef.harmsHealth &&
                props.GetDamageAmount(weapon) * props.damageDef.buildingDamageFactor * props.damageDef.buildingDamageFactorImpassable > 0f;
        }
    }
}
