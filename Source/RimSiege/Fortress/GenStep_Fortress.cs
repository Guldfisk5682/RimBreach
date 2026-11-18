using System.Collections.Generic;
using System.Linq;
using ProjectZ;
using RimWorld;
using Verse;

namespace RimSiege.Fortress
{
    public sealed class GenStep_Fortress : GenStep
    {
        private const string LayoutSeedKey = "RimSiege.FortressLayoutSeed";
        private const int FallbackLayoutBudget = 64;

        public bool compact;

        public override int SeedPart => 1496780451;

        private FortressDimensions Dimensions(FortressSite site) => compact || site is { OriginalPoints: < 5000f }
            ? FortressDimensions.Compact : FortressDimensions.Large;

        public override void Generate(Map map, GenStepParams parms)
        {
            var site = map.Parent as FortressSite;
            if (site?.Blueprint != null) return;
            MapGenerator.SetVar(LayoutSeedKey, Rand.Int);
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

            if (!MapGenerator.TryGetVar(LayoutSeedKey, out int seed)) return;
            Rand.PushState(seed);
            try
            {
                FactionMaterial material = FactionMaterial.For(map);
                if (material.Slab == null)
                {
                    Log.Warning($"[RimSiege] No compatible fortress floor material on tile {map.Tile}.");
                    site?.GenerationFailed();
                    return;
                }

                FortressLayout layout = FindLayout(map, Dimensions(site), seed);
                if (layout == null)
                {
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

                IntVec3 lootShelf = FortressStronghold.Furnish(map, layout, blueprint.Stuff, site?.Harassment == true);
                if (site != null)
                {
                    blueprint.LootShelfGround = lootShelf.IsValid ? Api.GroundCellOf(lootShelf, map) : IntVec3.Invalid;
                    blueprint.LootShelfFloor = lootShelf.IsValid ? Api.FloorOf(lootShelf, map) : 0;
                    if (blueprint.Stock == null) blueprint.Stock = FortressStock.Capture(map, layout.Facilities);
                    else FortressStock.Restore(map, layout.Facilities, blueprint.Stock);
                }

                blueprint.Defenses ??= FortressDefensePlanner.Plan(map, layout, site?.OriginalPoints ?? 5000f);
                List<ArmedEmplacement> emplacements = FortressArmament.Spawn(map, layout, blueprint.Defenses);
                var ammunition = new FortressAmmunition(map, layout, blueprint.Stuff, lootShelf);
                ammunition.StockEmplacements(emplacements);
                if (!layout.Facilities.Power.IsEmpty) FortressPower.Connect(map, layout);

                if (site == null) return;
                site.Blueprint = blueprint;
                ammunition.StockInfantry(FortressGarrison.Spawn(map, layout, parms.sitePart), emplacements);
                FortressLoot.PlaceHarassmentLoot(map, site, lootShelf);
                MapGenUtility.ForbidAllItems(map);
                if (RimSiegeMod.Settings.FortressFog)
                    map.GetComponent<FortressFog>().Conceal(layout);
            }
            finally { Rand.PopState(); }
        }

        private static FortressLayout FindLayout(Map map, FortressDimensions dimensions, int seed)
        {
            var attempts = new List<string>();
            FortressDimensions[] sizes = dimensions == FortressDimensions.Large
                ? new[] { dimensions, FortressDimensions.Compact } : new[] { dimensions };
            foreach (FortressDimensions size in sizes)
                if (Search(FortressSiting.GroundCandidates(map, size.OuterSide), size) is { } layout)
                    return layout;

            var survey = new FortressGroundSurvey(map);
            foreach (FortressDimensions size in sizes)
                if (Search(survey.AdditionalCandidates(size.OuterSide), size, fallback: true) is { } layout)
                {
                    Log.Message($"[RimSiege] Fortress fallback found a {size.OuterSide}-cell layout on tile " +
                        $"{map.Tile}, layout seed {seed}: {layout.Outer}. {string.Join("; ", attempts)}");
                    return layout;
                }

            Log.Warning($"[RimSiege] No compatible fortress layout on tile {map.Tile}, layout seed {seed}. " +
                string.Join("; ", attempts));
            return null;

            FortressLayout Search(IEnumerable<CellRect> candidates, FortressDimensions size, bool fallback = false)
            {
                var failures = new int[(int)FortressSitingResult.ExteriorRoute + 1];
                int testedLayouts = 0;
                foreach (CellRect outer in candidates)
                {
                    FortressSitingResult result = FortressSiting.Plan(map, outer, size, out FortressLayout layout);
                    if (result == FortressSitingResult.Accepted)
                    {
                        if (fallback) Record();
                        return layout;
                    }
                    failures[(int)result]++;
                    if (fallback && result >= FortressSitingResult.Layout && ++testedLayouts == FallbackLayoutBudget)
                        break;
                }
                Record();
                return null;

                void Record()
                {
                    string summary = string.Join(", ", failures.Select((count, index) =>
                        count == 0 ? null : $"{(FortressSitingResult)index}={count}").Where(entry => entry != null));
                    attempts.Add($"{size.OuterSide} {(fallback ? "fallback" : "coarse")} [{summary}]" +
                        (testedLayouts == FallbackLayoutBudget ? " layout budget exhausted" : ""));
                }
            }
        }
    }
}
