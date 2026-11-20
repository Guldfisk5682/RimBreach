using System.Collections.Generic;
using System.Linq;
using RimSiege.Integration;
using RimWorld;
using Verse;

namespace RimSiege.Fortress
{
    public sealed class FortressRebuildQueue : MapComponent
    {
        private const int ChecksPerReview = 8;
        private List<RebuildOrder> orders = new();
        private int cursor;

        public FortressRebuildQueue(Map map) : base(map) { }

        internal void Add(Thing destroyed)
        {
            IntVec3 cell = destroyed.Position;
            ThingDef def = destroyed.def;
            if (orders.Any(order => order.Cell == cell && order.Def == def)) return;
            orders.Add(new RebuildOrder
            {
                Cell = cell,
                Def = def,
                Stuff = destroyed.Stuff,
                Rotation = destroyed.Rotation,
                StyleSource = destroyed.StyleSourcePrecept,
                Style = destroyed.StyleDef
            });
        }

        internal void PlacePending(LordJob_MechFortressDefense defense, Faction faction)
        {
            int checks = System.Math.Min(orders.Count, ChecksPerReview);
            for (int i = 0; i < checks; i++)
            {
                if (cursor >= orders.Count) cursor = 0;
                RebuildOrder order = orders[cursor];
                if (order.Def == null || HasReplacement(order, faction) || TryPlace(order, defense, faction))
                    orders.RemoveAt(cursor);
                else
                    cursor++;
            }
        }

        internal void CancelPendingProjects(FortressBlueprint blueprint, Faction faction)
        {
            orders.Clear();
            cursor = 0;
            var projects = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint)
                .Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame))
                .Where(thing => thing.Spawned && thing.Faction == faction && blueprint.ContainsStructure(thing.Position, map))
                .ToList();
            foreach (Thing project in projects)
                project.Destroy(DestroyMode.Cancel);
        }

        private bool HasReplacement(RebuildOrder order, Faction faction) =>
            order.Cell.GetThingList(map).Any(thing => thing.Position == order.Cell && thing.Faction == faction &&
                (thing.def == order.Def || thing.def.entityDefToBuild == order.Def));

        private bool TryPlace(RebuildOrder order, LordJob_MechFortressDefense defense, Faction faction)
        {
            if (!defense.CanMaintain(order.Cell)) return false;
            if (FortressMechMaintenance.OccupiedByHostile(
                    GenAdj.OccupiedRect(order.Cell, order.Rotation, order.Def.Size), map, faction)) return false;
            if (!NativeConstruction.CanPlaceFortressRebuild(order.Def, order.Cell, order.Rotation, map, order.Stuff))
                return false;

            GenConstruct.PlaceBlueprintForBuild(order.Def, order.Cell, map, order.Rotation, faction,
                order.Stuff, order.StyleSource, order.Style);
            return true;
        }

        public override void ExposeData()
        {
            if (Scribe.mode != LoadSaveMode.Saving || orders.Count > 0)
                Scribe_Collections.Look(ref orders, "rebuildOrders", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) orders ??= new List<RebuildOrder>();
        }

        private sealed class RebuildOrder : IExposable
        {
            internal IntVec3 Cell;
            internal ThingDef Def;
            internal ThingDef Stuff;
            internal Rot4 Rotation;
            internal Precept_ThingStyle StyleSource;
            internal ThingStyleDef Style;

            public void ExposeData()
            {
                Scribe_Values.Look(ref Cell, "cell");
                Scribe_Defs.Look(ref Def, "def");
                Scribe_Defs.Look(ref Stuff, "stuff");
                Scribe_Values.Look(ref Rotation, "rotation");
                Scribe_References.Look(ref StyleSource, "styleSource");
                Scribe_Defs.Look(ref Style, "style");
            }
        }
    }
}
