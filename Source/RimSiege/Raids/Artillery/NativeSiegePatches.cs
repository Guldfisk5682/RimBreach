using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using CombatExtended;
using CombatExtended.Compatibility;
using HarmonyLib;
using RimSiege.Integration;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimSiege.Raids.Artillery
{
    internal static class NativeSiege
    {
        internal sealed class InitScope
        {
            internal ArtilleryRaid Raid;
            internal InitScope Previous;
            internal bool Failed;
        }

        [ThreadStatic] internal static InitScope Initializing;
        [ThreadStatic] internal static bool SupplementalDrop;
        private static readonly Action<LordToil_Siege, Pawn> SetBuilder =
            AccessTools.MethodDelegate<Action<LordToil_Siege, Pawn>>(AccessTools.Method(typeof(LordToil_Siege), "SetAsBuilder"));
        private static readonly Action<LordToil_Siege, Pawn> SetDefender =
            AccessTools.MethodDelegate<Action<LordToil_Siege, Pawn>>(AccessTools.Method(typeof(LordToil_Siege), "SetAsDefender"));
        private static readonly Func<LordToil_Siege, Pawn, bool> Builder =
            AccessTools.MethodDelegate<Func<LordToil_Siege, Pawn, bool>>(AccessTools.Method(typeof(LordToil_Siege), "CanBeBuilder"));
        private static readonly Action<LordToil_Siege, ThingDef, int> DropSupplies =
            AccessTools.MethodDelegate<Action<LordToil_Siege, ThingDef, int>>(AccessTools.Method(typeof(LordToil_Siege), "DropSupplies"));
        private static readonly AccessTools.FieldRef<LordToil, LordToilData> ToilData =
            AccessTools.FieldRefAccess<LordToil, LordToilData>("data");

        internal static ArtilleryRaid Raid(Lord lord) => lord?.Map?.GetComponent<ArtilleryMap>().ForLord(lord);
        internal static ArtilleryRaid Raid(LordToil_Siege toil) => Raid(toil?.lord);
        internal static LordToilData_Siege Data(LordToil_Siege toil) => (LordToilData_Siege)ToilData(toil);
        internal static void AssignDefenderDuty(LordToil_Siege toil, Pawn pawn) => SetDefender(toil, pawn);
        internal static void AssignBuilderDuty(LordToil_Siege toil, Pawn pawn)
        {
            if (!((LordJob_Artillery)toil.lord.LordJob).AssignBuilder(toil, pawn)) SetBuilder(toil, pawn);
        }
        internal static bool CanBuild(LordToil_Siege toil, Pawn pawn) =>
            ((LordJob_Artillery)toil.lord.LordJob).CanBeBuilder(pawn, Builder(toil, pawn));

        // The original Init owns the enumeration and its material/ammo drop list.
        internal static IEnumerable<Blueprint_Build> Blueprints(IntVec3 center, Map map, Faction faction, float points)
        {
            InitScope scope = Initializing;
            if (scope == null) return SiegeBlueprintPlacer.PlaceBlueprints(center, map, faction, points);
            return ArtilleryCamp.Blueprints(scope.Raid, scope.Raid.CampLord);
        }

        internal static ThingDef Shell(ThingDef turret, bool allowEMP, bool allowToxGas, bool mustHarmHealth,
            TechLevel techLevel, bool allowAntigrainWarhead, float maxMarketValue, Faction faction)
        {
            InitScope scope = Initializing;
            if (scope == null)
                return TurretGunUtility.TryFindRandomShellDef(turret, allowEMP, allowToxGas, mustHarmHealth,
                    techLevel, allowAntigrainWarhead, maxMarketValue, faction);
            // Construction pods contain materials only. The native ammo
            // supplement starts once a planned emplacement has been built.
            return null;
        }

        internal static void InitialSupplies(IntVec3 center, Map map, List<List<Thing>> groups, int openDelay,
            bool instaDrop, bool leaveSlag, bool canRoofPunch, bool forbid, bool allowFogged, bool canTransfer, Faction faction)
        {
            InitScope scope = Initializing;
            if (scope == null)
            {
                DropPodUtility.DropThingGroupsNear(center, map, groups, openDelay, instaDrop, leaveSlag,
                    canRoofPunch, forbid, allowFogged, canTransfer, faction);
                return;
            }
            AdjustInitialMeals(scope.Raid, groups);
            if (CampSupplies.DropGroups(map, scope.Raid, groups, openDelay, instaDrop, leaveSlag,
                canRoofPunch, forbid, allowFogged, faction)) return;
            scope.Failed = true;
            CampSupplies.Discard(groups);
        }

        private static void AdjustInitialMeals(ArtilleryRaid raid, List<List<Thing>> groups)
        {
            ThingDef mealDef = ThingDefOf.MealSurvivalPack;
            int total = 0;
            foreach (List<Thing> group in groups)
                for (int i = group.Count - 1; i >= 0; i--)
                {
                    Thing thing = group[i];
                    if (thing.def != mealDef) continue;
                    total += thing.stackCount;
                    group.RemoveAt(i);
                    thing.Destroy(DestroyMode.Vanish);
                }
            groups.RemoveAll(group => group.Count == 0);
            if (raid.Kind == ArtilleryRaidKind.Mechanoid) return;

            int count = Math.Max(10, (total + 9) / 10);
            var meals = new List<Thing>();
            while (count > 0)
            {
                Thing meal = ThingMaker.MakeThing(mealDef);
                meal.stackCount = Math.Min(count, mealDef.stackLimit);
                meals.Add(meal);
                count -= meal.stackCount;
            }
            groups.Add(meals);
        }

        internal static void LaterSupplies(IntVec3 center, Map map, IEnumerable<Thing> things, int openDelay,
            bool instaDrop, bool leaveSlag, bool canRoofPunch, bool forbid, bool allowFogged, Faction faction,
            LordToil_Siege toil)
        {
            ArtilleryRaid raid = Raid(toil);
            if (raid == null)
            {
                DropPodUtility.DropThingsNear(center, map, things, openDelay, instaDrop, leaveSlag,
                    canRoofPunch, forbid, allowFogged, faction);
                return;
            }
            var groups = things.Where(thing =>
            {
                if (!raid.Camp.Guns.Any(gun => gun.Ammo == thing.def) || HasCompletedGun(raid)) return true;
                thing.Destroy(DestroyMode.Vanish);
                return false;
            }).Select(thing => new List<Thing> { thing }).ToList();
            if (!CampSupplies.DropGroups(map, raid, groups, openDelay, instaDrop, leaveSlag,
                canRoofPunch, forbid, allowFogged, faction)) CampSupplies.Discard(groups);
        }

        internal static void Memo(Lord lord, string memo, LordToil_Siege toil)
        {
            ArtilleryRaid raid = Raid(toil);
            if (raid != null)
            {
                if (raid.InfantryHold is { } hold && (memo == "NoBuilders" || memo == "NoArtillery"))
                {
                    hold.ReviewRequested = true;
                    return;
                }
                if (raid.WaitingForFirstGun && (memo == "NoBuilders" || memo == "NoArtillery")) return;
                if (memo == "NoBuilders" && (ArtilleryCrew.HasArrivingBuilder(raid, lord) ||
                    ArtilleryCrew.ReplaceBuilder(raid, toil))) return;
                if (memo == "NoArtillery" && raid.Camp.Guns.Any(g => g.IsSupport && g.Building != null && g.Building.Spawned &&
                    g.Building.Faction == raid.Faction)) return;
                if (memo == "NoBuilders" && raid.Camp.Guns.Any(g => g.IsSupport &&
                    g.Building?.Spawned == true && g.Building.Faction == raid.Faction))
                {
                    // Preserve completed guns and their operators while failed
                    // construction is handed off on the lord's next safe update.
                    if (ArtilleryCrew.HasConstruction(raid) || !raid.InfantryReleased)
                        ((LordJob_Artillery)lord.LordJob).RequestInfantryAssault();
                    return;
                }
            }
            lord.ReceiveMemo(memo);
        }

        internal static void AssignGunners(LordToil_Siege toil)
        {
            ArtilleryRaid raid = Raid(toil);
            if (raid == null || !((LordJob_Artillery)toil.lord.LordJob).RequiresOperators ||
                (raid.Phase != ArtilleryPhase.Building && raid.Phase != ArtilleryPhase.Supporting) ||
                !raid.InfantryReleased && !toil.rememberedDuties.Values.Contains(DutyDefOf.Build)) return;
            foreach (ArtilleryPlacement slot in raid.Camp.Guns)
            {
                Pawn pawn = slot.Operator;
                if (pawn == null || !toil.lord.ownedPawns.Contains(pawn)) continue;
                AssignGunnerDuty(toil, pawn);
            }
        }

        internal static void AssignGunnerDuty(LordToil_Siege toil, Pawn pawn)
        {
            if (!pawn.WorkTypeIsDisabled(WorkTypeDefOf.Construction)) AssignBuilderDuty(toil, pawn);
            else SetManningDuty(toil, pawn);
        }

        internal static void SetManningDuty(LordToil_Siege toil, Pawn pawn)
        {
            SetDefender(toil, pawn);
            float radius = pawn.mindState.duty.radius;
            pawn.mindState.duty = new PawnDuty(DutyDefOf.Build, toil.FlagLoc) { radius = radius };
            pawn.workSettings.EnableAndInitialize();
        }

        internal static bool HasCompletedGun(ArtilleryRaid raid) =>
            raid.Camp.Guns.Any(gun => gun.Building is { Spawned: true } building && building.Faction == raid.Faction);

        // CE's common shell count cannot detect a missing calibre when another
        // calibre has stock. Only the managed shell supplement is per ammo type.
        internal static void Supplement(LordToil_Siege toil, ArtilleryRaid raid)
        {
            var defs = raid.Camp.Guns.Where(g => g.Building is Building_TurretGunCE turret && turret.Spawned &&
                    turret.Faction == raid.Faction && turret.GetAmmo()?.UseAmmo == true && g.Ammo != null).Select(g => g.Ammo).Distinct().ToList();
            if (defs.Count == 0) return;
            var counts = defs.ToDictionary(def => def, _ => 0);
            Map map = toil.lord.Map;
            IntVec3 center = raid.Camp.Center;
            bool floors = IsoFloor.HasFloors(map);
            int floor = floors ? IsoFloor.FloorOf(center, map) : 0;
            int cells = GenRadial.NumCellsInRadius(20f);
            for (int i = 0; i < cells; i++)
            {
                IntVec3 cell = center + GenRadial.RadialPattern[i];
                if (!cell.InBounds(map) || (floors && IsoFloor.FloorOf(cell, map) != floor)) continue;
                foreach (Thing thing in cell.GetThingList(map))
                    if (counts.ContainsKey(thing.def)) counts[thing.def] += thing.stackCount;
            }
            foreach (ThingDef def in defs)
                if (counts[def] < 4) DropSupplies(toil, def, 6);
        }
    }

    [HarmonyPatch(typeof(LordToil_Siege), nameof(LordToil_Siege.Init))]
    internal static class NativeSiegeInitPatch
    {
        private static bool Prefix(LordToil_Siege __instance, out NativeSiege.InitScope __state)
        {
            __state = null;
            ArtilleryRaid raid = NativeSiege.Raid(__instance);
            if (raid == null) return true;
            if (raid.SuppliesDelivered) return false;
            if (!ArtilleryCamp.Prepare(raid, __instance.lord))
            {
                raid.Phase = ArtilleryPhase.Assaulting;
                return false;
            }
            __state = new NativeSiege.InitScope { Raid = raid, Previous = NativeSiege.Initializing };
            NativeSiege.Initializing = __state;
            return true;
        }

        private static void Postfix(NativeSiege.InitScope __state)
        {
            if (__state == null) return;
            if (__state.Failed)
            {
                __state.Raid.Phase = ArtilleryPhase.Assaulting;
                return;
            }
            __state.Raid.SuppliesDelivered = true;
            __state.Raid.Phase = ArtilleryPhase.Building;
        }

        private static void Finalizer(NativeSiege.InitScope __state)
        {
            if (__state != null) NativeSiege.Initializing = __state.Previous;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo blueprints = AccessTools.Method(typeof(SiegeBlueprintPlacer), nameof(SiegeBlueprintPlacer.PlaceBlueprints));
            MethodInfo shell = AccessTools.Method(typeof(TurretGunUtility), nameof(TurretGunUtility.TryFindRandomShellDef));
            MethodInfo supplies = AccessTools.Method(typeof(DropPodUtility), nameof(DropPodUtility.DropThingGroupsNear));
            foreach (CodeInstruction code in instructions)
            {
                if (code.Calls(blueprints)) code.operand = AccessTools.Method(typeof(NativeSiege), nameof(NativeSiege.Blueprints));
                else if (code.Calls(shell)) code.operand = AccessTools.Method(typeof(NativeSiege), nameof(NativeSiege.Shell));
                else if (code.Calls(supplies)) code.operand = AccessTools.Method(typeof(NativeSiege), nameof(NativeSiege.InitialSupplies));
                yield return code;
            }
        }
    }

    [HarmonyPatch(typeof(LordToil_Siege), "DropSupplies")]
    internal static class NativeSiegeDropSuppliesPatch
    {
        private static bool Prefix(LordToil_Siege __instance, ThingDef thingDef) =>
            thingDef != ThingDefOf.MealSurvivalPack || NativeSiege.Raid(__instance)?.Kind != ArtilleryRaidKind.Mechanoid;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo supplies = AccessTools.Method(typeof(DropPodUtility), nameof(DropPodUtility.DropThingsNear));
            foreach (CodeInstruction code in instructions)
            {
                if (code.Calls(supplies))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(code).MoveBlocksFrom(code);
                    code.operand = AccessTools.Method(typeof(NativeSiege), nameof(NativeSiege.LaterSupplies));
                }
                yield return code;
            }
        }
    }

    [HarmonyPatch(typeof(LordToil_Siege), "SetAsBuilder")]
    internal static class NativeSiegeBuilderPatch
    {
        private static bool Prefix(LordToil_Siege __instance, Pawn p)
        {
            ArtilleryRaid raid = NativeSiege.Raid(__instance);
            if (__instance.lord.LordJob is LordJob_Artillery job && job.AssignBuilder(__instance, p)) return false;
            if (raid != null && (SiegeRoles.IsMainForceOnly(p, __instance.lord.LordJob is LordJob_MechArtillery) ||
                p.skills == null || p.workSettings == null))
            {
                SiegeRoles.AssignMainForceEscortDuty(__instance.lord, p, __instance.FlagLoc, raid);
                __instance.rememberedDuties.Remove(p);
                return false;
            }
            if (raid == null || !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) ||
                !raid.Camp.Guns.Any(g => g.Operator == p)) return true;
            NativeSiege.SetManningDuty(__instance, p);
            return false;
        }

        private static void Postfix(LordToil_Siege __instance, Pawn p)
        {
            if (__instance.lord.LordJob is LordJob_Artillery job && !job.RequiresOperators) return;
            ArtilleryRaid raid = NativeSiege.Raid(__instance);
            if (raid == null || p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) || p.skills == null) return;
            int level = ArtilleryCamp.Placements(raid.Camp).Select(item => item.Def.constructionSkillPrerequisite).DefaultIfEmpty(0).Max();
            p.skills.GetSkill(SkillDefOf.Construction).EnsureMinLevelWithMargin(level);
        }
    }

    // Native eligibility alone does not distinguish hostile combat mechs from builders.
    [HarmonyPatch(typeof(LordToil_Siege), "CanBeBuilder")]
    internal static class NativeSiegeCanBeBuilderPatch
    {
        private static void Postfix(LordToil_Siege __instance, Pawn p, ref bool __result)
        {
            if (__instance.lord.LordJob is LordJob_Artillery job)
                __result = job.CanBeBuilder(p, __result);
        }
    }

    [HarmonyPatch]
    internal static class NativeSiegeMemoPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(LordToil_Siege), nameof(LordToil_Siege.UpdateAllDuties));
            yield return AccessTools.Method(typeof(LordToil_Siege), nameof(LordToil_Siege.LordToilTick));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo memo = AccessTools.Method(typeof(Lord), nameof(Lord.ReceiveMemo));
            foreach (CodeInstruction code in instructions)
            {
                if (code.Calls(memo))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(code).MoveBlocksFrom(code);
                    code.opcode = OpCodes.Call;
                    code.operand = AccessTools.Method(typeof(NativeSiege), nameof(NativeSiege.Memo));
                }
                yield return code;
            }
        }
    }

    [HarmonyPatch(typeof(LordToil_Siege), nameof(LordToil_Siege.UpdateAllDuties))]
    internal static class NativeSiegeDutiesPatch
    {
        private static void Postfix(LordToil_Siege __instance)
        {
            ArtilleryRaid raid = NativeSiege.Raid(__instance);
            if (raid?.InfantryHold is { } hold)
            {
                hold.ApplyDuties(raid, __instance);
                return;
            }
            if (raid != null) ArtilleryCrew.RestoreBuilders(raid, __instance);
            NativeSiege.AssignGunners(__instance);
            raid?.OptionalSmoke?.ApplyOptionalDuties(__instance.lord);
            if (__instance.lord.LordJob is LordJob_Artillery { RequiresOperators: true })
            {
                if (raid != null && raid.InfantryReleased && raid.AssaultLord != null &&
                    __instance.lord.Map.lordManager.lords.Contains(raid.AssaultLord))
                {
                    var mainForceOnly = __instance.lord.ownedPawns.Where(p => SiegeRoles.IsMainForceOnly(p)).ToList();
                    if (mainForceOnly.Count > 0)
                        ArtilleryCamp.TransferToAssault(raid, __instance.lord, mainForceOnly);
                }
                foreach (Pawn pawn in __instance.lord.ownedPawns)
                {
                    if (!SiegeRoles.IsMainForceOnly(pawn)) continue;
                    SiegeRoles.AssignMainForceEscortDuty(__instance.lord, pawn, __instance.FlagLoc, raid);
                    __instance.rememberedDuties.Remove(pawn);
                }
            }
            // Remembered native Build duties are counted without rechecking
            // eligibility. The last constructoid can be downed or disabled.
            if (__instance.lord.CurLordToil == __instance &&
                __instance.lord.LordJob is LordJob_Artillery job && !job.RequiresOperators && job.ConstructionWorker == null)
                NativeSiege.Memo(__instance.lord, "NoBuilders", __instance);
        }
    }

    [HarmonyPatch(typeof(SiegeUtility), "UniqueArtilleryDefs")]
    internal static class NativeSiegeCeArtilleryPatch
    {
        private static void Postfix(LordToil_Siege siege, ref IEnumerable<ThingDef> __result)
        {
            ArtilleryRaid raid = NativeSiege.Raid(siege);
            if (raid == null) return;
            __result = __result.Concat(raid.Camp.Guns.Where(g => g.Building != null && g.Building.Spawned &&
                g.Building.Faction == raid.Faction).Select(g => g.Building.def)).Distinct();
        }
    }

    [HarmonyPatch(typeof(SiegeUtility), nameof(SiegeUtility.DropAdditionalShells))]
    internal static class NativeSiegeCeSuppliesPatch
    {
        private static bool Prefix(LordToil_Siege siege)
        {
            ArtilleryRaid raid = NativeSiege.Raid(siege);
            if (raid == null) return true;
            NativeSiege.Supplement(siege, raid);
            NativeSiege.SupplementalDrop = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(LordToil_Siege), nameof(LordToil_Siege.LordToilTick))]
    internal static class NativeSiegeTickPatch
    {
        private static void Prefix(out bool __state)
        {
            __state = NativeSiege.SupplementalDrop;
            NativeSiege.SupplementalDrop = false;
        }

        private static void Postfix(LordToil_Siege __instance)
        {
            ArtilleryRaid raid = NativeSiege.Raid(__instance);
            if (raid == null || __instance.lord.CurLordToil != __instance || !raid.SuppliesDelivered ||
                raid.Phase == ArtilleryPhase.Abandoned || Find.TickManager.TicksGame % 500 != 0) return;
            ArtilleryCamp.RefreshConstruction(raid, __instance.lord.Map);
            if (!raid.Camp.Guns.Any(g => g.IsSupport))
            {
                NativeSiege.Memo(__instance.lord, "NoArtillery", __instance);
                return;
            }
            ArtilleryCamp.RefreshOperators(raid, __instance.lord);
            if (__instance.lord.CurLordToil != __instance) return;
            SupportRoute.Refresh(__instance.lord.Map, raid);
            if (raid.InfantryHold is { } hold)
            {
                hold.ReviewRequested = true;
                __instance.UpdateAllDuties();
                return;
            }
            if (raid.Phase == ArtilleryPhase.Supporting || raid.InfantryReleased)
            {
                if (raid.InfantryReleased) __instance.UpdateAllDuties();
                if (__instance.lord.CurLordToil != __instance) return;
            }
            if (raid.Phase == ArtilleryPhase.Building && ArtilleryCrew.SupportReady(raid))
            {
                raid.Phase = ArtilleryPhase.Supporting;
                if (raid.Kind != ArtilleryRaidKind.Siege) ArtilleryCamp.ReleaseInfantry(raid, __instance.lord);
            }
            else ArtilleryDeployment.ReviewInfantryRelease(raid, __instance.lord);
            if (__instance.lord.CurLordToil != __instance) return;
            ArtilleryCrew.Update(raid, __instance.lord);
            if (__instance.lord.ownedPawns.Count == 0 || __instance.lord.CurLordToil != __instance) return;
            if (!NativeSiege.SupplementalDrop) NativeSiege.Supplement(__instance, raid);
        }

        private static void Finalizer(bool __state) => NativeSiege.SupplementalDrop = __state;
    }

    [HarmonyPatch(typeof(LordToil_Siege), nameof(LordToil_Siege.Cleanup))]
    internal static class NativeSiegeCleanupPatch
    {
        private static void Prefix(LordToil_Siege __instance)
        {
            if (__instance.lord.LordJob is LordJob_Artillery job) job.PrepareCleanup(__instance);
        }

        private static void Postfix(LordToil_Siege __instance)
        {
            ArtilleryRaid raid = NativeSiege.Raid(__instance);
            if (raid == null) return;
            Map map = __instance.lord.Map;
            ArtilleryCamp.RetainHostileBattery(raid, map);
            ArtilleryCamp.Abandon(raid, map);
        }
    }

    [HarmonyPatch(typeof(Lord), nameof(Lord.Notify_BuildingSpawnedOnMap))]
    internal static class NativeSiegeBuildingSpawnedPatch
    {
        private static void Postfix(Lord __instance, Building b)
        {
            if (__instance.CurLordToil is not LordToil_Siege) return;
            ArtilleryRaid raid = NativeSiege.Raid(__instance);
            if (raid == null) return;
            if (ArtilleryCamp.ObserveBuilding(raid, b) && raid.Camp.Guns.Find(gun => gun.Building == b) is { } gun &&
                __instance.LordJob is LordJob_Artillery job)
                ArtilleryDeployment.NotifyGunCompleted(raid, gun, job);
        }
    }

    [HarmonyPatch(typeof(Lord), nameof(Lord.Notify_ConstructionFailed))]
    internal static class NativeSiegeConstructionFailedPatch
    {
        private static void Postfix(Lord __instance, Frame frame, Blueprint_Build newBlueprint)
        {
            if (__instance.CurLordToil is not LordToil_Siege) return;
            ArtilleryRaid raid = NativeSiege.Raid(__instance);
            if (raid != null) ArtilleryCamp.ObserveFailure(raid, frame, newBlueprint);
        }
    }
}
