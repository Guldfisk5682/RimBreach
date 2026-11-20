using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Fortress
{
    [DefOf]
    internal static class FortressMechJobDefOf
    {
        public static JobDef RS_MechFortressLoadGestator = null;

        static FortressMechJobDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(FortressMechJobDefOf));
    }

    public sealed class FortressMechResurrection : MapComponent
    {
        internal const string DutyTag = "resurrect";
        private const int TickInterval = 60;

        private List<PodTask> tasks = new();

        public FortressMechResurrection(Map map) : base(map) { }

        internal static int WeightOrder(Pawn mech)
        {
            MechWeightClassDef weight = mech?.RaceProps?.mechWeightClass;
            if (weight == MechWeightClassDefOf.UltraHeavy) return 4;
            if (weight == MechWeightClassDefOf.Heavy) return 3;
            if (weight == MechWeightClassDefOf.Medium) return 2;
            if (weight == MechWeightClassDefOf.Light) return 1;
            return 2;
        }

        internal static int IncubationTicksFor(Pawn mech)
        {
            MechWeightClassDef weight = mech?.RaceProps?.mechWeightClass;
            if (weight == MechWeightClassDefOf.Light) return 7500;        // 3 in-game hours
            if (weight == MechWeightClassDefOf.Medium) return 15000;      // 6 in-game hours
            if (weight == MechWeightClassDefOf.Heavy) return 30000;       // 12 in-game hours
            if (weight == MechWeightClassDefOf.UltraHeavy) return 45000;  // 18 in-game hours
            return 15000;
        }

        internal static bool IsGestatorDef(ThingDef def) =>
            def != null && (def.defName == "MechGestator" || def.defName == "LargeMechGestator");

        internal static bool IsPowered(Building gestator) =>
            gestator != null && gestator.TryGetComp<CompPowerTrader>() is not { PowerOn: false };

        internal bool IsBusy(Building gestator) =>
            tasks.Any(task => task.Gestator == gestator);

        internal bool CanGestatorAccept(Building gestator, Pawn mech)
        {
            if (gestator is not { Spawned: true, Destroyed: false } || mech == null ||
                !mech.RaceProps.IsMechanoid || !IsGestatorDef(gestator.def) ||
                IsBusy(gestator) || !IsPowered(gestator))
                return false;

            if (gestator.def.defName == "LargeMechGestator") return true;
            MechWeightClassDef weight = mech.RaceProps.mechWeightClass;
            return weight == MechWeightClassDefOf.Light || weight == MechWeightClassDefOf.Medium;
        }

        internal List<Building> AvailableGestators(Faction faction) =>
            map.listerBuildings.allBuildingsNonColonist
                .Where(building => building.Spawned && !building.Destroyed &&
                    building.Faction == faction && IsGestatorDef(building.def) &&
                    !IsBusy(building) && IsPowered(building))
                .ToList();

        internal bool LoadCorpse(Building gestator, Corpse corpse)
        {
            if (corpse == null || corpse.Destroyed || corpse.InnerPawn == null ||
                !CanGestatorAccept(gestator, corpse.InnerPawn))
                return false;

            int totalTicks = IncubationTicksFor(corpse.InnerPawn);
            var task = new PodTask(map)
            {
                Gestator = gestator,
                LastPos = gestator.InteractionCell.InBounds(map) ? gestator.InteractionCell : gestator.Position,
                TicksRemaining = totalTicks,
                TotalTicks = totalTicks
            };
            if (corpse.Spawned) corpse.DeSpawn(DestroyMode.Vanish);
            if (corpse.holdingOwner != null) corpse.holdingOwner.Remove(corpse);
            if (!task.Container.TryAdd(corpse, canMergeWithExistingStacks: false))
            {
                GenPlace.TryPlaceThing(corpse, task.LastPos, map, ThingPlaceMode.Near);
                return false;
            }
            tasks.Add(task);
            return true;
        }

        internal void CancelAll()
        {
            for (int i = tasks.Count - 1; i >= 0; i--)
            {
                EjectCorpse(tasks[i]);
                tasks.RemoveAt(i);
            }
        }

        public override void MapComponentTick()
        {
            if (tasks.Count == 0 || Find.TickManager.TicksGame % TickInterval != 0) return;
            if (map.Parent is not FortressSite { Resolved: false })
            {
                CancelAll();
                return;
            }

            for (int i = tasks.Count - 1; i >= 0; i--)
            {
                PodTask task = tasks[i];
                if (task.Corpse == null)
                {
                    tasks.RemoveAt(i);
                    continue;
                }
                if (task.Gestator is not { Spawned: true, Destroyed: false })
                {
                    EjectCorpse(task);
                    tasks.RemoveAt(i);
                    continue;
                }
                task.LastPos = task.Gestator.InteractionCell.InBounds(map)
                    ? task.Gestator.InteractionCell : task.Gestator.Position;

                if (!IsPowered(task.Gestator)) continue;

                task.TicksRemaining -= TickInterval;
                if (task.TicksRemaining > 0) continue;

                CompleteResurrection(task);
                tasks.RemoveAt(i);
            }
        }

        private void CompleteResurrection(PodTask task)
        {
            Corpse corpse = task.Corpse;
            Building gestator = task.Gestator;
            if (corpse == null || corpse.InnerPawn == null) return;

            IntVec3 spawnCell = gestator.InteractionCell.InBounds(map) && gestator.InteractionCell.Standable(map)
                ? gestator.InteractionCell : task.LastPos;
            task.Container.TryDrop(corpse, spawnCell, map, ThingPlaceMode.Near, out Thing dropped);
            if (dropped is not Corpse placed || placed.InnerPawn == null) return;

            Pawn mech = placed.InnerPawn;
            Faction faction = gestator.Faction;
            if (!ResurrectionUtility.TryResurrect(mech)) return;

            if (mech.Faction != faction && faction != null) mech.SetFactionDirect(faction);
            mech.mindState?.mentalStateHandler?.Reset();
            Lord defenseLord = map.lordManager.lords.FirstOrDefault(lord =>
                lord.faction == faction && lord.LordJob is LordJob_MechFortressDefense { IsDefenseActive: true });
            if (defenseLord != null && mech.GetLord() != defenseLord)
                defenseLord.AddPawn(mech);
        }

        private void EjectCorpse(PodTask task)
        {
            Corpse corpse = task.Corpse;
            if (corpse == null) return;
            IntVec3 dropCell = task.LastPos.IsValid && task.LastPos.InBounds(map) ? task.LastPos : map.Center;
            task.Container.TryDrop(corpse, dropCell, map, ThingPlaceMode.Near, out _);
        }

        internal string InspectStringFor(Building building)
        {
            PodTask task = tasks.FirstOrDefault(item => item.Gestator == building);
            if (task?.Corpse?.InnerPawn == null) return null;
            float progress = task.TotalTicks > 0
                ? Math.Clamp(1f - (float)task.TicksRemaining / task.TotalTicks, 0f, 1f) : 1f;
            string status = IsPowered(building)
                ? task.TicksRemaining.ToStringTicksToPeriod()
                : "NoPower".Translate().Resolve();
            return $"Resurrecting {task.Corpse.InnerPawn.LabelShortCap}: {progress:P0} ({status})";
        }

        public override void ExposeData()
        {
            if (Scribe.mode != LoadSaveMode.Saving || tasks.Count > 0)
                Scribe_Collections.Look(ref tasks, "mechResurrectionTasks", LookMode.Deep, map);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
                tasks ??= new List<PodTask>();
        }

        private sealed class PodTask : IExposable, IThingHolder
        {
            private Map map;
            internal Building Gestator;
            internal IntVec3 LastPos = IntVec3.Invalid;
            internal int TicksRemaining;
            internal int TotalTicks;
            internal ThingOwner<Thing> Container;

            public PodTask() => Container = new ThingOwner<Thing>(this, oneStackOnly: true);

            public PodTask(Map map)
            {
                this.map = map;
                Container = new ThingOwner<Thing>(this, oneStackOnly: true);
            }

            internal Corpse Corpse => Container.Count > 0 ? Container[0] as Corpse : null;

            public IThingHolder ParentHolder => map;

            public void GetChildHolders(List<IThingHolder> outChildren) =>
                ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, GetDirectlyHeldThings());

            public ThingOwner GetDirectlyHeldThings() => Container;

            public void ExposeData()
            {
                Scribe_References.Look(ref Gestator, "gestator");
                Scribe_Values.Look(ref LastPos, "lastPos", IntVec3.Invalid);
                Scribe_Values.Look(ref TicksRemaining, "ticksRemaining");
                Scribe_Values.Look(ref TotalTicks, "totalTicks");
                Scribe_Deep.Look(ref Container, "container", this);
                if (Scribe.mode == LoadSaveMode.PostLoadInit)
                    Container ??= new ThingOwner<Thing>(this, oneStackOnly: true);
            }
        }
    }

    [HarmonyPatch(typeof(Building_MechGestator), "GetInspectStringExtra")]
    internal static class FortressGestatorInspectPatch
    {
        private static void Postfix(Building_MechGestator __instance, ref string __result)
        {
            if (!FortressMechResurrection.IsGestatorDef(__instance.def) ||
                __instance.Map?.Parent is not FortressSite) return;
            string line = __instance.Map.GetComponent<FortressMechResurrection>()?.InspectStringFor(__instance);
            if (line.NullOrEmpty()) return;
            __result = __result.NullOrEmpty() ? line : __result + "\n" + line;
        }
    }

    public sealed class JobGiver_MechFortressResurrect : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            PawnDuty duty = pawn.mindState?.duty;
            if (duty?.tag != FortressMechResurrection.DutyTag ||
                pawn.GetLord()?.LordJob is not LordJob_MechFortressDefense { IsDefenseActive: true } ||
                duty.focusSecond.Thing is not Corpse { Spawned: true } corpse ||
                duty.focusThird.Thing is not Building { Spawned: true } gestator)
                return null;

            FortressMechResurrection resurrection = pawn.Map.GetComponent<FortressMechResurrection>();
            if (resurrection == null || !resurrection.CanGestatorAccept(gestator, corpse.InnerPawn))
                return null;
            if (!pawn.CanReserveAndReach(corpse, PathEndMode.ClosestTouch, Danger.Deadly) ||
                !pawn.CanReserveAndReach(gestator, PathEndMode.InteractionCell, Danger.Deadly))
                return null;

            Job job = JobMaker.MakeJob(FortressMechJobDefOf.RS_MechFortressLoadGestator, corpse, gestator);
            job.count = 1;
            return job;
        }
    }

    public sealed class JobDriver_MechFortressLoadGestator : JobDriver
    {
        private Corpse TargetCorpse => job.targetA.Thing as Corpse;
        private Building TargetGestator => job.targetB.Thing as Building;

        public override bool TryMakePreToilReservations(bool errorOnFailed) =>
            pawn.Reserve(job.targetA, job, errorOnFailed: errorOnFailed) &&
            pawn.Reserve(job.targetB, job, errorOnFailed: errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDestroyedNullOrForbidden(TargetIndex.A);
            this.FailOnDestroyedNullOrForbidden(TargetIndex.B);
            this.FailOn(() => TargetCorpse?.InnerPawn == null || TargetGestator == null ||
                Map.GetComponent<FortressMechResurrection>()?.CanGestatorAccept(TargetGestator, TargetCorpse.InnerPawn) != true);

            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch);
            yield return Toils_Haul.StartCarryThing(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.InteractionCell);
            yield return Toils_General.Wait(60).WithProgressBarToilDelay(TargetIndex.B);
            yield return new Toil
            {
                initAction = () =>
                {
                    Corpse corpse = pawn.carryTracker?.CarriedThing as Corpse ?? TargetCorpse;
                    if (corpse == null || TargetGestator == null) return;
                    Map.GetComponent<FortressMechResurrection>()?.LoadCorpse(TargetGestator, corpse);
                },
                defaultCompleteMode = ToilCompleteMode.Instant
            };
        }
    }
}
