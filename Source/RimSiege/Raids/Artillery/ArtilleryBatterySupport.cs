using System.Collections.Generic;
using System.Linq;
using RimSiege.Integration;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Raids.Artillery
{
    internal sealed class ArtilleryBatterySupport : IExposable
    {
        internal List<Pawn> Helpers = new();
        internal ArtilleryLocalSmoke Screen;

        public ArtilleryBatterySupport() { }

        internal static ArtilleryBatterySupport Start(ArtilleryRaid raid, LordToil_Siege toil, ArtillerySmoke opening)
        {
            var support = new ArtilleryBatterySupport { Screen = new(opening, raid.Camp.Center) };
            Map map = toil.lord.Map;
            var reserved = CampSupplies.Reserved(map, raid.Camp);
            // Keep the fixed duties and stations in the completed camp layout.
            foreach (SmokeTarget target in opening.Targets.Where(t => t.Required))
            {
                if (!target.Cell.InHorDistOf(raid.Camp.Center, 10f) || !target.Stand.InHorDistOf(raid.Camp.Center, 12f) ||
                    reserved.Contains(target.Cell) || !target.Stand.Standable(map)) continue;
                support.Screen.Work.Targets.Add(new SmokeTarget { Cell = target.Cell, Stand = target.Stand, Required = true });
            }
            if (support.Screen.Work.Targets.Count == 0)
                support.Screen.Work.AddLocalTarget(map, raid.Camp.Center, cell => !reserved.Contains(cell));
            return support;
        }

        internal bool Review(ArtilleryRaid raid, LordToil_Siege toil, int now)
        {
            Lord camp = toil.lord;
            if (!raid.Camp.Guns.Any(g => g.IsSupport && (ArtilleryCrew.Pending(g) ||
                g.Building is { Spawned: true } building && building.Faction == raid.Faction)))
            {
                Screen.Work.RestoreWeapons();
                raid.BatterySupport = null;
                return Helpers.Count != 0;
            }

            var operators = raid.Camp.Guns.Where(g => g.Operator != null).Select(g => g.Operator).ToHashSet();
            bool Eligible(Pawn pawn) => ArtillerySmoke.Ready(pawn) && !operators.Contains(pawn) &&
                LadderInventory.Get(pawn) == null && ArtilleryCrew.CanBuild(toil, pawn);

            int previous = Helpers.Count;
            int removed = Helpers.RemoveAll(p => p == null || p.GetLord() != camp || !Eligible(p));
            int slots = System.Math.Max(1, (raid.InitialRaidSize + 19) / 20) - Helpers.Count;
            if (slots > 0)
            {
                var specialists = ArtilleryDeployment.Specialists(raid.Strategy, camp.ownedPawns);
                Helpers.AddRange(camp.ownedPawns.Where(p => Eligible(p) && !specialists.Contains(p) && !Helpers.Contains(p))
                    .OrderBy(p => Screen.Work.FindSmokeWeapon(p) == null)
                    .ThenBy(p => IsoFloor.Distance3D(p.Position, raid.Camp.Center, camp.Map)).Take(slots));
            }
            // A single smoke assignment per battery leaves the other helpers on native work.
            Screen.Review(raid, camp, Helpers.Take(1).ToArray(), now);
            return removed != 0 || Helpers.Count != previous;
        }

        public void ExposeData()
        {
            Scribe_Collections.Look(ref Helpers, "helpers", LookMode.Reference);
            Helpers ??= new();
            Helpers.RemoveAll(p => p == null);
            Scribe_Deep.Look(ref Screen, "screen");
        }
    }
}
