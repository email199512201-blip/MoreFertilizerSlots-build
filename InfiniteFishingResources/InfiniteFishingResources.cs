using System;
using System.Collections;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace InfiniteFishingResources
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class InfiniteFishingResourcesPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.gk2.infinitefishingresources";
        public const string PluginName = "Infinite Fishing Resources";
        public const string PluginVersion = "1.0.0";

        internal static ConfigEntry<bool> InfiniteFishResources;
        internal static ConfigEntry<bool> RestoreDepletedReservoirs;

        private void Awake()
        {
            InfiniteFishResources = Config.Bind(
                "General",
                "InfiniteFishResources",
                true,
                "Successful fishing does not reduce the reservoir's fish stock.");

            RestoreDepletedReservoirs = Config.Bind(
                "General",
                "RestoreDepletedReservoirs",
                true,
                "When interacting with a fishing reservoir, restore any depleted fish species to its vanilla base count.");

            new Harmony(PluginGuid).PatchAll(Assembly.GetExecutingAssembly());

            Logger.LogInfo(
                PluginName + " " + PluginVersion +
                " loaded. InfiniteFishResources=" + InfiniteFishResources.Value +
                ", RestoreDepletedReservoirs=" + RestoreDepletedReservoirs.Value);
        }
    }

    internal static class FishingReflection
    {
        private static readonly Type FishingDefType = AccessTools.TypeByName("FishingDef");
        private static readonly FieldInfo MiniGameReservoirField =
            AccessTools.Field(AccessTools.TypeByName("FishingMiniGame"), "reservoir");
        private static readonly FieldInfo MiniGameFishingDefField =
            AccessTools.Field(AccessTools.TypeByName("FishingMiniGame"), "fishingDef");
        private static readonly FieldInfo HandlerAssignedWgoField =
            AccessTools.Field(AccessTools.TypeByName("WGOInteractionHandlerBase"), "assignedWgo");
        private static readonly FieldInfo FishIdField =
            FishingDefType == null ? null : AccessTools.Field(FishingDefType, "fishId");
        private static readonly FieldInfo BaseCountField =
            FishingDefType == null ? null : AccessTools.Field(FishingDefType, "baseCount");
        private static readonly MethodInfo GetAllForReservoirMethod =
            FishingDefType == null ? null : AccessTools.Method(
                FishingDefType, "GetAllForReservoir", new Type[] { typeof(string) });

        internal static string GetFishId(object fishingDef)
        {
            return fishingDef == null || FishIdField == null ? null : FishIdField.GetValue(fishingDef) as string;
        }

        internal static int GetBaseCount(object fishingDef)
        {
            if (fishingDef == null || BaseCountField == null) return 0;
            object value = BaseCountField.GetValue(fishingDef);
            return value is int ? (int)value : 0;
        }

        internal static object GetMiniGameReservoir(object miniGame)
        {
            return miniGame == null || MiniGameReservoirField == null ? null : MiniGameReservoirField.GetValue(miniGame);
        }

        internal static object GetMiniGameFishingDef(object miniGame)
        {
            return miniGame == null || MiniGameFishingDefField == null ? null : MiniGameFishingDefField.GetValue(miniGame);
        }

        internal static object GetAssignedWgo(object handler)
        {
            return handler == null || HandlerAssignedWgoField == null ? null : HandlerAssignedWgoField.GetValue(handler);
        }

        internal static object GetWgoData(object wgo)
        {
            if (wgo == null) return null;
            MethodInfo getter = AccessTools.Method(wgo.GetType(), "get_Data");
            return getter == null ? null : getter.Invoke(wgo, null);
        }

        internal static string GetWgoId(object wgo)
        {
            if (wgo == null) return null;
            MethodInfo getter = AccessTools.Method(wgo.GetType(), "get_Id");
            return getter == null ? null : getter.Invoke(wgo, null) as string;
        }

        internal static int GetGameResInt(object wgoData, string id)
        {
            if (wgoData == null || string.IsNullOrEmpty(id)) return 0;
            MethodInfo method = AccessTools.Method(
                wgoData.GetType(), "GetGameResInt", new Type[] { typeof(string) });
            if (method == null) return 0;
            object value = method.Invoke(wgoData, new object[] { id });
            return value is int ? (int)value : 0;
        }

        internal static void SetGameRes(object wgoData, string id, int value)
        {
            if (wgoData == null || string.IsNullOrEmpty(id)) return;
            MethodInfo method = AccessTools.Method(
                wgoData.GetType(), "SetGameRes", new Type[] { typeof(string), typeof(int) });
            if (method != null) method.Invoke(wgoData, new object[] { id, value });
        }

        internal static IList GetFishingDefsForReservoir(string reservoirId)
        {
            if (GetAllForReservoirMethod == null || string.IsNullOrEmpty(reservoirId)) return null;
            return GetAllForReservoirMethod.Invoke(null, new object[] { reservoirId }) as IList;
        }
    }

    [HarmonyPatch]
    internal static class RestoreDepletedReservoirPatch
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("ReservoirInteractionHandler");
            return type == null ? null : AccessTools.Method(type, "Interact");
        }

        private static void Prefix(object __instance)
        {
            if (InfiniteFishingResourcesPlugin.RestoreDepletedReservoirs == null ||
                !InfiniteFishingResourcesPlugin.RestoreDepletedReservoirs.Value)
                return;

            try
            {
                object wgo = FishingReflection.GetAssignedWgo(__instance);
                object data = FishingReflection.GetWgoData(wgo);
                string reservoirId = FishingReflection.GetWgoId(wgo);
                if (data == null || string.IsNullOrEmpty(reservoirId)) return;

                IList defs = FishingReflection.GetFishingDefsForReservoir(reservoirId);
                if (defs == null) return;

                for (int i = 0; i < defs.Count; i++)
                {
                    object def = defs[i];
                    string fishId = FishingReflection.GetFishId(def);
                    if (string.IsNullOrEmpty(fishId)) continue;
                    if (FishingReflection.GetGameResInt(data, fishId) > 0) continue;

                    int baseCount = FishingReflection.GetBaseCount(def);
                    if (baseCount < 1) baseCount = 1;
                    FishingReflection.SetGameRes(data, fishId, baseCount);
                }
            }
            catch
            {
                // Never block reservoir interaction if restoration fails.
            }
        }
    }

    [HarmonyPatch]
    internal static class InfiniteFishStockPatch
    {
        internal sealed class FishStockState
        {
            internal object Reservoir;
            internal string FishId;
            internal int CountBefore;
        }

        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("FishingMiniGame");
            return type == null ? null : AccessTools.Method(type, "HandleSuccess");
        }

        private static void Prefix(object __instance, ref FishStockState __state)
        {
            __state = null;
            if (InfiniteFishingResourcesPlugin.InfiniteFishResources == null ||
                !InfiniteFishingResourcesPlugin.InfiniteFishResources.Value)
                return;

            try
            {
                object reservoir = FishingReflection.GetMiniGameReservoir(__instance);
                object def = FishingReflection.GetMiniGameFishingDef(__instance);
                string fishId = FishingReflection.GetFishId(def);
                if (reservoir == null || string.IsNullOrEmpty(fishId)) return;

                __state = new FishStockState
                {
                    Reservoir = reservoir,
                    FishId = fishId,
                    CountBefore = FishingReflection.GetGameResInt(reservoir, fishId)
                };
            }
            catch
            {
                __state = null;
            }
        }

        private static void Postfix(FishStockState __state)
        {
            if (__state == null) return;

            try
            {
                int countAfter = FishingReflection.GetGameResInt(__state.Reservoir, __state.FishId);

                // Undo only the vanilla stock decrease. If another mod raised stock, keep it.
                if (countAfter < __state.CountBefore)
                    FishingReflection.SetGameRes(__state.Reservoir, __state.FishId, __state.CountBefore);
            }
            catch
            {
                // Fishing success must not fail because of this compatibility mod.
            }
        }
    }
}
