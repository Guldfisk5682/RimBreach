using System;
using System.Collections.Generic;
using System.Linq;
using ProjectZ;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimSiege.Fortress
{
    internal sealed class FortressTargeting(Map map, Faction faction)
    {
        private const int RefreshInterval = 30;
        private const float SupportRadius = 10f;
        [ThreadStatic] private static Pawn combatOnly;
        private readonly List<Thing> threats = new();
        private readonly Dictionary<Thing, bool> supported = new();
        private int nextRefresh;

        internal static bool IsCombatTarget(Thing thing) => thing is Pawn or Building_Turret;

        internal static Thing PreferCombat(Pawn pawn, Func<Thing> find)
        {
            Pawn previous = combatOnly;
            Thing target;
            combatOnly = pawn;
            try { target = find(); }
            finally { combatOnly = previous; }
            return target ?? find();
        }

        internal bool Allows(Thing target, Pawn searcher = null)
        {
            if (IsCombatTarget(target)) return ActiveThreat(target, searcher);
            if (searcher != null && combatOnly == searcher) return false;
            if (target is not Building { Spawned: true } || !faction.HostileTo(target.Faction)) return false;
            Refresh();
            if (!supported.TryGetValue(target, out bool result))
            {
                result = threats.Any(threat => ActiveThreat(threat, null) &&
                    Api.Distance3D(threat.Position, target.Position, map) <= SupportRadius);
                supported.Add(target, result);
            }
            return result;
        }

        internal IEnumerable<Thing> CombatTargets()
        {
            Refresh();
            return threats;
        }

        private bool ActiveThreat(Thing target, Pawn searcher) =>
            target is IAttackTarget attack && target.Spawned && target.Map == map &&
            faction.HostileTo(target.Faction) && !attack.ThreatDisabled(searcher);

        private void Refresh()
        {
            int now = GenTicks.TicksGame;
            if (now < nextRefresh) return;
            nextRefresh = now + RefreshInterval;
            supported.Clear();
            threats.Clear();
            foreach (IAttackTarget target in map.attackTargetsCache.TargetsHostileToFaction(faction))
                if (IsCombatTarget(target.Thing) && ActiveThreat(target.Thing, null)) threats.Add(target.Thing);
        }
    }
}
