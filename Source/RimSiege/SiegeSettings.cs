using Verse;

namespace RimSiege
{
    public sealed class SiegeSettings : ModSettings
    {
        public bool EnhancedAssault;
        public bool StubbornEnemies;
        public bool ResoluteArtillery;
        public int ArtilleryChance = 3;
        public bool FortressFog;
        public bool MechSupportShield = true;
        public int MechShieldPoints = 5000;
        public bool ScaleArtilleryConstruction = true;
        public bool ModifySpecialWeapons;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref EnhancedAssault, "enhancedAssault", false);
            Scribe_Values.Look(ref StubbornEnemies, "stubbornEnemies", false);
            Scribe_Values.Look(ref ResoluteArtillery, "resoluteArtillery", false);
            Scribe_Values.Look(ref ArtilleryChance, "artilleryChance", 3);
            Scribe_Values.Look(ref MechSupportShield, "mechSupportShield", true);
            Scribe_Values.Look(ref MechShieldPoints, "mechShieldPoints", 5000);
            Scribe_Values.Look(ref ScaleArtilleryConstruction, "scaleArtilleryConstruction", true);
            Scribe_Values.Look(ref FortressFog, "fortressFog", false);
            Scribe_Values.Look(ref ModifySpecialWeapons, "modifySpecialWeapons", false);
        }
    }
}
