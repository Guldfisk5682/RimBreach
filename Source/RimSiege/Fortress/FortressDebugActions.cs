using System.Collections.Generic;
using LudeonTK;
using RimWorld;
using RimWorld.QuestGen;
using Verse;

namespace RimSiege.Fortress
{
    public static class FortressDebugActions
    {
        private static readonly (float Points, string Tier)[] Presets =
        {
            (2500f, "Compact"),
            (4000f, "Compact"),
            (5500f, "Large"),
            (7500f, "Large, Reinforced"),
            (10000f, "Large, Reinforced")
        };

        [DebugAction("RimBreach", "Spawn human bastion fortress", actionType = DebugActionType.Action,
            allowedGameStates = AllowedGameStates.Playing)]
        private static List<DebugActionNode> SpawnHumanBastion() => BuildQuestNodes(
            (scriptDefName, points) => SpawnBastionQuest(scriptDefName, points, FortressLayoutKind.HumanBastion));

        [DebugAction("RimBreach", "Spawn mechanoid bastion fortress", actionType = DebugActionType.Action,
            allowedGameStates = AllowedGameStates.Playing)]
        private static List<DebugActionNode> SpawnMechanoidBastion() => BuildQuestNodes(
            (_, _) =>
            {
                const string message = "DEV: Mechanoid bastion fortress is not implemented yet (Stage 2).";
                Log.Warning($"[RimSiege] {message}");
                Messages.Message(message, MessageTypeDefOf.RejectInput, historical: false);
            });

        private static List<DebugActionNode> BuildQuestNodes(System.Action<string, float> invoke)
        {
            var nodes = new List<DebugActionNode>
            {
                ModeNode("Contract (RS_FortressAssault)", "RS_FortressAssault", invoke),
                ModeNode("Harassment (RS_FortressHarassment)", "RS_FortressHarassment", invoke)
            };
            return nodes;
        }

        private static DebugActionNode ModeNode(string label, string scriptDefName, System.Action<string, float> invoke)
        {
            var group = new DebugActionNode(label);
            foreach (var (points, tier) in Presets)
            {
                float localPoints = points;
                group.AddChild(new DebugActionNode($"{localPoints:F0} points ({tier})",
                    DebugActionType.Action, () => invoke(scriptDefName, localPoints)));
            }
            return group;
        }

        private static void SpawnBastionQuest(string scriptDefName, float points, FortressLayoutKind kind)
        {
            QuestScriptDef script = DefDatabase<QuestScriptDef>.GetNamedSilentFail(scriptDefName);
            if (script == null)
            {
                Messages.Message($"DEV: Missing quest script {scriptDefName}.", MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            Map home = Find.CurrentMap?.IsPlayerHome == true ? Find.CurrentMap : Find.RandomSurfacePlayerHomeMap;
            var slate = new Slate();
            slate.Set("points", points);
            slate.Set("discoveryMethod", "QuestDiscoveredFromDebug".Translate());
            slate.Set("fortressIgnoreEligibility", true);
            slate.Set("fortressForcedLayout", kind);
            if (home != null) slate.Set("map", home);

            IIncidentTarget target = home ?? (IIncidentTarget)Find.World;
            if (!script.CanRun(slate.DeepCopy(), target))
            {
                string error = $"DEV: Cannot generate {scriptDefName} ({kind}, {points:F0} pts). Check eligible hostile faction and nearby site tiles.";
                Log.Warning($"[RimSiege] {error}");
                Messages.Message(error, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            Quest quest = QuestUtility.GenerateQuestAndMakeAvailable(script, slate);
            if (!quest.hidden && quest.root.sendAvailableLetter)
                QuestUtility.SendLetterQuestAvailable(quest);

            Log.Message($"[RimSiege] DEV spawned {scriptDefName} ({kind}, {points:F0} pts): quest #{quest.id} ({quest.name}).");
            Messages.Message($"DEV: Generated {kind} fortress ({points:F0} pts).", MessageTypeDefOf.NeutralEvent, historical: false);
        }
    }
}
