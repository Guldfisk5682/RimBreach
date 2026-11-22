using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using CombatExtended;
using HarmonyLib;
using RimSiege.Integration;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimSiege.Fortress
{
    internal static class FortressFriendlyFire
    {
        [ThreadStatic] private static (Thing Source, Thing Target) melee;
        [ThreadStatic] private static ProjectileCE detonation;

        private static bool PlayerSource(Thing source)
        {
            if (source is ProjectileCE projectile) source = projectile.launcher;
            return source?.Faction == Faction.OfPlayer ||
                source is Building_Turret turret && turret.TryGetComp<CompMannable>()?.ManningPawn?.Faction == Faction.OfPlayer;
        }

        private static bool IsProtectedTarget(Thing target, Map map) =>
            map?.Parent is FortressSite { Resolved: false, AlliedFaction: { } ally } site &&
            target?.Faction == ally && (target is Building || target is Pawn pawn && site.IsSupportPawn(pawn)) &&
            !ally.HostileTo(Faction.OfPlayer);

        private static bool IsIncidental(Thing target, DamageInfo damage, Map map)
        {
            if (damage.Instigator == null || !IsProtectedTarget(target, map) || !PlayerSource(damage.Instigator)) return false;
            Thing intended = damage.IntendedTarget ?? (damage.Instigator as ProjectileCE)?.intendedTargetThing;
            return intended != target && (melee.Target != target || melee.Source != damage.Instigator);
        }

        [HarmonyPatch(typeof(Pawn), nameof(Pawn.PreApplyDamage))]
        private static class Damage
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(Pawn __instance, ref DamageInfo dinfo)
            {
                if (IsIncidental(__instance, dinfo, __instance.MapHeld))
                    dinfo.SetAmount(dinfo.Amount * 0.2f);
            }
        }

        [HarmonyPatch(typeof(Faction), nameof(Faction.Notify_MemberTookDamage))]
        private static class GoodwillOnDamage
        {
            private static bool Prefix(Pawn member, DamageInfo dinfo) => !IsIncidental(member, dinfo, member.MapHeld);
        }

        [HarmonyPatch(typeof(Faction), nameof(Faction.Notify_BuildingTookDamage))]
        private static class GoodwillOnBuildingDamage
        {
            private static bool Prefix(Building building, DamageInfo dinfo) =>
                !IsIncidental(building, dinfo, building.MapHeld);
        }

        [HarmonyPatch(typeof(Faction), nameof(Faction.Notify_MemberDied))]
        private static class GoodwillOnDeath
        {
            // Keep the native ideology and faction-leader notifications.
            private static bool AffectGoodwill(Faction source, Faction other, int change, bool sendMessage,
                bool sendHostilityLetter, HistoryEventDef reason, GlobalTargetInfo? lookTarget,
                Pawn member, DamageInfo? dinfo, Map map) =>
                (dinfo is not { } damage || !IsIncidental(member, damage, map)) &&
                source.TryAffectGoodwillWith(other, change, sendMessage, sendHostilityLetter, reason, lookTarget);

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                MethodInfo original = AccessTools.Method(typeof(Faction), nameof(Faction.TryAffectGoodwillWith));
                MethodInfo replacement = AccessTools.Method(typeof(GoodwillOnDeath), nameof(AffectGoodwill));
                foreach (CodeInstruction instruction in instructions)
                {
                    if (instruction.Calls(original))
                    {
                        yield return new CodeInstruction(OpCodes.Ldarg_1).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                        yield return new CodeInstruction(OpCodes.Ldarg_2);
                        yield return new CodeInstruction(OpCodes.Ldarg_S, (byte)5);
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = replacement;
                    }
                    yield return instruction;
                }
            }
        }

        [HarmonyPatch]
        private static class Melee
        {
            private static IEnumerable<MethodBase> TargetMethods() =>
                CeAccess.Methods(typeof(Verb_MeleeAttack), "ApplyMeleeDamageToTarget");

            private static void Prefix(Verb_MeleeAttack __instance, LocalTargetInfo target,
                out (Thing Source, Thing Target) __state)
            {
                __state = melee;
                Thing thing = target.Thing;
                melee = IsProtectedTarget(thing, thing?.MapHeld) ? (__instance.Caster, thing) : default;
            }

            private static void Finalizer((Thing Source, Thing Target) __state) => melee = __state;
        }

        [HarmonyPatch(typeof(ProjectileCE), nameof(ProjectileCE.Impact))]
        private static class CeImpact
        {
            private static void Prefix(ProjectileCE __instance, out ProjectileCE __state)
            {
                __state = detonation;
                detonation = IsProtectedTarget(__instance.intendedTargetThing, __instance.Map) &&
                    PlayerSource(__instance.launcher) ? __instance : null;
            }

            private static void Finalizer(ProjectileCE __state) => detonation = __state;
        }

        [HarmonyPatch(typeof(GenExplosionCE), nameof(GenExplosionCE.DoExplosion))]
        private static class CeExplosionAim
        {
            // The blast propagates after Impact returns. Preserve the aimed target with the native explosion data.
            private static void Prefix(Map map, Thing instigator, ThingDef projectile, ref Thing intendedTarget)
            {
                ProjectileCE shot = instigator as ProjectileCE ?? detonation;
                if (intendedTarget != null || shot == null || shot.def != projectile || shot.Map != map ||
                    instigator != shot && instigator != shot.launcher || !PlayerSource(shot.launcher) ||
                    !IsProtectedTarget(shot.intendedTargetThing, map)) return;

                intendedTarget = shot.intendedTargetThing;
            }
        }
    }
}
