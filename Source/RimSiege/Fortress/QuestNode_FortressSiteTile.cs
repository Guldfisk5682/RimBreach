using RimWorld.Planet;
using RimWorld.QuestGen;
using Verse;

namespace RimSiege.Fortress
{
    public sealed class QuestNode_FortressSiteTile : QuestNode
    {
        public SlateRef<string> storeAs;

        protected override bool TestRunInt(Slate slate) => FindTile(slate);

        protected override void RunInt()
        {
            Slate slate = QuestGen.slate;
            if (!slate.TryGet(storeAs.GetValue(slate), out PlanetTile _)) FindTile(slate);
        }

        private bool FindTile(Slate slate)
        {
            Map origin = slate.Get<Map>("map") ?? Find.RandomSurfacePlayerHomeMap;
            PlanetTile near = origin?.Tile ?? PlanetTile.Invalid;
            IntRange distance = slate.Get("siteDistRange", new IntRange(7, 27));
            if (!TrySite(Hilliness.SmallHills, out PlanetTile tile) &&
                !TrySite(Hilliness.LargeHills, out tile)) return false;

            slate.Set(storeAs.GetValue(slate), tile);
            return true;

            bool TrySite(Hilliness maximumHilliness, out PlanetTile site) =>
                TileFinder.TryFindNewSiteTile(out site, near, distance.min, distance.max, allowCaravans: true,
                    selectLandmarkChance: 0, validator: candidate =>
                    {
                        Tile terrain = Find.WorldGrid[candidate];
                        return !terrain.WaterCovered && terrain.swampiness < 0.5f &&
                            terrain.HillinessForElevationGen <= maximumHilliness;
                    });
        }
    }
}
