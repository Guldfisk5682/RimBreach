using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimSiege.Raids.Artillery
{
    // Constructoid builders for mechanoid artillery; drop-pod battery removed.
    internal static class MechBattery
    {
        internal const float ConstructoidCombatPower = 10f;
        internal const int ConstructoidMin = 2;
        internal const int ConstructoidMax = 8;

        internal static int CountFor(IReadOnlyList<ArtilleryPlacement> guns, bool grouped)
        {
            if (guns == null || guns.Count == 0) return 0;
            float weighted = 0f;
            int support = 0;
            foreach (ArtilleryPlacement gun in guns)
            {
                weighted += gun.Def.Size.x > 1 || gun.Def.Size.z > 1 ? 1f : 0.5f;
                if (gun.IsSupport) support++;
            }
            int min = grouped ? Mathf.Max(ConstructoidMin, Mathf.Min(3, support)) : ConstructoidMin;
            int target = Mathf.CeilToInt(weighted * 0.6f) + Rand.RangeInclusive(-1, 1);
            return Mathf.Clamp(target, min, ConstructoidMax);
        }

        // Must run before PawnsArrivalModeWorker.Arrive — TryGenerateRaidInfo
        // arrives the roster before its Postfix, so a postfix inject never lands.
        internal static void InjectConstructoids(ArtilleryRaid raid, List<Pawn> pawns)
        {
            if (raid == null || pawns == null || raid.Kind != ArtilleryRaidKind.Mechanoid || raid.ConstructoidCount <= 0)
                return;
            int have = pawns.Count(IsConstructoid);
            if (have >= raid.ConstructoidCount) return;
            PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamed("Mech_Constructoid");
            for (int i = have; i < raid.ConstructoidCount; i++)
            {
                Pawn pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                    kind, raid.Faction, PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true,
                    canGeneratePawnRelations: false, mustBeCapableOfViolence: false,
                    allowFood: false, allowAddictions: false));
                if (pawn.needs?.energy != null)
                    pawn.needs.energy.CurLevel = pawn.needs.energy.MaxLevel;
                pawns.Add(pawn);
                if (RaidSpawnPatch.Current?.Artillery == raid)
                    RaidSpawnPatch.Current.InjectedConstructoids.Add(pawn);
            }
        }

        internal static bool IsConstructoid(Pawn pawn) =>
            pawn?.def == ThingDefOf.Mech_Constructoid;
    }
}
