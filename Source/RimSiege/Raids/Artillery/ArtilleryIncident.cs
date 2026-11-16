using System.Collections.Generic;
using Verse;
using Verse.AI.Group;

namespace RimSiege.Raids.Artillery
{
    // Shared completion and a transient target window; child raids own all camp/pawn/gun state.
    internal sealed class ArtilleryIncident : IExposable
    {
        internal int Key;
        internal int InitialRaidSize;
        internal HashSet<int> ViolentLosses = new();
        internal bool ReachedDefenses;
        internal bool Breaches;
        internal bool RetreatAnnounced;
        internal bool ShieldAssigned;
        internal List<Lord> NativeLords = new();
        internal List<string> CompletionTags = new();
        internal bool CompletionPending, CompletionSent;
        // Native lord creation is one atomic operation; loaded incidents have completed it.
        internal bool SetupComplete = true;
        private ArtilleryTargets.Catalog catalog;
        private int nextCatalogTick;

        internal ArtilleryTargets.Catalog Targets(Map map, int tick)
        {
            if (catalog == null || tick >= nextCatalogTick)
            {
                catalog = new(map);
                nextCatalogTick = tick + ArtilleryTargets.SearchInterval;
            }
            return catalog;
        }

        public ArtilleryIncident() { }

        public void ExposeData()
        {
            Scribe_Values.Look(ref Key, "key");
            Scribe_Values.Look(ref ShieldAssigned, "shieldAssigned");
            Scribe_Values.Look(ref InitialRaidSize, "initialRaidSize");
            Scribe_Collections.Look(ref ViolentLosses, "violentLossIds", LookMode.Value);
            ViolentLosses ??= new();
            Scribe_Values.Look(ref ReachedDefenses, "reachedDefenses");
            Scribe_Values.Look(ref Breaches, "breaches");
            Scribe_Values.Look(ref RetreatAnnounced, "retreatAnnounced");
            Scribe_Collections.Look(ref NativeLords, "nativeLords", LookMode.Reference);
            Scribe_Collections.Look(ref CompletionTags, "completionTags", LookMode.Value);
            Scribe_Values.Look(ref CompletionPending, "completionPending");
            Scribe_Values.Look(ref CompletionSent, "completionSent");
            NativeLords ??= new(); CompletionTags ??= new();
        }
    }
}
