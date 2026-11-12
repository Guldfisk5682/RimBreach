using System.Linq;
using HarmonyLib;
using ProjectZ;
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

            Map map = pawn.Map;
            IntVec3 heading = Api.GroundCellOf(target.Cell, map) - Api.GroundCellOf(pawn.Position, map);
            ThingDef barricade = ThingDefOf.Barricade;
            ThingDef steel = ThingDefOf.Steel;
            foreach (IntVec3 direction in GenAdj.CardinalDirections.OrderByDescending(Projection))
            {
                if (Projection(direction) <= 0) break;
                IntVec3 cell = pawn.Position + direction;
                if (!cell.InBounds(map) || cell.InNoBuildEdgeArea(map) || !defense.Ground.Outside(cell) ||
                    !cell.Standable(map) || cell.GetThingList(map).Any(BlocksPlacement) ||
                    !GenConstruct.CanBuildOnTerrain(barricade, cell, map, Rot4.North, stuffDef: steel) ||
                    !GenSpawn.CanSpawnAt(barricade, cell, map, Rot4.North, canWipeEdifices: false)) continue;

                FortressConstruction.Spawn(barricade, cell, Rot4.North, steel, map);
                duty.focusThird = cell;
                return;
            }

            int Projection(IntVec3 direction) => heading.x * direction.x + heading.z * direction.z;
        }

        private static bool BlocksPlacement(Thing thing) => thing is Building or Blueprint or Frame or Pawn ||
            thing.def.category == ThingCategory.Plant && thing.def.Fillage != FillCategory.None;
    }
}
