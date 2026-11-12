using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using ProjectZ;
using RimWorld;
using Verse;

namespace RimSiege.Fortress
{
    public sealed class FortressFog : MapComponent
    {
        private static readonly ConditionalWeakTable<FogGrid, FortressFog> ConcealedMaps = new();
        private bool awaitingBreach;
        private CellRect courtyard;
        private CellRect innerLip;
        private CellRect[] concealedBands;

        public FortressFog(Map map) : base(map) { }

        internal static bool TryGet(FogGrid grid, out FortressFog fog) => ConcealedMaps.TryGetValue(grid, out fog);

        internal bool Conceals(IntVec3 cell)
        {
            foreach (CellRect band in concealedBands)
                if (band.Contains(cell)) return true;
            return false;
        }

        public override void ExposeData() => Scribe_Values.Look(ref awaitingBreach, "awaitingBreach");

        public override void FinalizeInit()
        {
            if (awaitingBreach && !TryGet(map.fogGrid, out _) &&
                map.Parent is FortressSite { Blueprint: { } blueprint })
                Track(blueprint.Outer, blueprint.Top);
        }

        internal void Conceal(FortressLayout layout)
        {
            Track(layout.Outer, layout.Tiers.Length - 1);
            map.fogGrid.Refog(courtyard);
            for (int floor = 1; floor < concealedBands.Length; floor++)
                foreach (IntVec3 cell in courtyard)
                    if (Api.HasFloorAt(cell, floor, map))
                        map.fogGrid.Refog(CellRect.SingleCell(Api.CellOnFloor(cell, floor, map)));

            // The wall walk need not be reachable from the arrival point.
            foreach (IntVec3 cell in layout.Tiers[1].Deck)
                if (!Conceals(cell) && !layout.Tiers[1].Openings.Contains(cell) && cell.Fogged(map))
                    map.fogGrid.Unfog(cell);
        }

        private void Track(CellRect outer, int top)
        {
            courtyard = outer.ContractedBy(FortressLayout.CurtainThickness);
            innerLip = courtyard.ExpandedBy(1);
            concealedBands = new CellRect[top + 1];
            for (int floor = 0; floor <= top; floor++)
                concealedBands[floor] = CellRect.FromLimits(Api.CellOnFloor(courtyard.Min, floor, map),
                    Api.CellOnFloor(courtyard.Max, floor, map));
            awaitingBreach = true;
            ConcealedMaps.Add(map.fogGrid, this);
        }

        internal static void PawnArrived(Pawn pawn)
        {
            if (!pawn.Spawned || pawn.Faction != Faction.OfPlayer && !pawn.IsPlayerControlled ||
                !TryGet(pawn.Map.fogGrid, out FortressFog fog)) return;
            fog.CheckBreach(pawn.Position, ((FortressSite)fog.map.Parent).Blueprint);
        }

        private void CheckBreach(IntVec3 position, FortressBlueprint blueprint)
        {
            IntVec3 ground = Api.GroundCellOf(position, map);
            if (!blueprint.Outer.Contains(ground)) return;
            // The last cell of a ground passage is visible and can be ordered to.
            if (!innerLip.Contains(ground) &&
                (Api.FloorOf(position, map) != 1 || !Api.HasFloorAt(ground, 1, map))) return;

            ConcealedMaps.Remove(map.fogGrid);
            awaitingBreach = false;

            FortressDimensions dimensions = blueprint.Outer.Width == FortressDimensions.Large.OuterSide
                ? FortressDimensions.Large : FortressDimensions.Compact;
            var roofs = FortressLayout.TowerFootprints(blueprint.Outer, dimensions.TowerSide)
                .Append(FortressLayout.KeepFootprint(blueprint.Outer, dimensions.KeepSide)).ToArray();
            var rooms = new List<CellRect>(roofs);
            rooms.Add(blueprint.Facilities.Living.Rect);
            rooms.Add(blueprint.Facilities.Supply.Rect);
            if (!blueprint.Facilities.Power.IsEmpty) rooms.Add(blueprint.Facilities.Power.Rect);
            CellRect[] interiors = rooms.Select(room => room.ContractedBy(1)).ToArray();

            foreach (IntVec3 cell in courtyard)
                if (!interiors.Any(room => room.Contains(cell))) Reveal(cell);
            foreach (CellRect roof in roofs)
                foreach (IntVec3 cell in roof)
                    if (Api.HasFloorAt(cell, blueprint.Top, map))
                        Reveal(Api.CellOnFloor(cell, blueprint.Top, map));

            // ReachFog can have cached regions it walked while the courtyard was sealed.
            map.events.Notify_MapFogged();
            Api.RequestFogRefresh(map);
        }

        private void Reveal(IntVec3 cell)
        {
            if (cell.Fogged(map)) map.fogGrid.Unfog(cell);
        }
    }
}
