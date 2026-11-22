using System.Collections.Generic;
using CombatExtended;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Raids.Artillery
{
    // Smoke preparation precedes the independent infantry split.
    // Native duties, construction and paths own all work after the handoff.
    internal static class ArtilleryDeployment
    {
        private const int FirstGunWaitTicks = 7500;

        private static bool HasCompletedSupportGun(ArtilleryRaid raid) =>
            raid.Camp.Guns.Any(g => g.IsSupport && g.Building is { Spawned: true } building && building.Faction == raid.Faction);

        private static bool HasPendingSupportGun(ArtilleryRaid raid) =>
            raid.Camp.Guns.Any(g => g.IsSupport && ArtilleryCrew.Pending(g));

        private static bool ShouldWaitForFirstGun(ArtilleryRaid raid, Lord camp) =>
            raid.HoldsForFirstGun && !raid.InfantryReleased && camp.CurLordToil is LordToil_Siege &&
            raid.Phase == ArtilleryPhase.Building && !HasCompletedSupportGun(raid) &&
            HasPendingSupportGun(raid) && camp.ticksInToil < FirstGunWaitTicks;

        internal static void ReviewInfantryRelease(ArtilleryRaid raid, Lord camp)
        {
            if (!raid.InfantryReleased && raid.HoldsForFirstGun && !ShouldWaitForFirstGun(raid, camp))
                ArtilleryCamp.ReleaseInfantry(raid, camp, force: true);
        }

        internal static void NotifyGunCompleted(ArtilleryRaid raid, ArtilleryPlacement gun, LordJob_Artillery job)
        {
            if (!raid.InitialAmmoRequested) job.InitialAmmoReady = true;
            if (gun.IsSupport && raid.InfantryHold is { } hold) hold.ReviewRequested = true;
            if (!raid.InfantryReleased && raid.HoldsForFirstGun && gun.IsSupport)
                job.RequestInfantryAssault();
        }

        internal static void Begin(ArtilleryRaid raid)
        {
            if (raid.InfantryReleased || raid.Smoke != null || raid.InfantryHold != null || raid.CampLord == null ||
                raid.CampLord.CurLordToil is LordToil_ExitMap or LordToil_PanicFlee) return;
            if (!raid.SmokeAttempted && ArtillerySmoke.Eligible(raid) && raid.CampLord.CurLordToil == raid.CampLord.Graph.StartingToil)
            {
                if (raid.CampLord.ownedPawns.Any(p => !p.Dead && !p.Spawned && p.SpawnedOrAnyParentSpawned)) return;
                raid.SmokeAttempted = true;
                raid.Smoke = ArtillerySmoke.Plan(raid, out IntVec3 rally, out _);
                if (raid.Smoke != null)
                {
                    foreach (Pawn pawn in raid.CampLord.ownedPawns)
                        if (pawn.CurJob != null) pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                    raid.CampLord.GotoToil(raid.CampLord.Graph.lordToils.OfType<LordToil_ArtillerySmoke>().Single());
                    return;
                }
                EndSmoke(raid, rally);
                return;
            }
            if (raid.HoldsForFirstGun || HasMechEscorts(raid))
            {
                if (raid.CampLord.CurLordToil == raid.CampLord.Graph.StartingToil &&
                    !raid.CampLord.ownedPawns.Any(p => !p.Dead && !p.Spawned && p.SpawnedOrAnyParentSpawned))
                    EndSmoke(raid);
                return;
            }
            if (raid.IndependentCrew) Release(raid);
        }

        internal static void EndSmoke(ArtilleryRaid raid, IntVec3? fallbackRally = null)
        {
            Lord camp = raid.CampLord;
            ArtillerySmoke screen = raid.Smoke;
            LordJob_ArtilleryMechEscort.RejoinCamp(raid);
            raid.Smoke = null;
            foreach (Pawn pawn in camp.ownedPawns)
                if (pawn.CurJob != null) pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
            screen?.RestoreWeapons();
            if (camp.CurLordToil != camp.Graph.StartingToil) camp.GotoToil(camp.Graph.StartingToil);
            var toil = camp.Graph.lordToils.OfType<LordToil_Siege>().Single();
            Pawn builder = camp.ownedPawns.FirstOrDefault(pawn => ArtilleryCrew.CanBuild(toil, pawn));
            if (builder == null)
            {
                raid.InfantryReleased = true;
                ArtilleryCamp.TransferToAssault(raid, camp, camp.ownedPawns.ToList());
                return;
            }
            // Keep repaired pieces inside the actual smoke screen during final layout validation.
            raid.ScreenedDeployment = screen?.CampCovered(raid, camp.Map) == true;
            try { EnterSiege(camp); }
            finally { raid.ScreenedDeployment = false; }
            if (raid.SuppliesDelivered && camp.CurLordToil == toil)
            {
                IntVec3 follow = screen?.LastFollowCell is { IsValid: true } last ? last : screen?.Rally ??
                    (fallbackRally is { IsValid: true } rally ? rally : builder.Position);
                screen ??= new ArtillerySmoke { Rally = follow, ThreatFaction = raid.TargetFaction };
                int now = GenTicks.TicksGame;
                if (ArtillerySmoke.Eligible(raid))
                {
                    raid.BatterySupport = ArtilleryBatterySupport.Start(raid, toil, screen);
                    raid.BatterySupport.Review(raid, toil, now);
                }
                if (HasMechEscorts(raid) && HasPendingSupportGun(raid) && !HasCompletedSupportGun(raid))
                {
                    raid.InfantryHold = ArtilleryInfantryHold.Start(raid, toil, screen, follow);
                    toil.UpdateAllDuties();
                    raid.InfantryHold.ReviewSmoke(raid, camp, now);
                    ReviewSmokeHold(raid, toil);
                    return;
                }
                if (ShouldWaitForFirstGun(raid, camp))
                {
                    toil.UpdateAllDuties();
                    return;
                }
            }
            else if (ShouldWaitForFirstGun(raid, camp)) return;
            ArtilleryCamp.ReleaseInfantry(raid, camp, force: true);
        }

        private static bool HasMechEscorts(ArtilleryRaid raid) => raid.Kind != ArtilleryRaidKind.Mechanoid &&
            raid.CampLord.ownedPawns.Any(p => !p.Dead && !p.Downed && SiegeRoles.IsHumanRaidEscortMech(p));

        internal static void ReviewSmokeHold(ArtilleryRaid raid, LordToil_Siege toil)
        {
            Lord camp = toil.lord;
            raid.InfantryHold.ReviewRequested = false;
            ArtilleryIncident incident = camp.Map.GetComponent<ArtilleryMap>().IncidentForLord(camp);
            int losses = (incident?.ViolentLosses ?? raid.ViolentLosses).Count;
            int initial = incident?.InitialRaidSize ?? raid.InitialRaidSize;
            bool pending = HasPendingSupportGun(raid);
            bool cannotBuild = pending && !ArtilleryCrew.HasBuilder(camp) && !ArtilleryCrew.HasArrivingBuilder(raid, camp);
            if (!HasCompletedSupportGun(raid) && pending && !cannotBuild && losses * 10 < initial &&
                camp.ticksInToil < FirstGunWaitTicks) return;
            if (cannotBuild) ArtilleryCamp.CancelUnfinished(raid);
            ArtilleryCamp.ReleaseInfantry(raid, camp, force: true);
        }

        internal static bool HasMeleePrimary(Pawn pawn) => pawn.equipment?.Primary?.def.IsMeleeWeapon == true;

        internal static bool CarriesLadderPriorityEquipment(Pawn pawn) =>
            CarriesSmokeBelt(pawn) || CarriesWeapon(pawn, weapon => IsThrown(weapon.def) && IsExplosive(weapon.def));

        internal static bool CarriesVanguardEquipment(Pawn pawn) =>
            pawn?.RaceProps?.Humanlike == true &&
            (CarriesSmokeBelt(pawn) || pawn.equipment?.Primary?.def is ThingDef weapon &&
                (weapon.IsMeleeWeapon || IsThrown(weapon) || weapon.weaponTags?.Contains("CE_AI_Launcher") == true ||
                 IsExplosive(weapon) || pawn.equipment.PrimaryEq?.PrimaryVerb?.IsIncendiary_Ranged() == true));

        private static bool CarriesSmokeBelt(Pawn pawn) =>
            pawn.apparel?.WornApparel.Any(apparel => apparel is SmokepopBelt) == true;

        private static bool CarriesWeapon(Pawn pawn, System.Predicate<ThingWithComps> qualifies) =>
            pawn.equipment?.Primary is ThingWithComps primary && qualifies(primary) ||
            pawn.TryGetComp<CompInventory>()?.weapons.Any(weapon => qualifies(weapon)) == true;

        private static bool IsThrown(ThingDef def) =>
            def.weaponTags?.Contains("CE_AI_Grenade") == true ||
            def.Verbs?.Any(verb => typeof(Verb_ThrowGrenade).IsAssignableFrom(verb.verbClass) ||
                verb.defaultProjectile?.projectile?.postExplosionGasType == GasType.BlindSmoke) == true;

        private static bool IsExplosive(ThingDef def) => def.Verbs?.Any(verb =>
            verb.defaultProjectile?.projectile is { } projectile && projectile.damageDef.harmsHealth &&
            (projectile.explosionRadius > 0 || projectile.damageDef.isExplosive ||
             typeof(ProjectileCE_Explosive).IsAssignableFrom(verb.defaultProjectile.thingClass))) == true;

        internal static void ResumeLegacyHandoff(ArtilleryRaid raid, LordToil_Siege toil)
        {
            raid.WaitingForFirstGun = false;
            raid.OptionalSmoke?.RestoreWeapons();
            raid.OptionalSmoke = null;
            ArtilleryCamp.ReleaseInfantry(raid, toil.lord, force: true);
        }

        private static void Release(ArtilleryRaid raid)
        {
            Lord camp = raid.CampLord;
            var toil = camp.Graph.lordToils.OfType<LordToil_Siege>().Single();
            HashSet<Pawn> specialists = Specialists(raid.Strategy, camp.ownedPawns);

            var crew = new HashSet<Pawn>();
            if (((LordJob_Artillery)camp.LordJob).RequiresOperators)
            {
                ArtilleryCamp.ReserveOperators(raid, camp.ownedPawns.Where(p =>
                    !specialists.Contains(p) && ArtilleryCamp.OperatorCandidate(p)));
                crew.UnionWith(raid.Camp.Guns.Where(g => g.Operator != null).Select(g => g.Operator));
            }
            else crew.UnionWith(camp.ownedPawns.Where(MechBattery.IsConstructoid));

            bool Builder(Pawn pawn) => ArtilleryCrew.BuilderCandidate(toil, pawn);
            // Gunners build first. Reserve one native-eligible helper only when
            // that crew has no builder; never take the strategy's required lead.
            if (!crew.Any(Builder))
            {
                Pawn helper = camp.ownedPawns.Where(p => !specialists.Contains(p) && Builder(p))
                    .OrderBy(p => p.PositionHeld.DistanceToSquared(raid.Camp.Center)).FirstOrDefault();
                if (helper != null) crew.Add(helper);
            }
            if (!crew.Any(Builder) || !raid.Camp.Guns.Any(g => g.IsSupport))
            {
                crew.Clear();
                raid.Camp.Guns.Clear();
                raid.Camp.Cover.Clear();
            }

            raid.InfantryReleased = true;
            ArtilleryCamp.TransferToAssault(raid, camp, camp.ownedPawns.Where(p => !crew.Contains(p)).ToList());
            TryDeploy(raid);
        }

        internal static HashSet<Pawn> Specialists(RaidStrategyDef strategy, IEnumerable<Pawn> roster)
        {
            var pawns = roster.Where(p => p != null && p.RaceProps.Humanlike).ToList();
            var specialists = new HashSet<Pawn>();
            if (strategy.Worker is RaidStrategyWorker_ImmediateAttackBreaching)
                specialists.UnionWith(pawns.Where(p => p.kindDef.isGoodBreacher));
            if (strategy.Worker is RaidStrategyWorker_ImmediateAttackSappers)
            {
                Pawn sapper = pawns.FirstOrDefault(SappersUtility.IsGoodSapper)
                    ?? pawns.FirstOrDefault(SappersUtility.IsGoodBackupSapper);
                if (sapper != null) specialists.Add(sapper);
            }

            return specialists;
        }

        internal static void PawnArrived(Pawn pawn, Map map)
        {
            ArtilleryRaid raid = map.GetComponent<ArtilleryMap>().ForPawn(pawn);
            if (raid == null || pawn.GetLord() != raid.CampLord) return;
            if (!raid.InfantryReleased) Begin(raid);
            else TryDeploy(raid);
        }

        internal static void TryDeploy(ArtilleryRaid raid)
        {
            if (!raid.IndependentCrew || !raid.InfantryReleased || raid.SuppliesDelivered || raid.CampLord == null ||
                raid.CampLord.CurLordToil is LordToil_Siege or LordToil_ExitMap or LordToil_PanicFlee) return;
            Lord camp = raid.CampLord;
            var siege = camp.Graph.lordToils.OfType<LordToil_Siege>().Single();
            if (!camp.ownedPawns.Any(p => ArtilleryCrew.CanBuild(siege, p))) return;
            // Enter the existing native preparation transition, so Init creates
            // the planned blueprints and native material/ammo drop list exactly once.
            EnterSiege(camp);
        }

        private static void EnterSiege(Lord camp)
        {
            camp.Graph.transitions.First(t => t.target is LordToil_Siege &&
                t.sources.Contains(camp.CurLordToil)).Execute(camp);
        }
    }
}
