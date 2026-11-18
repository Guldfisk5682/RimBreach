using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Raids.Artillery
{
    internal sealed class LordJob_ArtilleryMechEscort : LordJob_AssaultColony
    {
        private int raidKey;

        public LordJob_ArtilleryMechEscort() { }

        internal ArtilleryRaid Raid => Map.GetComponent<ArtilleryMap>().ForKey(raidKey);

        // Saved escorts from the previous opening finish through the same handoff.
        internal static void RejoinCamp(ArtilleryRaid raid)
        {
            if (raid.MechEscortLord is not { CurLordToil: LordToil_ArtilleryMechEscort } escort) return;
            var pawns = escort.ownedPawns.ToList();
            escort.RemovePawns(pawns);
            raid.CampLord.AddPawns(pawns, updateDuties: false);
        }

        public override StateGraph CreateGraph()
        {
            StateGraph graph = base.CreateGraph();
            LordToil assault = graph.StartingToil;
            var follow = new LordToil_ArtilleryMechEscort();
            graph.StartingToil = follow;
            foreach (Transition transition in graph.transitions)
                if (transition.sources.Contains(assault) && transition.target is LordToil_PanicFlee or LordToil_ExitMap)
                    transition.AddSource(follow);
            var exit = new Transition(follow, graph.lordToils.OfType<LordToil_ExitMap>().First());
            exit.AddTrigger(new Trigger_Memo("RS_MechEscortCampEnded"));
            graph.AddTransition(exit);
            return graph;
        }

        public override void LordJobTick()
        {
            base.LordJobTick();
            if (lord.CurLordToil is LordToil_ArtilleryMechEscort && Raid?.Smoke == null)
                lord.ReceiveMemo("RS_MechEscortCampEnded");
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref raidKey, "raidKey");
        }
    }

    internal sealed class LordToil_ArtilleryMechEscort : LordToil
    {
        public override bool ShouldFail => lord.ownedPawns.Count == 0;

        internal static void Assign(Pawn pawn, IntVec3 focus) =>
            pawn.mindState.duty = new PawnDuty(ArtillerySmokeDefOf.RS_ArtilleryMechSmokeEscort, focus) { focusSecond = focus, wanderRadius = 8f };

        public override void UpdateAllDuties()
        {
            IntVec3 rally = ((LordJob_ArtilleryMechEscort)lord.LordJob).Raid?.Smoke?.Rally ?? IntVec3.Invalid;
            foreach (Pawn pawn in lord.ownedPawns)
                Assign(pawn, rally.IsValid ? rally : pawn.PositionHeld);
        }
    }
}
