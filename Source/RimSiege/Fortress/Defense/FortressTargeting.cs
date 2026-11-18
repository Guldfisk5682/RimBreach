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
        [ThreadStatic] private static bool skippedNonCombat;
        private readonly List<Thing> threats = new();
        private readonly Dictionary<Thing, bool> supported = new();
        private int nextRefresh;

        internal static bool IsCombatTarget(Thing thing) => thing is Pawn or Building_Turret;

        internal static bool IsActivePlayerCombatThreat(Thing target, Faction faction, Map map)
        {
            if (!IsCombatTarget(target) || target is not IAttackTarget attack ||
                !target.Spawned || target.Map != map ||
                target.Faction != Faction.OfPlayer || !faction.HostileTo(Faction.OfPlayer) ||
                attack.ThreatDisabled(null))
                return false;
            if (target is Pawn pawn)
                return !pawn.Downed && !pawn.Dead &&
                    (!pawn.IsAnimal || pawn.training?.HasLearned(TrainableDefOf.Release) == true);
            return true;
        }

        internal bool HasPlayerCombatThreats() =>
            CombatTargets().Any(target => IsActivePlayerCombatThreat(target, faction, map));

        internal static Thing PreferCombat(Pawn pawn, Func<Thing> find)
        {
            Pawn previous = combatOnly;
            bool previousSkipped = skippedNonCombat;
            Thing target;
            bool skipped;
            combatOnly = pawn;
            skippedNonCombat = false;
            try
            {
                target = find();
                skipped = skippedNonCombat;
            }
            finally
            {
                combatOnly = previous;
                skippedNonCombat = previousSkipped;
            }
            return target ?? (skipped ? find() : null);
        }

        internal bool Allows(Thing target, Pawn searcher = null)
        {
            if (IsCombatTarget(target)) return ActiveThreat(target, searcher);
            if (target is not Building { Spawned: true } || !faction.HostileTo(target.Faction)) return false;
            if (searcher != null && combatOnly == searcher)
            {
                skippedNonCombat = true;
                return false;
            }
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
