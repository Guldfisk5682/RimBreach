using System;
using System.Collections.Generic;
using System.Linq;
using RimSiege.Integration;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Raids.Artillery
{
    internal sealed class ArtilleryInfantryHold : IExposable
    {
        private const int PawnsPerPoint = 8;
        private const int MaxPoints = 4;
        private const float FlankClearance = 8f;
        private const float FlankSearchRadius = 22f;
        private readonly Dictionary<Pawn, IntVec3> mechFlanks = new();
        internal List<ArtilleryWaitingPoint> Points = new();
        internal ArtillerySmoke MechScreen;
        internal bool ReviewRequested;

        public ArtilleryInfantryHold() { }

        internal static ArtilleryInfantryHold Start(ArtilleryRaid raid, LordToil_Siege toil, ArtillerySmoke opening, IntVec3 rear)
        {
            Lord camp = toil.lord;
            Map map = camp.Map;
            var hold = new ArtilleryInfantryHold { MechScreen = opening.LocalScreen(raid.Camp.Center) };
            hold.MechScreen.IsLocal = false;
            var crew = ArtilleryCrew.Retained(raid, camp);
            var candidates = camp.ownedPawns.Where(pawn => !crew.Contains(pawn) && ArtillerySmoke.Ready(pawn) &&
                pawn.TryGetComp<CombatExtended.CompInventory>() != null).ToList();
            int desired = Math.Clamp((camp.ownedPawns.Count - crew.Count + PawnsPerPoint - 1) / PawnsPerPoint, 1, MaxPoints);
            IntVec3 centerOnGround = IsoFloor.GroundCellOf(raid.Camp.Center, map);
            IntVec3 front = centerOnGround - IsoFloor.GroundCellOf(rear, map);
            if (front.LengthHorizontalSquared == 0 && raid.Camp.Goal.IsValid)
                front = IsoFloor.GroundCellOf(raid.Camp.Goal, map) - centerOnGround;
            foreach (Pawn pawn in candidates.OrderBy(p => p.Position.DistanceToSquared(rear)))
            {
                IntVec3 center = GenRadial.RadialCellsAround(pawn.Position, 3.9f, false)
                    .FirstOrDefault(cell => cell.InBounds(map) && IsoFloor.SameFloor(rear, cell, map) && cell.Standable(map) &&
                        BehindCamp(cell) && hold.Points.All(point => !point.Cell.InHorDistOf(cell, 5f)) &&
                        SmokeCombat.CanThrowNow(pawn.Position, cell, map, opening.ThrowRange), IntVec3.Invalid);
                if (!center.IsValid) continue;
                var point = new ArtilleryWaitingPoint { Smoke = new ArtilleryLocalSmoke(opening, center), Carrier = pawn };
                point.Smoke.Work.Targets.Add(new SmokeTarget { Cell = center, Stand = pawn.Position });
                if (point.Smoke.Work.FindSmokeWeapon(pawn) == null)
                    SmokeCombat.GiveSmoke(pawn, ArtillerySmokeDefOf.CE_Weapon_GrenadeSmoke, ArtillerySmoke.GrenadesPerCarrier);
                hold.Points.Add(point);
                if (hold.Points.Count == desired) break;
            }
            return hold;

            bool BehindCamp(IntVec3 cell)
            {
                IntVec3 offset = centerOnGround - IsoFloor.GroundCellOf(cell, map);
                return front.LengthHorizontalSquared == 0 || (long)offset.x * front.x + (long)offset.z * front.z > 0;
            }
        }

        internal ArtilleryWaitingPoint PointFor(Pawn pawn) =>
            Points.Find(point => point.Cell == pawn.mindState.duty.focus.Cell);

        internal ArtilleryLocalSmoke ScreenForCarrier(Pawn pawn) =>
            Points.Find(point => point.Smoke.Work.AssignmentFor(pawn) != null)?.Smoke;

        internal bool UsesMechFlank(Pawn pawn, out IntVec3 cell)
        {
            cell = IntVec3.Invalid;
            if (!SiegeRoles.IsHumanRaidEscortMech(pawn) || !HumanMechEscorts.CarriesVolatileMortar(pawn)) return false;
            if (pawn.Spawned && mechFlanks.TryGetValue(pawn, out IntVec3 flank) && MechScreen.CanAdvance(flank, pawn.Map))
                cell = flank;
            return true;
        }

        private void ReviewMechFlanks(ArtilleryRaid raid, Lord camp)
        {
            Pawn[] mechs = camp.ownedPawns.Where(pawn => ArtillerySmoke.Ready(pawn) &&
                SiegeRoles.IsHumanRaidEscortMech(pawn) && HumanMechEscorts.CarriesVolatileMortar(pawn)).ToArray();
            mechFlanks.Clear();
            if (mechs.Length == 0) return;

            Map map = camp.Map;
            IntVec3 center = raid.Camp.Center;
            IntVec3 approach = IsoFloor.GroundCellOf(raid.Camp.Goal, map) - IsoFloor.GroundCellOf(center, map);
            var occupied = CampSupplies.Reserved(map, raid.Camp);
            occupied.UnionWith(map.GetComponent<ArtilleryMap>().SupplyOwnership.GroundPositions(raid, map)
                .Where(cell => IsoFloor.SameFloor(center, cell, map) && cell.InHorDistOf(center, FlankSearchRadius + FlankClearance)));
            occupied.UnionWith(Points.Select(point => point.Cell));
            IntVec3[] positions = GenRadial.RadialCellsAround(center, FlankSearchRadius, false)
                .Where(cell => cell.InBounds(map) && IsoFloor.SameFloor(center, cell, map) &&
                    cell.Standable(map) && IsLateral(cell - center) &&
                    !occupied.Any(from => cell.InHorDistOf(from, FlankClearance))).ToArray();

            foreach (Pawn pawn in mechs)
            {
                IntVec3[] available = positions
                    .Where(cell => !mechFlanks.Values.Any(from => cell.InHorDistOf(from, FlankClearance)))
                    .OrderBy(cell => cell.DistanceToSquared(pawn.Position)).ToArray();
                IntVec3 smoked = available.FirstOrDefault(cell => ArtillerySmoke.Screened(cell, map) &&
                    pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly), IntVec3.Invalid);
                if (UseStation(smoked)) continue;
                IntVec3 sheltered = available.FirstOrDefault(cell => !ArtillerySmoke.Screened(cell, map) &&
                    MechScreen.CanAdvance(cell, map) && pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly), IntVec3.Invalid);
                UseStation(sheltered);

                bool UseStation(IntVec3 cell)
                {
                    if (!cell.IsValid || cell != pawn.Position &&
                        JobGiver_ArtillerySmoke.CoveredApproach(pawn, cell, MechScreen) != cell) return false;
                    mechFlanks.Add(pawn, cell);
                    return true;
                }
            }

            bool IsLateral(IntVec3 offset) => offset.LengthHorizontalSquared >= FlankClearance * FlankClearance &&
                Math.Abs(offset.x * approach.z - offset.z * approach.x) >=
                Math.Abs(offset.x * approach.x + offset.z * approach.z);
        }

        internal void ApplyDuties(ArtilleryRaid raid, LordToil_Siege toil)
        {
            Lord camp = toil.lord;
            var crew = ArtilleryCrew.Retained(raid, camp);
            foreach (Pawn pawn in camp.ownedPawns)
            {
                if (crew.Contains(pawn))
                {
                    if (raid.Camp.Guns.Any(gun => gun.Operator == pawn)) NativeSiege.AssignGunnerDuty(toil, pawn);
                    else NativeSiege.AssignBuilderDuty(toil, pawn);
                    toil.rememberedDuties[pawn] = DutyDefOf.Build;
                    continue;
                }
                if (SiegeRoles.IsHumanRaidEscortMech(pawn))
                {
                    IntVec3 escortFocus = UsesMechFlank(pawn, out IntVec3 cell)
                        ? (cell.IsValid ? cell : pawn.PositionHeld) : raid.Camp.Center;
                    LordToil_ArtilleryMechEscort.Assign(pawn, escortFocus);
                    toil.rememberedDuties[pawn] = ArtillerySmokeDefOf.RS_ArtilleryMechSmokeEscort;
                    continue;
                }
                ArtilleryWaitingPoint point = Points.Find(site => site.Carrier == pawn) ?? Points
                    .OrderBy(site => pawn.PositionHeld.DistanceToSquared(site.Cell)).FirstOrDefault();
                IntVec3 focus = point?.Cell ?? pawn.PositionHeld;
                DutyDef duty = ArtillerySmokeDefOf.RS_ArtilleryInfantryHold;
                pawn.mindState.duty = new PawnDuty(duty, focus) { wanderRadius = 3f };
                toil.rememberedDuties[pawn] = duty;
            }
        }

        internal void ReviewSmoke(ArtilleryRaid raid, Lord camp, int now)
        {
            Pawn[] waiting = camp.ownedPawns.Where(pawn => ArtillerySmoke.Ready(pawn) &&
                pawn.mindState.duty?.def == ArtillerySmokeDefOf.RS_ArtilleryInfantryHold &&
                pawn.TryGetComp<CombatExtended.CompInventory>() != null).ToArray();
            foreach (ArtilleryWaitingPoint point in Points.ToArray())
            {
                if (!waiting.Contains(point.Carrier) || PointFor(point.Carrier) != point)
                {
                    point.Carrier = waiting.Where(pawn => !Points.Any(site => site.Carrier == pawn) &&
                            (PointFor(pawn) == point || SmokeCombat.CanThrowNow(pawn.Position, point.Cell, camp.Map, point.Smoke.Work.ThrowRange)))
                        .OrderBy(pawn => pawn.Position.DistanceToSquared(point.Cell)).FirstOrDefault();
                    if (point.Carrier == null)
                    {
                        point.Smoke.Work.RestoreWeapons();
                        Points.Remove(point);
                        continue;
                    }
                    point.Carrier.mindState.duty.focus = point.Cell;
                }
                Pawn carrier = point.Carrier;
                point.Smoke.Review(raid, camp, new[] { carrier }, now,
                    cell => cell.AdjacentTo8WayOrInside(carrier.Position) || ArtillerySmoke.Screened(cell, camp.Map));
            }
            ReviewMechFlanks(raid, camp);
        }

        internal void Stop()
        {
            foreach (ArtilleryWaitingPoint point in Points) point.Smoke.Work.RestoreWeapons();
        }

        public void ExposeData()
        {
            Scribe_Deep.Look(ref MechScreen, "mechScreen");
            Scribe_Collections.Look(ref Points, "points", LookMode.Deep);
        }
    }

    internal sealed class ArtilleryWaitingPoint : IExposable
    {
        internal ArtilleryLocalSmoke Smoke;
        internal Pawn Carrier;
        internal IntVec3 Cell => Smoke.Work.Rally;

        public void ExposeData()
        {
            Scribe_Deep.Look(ref Smoke, "smoke");
            Scribe_References.Look(ref Carrier, "carrier");
        }
    }

    public sealed class JobGiver_ArtillerySmokeWander : JobGiver_WanderNearDutyLocation
    {
        public JobGiver_ArtillerySmokeWander()
        {
            wanderDestValidator = (pawn, cell, _) => ArtillerySmoke.Screened(cell, pawn.Map);
        }

        protected override Job TryGiveJob(Pawn pawn)
        {
            Map map = pawn.Map;
            if (!GenAdj.AdjacentCells.Any(offset => ArtillerySmoke.Screened(pawn.Position + offset, map)))
                return JobMaker.MakeJob(JobDefOf.Wait_Wander, 60, checkOverrideOnExpiry: true);
            Job job = base.TryGiveJob(pawn);
            if (job == null || job.def == JobDefOf.Wait_Wander)
                return JobMaker.MakeJob(JobDefOf.Wait_Wander, 60, checkOverrideOnExpiry: true);
            return job;
        }
    }
}
