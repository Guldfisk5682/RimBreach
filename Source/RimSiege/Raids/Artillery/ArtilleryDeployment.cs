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
        internal static void Begin(ArtilleryRaid raid)
        {
            if (raid.InfantryReleased || raid.Smoke != null || raid.CampLord == null ||
                raid.CampLord.CurLordToil is LordToil_ExitMap or LordToil_PanicFlee) return;
            if (!raid.SmokeAttempted && ArtillerySmoke.Eligible(raid) && raid.CampLord.CurLordToil == raid.CampLord.Graph.StartingToil)
            {
                if (raid.CampLord.ownedPawns.Any(p => !p.Dead && !p.Spawned && p.SpawnedOrAnyParentSpawned)) return;
                raid.SmokeAttempted = true;
                raid.Smoke = ArtillerySmoke.Plan(raid, out _);
                if (raid.Smoke != null)
                {
                    foreach (Pawn pawn in raid.CampLord.ownedPawns)
                        if (pawn.CurJob != null) pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                    raid.CampLord.GotoToil(raid.CampLord.Graph.lordToils.OfType<LordToil_ArtillerySmoke>().Single());
                    return;
                }
                EndSmoke(raid);
                return;
            }
            if (raid.IndependentCrew) Release(raid);
        }

        internal static void EndSmoke(ArtilleryRaid raid)
        {
            Lord camp = raid.CampLord;
            ArtillerySmoke screen = raid.Smoke;
            raid.Smoke = null;
            foreach (Pawn pawn in camp.ownedPawns)
                if (pawn.CurJob != null) pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
            screen?.RestoreWeapons();
            if (camp.CurLordToil != camp.Graph.StartingToil) camp.GotoToil(camp.Graph.StartingToil);
            raid.WaitingForFirstGun = true;
            var toil = camp.Graph.lordToils.OfType<LordToil_Siege>().Single();
            if (!camp.ownedPawns.Any(pawn => ArtilleryCrew.CanBuild(toil, pawn)))
            {
                raid.WaitingForFirstGun = false;
                raid.InfantryReleased = true;
                ArtilleryCamp.TransferToAssault(raid, camp, camp.ownedPawns.ToList());
                return;
            }
            // Keep repaired pieces inside the actual smoke screen during final layout validation.
            raid.ScreenedDeployment = screen?.CampCovered(raid, camp.Map) == true;
            try { EnterSiege(camp); }
            finally { raid.ScreenedDeployment = false; }
            if (!raid.SuppliesDelivered)
            {
                ReleaseWaitingInfantry(raid);
                return;
            }

            var specialists = Specialists(raid.Strategy, camp.ownedPawns);
            var vanguard = camp.ownedPawns.Where(pawn => ArtillerySmoke.Ready(pawn) &&
                !specialists.Contains(pawn) && !raid.Camp.Guns.Any(gun => gun.Operator == pawn) &&
                CarriesVanguardEquipment(pawn, screen?.HasCarrier(pawn) == true)).ToList();
            if (!camp.ownedPawns.Any(pawn => !vanguard.Contains(pawn) && ArtilleryCrew.CanBuild(toil, pawn)))
            {
                Pawn builder = vanguard.FirstOrDefault(pawn => ArtilleryCrew.CanBuild(toil, pawn));
                if (builder != null) vanguard.Remove(builder);
            }
            screen?.BeginOptionalCover(raid, camp);
            if (vanguard.Count == 0) return;

            camp.RemovePawns(vanguard);
            foreach (Pawn pawn in vanguard)
                if (pawn.CurJob != null) pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
            raid.VanguardLord = LordMaker.MakeNewLord(raid.Faction,
                new LordJob_AssaultColony(raid.Faction, canKidnap: false, canTimeoutOrFlee: false, canSteal: false), camp.Map, vanguard);
            camp.Map.GetComponent<SiegeRaidMap>().IssueInOrder(vanguard);
            toil.UpdateAllDuties();
        }

        internal static bool CarriesLadderPriorityEquipment(Pawn pawn) =>
            CarriesSmokeBelt(pawn) || CarriesWeapon(pawn, weapon => IsThrown(weapon.def) && IsExplosive(weapon.def));

        internal static bool CarriesVanguardEquipment(Pawn pawn) => CarriesVanguardEquipment(pawn, false);

        private static bool CarriesVanguardEquipment(Pawn pawn, bool smokeCarrier) =>
            CarriesSmokeBelt(pawn) || pawn.equipment?.Primary?.def is ThingDef weapon &&
                (!smokeCarrier || weapon != ArtillerySmokeDefOf.CE_Weapon_GrenadeSmoke) &&
                (IsThrown(weapon) || weapon.weaponTags?.Contains("CE_AI_Launcher") == true || IsExplosive(weapon));

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

        internal static void ReleaseWaitingInfantry(ArtilleryRaid raid)
        {
            raid.WaitingForFirstGun = false;
            if (!raid.SuppliesDelivered)
                foreach (ArtilleryPlacement gun in raid.Camp.Guns) gun.Operator = null;
            ArtilleryCamp.ReleaseInfantry(raid, raid.CampLord, underFire: true);
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
            var pawns = roster.ToList();
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
