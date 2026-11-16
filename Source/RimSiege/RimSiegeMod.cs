using HarmonyLib;
using RimSiege.Integration;
using UnityEngine;
using Verse;

namespace RimSiege
{
    public class RimSiegeMod : Mod
    {
        internal static SiegeSettings Settings { get; private set; }

        public RimSiegeMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<SiegeSettings>();
            IsoSiegeModels.Register();
            Harmony harmony = new Harmony("Guld.ThreeDSiege");
            harmony.PatchAll(typeof(RimSiegeMod).Assembly);
            Log.Message("[RimSiege] Siege gameplay loaded; CE geometry is provided by IsoRim CE Pack.");
        }

        public override string SettingsCategory() => Content.Name;

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.CheckboxLabeled("RS_EnhancedAssault".Translate(), ref Settings.EnhancedAssault,
                "RS_EnhancedAssaultDescription".Translate());
            if (Settings.EnhancedAssault)
            {
                listing.CheckboxLabeled("RS_StubbornEnemies".Translate(), ref Settings.StubbornEnemies,
                    "RS_StubbornEnemiesDescription".Translate());
                listing.Label("RS_ArtilleryChance".Translate((Settings.ArtilleryChance / 10f).ToString("0.0")));
                Settings.ArtilleryChance = Mathf.RoundToInt(listing.Slider(Settings.ArtilleryChance, 0f, 10f));
                listing.CheckboxLabeled("RS_ScaleArtilleryConstruction".Translate(), ref Settings.ScaleArtilleryConstruction);
                if (ModsConfig.BiotechActive)
                {
                    listing.CheckboxLabeled("RS_MechSupportShield".Translate(), ref Settings.MechSupportShield);
                    if (Settings.MechSupportShield)
                    {
                        int minimum = Mathf.CeilToInt(DefDatabase<Raids.Artillery.ArtilleryProfileDef>
                            .GetNamed("RS_ArtilleryMechanoid").minPoints / 1000f);
                        listing.Label("RS_MechShieldPoints".Translate(Settings.MechShieldPoints));
                        Settings.MechShieldPoints = 1000 * Mathf.RoundToInt(
                            listing.Slider(Settings.MechShieldPoints / 1000f, minimum, 10));
                    }
                }
            }
            listing.CheckboxLabeled("RS_FortressFog".Translate(), ref Settings.FortressFog,
                "RS_FortressFogDescription".Translate());
            listing.CheckboxLabeled("RS_ModifySpecialWeapons".Translate(), ref Settings.ModifySpecialWeapons);
            listing.End();
        }
    }
}
