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
            IsoRim.Api.AddModelRule(def =>
                def.defName == "RS_MechSupportShield" ? "R3D_BroadshieldProjector" : null);
        }
    }
}
