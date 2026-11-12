using System;
using System.Collections.Generic;
using System.Linq;
using ProjectZ;
using Verse;

namespace RimSiege.Fortress
{
    internal enum FortressLine { Curtain, Courtyard, Keep }

    internal sealed class FortressDefenseGround
    {
        private readonly Map map;
        private readonly FortressBlueprint blueprint;
        private readonly CellRect keep;
        private readonly CellRect[] towers;
        private readonly CellRect courtyard;
        private readonly Dictionary<(CellRect Rect, int Floor), IntVec3> anchors = new();

        internal FortressDefenseGround(Map map, FortressBlueprint blueprint)
        {
            this.map = map;
            this.blueprint = blueprint;
            var dimensions = blueprint.Outer.Width == FortressDimensions.Large.OuterSide
                ? FortressDimensions.Large : FortressDimensions.Compact;
            keep = CellRect.CenteredOn(blueprint.Outer.CenterCell, dimensions.KeepSide, dimensions.KeepSide);
            courtyard = blueprint.Outer.ContractedBy(FortressLayout.CurtainThickness);
            towers = FortressLayout.TowerFootprints(blueprint.Outer, dimensions.TowerSide).ToArray();
        }

        internal IntVec3 RestPost => Anchor(blueprint.Facilities.Living.Rect, 0);
        internal IntVec3 MedicalPost => Anchor(blueprint.Facilities.Supply.Rect, 1);
        internal bool Inside(IntVec3 cell) => courtyard.Contains(Api.GroundCellOf(cell, map));
        internal bool InDefenseArea(IntVec3 cell) => Inside(cell) || OnCurtain(cell);
        internal bool InKeep(IntVec3 cell) => keep.Contains(Api.GroundCellOf(cell, map));
        internal bool Outside(IntVec3 cell) => !blueprint.Outer.Contains(Api.GroundCellOf(cell, map));
        internal bool OnCurtain(IntVec3 cell) => Api.FloorOf(cell, map) > 0 && IsCurtain(Api.GroundCellOf(cell, map));
        internal bool IsCurtain(IntVec3 ground) => blueprint.Outer.Contains(ground) && !courtyard.Contains(ground);
        internal bool TowerMiddle(IntVec3 cell)
        {
            if (blueprint.Top != 2 || Api.FloorOf(cell, map) != 1) return false;
            IntVec3 g = Api.GroundCellOf(cell, map);
            if (keep.Contains(g)) return true;
            foreach (CellRect tower in towers)
                if (tower.Contains(g)) return true;
            return false;
        }

        internal bool TowerFaces(IntVec3 cell, int sector)
        {
            IntVec3 g = Api.GroundCellOf(cell, map);
            foreach (CellRect tower in towers)
                if (tower.Contains(g))
                    return Projection(tower.CenterCell - blueprint.Outer.CenterCell, new Rot4(sector).FacingCell) > 0;
            return false;
        }

        internal int Sector(IntVec3 cell)
        {
            IntVec3 delta = Api.GroundCellOf(cell, map) - blueprint.Outer.CenterCell;
            return Math.Abs(delta.x) > Math.Abs(delta.z) ? (delta.x >= 0 ? 1 : 3) : (delta.z >= 0 ? 0 : 2);
        }

        internal IntVec3 CurtainPost(int sector)
        {
            Rot4 side = new(sector);
            IntVec3 center = blueprint.Outer.GetCenterCellOnEdge(side) -
                side.FacingCell * (FortressLayout.CurtainThickness - 1);
            return Anchor(CellRect.CenteredOn(center, 5, 5), 1, IsCurtain);
        }

        internal IntVec3 Post(FortressLine line, int sector, int index)
        {
            Rot4 side = new(sector);
            if (line == FortressLine.Keep)
                return Anchor(keep, blueprint.Top == 2 && index % 2 == 0 ? 1 : blueprint.Top);
            CellRect tower = towers.OrderByDescending(rect => Projection(rect.CenterCell, side.FacingCell))
                .ElementAt(index / 2 % 2);
            if (line == FortressLine.Courtyard)
            {
                if (blueprint.Top == 2 && index % 2 == 0)
                {
                    CellRect interior = tower.ContractedBy(1);
                    IntVec3 firingSide = interior.GetCenterCellOnEdge(side.Opposite);
                    IntVec3 post = Anchor(CellRect.CenteredOn(firingSide, 3, 3).ClipInsideRect(interior), 1);
                    if (post.IsValid) return post;
                }
                IntVec3 inner = courtyard.GetCenterCellOnEdge(side) - side.FacingCell * 2;
                return ((index + sector) % 3) switch
                {
                    0 => Anchor(tower, blueprint.Top),
                    1 => Anchor(keep, blueprint.Top),
                    _ => Anchor(CellRect.CenteredOn(inner, 5, 5), 0, courtyard.Contains)
                };
            }
            if (index % 2 == 1) return Anchor(tower, blueprint.Top);
            return CurtainPost(sector);
        }

        internal bool PassableBreach(int sector)
        {
            Rot4 side = new(sector);
            IntVec3 direction = side.FacingCell;
            IntVec3 edge = blueprint.Outer.GetCenterCellOnEdge(side);
            int width = blueprint.Outer.Width / 2;
            IntVec3 across = side.Rotated(RotationDirection.Clockwise).FacingCell;
            var visited = new HashSet<IntVec3>();
            var queue = new Queue<IntVec3>();
            for (int offset = -width; offset <= width; offset++)
            {
                IntVec3 start = edge + across * offset;
                if ((start + direction).Walkable(map)) Add(start);
            }
            while (queue.TryDequeue(out IntVec3 cell))
            {
                if (courtyard.Contains(cell)) return true;
                foreach (IntVec3 neighbor in GenAdj.CardinalDirections) Add(cell + neighbor);
            }
            return false;

            void Add(IntVec3 cell)
            {
                IntVec3 delta = cell - edge;
                int depth = -Projection(delta, direction);
                if (depth < 0 || depth > FortressLayout.CurtainThickness || Math.Abs(Projection(delta, across)) > width ||
                    !visited.Add(cell) || !cell.Walkable(map) || cell.GetDoor(map) != null) return;
                queue.Enqueue(cell);
            }
        }

        private IntVec3 Anchor(CellRect rect, int floor, Func<IntVec3, bool> allowed = null)
        {
            if (anchors.TryGetValue((rect, floor), out IntVec3 cached) && cached.IsValid && cached.Standable(map))
                return cached;
            IntVec3 center = rect.CenterCell;
            return anchors[(rect, floor)] = rect.Where(cell => allowed == null || allowed(cell))
                .OrderBy(cell => (cell - center).LengthHorizontalSquared)
                .Select(cell => Api.CellOnFloor(cell, floor, map))
                .FirstOrDefault(cell => cell.IsValid && cell.Standable(map), IntVec3.Invalid);
        }

        private static int Projection(IntVec3 cell, IntVec3 direction) => cell.x * direction.x + cell.z * direction.z;
    }
}
