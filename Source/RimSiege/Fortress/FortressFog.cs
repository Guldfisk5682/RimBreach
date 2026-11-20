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
        private FortressGeometry geometry;
        private HashSet<IntVec3> innerLip;
        private HashSet<IntVec3> concealedCells;

        public FortressFog(Map map) : base(map) { }

        internal static bool TryGet(FogGrid grid, out FortressFog fog) => ConcealedMaps.TryGetValue(grid, out fog);

        internal bool Conceals(IntVec3 cell) => concealedCells != null && concealedCells.Contains(cell);

        public override void ExposeData() => Scribe_Values.Look(ref awaitingBreach, "awaitingBreach");

        public override void FinalizeInit()
        {
            if (awaitingBreach && !TryGet(map.fogGrid, out _) &&
                map.Parent is FortressSite { Blueprint: { } blueprint } &&
                blueprint.Rebuild(map) is { } layout)
                Track(layout.Geometry, blueprint.Top);
        }

        internal void Conceal(FortressLayout layout)
        {
            FortressGeometry geo = layout.Geometry;
            int top = layout.Tiers.Length - 1;
            Track(geo, top);

            foreach (IntVec3 cell in geo.Interior)
                map.fogGrid.Refog(CellRect.SingleCell(cell));
            for (int floor = 1; floor <= top; floor++)
            {
                foreach (IntVec3 cell in geo.Interior)
                {
                    if (floor == 1 && geo.RampartDeckGround.Contains(cell)) continue;
                    if (Api.HasFloorAt(cell, floor, map))
                        map.fogGrid.Refog(CellRect.SingleCell(Api.CellOnFloor(cell, floor, map)));
                }
            }

            // The outer wall walk and bastion platforms need not be reachable from the arrival point.
            foreach (IntVec3 cell in layout.Tiers[1].Deck)
                if (!Conceals(cell) && !layout.Tiers[1].Openings.Contains(cell) && cell.Fogged(map))
                    map.fogGrid.Unfog(cell);
        }

        private void Track(FortressGeometry geo, int top)
        {
            geometry = geo;
            innerLip = new HashSet<IntVec3>(geo.Interior);
            foreach (IntVec3 cell in geo.Interior)
            {
                foreach (IntVec3 dir in GenAdj.CardinalDirections)
                {
                    IntVec3 neighbor = cell + dir;
                    if (geo.Footprint.Contains(neighbor)) innerLip.Add(neighbor);
                }
            }

            concealedCells = new HashSet<IntVec3>();
            foreach (IntVec3 cell in geo.Interior)
            {
                concealedCells.Add(cell);
                for (int floor = 1; floor <= top; floor++)
                {
                    if (floor == 1 && geo.RampartDeckGround.Contains(cell)) continue;
                    IntVec3 upper = Api.CellOnFloor(cell, floor, map);
                    if (upper.IsValid) concealedCells.Add(upper);
                }
            }

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
            if (geometry == null) return;
            IntVec3 ground = Api.GroundCellOf(position, map);
            if (!geometry.Footprint.Contains(ground)) return;
            // The last cell of a ground passage is visible and can be ordered to.
            if (!innerLip.Contains(ground) &&
                (Api.FloorOf(position, map) != 1 || !geometry.RampartDeckGround.Contains(ground) ||
                    !Api.HasFloorAt(ground, 1, map))) return;

            ConcealedMaps.Remove(map.fogGrid);
            awaitingBreach = false;

            FortressLayout rebuilt = blueprint.Rebuild(map);
            var roofs = new List<CellRect> { geometry.Core };
            if (geometry.Kind == FortressLayoutKind.SquareKeep)
                roofs.AddRange(geometry.Zones.Select(zone => zone.Ground));

            var rooms = new List<CellRect>(rebuilt != null
                ? rebuilt.Modules.Select(module => module.Ground)
                : roofs.Concat(geometry.Zones.Select(zone => zone.Ground)));
            if (rebuilt == null)
            {
                rooms.Add(blueprint.Facilities.Living.Rect);
                rooms.Add(blueprint.Facilities.Supply.Rect);
                if (!blueprint.Facilities.Power.IsEmpty) rooms.Add(blueprint.Facilities.Power.Rect);
                rooms.AddRange(blueprint.Facilities.ExtraLots().Select(lot => lot.Rect));
            }
            CellRect[] interiors = rooms.Select(room => room.ContractedBy(1)).ToArray();
            var vaultInteriors = new HashSet<IntVec3>();
            foreach (FortressBastionZone zone in geometry.Zones)
                if (zone.VaultCells != null) vaultInteriors.UnionWith(zone.VaultCells);

            foreach (IntVec3 cell in geometry.Interior)
                if (!vaultInteriors.Contains(cell) && !interiors.Any(room => room.Contains(cell))) Reveal(cell);
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
