using HarmonyLib;
using RimSiege.Raids;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Fortress
{
    [HarmonyPatch(typeof(Pawn_MindState), "Notify_AttackedTarget")]
    internal static class FortressSortieCover
    {
        private static void Postfix(Pawn_MindState __instance, LocalTargetInfo target)
        {
            PawnDuty duty = __instance.duty;
            if (duty == null) return;
            Pawn pawn = __instance.pawn;
            Lord lord = pawn.GetLord();
            if (lord?.CurLordToil is not LordToil_FortressDefense ||
                lord.LordJob is not LordJob_FortressDefense defense) return;
            if (duty.def != FortressDutyDefOf.RS_FortressSortie)
            {
                defense.RecordRetaliation(target.Thing);
                return;
            }
            defense.RecordSortieContact(pawn, target.Thing);
            if (duty.focusThird.IsValid) return;
            if (!FortressDefensePosts.CanFight(pawn) ||
                !defense.Ground.Outside(pawn.Position) ||
                pawn.CurrentEffectiveVerb?.verbProps.IsMeleeAttack != false ||
                !defense.ValidTarget(target.Thing)) return;

            IntVec3 cover = FieldCover.TryPlace(pawn, target.Cell, defense.Ground.Outside);
            if (cover.IsValid) duty.focusThird = cover;
        }
    }
}
