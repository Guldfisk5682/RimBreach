using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimSiege.Fortress
{
    public sealed class FortressSite : Site
    {
        internal const int RaidInterval = 5 * GenDate.TicksPerDay;
        internal FortressBlueprint Blueprint;
        internal float OriginalPoints;
        internal float DefenderPoints;
        internal MapParent RaidTarget;
        internal bool Harassment;
        internal bool Resolved;
        internal int NextSortieTick;

        private int nextRaidTick;

        internal int TicksUntilRaid => System.Math.Max(0, nextRaidTick - Find.TickManager.TicksGame);

        private bool RaidScheduled => Harassment && !Resolved && !Destroyed && !HasMap;

        internal Map RaidHome => RaidScheduled && RaidTarget?.Map is { IsPlayerHome: true } home &&
            !Faction.deactivated && !Faction.def.raidsForbidden && Faction.HostileTo(Faction.OfPlayer) ? home : null;

        public override void PostAdd()
        {
            base.PostAdd();
            if (Harassment) StartRaidCooldown();
        }

        public override bool ShouldRemoveMapNow(out bool alsoRemoveWorldObject)
        {
            bool remove = base.ShouldRemoveMapNow(out alsoRemoveWorldObject);
            if (!Resolved) alsoRemoveWorldObject = false;
            return remove;
        }

        public override void Notify_MyMapRemoved(Map map)
        {
            base.Notify_MyMapRemoved(map);
            if (Harassment && !Resolved) StartRaidCooldown();
        }

        protected override void TickInterval(int delta)
        {
            base.TickInterval(delta);
            if (!RaidScheduled || TicksUntilRaid > 0) return;

            StartRaidCooldown();
            if (RaidHome is not Map home) return;

            IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, home);
            parms.faction = Faction;
            parms.points = DefenderPoints / 3f;
            parms.customLetterText = "{BASETEXT}\n\n" + "RS_FortressRaidSource".Translate(LabelCap).Resolve();
            IncidentWorker worker = IncidentDefOf.RaidEnemy.Worker;
            if (worker.CanFireNow(parms)) worker.TryExecute(parms);
        }

        public override string GetInspectString()
        {
            string text = base.GetInspectString();
            if (!RaidScheduled) return text;

            return text + "\n" + "RS_FortressNextRaid".Translate(TicksUntilRaid.ToStringTicksToPeriod());
        }

        private void StartRaidCooldown() => nextRaidTick = Find.TickManager.TicksGame + RaidInterval;

        internal void GenerationFailed()
        {
            Resolved = true;
            QuestUtility.SendQuestTargetSignals(questTags, "FortressGenerationFailed", this.Named("SUBJECT"));
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Deep.Look(ref Blueprint, "fortressBlueprint");
            Scribe_Values.Look(ref OriginalPoints, "fortressOriginalPoints");
            Scribe_Values.Look(ref DefenderPoints, "fortressDefenderPoints");
            Scribe_References.Look(ref RaidTarget, "fortressRaidTarget");
            Scribe_Values.Look(ref Harassment, "fortressHarassment");
            Scribe_Values.Look(ref Resolved, "fortressResolved");
            Scribe_Values.Look(ref NextSortieTick, "fortressNextSortieTick");
            Scribe_Values.Look(ref nextRaidTick, "fortressNextRaidTick");
        }
    }

    public sealed class SitePartWorker_Fortress : SitePartWorker_Outpost
    {
        public override bool FactionCanOwn(Faction faction) => base.FactionCanOwn(faction) &&
            faction?.def.humanlikeFaction == true &&
            faction.def.pawnGroupMakers?.Any(maker => maker.kindDef == PawnGroupKindDefOf.Settlement) == true;
    }
}
