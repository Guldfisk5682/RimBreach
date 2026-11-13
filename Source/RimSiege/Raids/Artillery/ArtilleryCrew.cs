using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimSiege.Integration;
using Verse.AI;
using Verse;
using Verse.AI.Group;

namespace RimSiege.Raids.Artillery
{
    // The camp lord keeps construction/manning; the original strategy owns the
    // assault lord. No second builder roster or tick scheduler is introduced.
    internal static class ArtilleryCrew
    {
        internal static bool Pending(ArtilleryPlacement gun) => gun.Building == null &&
            (gun.Frame?.Spawned == true || gun.Blueprint?.Spawned == true);

        internal static bool HasConstruction(ArtilleryRaid raid) => ArtilleryCamp.Placements(raid.Camp).Any(Pending);
        internal static bool HasSupportConstruction(ArtilleryRaid raid) => raid.Camp.Guns.Any(g => g.IsSupport && Pending(g));
        internal static bool SupportReady(ArtilleryRaid raid) => raid.Camp.Guns.Any(g => g.IsSupport) &&
            raid.Camp.Guns.Where(g => g.IsSupport).All(g => g.Building?.Spawned == true);

        internal static bool BuilderCandidate(LordToil_Siege toil, Pawn pawn) => !pawn.Dead &&
            !pawn.Downed && !pawn.InMentalState && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) &&
            NativeSiege.CanBuild(toil, pawn);

        internal static bool CanBuild(LordToil_Siege toil, Pawn pawn) => pawn.Spawned && BuilderCandidate(toil, pawn);

        internal static bool HasBuilder(Lord camp) => camp.CurLordToil is LordToil_Siege toil &&
            camp.ownedPawns.Any(p => CanBuild(toil, p));

        internal static bool HasArrivingBuilder(ArtilleryRaid raid, Lord camp)
        {
            var toil = camp.Graph.lordToils.OfType<LordToil_Siege>().Single();
            return raid.IndependentCrew && camp.ownedPawns.Any(p => !p.Spawned && BuilderCandidate(toil, p));
        }

        internal static IEnumerable<Pawn> NearbyReplacements(ArtilleryRaid raid, Lord camp)
        {
            Lord assault = raid.AssaultLord;
            if (assault == null || raid.AssaultEnded || assault.CurLordToil is LordToil_ExitMap or LordToil_PanicFlee)
                return Enumerable.Empty<Pawn>();
            var specialists = ArtilleryDeployment.Specialists(raid.Strategy, assault.ownedPawns);
            return assault.ownedPawns.Where(p => p.Spawned && !p.Dead && !p.Downed && !p.InMentalState &&
                p.Map == camp.Map && p.Faction == raid.Faction && !specialists.Contains(p) && LadderInventory.Get(p) == null &&
                IsoFloor.Distance3D(p.Position, raid.Camp.Center, camp.Map) <= 20f);
        }

        internal static void TransferReplacement(Pawn pawn, Lord assault, LordToil_Siege toil, bool gunner)
        {
            assault.RemovePawn(pawn);
            if (pawn.CurJob != null) pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
            if (gunner) NativeSiege.AssignGunnerDuty(toil, pawn);
            else NativeSiege.AssignBuilderDuty(toil, pawn);
            toil.rememberedDuties[pawn] = pawn.mindState.duty.def;
            toil.lord.AddPawns(new[] { pawn }, updateDuties: false);
        }

        internal static bool ReplaceBuilder(ArtilleryRaid raid, LordToil_Siege toil)
        {
            if (!raid.Faction.def.humanlikeFaction || !HasConstruction(raid)) return false;
            Lord camp = toil.lord;
            Pawn replacement = NearbyReplacements(raid, camp).Where(p => CanBuild(toil, p) &&
                    p.CanReach(raid.Camp.Center, PathEndMode.Touch, Danger.Deadly))
                .OrderBy(p => IsoFloor.Distance3D(p.Position, raid.Camp.Center, camp.Map)).FirstOrDefault();
            if (replacement == null) return false;
            TransferReplacement(replacement, raid.AssaultLord, toil, gunner: false);
            return true;
        }

        internal static HashSet<Pawn> Retained(ArtilleryRaid raid, Lord camp)
        {
            var crew = new HashSet<Pawn>(raid.Camp.Guns.Where(g => g.Operator != null).Select(g => g.Operator));
            if (!HasConstruction(raid)) return crew;
            var toil = (LordToil_Siege)camp.CurLordToil;
            bool autonomous = !((LordJob_Artillery)camp.LordJob).RequiresOperators;
            foreach (Pawn pawn in camp.ownedPawns)
                if (CanBuild(toil, pawn) && (autonomous || pawn.mindState.duty?.def == DutyDefOf.Build)) crew.Add(pawn);
            // Before vanilla's initial builder assignment, the reserved human
            // gunners can build; retain one native-eligible helper if none can.
            if (!crew.Any(p => CanBuild(toil, p)))
            {
                Pawn builder = camp.ownedPawns.Where(p => CanBuild(toil, p))
                    .OrderBy(p => p.Position.DistanceToSquared(raid.Camp.Center)).FirstOrDefault();
                if (builder != null) crew.Add(builder);
            }
            foreach (Pawn pawn in crew)
                if (CanBuild(toil, pawn)) NativeSiege.AssignBuilderDuty(toil, pawn);
            return crew;
        }

        internal static void RestoreBuilders(ArtilleryRaid raid, LordToil_Siege toil)
        {
            if (!raid.SuppliesDelivered || !HasConstruction(raid) ||
                !raid.InfantryReleased && !raid.SmokeAttempted) return;
            var builders = toil.lord.ownedPawns.Where(pawn => CanBuild(toil, pawn));
            if (!raid.InfantryReleased)
            {
                if (toil.lord.ticksInToil >= 450) return;
                int count = System.Math.Max(1, UnityEngine.Mathf.RoundToInt(
                    toil.lord.ownedPawns.Count * NativeSiege.Data(toil).desiredBuilderFraction));
                builders = builders.OrderBy(pawn => pawn.Position.DistanceToSquared(raid.Camp.Center)).Take(count);
            }
            foreach (Pawn pawn in builders) NativeSiege.AssignBuilderDuty(toil, pawn);
        }

        // Called by the existing siege refresh, after construction/operator state
        // is refreshed. Completed guns release spare builders, not their gunners.
        internal static void Update(ArtilleryRaid raid, Lord camp)
        {
            if (!raid.InfantryReleased) return;
            if (HasConstruction(raid) && !HasBuilder(camp) && !HasArrivingBuilder(raid, camp) &&
                !ReplaceBuilder(raid, (LordToil_Siege)camp.CurLordToil))
            {
                ArtilleryCamp.CancelUnfinished(raid);
                ArtilleryCamp.RefreshConstruction(raid, camp.Map);
                ArtilleryCamp.RefreshOperators(raid, camp);
            }
            if (HasConstruction(raid)) return;
            var operators = new HashSet<Pawn>(raid.Camp.Guns.Where(g => g.Operator != null).Select(g => g.Operator));
            raid.Phase = operators.Count != 0 ? ArtilleryPhase.Supporting : ArtilleryPhase.Assaulting;
            var attackers = camp.ownedPawns.Where(p => !operators.Contains(p)).ToList();
            foreach (Pawn pawn in attackers) RestUtility.WakeUp(pawn);
            ArtilleryCamp.TransferToAssault(raid, camp, attackers);
        }
    }
}
