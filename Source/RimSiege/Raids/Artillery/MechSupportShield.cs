using System.Linq;
using CombatExtended;
using HarmonyLib;
using RimSiege.Integration;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace RimSiege.Raids.Artillery
{
    [DefOf]
    internal static class MechSupportShield
    {
        private const float ApproachRadius = 10f;
        private const int DropRetryIntervalTicks = 60;

        public static ThingDef RS_MechSupportShield = null;
        static MechSupportShield() => DefOfHelper.EnsureInitializedInCtor(typeof(MechSupportShield));

        internal static bool Eligible(ArtilleryRaid raid) =>
            raid.Kind == ArtilleryRaidKind.Mechanoid && RimSiegeMod.Settings.MechSupportShield &&
            raid.RaidPoints >= RimSiegeMod.Settings.MechShieldPoints;

        internal static void Tick(ArtilleryRaid raid, Lord lord, ref int nextRetryTick)
        {
            if (raid.ShieldAssigned || !Eligible(raid) || !raid.SuppliesDelivered ||
                lord.CurLordToil is not LordToil_Siege || raid.Phase is ArtilleryPhase.Assaulting or ArtilleryPhase.Abandoned)
                return;
            int tick = GenTicks.TicksGame;
            if (tick < nextRetryTick) return;
            nextRetryTick = tick + DropRetryIntervalTicks;

            Map map = lord.Map;
            ArtilleryIncident incident = map.GetComponent<ArtilleryMap>().Incident(raid.IncidentKey);
            if (incident?.ShieldAssigned == true)
            {
                raid.ShieldAssigned = true;
                return;
            }

            CampPlan camp = raid.Camp;
            var arrived = lord.ownedPawns.Where(pawn => pawn.Spawned && !pawn.Downed &&
                MechBattery.IsConstructoid(pawn) && IsoFloor.SameFloor(pawn.Position, camp.Center, map) &&
                (pawn.Position.InHorDistOf(camp.Center, ApproachRadius) || camp.Guns.Any(gun =>
                    pawn.Position.InHorDistOf(gun.Cell, ApproachRadius)))).ToList();
            if (arrived.Count == 0) return;

            float shieldRadius = RS_MechSupportShield.GetCompProperties<CompProperties_ProjectileInterceptor>().radius;
            var reserved = CampSupplies.Reserved(map, camp);
            IntVec3 spot = GenRadial.RadialCellsAround(camp.Center, CampSupplies.Radius, true).FirstOrFallback(cell =>
                cell.InBounds(map) && IsoFloor.SameFloor(cell, camp.Center, map) && !reserved.Contains(cell) &&
                DropCellFinder.IsGoodDropSpot(cell, map, allowFogged: true, canRoofPunch: true) &&
                !cell.GetThingList(map).Any(t => t is Building or Blueprint or Frame) &&
                GenSpawn.CanSpawnAt(RS_MechSupportShield, cell, map, Rot4.North, canWipeEdifices: false) &&
                arrived.Any(pawn => pawn.Position.InHorDistOf(cell, shieldRadius)), IntVec3.Invalid);
            if (!spot.IsValid || !Drop(spot, map, raid.Faction)) return;

            raid.ShieldAssigned = true;
            if (incident != null) incident.ShieldAssigned = true;
        }

        private static bool Drop(IntVec3 cell, Map map, Faction faction)
        {
            // The mech-cluster building pod lifecycle, with immediate opening
            // and a checked payload. Blueprint permissions do not govern a drop.
            Thing shield = ThingMaker.MakeThing(RS_MechSupportShield);
            shield.SetFactionDirect(faction);
            var pod = new ActiveTransporterInfo
            {
                openDelay = 0,
                leaveSlag = false,
                despawnPodBeforeSpawningThing = true,
                moveItemsAsideBeforeSpawning = true,
                spawnWipeMode = WipeMode.VanishOrMoveAside,
                setRotation = Rot4.North
            };
            if (!pod.innerContainer.TryAdd(shield))
            {
                shield.Destroy();
                return false;
            }
            DropPodUtility.MakeDropPodAt(cell, map, pod, faction);
            return true;
        }

        internal static void Destroy(Thing shield)
        {
            if (shield.def == RS_MechSupportShield && !shield.Destroyed)
                shield.Destroy(DestroyMode.Vanish);
        }
    }

    public sealed class CompMechSupportShieldLifetime : CompDestroyAfterDelay
    {
        public override void CompTick()
        {
            if (parent.Spawned) base.CompTick();
        }
    }

    [HarmonyPatch(typeof(ProjectileCE), "CheckIntercept")]
    internal static class MechSupportShieldCeEmp
    {
        private static void Postfix(ProjectileCE __instance, Thing interceptorThing, bool __result)
        {
            if (__result && interceptorThing.def == MechSupportShield.RS_MechSupportShield &&
                (__instance.DamageDef == DamageDefOf.EMP ||
                 (__instance.def.projectile as ProjectilePropertiesCE)?.secondaryDamage?.Any(d => d.def == DamageDefOf.EMP) == true))
                MechSupportShield.Destroy(interceptorThing);
        }
    }

    [HarmonyPatch(typeof(CompProjectileInterceptor), nameof(CompProjectileInterceptor.CheckIntercept))]
    internal static class MechSupportShieldVanillaEmp
    {
        private static void Postfix(CompProjectileInterceptor __instance, Projectile projectile, bool __result)
        {
            if (__result && __instance.parent?.def == MechSupportShield.RS_MechSupportShield &&
                projectile.DamageDef == DamageDefOf.EMP)
                MechSupportShield.Destroy(__instance.parent);
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.TakeDamage))]
    internal static class MechSupportShieldBodyEmp
    {
        private static bool Prefix(Thing __instance, DamageInfo dinfo, ref DamageWorker.DamageResult __result)
        {
            if (__instance.def != MechSupportShield.RS_MechSupportShield) return true;
            __result = new DamageWorker.DamageResult();
            if (dinfo.Def == DamageDefOf.EMP) MechSupportShield.Destroy(__instance);
            return false;
        }
    }
}
