using System.Collections.Generic;
using System.Linq;
using RimSiege.Raids.Artillery;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Raids.Encirclement
{
    public sealed class LordJob_EncirclementAssault : LordJob_AssaultColony
    {
        public LordJob_EncirclementAssault() { }

        internal LordJob_EncirclementAssault(Faction faction, bool mechanoid)
            : base(faction, canKidnap: false, canSteal: false, canTimeoutOrFlee: !mechanoid) { }
    }

    internal static class EncirclementAssaultHandoff
    {
        internal static void Transfer(QuestPart_Encirclement siege)
        {
            Map map = siege.Map;
            ArtilleryMap registry = map.GetComponent<ArtilleryMap>();
            var assault = (siege.CampLord?.ownedPawns ?? Enumerable.Empty<Pawn>())
                .Concat(siege.Waves.SelectMany(w => w.Pawns).Where(p => p != null &&
                    (p.GetLord() == null || p.GetLord() == siege.CampLord)))
                .Where(siege.InBattle).Distinct().ToList();
            var crews = new Dictionary<ArtilleryRaid, List<Pawn>>();
            siege.Work.Stop(siege, stopDevices: false);

            foreach (ArtilleryRaid raid in siege.Plan.Batteries(map))
            {
                ArtilleryCamp.CancelUnfinished(raid);
                raid.Camp.Guns.RemoveAll(g => g.Building?.Spawned != true || g.Building.Faction != siege.Faction);
                raid.Camp.Cover.RemoveAll(g => g.Building?.Spawned != true || g.Building.Faction != siege.Faction);
                var crew = new List<Pawn>();
                foreach (ArtilleryPlacement gun in raid.Camp.Guns)
                {
                    if (gun.Operator != null && assault.Count > 1 && assault.Remove(gun.Operator))
                        crew.Add(gun.Operator);
                    else
                        gun.Operator = null;
                }

                if (siege.Mechanoid && raid.Camp.Guns.Count != 0 &&
                    assault.Where(p => MechBattery.IsConstructoid(p) && LordJob_EncirclementCamp.Builder(p))
                        .OrderBy(p => p.Position.DistanceToSquared(raid.Camp.Center)).FirstOrDefault() is { } technician)
                {
                    assault.Remove(technician);
                    crew.Add(technician);
                }
                crews.Add(raid, crew);
            }

            if (siege.Mechanoid)
            {
                var available = assault.Where(EncirclementMechEscort.Eligible).ToList();
                int limit = System.Math.Min(available.Count - 1, EncirclementMechEscort.Limit(available.Count));
                foreach (List<Pawn> crew in crews.Values.Reverse())
                {
                    if (limit <= 0) break;
                    Pawn technician = crew.FirstOrDefault(MechBattery.IsConstructoid);
                    if (technician == null || EncirclementMechEscort.Closest(technician, available) is not { } guard)
                        continue;
                    crew.Add(guard);
                    assault.Remove(guard);
                    available.Remove(guard);
                    limit--;
                }
            }

            if (siege.CampLord != null && map.lordManager.lords.Contains(siege.CampLord))
            {
                siege.CampLord.RemoveAllPawns();
                map.lordManager.RemoveLord(siege.CampLord);
            }
            siege.CampLord = null;

            if (assault.Count > 0)
            {
                ResetAssaultJobs(assault);
                siege.AssaultLord = LordMaker.MakeNewLord(siege.Faction,
                    new LordJob_EncirclementAssault(siege.Faction, siege.Mechanoid), map, assault);
                siege.AssaultLord.lastPawnHarmTick = GenTicks.TicksGame;
                if (registry.Incident(siege.IncidentKey) is { } incident)
                {
                    incident.NativeLords.Clear();
                    incident.NativeLords.Add(siege.AssaultLord);
                    incident.InitialRaidSize = assault.Count + crews.Values.Sum(c => c.Count);
                    incident.ViolentLosses.Clear();
                    incident.ReachedDefenses = false;
                    incident.Breaches = false;
                    incident.RetreatAnnounced = false;
                }
            }

            SiegeParty party = assault.Count == 0 ? null : map.GetComponent<SiegeRaidMap>()
                .Register(assault, siege.Faction, encirclement: true);
            foreach (var pair in crews)
            {
                ArtilleryRaid raid = pair.Key;
                raid.Strategy = RaidStrategyDefOf.ImmediateAttack;
                raid.AssaultLord = siege.AssaultLord;
                raid.AssaultCreated = true;
                raid.Id = party?.Id ?? 0;
                if (pair.Value.Count > 0)
                {
                    registry.AssignMembers(raid, pair.Value);
                    LordJob_Artillery job = siege.Mechanoid ? new LordJob_MechArtillery(raid) : new LordJob_Artillery(raid);
                    raid.CampLord = LordMaker.MakeNewLord(siege.Faction, job, map, pair.Value);
                    raid.CampLord.ownedBuildings.AddRange(ArtilleryCamp.Placements(raid.Camp)
                        .Select(p => p.Building).Where(b => b != null && b.Spawned));
                }
            }

            foreach (Pawn pawn in crews.Values.SelectMany(c => c))
                if (pawn.CurJob is { } job && job.def != JobDefOf.ManTurret)
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
        }

        internal static void ReclaimRecovered(QuestPart_Encirclement siege)
        {
            Lord assault = siege.AssaultLord;
            Map map = siege.Map;
            if (assault == null || !map.lordManager.lords.Contains(assault) ||
                assault.CurLordToil is LordToil_ExitMap or LordToil_PanicFlee) return;
            var recovered = siege.Waves.SelectMany(w => w.Pawns)
                .Where(p => siege.InBattle(p) && p.GetLord() == null).Distinct().ToList();
            if (recovered.Count == 0) return;

            ResetAssaultJobs(recovered);
            assault.AddPawns(recovered);
            ArtilleryIncident incident = map.GetComponent<ArtilleryMap>().Incident(siege.IncidentKey);
            if (incident != null)
            {
                foreach (Pawn pawn in recovered)
                    if (!incident.ViolentLosses.Contains(pawn.thingIDNumber))
                        incident.InitialRaidSize++;
            }
            SiegeRaidMap raids = map.GetComponent<SiegeRaidMap>();
            SiegeParty party = assault.ownedPawns.Select(raids.Party).FirstOrDefault(p => p?.Encirclement == true);
            if (party != null)
            {
                foreach (Pawn pawn in recovered.OrderBy(SiegeRoles.LadderPriority))
                {
                    raids.Include(pawn, party);
                    raids.Issue(pawn, party);
                }
            }
        }

        internal static void ResumeLegacyAssault(QuestPart_Encirclement siege)
        {
            if (siege.AssaultLord is not { CurLordToil: LordToil_AssaultColonyBreaching } lord ||
                lord.Graph.lordToils.OfType<LordToil_AssaultColony>().FirstOrDefault() is not { } assault) return;

            // The assault toil already exists in the saved graph. Keep the same lord
            // so the artillery batteries and incident retain their references.
            lord.GotoToil(assault);
            var pawns = lord.ownedPawns.Where(siege.InBattle).ToList();
            ResetAssaultJobs(pawns);
            SiegeRaidMap raids = siege.Map.GetComponent<SiegeRaidMap>();
            SiegeParty party = pawns.Select(raids.Party).FirstOrDefault(p => p?.Encirclement == true) ??
                raids.Register(pawns, siege.Faction, deferKits: true, encirclement: true);
            if (party != null)
            {
                foreach (Pawn pawn in pawns) raids.Include(pawn, party);
                raids.IssueInOrder(pawns);
            }
            foreach (ArtilleryRaid battery in siege.Plan.Batteries(siege.Map))
            {
                battery.Strategy = RaidStrategyDefOf.ImmediateAttack;
                battery.Id = party?.Id ?? 0;
            }
        }

        private static void ResetAssaultJobs(IEnumerable<Pawn> pawns)
        {
            foreach (Pawn pawn in pawns)
            {
                if (pawn.CurJob != null)
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                RestUtility.WakeUp(pawn);
            }
        }

        internal static bool Defeated(QuestPart_Encirclement siege)
        {
            if (siege.Mechanoid)
                return !siege.Waves.Any(w => w.Pawns.Any(p => siege.InBattle(p) && !MechBattery.IsConstructoid(p))) &&
                    !siege.Plan.Batteries(siege.Map).Any(r => r.Camp.Guns.Any(g =>
                        g.Building is { Spawned: true } b && b.Faction == siege.Faction));

            Lord assault = siege.AssaultLord;
            return assault == null || assault.CurLordToil is LordToil_ExitMap or LordToil_PanicFlee ||
                !assault.ownedPawns.Any(siege.InBattle);
        }

        internal static void Cleanup(QuestPart_Encirclement siege)
        {
            Map map = siege.Map;
            ArtilleryMap registry = map.GetComponent<ArtilleryMap>();
            var exiting = new List<Pawn>();
            if (siege.CampLord != null && map.lordManager.lords.Contains(siege.CampLord))
            {
                exiting.AddRange(siege.CampLord.ownedPawns.Where(siege.InBattle));
                map.lordManager.RemoveLord(siege.CampLord);
            }
            if (siege.AssaultLord != null && map.lordManager.lords.Contains(siege.AssaultLord) &&
                siege.AssaultLord.CurLordToil is not (LordToil_ExitMap or LordToil_PanicFlee))
            {
                exiting.AddRange(siege.AssaultLord.ownedPawns.Where(siege.InBattle));
                map.lordManager.RemoveLord(siege.AssaultLord);
            }

            foreach (ArtilleryRaid raid in siege.Plan.Batteries(map).ToArray())
            {
                bool hasLord = raid.CampLord != null && map.lordManager.lords.Contains(raid.CampLord);
                if (hasLord)
                {
                    if (raid.CampLord.CurLordToil is LordToil_Siege)
                        raid.CampLord.ReceiveMemo(LordJob_Artillery.RaidEndedMemo);
                    else if (raid.CampLord.CurLordToil is not (LordToil_ExitMap or LordToil_PanicFlee))
                    {
                        exiting.AddRange(raid.CampLord.ownedPawns.Where(siege.InBattle));
                        map.lordManager.RemoveLord(raid.CampLord);
                        ArtilleryCamp.Abandon(raid, map, retainWithAssault: false);
                        registry.Remove(raid);
                    }
                }
                else
                {
                    ArtilleryCamp.Abandon(raid, map, retainWithAssault: false);
                    registry.Remove(raid);
                }
            }

            foreach (Pawn pawn in siege.Waves.SelectMany(w => w.Pawns))
                if (siege.InBattle(pawn) && pawn.GetLord() == null && !exiting.Contains(pawn))
                    exiting.Add(pawn);

            if (exiting.Count != 0)
                LordMaker.MakeNewLord(siege.Faction, new LordJob_ExitMapBest(LocomotionUrgency.Jog), map, exiting);
            registry.FinishIncident(registry.Incident(siege.IncidentKey));
        }
    }
}
