using RimWorld;
using Verse;
using Verse.AI;

namespace RimSiege.Raids.Encirclement
{
    [DefOf]
    public static class EncirclementDefs
    {
        public static QuestScriptDef RS_Encirclement;
        public static GameConditionDef RS_EncirclementSmokeVeil;
        public static GameConditionDef RS_EncirclementSunVeil;
        public static GameConditionDef RS_EncirclementWeather;
        public static GameConditionDef RS_EncirclementClimate;
        public static ThingDef RS_FieldWeatherController;
        public static ThingDef RS_FieldClimateAdjuster;
        public static ThingDef RS_FieldToxicSpewer;
        public static ThingDef RS_FieldEMIDynamo;
        public static ThingDef RS_SiegeMechNode;
        public static DutyDef RS_EncirclementBuild;
        public static DutyDef RS_EncirclementGunner;
        public static DutyDef RS_EncirclementSentry;
        public static DutyDef RS_EncirclementReserve;
        public static DutyDef RS_EncirclementSmoke;
        public static ThoughtDef RS_EncirclementBroken;

        static EncirclementDefs() => DefOfHelper.EnsureInitializedInCtor(typeof(EncirclementDefs));

        // A DefOf cannot contain two fields with the same name.
        internal static GameConditionDef Condition => GameConditionDef.Named(nameof(RS_Encirclement));
        internal static RaidStrategyDef Breaching => DefDatabase<RaidStrategyDef>.GetNamed("ImmediateAttackBreaching");
    }
}
