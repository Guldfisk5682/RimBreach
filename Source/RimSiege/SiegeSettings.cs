using Verse;

namespace RimSiege
{
    public sealed class SiegeSettings : ModSettings
    {
        public bool EnhancedAssault;
        public int ArtilleryChance = 3;
        public bool FortressFog;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref EnhancedAssault, "enhancedAssault", false);
            Scribe_Values.Look(ref ArtilleryChance, "artilleryChance", 3);
            Scribe_Values.Look(ref FortressFog, "fortressFog", false);
        }
    }
}
