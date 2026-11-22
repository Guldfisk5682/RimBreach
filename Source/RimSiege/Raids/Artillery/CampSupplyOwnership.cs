using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;
using Verse.AI.Group;

namespace RimSiege.Raids.Artillery
{
    internal sealed class CampSupplyOwnership
    {
        private static readonly ConditionalWeakTable<Thing, CampSupplyOwnership> Owners = new();
        // Incident keys and single-camp keys have separate ID sequences.
        private Dictionary<Thing, int> supplies = new();
        private List<Thing> loadingThings;
        private List<int> loadingKeys;

        private static int Key(ArtilleryRaid raid) => raid.IncidentKey != 0 ? -raid.IncidentKey : raid.Key;

        internal static void Record(Map map, ArtilleryRaid raid, Thing thing) =>
            map.GetComponent<ArtilleryMap>().SupplyOwnership.Add(thing, Key(raid));

        private void Add(Thing thing, int key)
        {
            if (thing == null) return;
            supplies[thing] = key;
            if (!Owners.TryGetValue(thing, out CampSupplyOwnership current) || current != this)
            {
                Owners.Remove(thing);
                Owners.Add(thing, this);
            }
        }

        internal static bool IsOwnSupply(Thing thing, Pawn thief)
        {
            if (thief?.Map is not Map map || !Owners.TryGetValue(thing, out CampSupplyOwnership owner)) return false;
            ArtilleryMap artillery = map.GetComponent<ArtilleryMap>();
            if (owner != artillery.SupplyOwnership) return false;
            if (!owner.supplies.TryGetValue(thing, out int key)) return false;
            Lord lord = thief.GetLord();
            if (lord == null) return false;
            return key < 0 ? artillery.IncidentForLord(lord)?.Key == -key
                : artillery.ForLord(lord)?.Key == key;
        }

        internal IEnumerable<IntVec3> GroundPositions(ArtilleryRaid raid, Map map)
        {
            int key = Key(raid);
            foreach (var supply in supplies)
                if (supply.Value == key && supply.Key.SpawnedParentOrMe is { } holder &&
                    holder is not Pawn && holder.Map == map)
                    yield return holder.Position;
        }

        internal IEnumerable<Thing> ItemsFor(ArtilleryRaid raid)
        {
            int key = Key(raid);
            foreach (var supply in supplies)
                if (supply.Value == key) yield return supply.Key;
        }

        internal void Release(int key)
        {
            foreach (Thing thing in supplies.Where(pair => pair.Value == key).Select(pair => pair.Key).ToArray())
                Forget(thing);
        }

        private void Forget(Thing thing)
        {
            supplies.Remove(thing);
            Owners.Remove(thing);
        }

        internal void ExposeData()
        {
            Scribe_Collections.Look(ref supplies, "rimSiegeCampSupplies", LookMode.Reference, LookMode.Value,
                ref loadingThings, ref loadingKeys, logNullErrors: false);
            if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
            supplies ??= new();
            foreach (Thing stale in supplies.Keys.Where(t => t == null || t.Destroyed).ToArray())
                supplies.Remove(stale);
            foreach (Thing thing in supplies.Keys)
                Add(thing, supplies[thing]);
        }

        [HarmonyPatch(typeof(Thing), nameof(Thing.CanStackWith))]
        private static class StackOwnership
        {
            private static void Postfix(Thing __instance, Thing other, ref bool __result)
            {
                if (!__result) return;
                Owners.TryGetValue(__instance, out CampSupplyOwnership first);
                Owners.TryGetValue(other, out CampSupplyOwnership second);
                if (first != second) { __result = false; return; }
                if (first == null) return;
                __result = first.supplies.TryGetValue(__instance, out int firstKey) &&
                    first.supplies.TryGetValue(other, out int secondKey) && firstKey == secondKey;
            }
        }

        [HarmonyPatch(typeof(Thing), nameof(Thing.SplitOff))]
        private static class SplitOwnership
        {
            private static void Postfix(Thing __instance, Thing __result)
            {
                if (__result != __instance && Owners.TryGetValue(__instance, out CampSupplyOwnership owner))
                    owner.Add(__result, owner.supplies[__instance]);
            }
        }

        [HarmonyPatch(typeof(Thing), nameof(Thing.Destroy))]
        private static class DestroyedSupply
        {
            private static void Postfix(Thing __instance)
            {
                if (__instance.Destroyed && Owners.TryGetValue(__instance, out CampSupplyOwnership owner))
                    owner.Forget(__instance);
            }
        }
    }
}
