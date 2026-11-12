using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimSiege.Fortress
{
    public sealed class Alert_FortressRaidApproaching : Alert
    {
        private FortressSite source;

        public override AlertReport GetReport()
        {
            source = null;
            Map map = Find.CurrentMap;
            if (WorldRendererUtility.WorldSelected || map?.IsPlayerHome != true) return AlertReport.Inactive;

            foreach (WorldObject worldObject in Find.WorldObjects.AllWorldObjects)
            {
                if (worldObject is FortressSite site && site.RaidHome == map)
                {
                    source = site;
                    return AlertReport.CulpritIs(site);
                }
            }
            return AlertReport.Inactive;
        }

        public override string GetLabel() =>
            "RS_FortressRaidAlert".Translate(source.TicksUntilRaid.ToStringTicksToPeriod());

        public override TaggedString GetExplanation() => source == null ? default :
            "RS_FortressRaidAlertText".Translate(source.LabelCap, source.RaidTarget.LabelCap,
                source.TicksUntilRaid.ToStringTicksToPeriod());
    }
}
