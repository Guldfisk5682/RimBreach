using System.Collections.Generic;
using Verse;

namespace RimSiege.Fortress
{
    internal interface IFortressDefenseLord
    {
        bool IsDefenseActive { get; }
        bool CanContinueSortie { get; }
        bool CanRout { get; }
        bool HasPendingCounterSortie { get; }
        FortressDefenseGround Ground { get; }
        FortressTargeting Targeting { get; }
        bool IntrusionActive { get; }
        IReadOnlyList<Pawn> Intruders { get; }
        bool SectorUnderWatch(int sector);
        bool AnySectorUnderWatch();
        bool CanMaintain(IntVec3 cell);
        bool ValidateAttackTarget(Pawn pawn, Thing target);
        void Attacked(Thing victim, DamageInfo damage);
        void RequestResupply();
        void CancelCounterSortie();
        void MergeSortie(List<Pawn> returning, int now);
        void RequestRoutCheck(int now);
    }
}
