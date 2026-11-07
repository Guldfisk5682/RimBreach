using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimSiege.Raids
{
    [DefOf]
    internal static class SiegeDefOf
    {
        public static ThingDef RS_AssaultLadder = null;
        static SiegeDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(SiegeDefOf));
    }

    public sealed class LadderKit : MinifiedThing
    {
        public int RaidId;
        internal Pawn Receiver;
        internal int RetryAt;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref RaidId, "siegeRaidId");
            // Old kits lack the minified container. Fill it before ThingOwner's post-load
            // cleanup so held kits survive while keeping their original object and ID.
            if (Scribe.mode == LoadSaveMode.ResolvingCrossRefs && GetDirectlyHeldThings() == null)
            {
                LegacyContainer.Of(this) = new ThingOwner<Thing>(this, oneStackOnly: true);
                InnerThing = ThingMaker.MakeThing(SiegeDefOf.RS_AssaultLadder);
            }
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            map.GetComponent<SiegeRaidMap>().RegisterDropped(this);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Map.GetComponent<SiegeRaidMap>().DeregisterDropped(this);
            base.DeSpawn(mode);
        }
        private static class LegacyContainer
        {
            internal static readonly AccessTools.FieldRef<MinifiedThing, ThingOwner> Of =
                AccessTools.FieldRefAccess<MinifiedThing, ThingOwner>("innerContainer");
        }
    }

    // The initial folded-ladder test build saved a separate wrapper name.
    [HarmonyPatch(typeof(BackCompatibility), nameof(BackCompatibility.GetBackCompatibleType))]
    internal static class LadderKitTypeMigration
    {
        private static void Postfix(string providedClassName, ref Type __result)
        {
            if (providedClassName == "RimSiege.Raids.AssaultLadderItem") __result = typeof(LadderKit);
        }
    }

    [HarmonyPatch(typeof(BackCompatibility), nameof(BackCompatibility.BackCompatibleDefName))]
    internal static class LadderKitDefMigration
    {
        private static void Postfix(Type defType, string defName, ref string __result)
        {
            if (defType == typeof(ThingDef) && defName == "RS_AssaultLadderItem") __result = "RS_LadderKit";
        }
    }

    internal static class LadderInventory
    {
        private static readonly ConditionalWeakTable<Pawn, LadderKit> Held = new();

        internal static LadderKit Get(Pawn pawn) => Held.TryGetValue(pawn, out LadderKit kit) ? kit : null;

        internal static void Added(Pawn pawn, LadderKit kit)
        {
            if (kit.RaidId == 0) return;
            Held.Remove(pawn);
            Held.Add(pawn, kit);
        }

        internal static void Removed(Pawn pawn, LadderKit kit)
        {
            if (Get(pawn) == kit) Restore(pawn);
        }

        internal static void Restore(Pawn pawn)
        {
            Held.Remove(pawn);
            if (pawn.inventory != null)
                foreach (Thing item in pawn.inventory.innerContainer)
                    if (item is LadderKit kit && kit.RaidId != 0) { Added(pawn, kit); break; }
        }

        internal static void Drop(Pawn pawn)
        {
            if (Get(pawn) is LadderKit kit && pawn.Spawned)
            {
                pawn.inventory.innerContainer.TryDrop(kit, pawn.Position, pawn.Map, ThingPlaceMode.Near, out Thing dropped);
                dropped?.SetForbidden(false);
            }
        }

        internal static void Consume(Pawn pawn, LadderKit kit)
        {
            pawn.inventory.innerContainer.Remove(kit);
            kit.Destroy(DestroyMode.Vanish);
        }
    }
}
