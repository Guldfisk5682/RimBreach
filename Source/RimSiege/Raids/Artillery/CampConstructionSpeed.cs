using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimSiege.Raids.Encirclement;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace RimSiege.Raids.Artillery
{
    // Scale only the native frame-construction speed read. Native stat caching,
    // materials, work requirements and completion remain unchanged.
    [HarmonyPatch]
    internal static class CampConstructionSpeed
    {
        private sealed class WorkRate
        {
            internal Lord Camp;
            internal Frame Frame;
            internal Faction TargetFaction;
            internal float Multiplier = 1f;
            internal float WorkFactor = 1f;
            internal Building ConstructionNode;
        }

        // Resolve the planned worksite once per native job. No raid/map scan is
        // added to the construction tick; entries expire with their job driver.
        private static readonly ConditionalWeakTable<JobDriver_ConstructFinishFrame, WorkRate> Rates = new();

        private static IEnumerable<MethodBase> TargetMethods() =>
            typeof(JobDriver_ConstructFinishFrame).GetNestedTypes(AccessTools.all)
                .SelectMany(AccessTools.GetDeclaredMethods)
                .Where(method => method.Name.Contains("<MakeNewToils>"));

        private static WorkRate Resolve(JobDriver_ConstructFinishFrame driver)
        {
            Pawn pawn = driver.pawn;
            var frame = driver.job.targetA.Thing as Frame;
            Lord camp = pawn.GetLord();
            if (camp?.LordJob is LordJob_EncirclementCamp { Campaign: { Work: { } work } siege } &&
                frame?.Faction == pawn.Faction && work.Owns(frame))
            {
                ArtilleryPlacement gun = work.GunPlacementFor(siege, frame);
                return new WorkRate
                {
                    Camp = camp, Frame = frame, Multiplier = siege.Mechanoid ? 9f : 8f,
                    WorkFactor = gun?.ConstructionWorkFactor ?? 1f,
                    TargetFaction = Faction.OfPlayer,
                    ConstructionNode = siege.Mechanoid ? work.NearbyNode(frame.Position, pawn.Map) : null
                };
            }

            if (CampConstructionMaterials.PlacementFor(pawn, frame) is not { } placement) return new WorkRate();
            ArtilleryRaid raid = ((LordJob_Artillery)camp.LordJob).Raid;
            return new WorkRate
            {
                Camp = camp, Frame = frame,
                Multiplier = raid.ConstructionSpeedMultiplier, WorkFactor = placement.ConstructionWorkFactor,
                TargetFaction = raid.EffectiveTargetFaction
            };
        }

        private static float Speed(Thing thing, StatDef stat, bool applyPostProcess, int cacheStaleAfterTicks)
        {
            float native = thing.GetStatValue(stat, applyPostProcess, cacheStaleAfterTicks);
            if (thing is not Pawn pawn || pawn.jobs.curDriver is not JobDriver_ConstructFinishFrame driver ||
                pawn.GetLord()?.LordJob is not (LordJob_Artillery or LordJob_EncirclementCamp)) return native;
            WorkRate rate = Rates.GetValue(driver, Resolve);
            if (rate.Camp == null || pawn.GetLord() != rate.Camp ||
                rate.Camp.CurLordToil is not (LordToil_Siege or LordToil_EncirclementCamp) ||
                pawn.Faction != rate.Frame.Faction || !pawn.HostileTo(rate.TargetFaction)) return native;
            float multiplier = RimSiegeMod.Settings.ScaleArtilleryConstruction ? rate.Multiplier : 1f;
            if (rate.ConstructionNode is { Spawned: true } node && node.Faction == pawn.Faction)
                multiplier *= 1.15f;
            return native * multiplier / rate.WorkFactor;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            FieldInfo speed = AccessTools.Field(typeof(StatDefOf), nameof(StatDefOf.ConstructionSpeed));
            MethodInfo original = AccessTools.Method(typeof(StatExtension), nameof(StatExtension.GetStatValue),
                new[] { typeof(Thing), typeof(StatDef), typeof(bool), typeof(int) });
            MethodInfo replacement = AccessTools.Method(typeof(CampConstructionSpeed), nameof(Speed));
            bool readingSpeed = false;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.LoadsField(speed)) readingSpeed = true;
                if (readingSpeed && instruction.Calls(original))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                    readingSpeed = false;
                }
                yield return instruction;
            }
        }
    }
}
