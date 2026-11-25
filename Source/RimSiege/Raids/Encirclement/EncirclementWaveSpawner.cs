using System.Collections.Generic;
using System.Linq;
using RimSiege.Integration;
using RimSiege.Raids.Artillery;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimSiege.Raids.Encirclement
{
    internal sealed class EncirclementWave : IExposable
    {
        internal float Points;
        internal int Count;
        internal List<Pawn> Pawns = new();
        internal HashSet<int> Losses = new();

        public EncirclementWave() { }

        internal void RecordLosses(QuestPart_Encirclement siege)
        {
            foreach (Pawn pawn in Pawns)
                if (pawn != null && !siege.InBattle(pawn)) Losses.Add(pawn.thingIDNumber);
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref Points, "points");
            Scribe_Values.Look(ref Count, "count");
            Scribe_Collections.Look(ref Pawns, "pawns", LookMode.Reference);
            Scribe_Collections.Look(ref Losses, "losses", LookMode.Value);
        }
    }

    internal static class EncirclementWaveSpawner
    {
        internal static float PersonnelFactor(float basePoints)
        {
            if (DefDatabase<ArtilleryProfileDef>.GetNamedSilentFail("RS_ArtilleryMechanoid")?.personnelCurve is { } curve)
                return curve.Evaluate(basePoints);
            return basePoints >= 5000f ? 0.65f : 0.80f;
        }

        internal static bool UsesHumanMechEscorts(QuestPart_Encirclement siege) => !siege.Mechanoid &&
            ModsConfig.BiotechActive && RimSiegeMod.Settings?.HumanMechEscorts == true &&
            siege.BasePoints >= RimSiegeMod.Settings.HumanMechMinPoints;

        internal static float PointsFor(QuestPart_Encirclement siege, int wave)
        {
            float points = siege.BasePoints * 0.5f;
            bool reallocates = siege.Plan is null or { AssaultFallback: false, ForwardBudget: >= 0f };
            if (wave is 1 or 2 && reallocates) return points * PersonnelFactor(siege.BasePoints);
            return points;
        }

        internal static int PlannedHumanCount(QuestPart_Encirclement siege)
        {
            if (siege.Mechanoid) return 0;
            return Enumerable.Range(1, 3).Sum(number =>
            {
                float wavePoints = PointsFor(siege, number);
                if (number == 3 && UsesHumanMechEscorts(siege))
                    wavePoints *= 1f - HumanMechEscorts.BudgetFraction(siege.Faction.def.techLevel);
                var parms = FighterParameters(siege, number, wavePoints);
                foreach (PawnGroupMaker group in FighterGroups(parms))
                {
                    var kinds = group.GeneratePawnKindsExample(parms).ToList();
                    if (kinds.Count != 0) return kinds.Count(kind => kind.RaceProps.Humanlike);
                }
                return 0;
            });
        }

        // The same seed selects the roster for housing and for the actual arrivals.
        private static PawnGroupMakerParms FighterParameters(QuestPart_Encirclement siege, int number, float points) => new()
        {
            groupKind = PawnGroupKindDefOf.Combat, faction = siege.Faction, tile = siege.Map.Tile,
            points = points, raidStrategy = EncirclementDefs.Breaching,
            seed = siege.PersonnelSeed == 0 ? null : Gen.HashCombineInt(siege.PersonnelSeed, number)
        };

        private static IEnumerable<PawnGroupMaker> FighterGroups(PawnGroupMakerParms parms)
        {
            if (PawnGroupMakerUtility.TryGetRandomPawnGroupMaker(parms, out PawnGroupMaker breach)) yield return breach;
            parms.raidStrategy = RaidStrategyDefOf.ImmediateAttack;
            if (PawnGroupMakerUtility.TryGetRandomPawnGroupMaker(parms, out PawnGroupMaker assault)) yield return assault;
        }

        internal static EncirclementWave Spawn(QuestPart_Encirclement siege, int number)
        {
            Map map = siege.Map;
            var wave = new EncirclementWave { Points = PointsFor(siege, number) };
            var ground = IsoFloor.GroundRect(map);
            var entries = siege.Plan.Entries.Where(ValidEntry).ToList();
            if (entries.Count == 0)
            {
                IntVec3 alternate = ground.EdgeCells.FirstOrFallback(ValidEntry, IntVec3.Invalid);
                if (!alternate.IsValid)
                {
                    alternate = ground.EdgeCells.FirstOrFallback(c => c.Standable(map) && !c.Roofed(map), IntVec3.Invalid);
                    if (!alternate.IsValid) return wave;
                }
                entries.Add(alternate);
            }

            float points = wave.Points;
            var pawns = new List<Pawn>();
            if (siege.Mechanoid && number == 1 && (!siege.Plan.AssaultFallback ||
                siege.Battery(0)?.Camp.Guns.Count > 0))
            {
                PawnKindDef builder = DefDatabase<PawnKindDef>.GetNamed("Mech_Constructoid");
                int scaledCount = Mathf.FloorToInt(siege.BasePoints / 1000f);
                int affordableCount = Mathf.FloorToInt(points / Mathf.Max(1f, builder.combatPower) * 0.3f);
                int count = Mathf.Max(10, Mathf.Min(scaledCount, affordableCount));
                for (int i = 0; i < count; i++)
                {
                    Pawn pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(builder, siege.Faction,
                        PawnGenerationContext.NonPlayer, map.Tile, forceGenerateNewPawn: true,
                        canGeneratePawnRelations: false, allowFood: false, allowAddictions: false));
                    if (pawn.needs?.energy != null) pawn.needs.energy.CurLevel = pawn.needs.energy.MaxLevel;
                    pawns.Add(pawn);
                }
                points -= count * builder.combatPower;
            }
            if (number == 3 && UsesHumanMechEscorts(siege))
            {
                TechLevel tech = siege.Faction.def.techLevel;
                float escortBudget = points * HumanMechEscorts.BudgetFraction(tech);
                bool allowBoss = tech >= TechLevel.Ultra &&
                    siege.BasePoints >= HumanMechEscorts.BossMinimumPoints(RimSiegeMod.Settings.HumanMechMinPoints);
                List<PawnKindDef> escortKinds = HumanMechEscorts.Select(escortBudget, breaching: true, allowBoss,
                    out float spentEscorts);
                foreach (PawnKindDef kind in escortKinds)
                    pawns.Add(HumanMechEscorts.GenerateMech(kind, siege.Faction, map.Tile));
                points = Mathf.Max(0f, points - spentEscorts);
            }

            if (points > 0f)
            {
                var parms = FighterParameters(siege, number, points);
                foreach (PawnGroupMaker group in FighterGroups(parms))
                {
                    var fighters = group.GeneratePawns(parms, errorOnZeroResults: false).ToList();
                    if (fighters.Count == 0) continue;
                    pawns.AddRange(fighters);
                    break;
                }
            }

            int skill = siege.Work?.Projects.Select(p => p.Building?.constructionSkillPrerequisite ?? 0)
                .DefaultIfEmpty(0).Max() ?? 0;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                IntVec3 anchor = entries[i % entries.Count];
                IntVec3 cell = CellFinder.TryFindRandomSpawnCellForPawnNear(anchor, map, out IntVec3 fit, 5,
                    c => ground.Contains(c) && !map.areaManager.Home[c]) ? fit : anchor;
                GenSpawn.Spawn(pawn, cell, map, siege.Plan.Facing);
                if (number == 1 && LordJob_EncirclementCamp.Builder(pawn) && pawn.RaceProps.Humanlike)
                    pawn.skills.GetSkill(SkillDefOf.Construction).EnsureMinLevelWithMargin(Mathf.Max(10, skill));
            }

            wave.Count = pawns.Count;
            wave.Pawns = pawns;
            return wave;

            bool ValidEntry(IntVec3 c) => ground.Contains(c) && c.Standable(map) && !c.Roofed(map) &&
                !map.areaManager.Home[c] && (siege.Plan.AssaultFallback || !siege.Plan.Rear.Standable(map) ||
                    map.reachability.CanReach(c, siege.Plan.Rear, PathEndMode.OnCell, TraverseMode.NoPassClosedDoors, Danger.Deadly));
        }
    }
}
