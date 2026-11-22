using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using RimSiege.Fortress;
using RimSiege.Integration;
using RimSiege.Raids.Artillery;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimSiege.Raids
{
    internal static class SmokeCombat
    {
        internal static bool IsSmokeWeapon(ThingDef def) => def != null &&
            (def == ArtillerySmokeDefOf.CE_Weapon_GrenadeSmoke || def == ArtillerySmokeDefOf.CE_Weapon_SmokeBomb);

        internal static bool Screened(IntVec3 cell, Map map) =>
            cell.InBounds(map) && cell.AnyGas(map, GasType.BlindSmoke);

        internal static bool Ready(Pawn pawn) =>
            pawn is { Spawned: true, Downed: false, Dead: false, InMentalState: false } &&
            pawn.RaceProps.Humanlike &&
            !pawn.WorkTagIsDisabled(WorkTags.Violent) &&
            pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) &&
            pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving);

        internal static ThingWithComps FindSmokeWeapon(Pawn pawn, ThingDef weaponDef)
        {
            if (pawn == null || weaponDef == null) return null;
            if (pawn.equipment?.Primary is { Destroyed: false } equipped && equipped.def == weaponDef)
                return equipped;
            return pawn.inventory?.innerContainer?
                .FirstOrDefault(t => !t.Destroyed && t.def == weaponDef) as ThingWithComps;
        }

        internal static int CountSmokeWeapon(Pawn pawn, ThingDef weaponDef)
        {
            if (pawn == null || weaponDef == null) return 0;
            int count = 0;
            if (pawn.equipment?.Primary is { Destroyed: false } equipped && equipped.def == weaponDef)
                count += equipped.stackCount;
            if (pawn.inventory?.innerContainer != null)
                foreach (Thing item in pawn.inventory.innerContainer)
                    if (!item.Destroyed && item.def == weaponDef) count += item.stackCount;
            return count;
        }

        internal static ThingWithComps GiveSmoke(Pawn pawn, ThingDef weaponDef, int count)
        {
            if (pawn == null || weaponDef == null || count <= 0) return null;
            var inventory = pawn.TryGetComp<CompInventory>();
            if (inventory == null) return null;
            var grenade = (ThingWithComps)ThingMaker.MakeThing(weaponDef);
            grenade.stackCount = Math.Min(count, weaponDef.stackLimit);
            if (FortressSite.SupportSite(pawn) != null) FortressAlliedReinforcement.Biocode(grenade, pawn);
            if (inventory.container.TryAdd(grenade, canMergeWithExistingStacks: false)) return grenade;
            grenade.Destroy();
            return null;
        }

        internal static float ThrowRange(ThingDef weaponDef, float margin = 1.2f, float fallback = 10f)
        {
            float range = weaponDef?.Verbs?.FirstOrDefault(v => !v.IsMeleeAttack)?.range ?? fallback;
            return Math.Max(4f, range - margin);
        }

        internal static float ExplosionRadius(ThingDef weaponDef, float fallback = 2f)
        {
            float radius = weaponDef?.Verbs?.FirstOrDefault(v => !v.IsMeleeAttack)?
                .defaultProjectile?.projectile?.explosionRadius ?? 0f;
            return radius > 0f ? radius : fallback;
        }

        internal static bool CanThrowNow(IntVec3 pos, IntVec3 target, Map map, float throwRange)
        {
            if (!pos.InBounds(map) || !target.InBounds(map) || !IsoFloor.SameFloor(pos, target, map)) return false;
            float dist = IsoFloor.RangeDistance(pos, target, map);
            return dist >= 2f && dist <= throwRange &&
                GenSight.LineOfSight(pos, target, map, skipFirstCell: true);
        }

        internal static Vector3 ApproachSide(List<IntVec3> route, int index, IntVec3 rally)
        {
            IntVec3 before = route[Math.Max(0, index - 3)];
            IntVec3 after = route[Math.Min(route.Count - 1, index + 3)];
            Vector3 tangent = (after - before).ToVector3();
            tangent.y = 0f;
            if (tangent.sqrMagnitude < 0.01f) tangent = (route[^1] - rally).ToVector3();
            return new Vector3(-tangent.z, 0f, tangent.x).normalized;
        }

        internal static IntVec3 FlankCell(IntVec3 spine, Vector3 side, int offset, Map map)
        {
            var cell = spine + new IntVec3(Mathf.RoundToInt(side.x * offset), 0, Mathf.RoundToInt(side.z * offset));
            return cell != spine && cell.InBounds(map) && IsoFloor.SameFloor(cell, spine, map) &&
                !cell.Filled(map) && cell.Walkable(map) && GenSight.LineOfSight(spine, cell, map, skipFirstCell: true)
                ? cell : IntVec3.Invalid;
        }

        internal static int FlightGraceTicks(IntVec3 from, IntVec3 to, Map map, Verb verb, ThingDef weaponDef = null)
        {
            ThingDef proj = (verb as Verb_LaunchProjectileCE)?.Projectile
                ?? verb?.GetProjectile()
                ?? verb?.verbProps?.defaultProjectile
                ?? weaponDef?.Verbs?.FirstOrDefault(v => !v.IsMeleeAttack)?.defaultProjectile;
            ProjectileProperties props = proj?.projectile;
            float speedPerSec = props != null && props.speed > 0f ? props.speed : 12f;
            int delay = props?.explosionDelay ?? 0;
            float dist = from.IsValid && to.IsValid && map != null
                ? Mathf.Max(2f, Mathf.Max(IsoFloor.Distance3D(from, to, map), IsoFloor.RangeDistance(from, to, map)))
                : 8f;
            // CE projectiles move in cells per real second (60 ticks/s); add fuse delay plus landing buffer.
            int flight = UnityEngine.Mathf.CeilToInt(dist * GenTicks.TicksPerRealSecond / speedPerSec);
            return Math.Clamp(flight + delay + 25, 35, 180);
        }
    }

    internal sealed class SmokeThreatCache
    {
        private readonly bool includeColonists;
        private readonly Faction threatFaction;
        private Verb[] currentThreats = Array.Empty<Verb>();
        private int threatWindow = -1;
        private readonly Dictionary<IntVec3, bool> exposure = new();

        internal SmokeThreatCache(bool includeColonists, Faction threatFaction)
        {
            this.includeColonists = includeColonists;
            this.threatFaction = threatFaction;
        }

        internal Verb[] CurrentThreats(Map map)
        {
            int window = GenTicks.TicksGame / 15;
            if (window != threatWindow)
            {
                threatWindow = window;
                currentThreats = CollectThreats(map, includeColonists, threatFaction);
                exposure.Clear();
            }
            return currentThreats;
        }

        internal bool IsExposed(IntVec3 cell, Map map)
        {
            if (!cell.InBounds(map)) return false;
            Verb[] threats = CurrentThreats(map);
            if (threats.Length == 0) return false;
            if (!exposure.TryGetValue(cell, out bool exposed))
                exposure[cell] = exposed = IsExposedToThreats(cell, map, threats);
            return exposed;
        }

        internal bool CanAdvance(IntVec3 cell, Map map)
        {
            if (!cell.InBounds(map)) return false;
            if (SmokeCombat.Screened(cell, map)) return true;
            return !IsExposed(cell, map);
        }

        internal static Verb[] CollectThreats(Map map, bool includeColonists, Faction threatFaction = null)
        {
            Faction faction = threatFaction ?? Faction.OfPlayer;
            bool player = faction == Faction.OfPlayer;
            IEnumerable<Building> buildings = player ? map.listerBuildings.allBuildingsColonistCombatTargets :
                map.listerBuildings.allBuildingsNonColonist.Where(building => building.Faction == faction);
            IEnumerable<IAttackTargetSearcher> shooters = buildings.OfType<IAttackTargetSearcher>();
            if (includeColonists)
                shooters = shooters.Concat(player ? map.mapPawns.FreeColonistsSpawned :
                    map.mapPawns.SpawnedPawnsInFaction(faction));

            var result = new List<Verb>();
            foreach (IAttackTargetSearcher shooter in shooters)
            {
                if (shooter is IAttackTarget target && target.ThreatDisabled(null)) continue;
                Verb verb = shooter is Building_Turret turret ? turret.AttackVerb : shooter.CurrentEffectiveVerb;
                if (verb == null || verb.verbProps.IsMeleeAttack) continue;
                ThingDef proj = verb is Verb_LaunchProjectileCE ce ? ce.Projectile : verb.GetProjectile();
                if (proj?.projectile?.flyOverhead == true) continue;
                result.Add(verb);
            }
            return result.ToArray();
        }

        internal static bool IsExposedToThreats(IntVec3 cell, Map map, Verb[] threats)
        {
            for (int i = 0; i < threats.Length; i++)
            {
                Verb verb = threats[i];
                if (verb.Caster is { Spawned: true } caster &&
                    IsoFloor.RangeDistance(caster.Position, cell, map) <= verb.EffectiveRange &&
                    verb.CanHitTarget(cell))
                    return true;
            }
            return false;
        }
    }
}
