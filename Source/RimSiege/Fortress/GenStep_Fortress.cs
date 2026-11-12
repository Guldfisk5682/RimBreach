using System.Collections.Generic;
using System.Linq;
using ProjectZ;
using RimWorld;
using Verse;

namespace RimSiege.Fortress
{
    public sealed class GenStep_Fortress : GenStep
    {
        private const string CandidateKey = "RimSiege.FortressCandidates";

        public bool compact;

        public override int SeedPart => 1496780451;

        private FortressDimensions Dimensions(FortressSite site) => compact || site is { OriginalPoints: < 5000f }
            ? FortressDimensions.Compact : FortressDimensions.Large;

        public override void Generate(Map map, GenStepParams parms)
        {
            var site = map.Parent as FortressSite;
            if (site?.Blueprint != null) return;
            MapGenerator.SetVar(CandidateKey, (FortressSiting.GroundCandidates(map, Dimensions(site).OuterSide), Rand.Int));
        }

        public override void PostMapInitialized(Map map, GenStepParams parms)
        {
            var site = map.Parent as FortressSite;
            if (Api.Version < 8 || !Api.HasFloors(map))
            {
                Log.Warning("[RimSiege] Fortress generation requires ProjectZ API 8 and an upper floor on this map.");
                site?.GenerationFailed();
                return;
            }

            if (site?.Blueprint is FortressBlueprint blueprint)
            {
                Build(map, parms, site, blueprint.Rebuild(map), blueprint);
                return;
            }

            if (!MapGenerator.TryGetVar(CandidateKey, out (List<CellRect> Candidates, int Seed) draft)) return;
            Rand.PushState(draft.Seed);
            try
            {
                FortressDimensions dimensions = Dimensions(site);
                FortressLayout layout = FindLayout(map, draft.Candidates, dimensions);
                if (layout == null && dimensions == FortressDimensions.Large)
                    layout = FindLayout(map, FortressSiting.GroundCandidates(map, FortressDimensions.Compact.OuterSide),
                        FortressDimensions.Compact);
                FactionMaterial material = FactionMaterial.For(map);
                if (layout == null || material.Slab == null)
                {
                    Log.Warning($"[RimSiege] No compatible fortress layout or floor material on tile {map.Tile}.");
                    site?.GenerationFailed();
                    return;
                }

                Build(map, parms, site, layout, new FortressBlueprint
                {
                    Outer = layout.Outer,
                    GateSide = layout.GateSide,
                    FourGates = layout.FourGates,
                    Top = layout.Tiers.Length - 1,
                    NaturalBarrier = layout.NaturalBarrier.ToList(),
                    Stuff = material.Stuff,
                    Slab = material.Slab,
                    Facilities = layout.Facilities,
                    FurnishingSeed = Rand.Int
                });
            }
            finally { Rand.PopState(); }
        }

        private static void Build(Map map, GenStepParams parms, FortressSite site, FortressLayout layout,
            FortressBlueprint blueprint)
        {
            bool large = blueprint.Outer.Width == FortressDimensions.Large.OuterSide;
            bool reinforced = site != null && site.OriginalPoints >= (large ? 7000f : 5000f);
            Rand.PushState(blueprint.FurnishingSeed);
            try
            {
                if (layout == null || !FortressConstruction.Build(map, layout,
                        new FactionMaterial(blueprint.Stuff, blueprint.Slab), reinforced))
                {
                    site?.GenerationFailed();
                    return;
                }

                if (site != null)
                {
                    if (blueprint.Stock == null) blueprint.Stock = FortressStock.Capture(map, layout.Facilities);
                    else FortressStock.Restore(map, layout.Facilities, blueprint.Stock);
                }

                blueprint.Defenses ??= FortressDefensePlanner.Plan(map, layout, site?.OriginalPoints ?? 5000f);
                List<ArmedEmplacement> emplacements = FortressArmament.Spawn(map, layout, blueprint.Defenses);
                var ammunition = new FortressAmmunition(map, layout, blueprint.Stuff);
                ammunition.StockEmplacements(emplacements);
                if (!layout.Facilities.Power.IsEmpty) FortressPower.Connect(map, layout);

                if (site == null) return;
                site.Blueprint = blueprint;
                ammunition.StockInfantry(FortressGarrison.Spawn(map, layout, parms.sitePart), emplacements);
                MapGenUtility.ForbidAllItems(map);
            }
            finally { Rand.PopState(); }
        }

        private static FortressLayout FindLayout(Map map, List<CellRect> candidates, FortressDimensions dimensions)
        {
            foreach (CellRect outer in candidates)
                if (FortressSiting.TryPlan(map, outer, dimensions, out FortressLayout layout)) return layout;
            return null;
        }
    }
}
