namespace RimSiege.Integration
{
    /// <summary>
    /// Registers IsoRim 3D model rules for RimBreach entities whose geometry matches an existing IsoRim model.
    /// </summary>
    internal static class IsoSiegeModels
    {
        internal static void Register()
        {
            if (IsoRim.Api.Version < 42) return;
            // Bind the mechanoid rapid shield projector to IsoRim's deployed Broadshield projector 3D model.
            IsoRim.Api.AddModelRule(def => def.defName switch
            {
                "RS_MechSupportShield" => "R3D_BroadshieldProjector",
                "RS_FieldWeatherController" => "R3D_WeatherController",
                "RS_FieldClimateAdjuster" => "R3D_ClimateAdjuster",
                "RS_FieldToxicSpewer" => "R3D_ToxicSpewer",
                "RS_FieldEMIDynamo" => "R3D_EMIDynamo",
                "RS_SiegeMechNode" => "R3D_Gloomlight",
                _ => null
            });
        }
    }
}
