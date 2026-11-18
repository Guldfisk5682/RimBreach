using HarmonyLib;
using RimSiege.Raids;
using Verse;
using Verse.AI.Group;

namespace RimSiege.Fortress
{
    [HarmonyPatch(typeof(Verb), nameof(Verb.TryStartCastOn), typeof(LocalTargetInfo), typeof(LocalTargetInfo),
        typeof(bool), typeof(bool), typeof(bool), typeof(bool))]
    internal static class FortressSortieCover
    {
        private static void Postfix(Verb __instance, LocalTargetInfo __0, bool __result)
        {
            if (!__result || __instance.Caster is not Pawn { Spawned: true, Downed: false } pawn) return;
            Lord lord = pawn.GetLord();
            Thing target = __0.Thing ?? pawn.mindState.enemyTarget;
            if (lord?.CurLordToil is not LordToil_FortressSortie ||
                lord.LordJob is not LordJob_FortressSortie sortie ||
                !FortressDefensePosts.CanFight(pawn) ||
                !sortie.Ground.Outside(pawn.Position) ||
                !sortie.ValidTarget(target) || SmokeCombat.IsSmokeWeapon(__instance.EquipmentSource?.def)) return;

            if (__0.HasThing || __0.Cell == target.Position) sortie.RecordContact(target);
            if (!__instance.IsMeleeAttack && pawn.pather?.Moving == false && __0.HasThing)
                FieldCover.TryPlace(pawn, target.Position, sortie.Ground.Outside);
        }
    }
}
