using System.Collections.Generic;
using System.Linq;
using ProjectZ;
using Verse;

namespace RimSiege.Fortress
{
    // Rectangle sums let the fallback check every center without rescanning its footprint.
    internal sealed class FortressGroundSurvey
    {
        private readonly CellRect ground;
        private readonly int[,] fixedObstacles;
        private readonly int[,] naturalBarriers;

        internal FortressGroundSurvey(Map map)
        {
            ground = Api.GroundRect(map);
            fixedObstacles = new int[ground.Width + 1, ground.Height + 1];
            naturalBarriers = new int[ground.Width + 1, ground.Height + 1];
            for (int z = 1; z <= ground.Height; z++)
                for (int x = 1; x <= ground.Width; x++)
                {
                    IntVec3 cell = new(ground.minX + x - 1, 0, ground.minZ + z - 1);
                    bool natural = FortressSiting.IsNaturalBarrier(cell, map);
                    Add(naturalBarriers, x, z, natural);
                    Add(fixedObstacles, x, z, !natural && FortressSiting.BlocksGround(cell, map));
                }
        }

        internal IEnumerable<CellRect> AdditionalCandidates(int side)
        {
            CellRect usable = ground.ContractedBy(FortressSiting.EdgeMargin);
            IntVec3 center = ground.CenterCell;
            var candidates = new List<CellRect>();
            for (int z = usable.minZ; z <= usable.maxZ - side + 1; z++)
                for (int x = usable.minX; x <= usable.maxX - side + 1; x++)
                {
                    CellRect rect = new(x, z, side, side);
                    if (!FortressSiting.IsCoarseCenter(rect.CenterCell - center) &&
                        Count(fixedObstacles, rect) == 0 &&
                        Count(naturalBarriers, rect.ContractedBy(FortressSiting.PreservedSkin)) == 0)
                        candidates.Add(rect);
                }
            return candidates.OrderBy(rect => (rect.CenterCell - center).LengthHorizontalSquared);
        }

        private static void Add(int[,] sums, int x, int z, bool occupied) =>
            sums[x, z] = sums[x - 1, z] + sums[x, z - 1] - sums[x - 1, z - 1] + (occupied ? 1 : 0);

        private int Count(int[,] sums, CellRect rect)
        {
            int x0 = rect.minX - ground.minX, z0 = rect.minZ - ground.minZ;
            int x1 = rect.maxX - ground.minX + 1, z1 = rect.maxZ - ground.minZ + 1;
            return sums[x1, z1] - sums[x0, z1] - sums[x1, z0] + sums[x0, z0];
        }
    }
}
