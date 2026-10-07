using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace GardenWorkerSafetyFix
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class GardenWorkerSafetyFixPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.gk2.gardenworkersafetyfix";
        public const string PluginName = "Garden Worker Safety Fix";
        public const string PluginVersion = "1.0.0";

        private ConfigEntry<bool> _autoRepairOnLoad;
        private ConfigEntry<bool> _runtimeWatchdog;
        private ConfigEntry<float> _watchdogInterval;
        private ConfigEntry<int> _loopThreshold;
        private ConfigEntry<float> _loopWindow;

        private float _nextScan;
        private bool _initialScanDone;
        private float _worldReadySince = -1f;
        private readonly Dictionary<string, GardenerWatchState> _watch = new Dictionary<string, GardenerWatchState>();

        private sealed class GardenerWatchState
        {
            public string LastState = "";
            public string LastOrder = "";
            public float SuspiciousSince = -1f;
            public float WindowStarted = -1f;
            public int ReturnCount;
        }

        private void Awake()
        {
            _autoRepairOnLoad = Config.Bind("General", "AutoRepairOnLoad", true,
                "Repair clearly inconsistent gardener states shortly after loading a save.");
            _runtimeWatchdog = Config.Bind("General", "EnableRuntimeWatchdog", true,
                "Watch gardeners for stuck return-to-station loops and stale orders.");
            _watchdogInterval = Config.Bind("General", "WatchdogIntervalSeconds", 1.0f,
                new ConfigDescription("Seconds between gardener health checks.", new AcceptableValueRange<float>(0.5f, 5f)));
            _loopThreshold = Config.Bind("General", "ReturnLoopThreshold", 3,
                new ConfigDescription("Repeated return-to-station transitions before automatic repair.", new AcceptableValueRange<int>(2, 8)));
            _loopWindow = Config.Bind("General", "ReturnLoopWindowSeconds", 20f,
                new ConfigDescription("Time window used to detect repeated short return loops.", new AcceptableValueRange<float>(5f, 60f)));

            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded. Manual repair hotkey: Ctrl+Shift+G");
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.G) &&
                (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) &&
                (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
            {
                int repaired = RepairAllGardeners("manual hotkey", true);
                Logger.LogInfo("Manual gardener repair completed. Gardeners repaired: " + repaired);
            }

            object world = ReflectionGame.TryGetWorldData();
            if (world == null)
            {
                _worldReadySince = -1f;
                _initialScanDone = false;
                _watch.Clear();
                return;
            }

            if (_worldReadySince < 0f)
                _worldReadySince = Time.unscaledTime;

            if (!_initialScanDone && Time.unscaledTime - _worldReadySince >= 4f)
            {
                _initialScanDone = true;
                if (_autoRepairOnLoad.Value)
                {
                    int repaired = RepairClearlyBrokenGardeners("load repair");
                    if (repaired > 0)
                        Logger.LogWarning("AutoRepairOnLoad repaired " + repaired + " gardener(s).");
                }
            }

            if (!_runtimeWatchdog.Value || Time.unscaledTime < _nextScan)
                return;

            _nextScan = Time.unscaledTime + _watchdogInterval.Value;
            WatchGardeners();
        }

        private int RepairClearlyBrokenGardeners(string reason)
        {
            int count = 0;
            foreach (object gardener in ReflectionGame.EnumerateGardeners())
            {
                if (gardener == null) continue;

                string state = ReflectionGame.GetGardenerStateName(gardener);
                bool hasOrder = ReflectionGame.HasGardenerOrder(gardener);
                object order = ReflectionGame.GetGardenerOrder(gardener);

                // Strong evidence of a stale serialized state:
                // - gardener says it has an order, but that order no longer exists;
                // - gardener is already OnStation but still owns an executing order for several ticks.
                if (hasOrder && order == null)
                {
                    if (RepairGardener(gardener, reason + ": missing order reference", false))
                        count++;
                    continue;
                }

                if (state == "OnStation" && hasOrder)
                {
                    string key = ReflectionGame.GetObjectGuid(gardener);
                    GardenerWatchState ws = GetWatch(key);
                    if (ws.SuspiciousSince < 0f)
                        ws.SuspiciousSince = Time.unscaledTime;
                    else if (Time.unscaledTime - ws.SuspiciousSince >= 2f)
                    {
                        if (RepairGardener(gardener, reason + ": OnStation with stale executing order", false))
                        {
                            count++;
                            ws.SuspiciousSince = -1f;
                        }
                    }
                }
            }
            return count;
        }

        private void WatchGardeners()
        {
            foreach (object gardener in ReflectionGame.EnumerateGardeners())
            {
                if (gardener == null) continue;

                string key = ReflectionGame.GetObjectGuid(gardener);
                if (string.IsNullOrEmpty(key)) key = gardener.GetHashCode().ToString();

                string state = ReflectionGame.GetGardenerStateName(gardener);
                bool hasOrder = ReflectionGame.HasGardenerOrder(gardener);
                object order = ReflectionGame.GetGardenerOrder(gardener);
                string orderId = ReflectionGame.GetOrderGuid(order);

                GardenerWatchState ws = GetWatch(key);

                if (hasOrder && order == null)
                {
                    RepairGardener(gardener, "watchdog: dangling executing order", false);
                    ResetWatch(ws, state, orderId);
                    continue;
                }

                if (state == "OnStation" && hasOrder)
                {
                    if (ws.SuspiciousSince < 0f)
                        ws.SuspiciousSince = Time.unscaledTime;
                    else if (Time.unscaledTime - ws.SuspiciousSince >= 3f)
                    {
                        RepairGardener(gardener, "watchdog: OnStation while still owning an order", false);
                        ResetWatch(ws, state, "");
                        continue;
                    }
                }
                else
                {
                    ws.SuspiciousSince = -1f;
                }

                bool enteredReturnState =
                    (state == "GoToStation" || state == "OnStation") &&
                    state != ws.LastState &&
                    !string.IsNullOrEmpty(orderId) &&
                    orderId == ws.LastOrder;

                if (enteredReturnState)
                {
                    if (ws.WindowStarted < 0f || Time.unscaledTime - ws.WindowStarted > _loopWindow.Value)
                    {
                        ws.WindowStarted = Time.unscaledTime;
                        ws.ReturnCount = 1;
                    }
                    else
                    {
                        ws.ReturnCount++;
                    }

                    if (ws.ReturnCount >= _loopThreshold.Value)
                    {
                        RepairGardener(gardener, "watchdog: repeated short return-to-station loop", false);
                        ResetWatch(ws, state, "");
                        continue;
                    }
                }

                ws.LastState = state;
                ws.LastOrder = orderId;
            }
        }

        private GardenerWatchState GetWatch(string key)
        {
            GardenerWatchState ws;
            if (!_watch.TryGetValue(key ?? "", out ws))
            {
                ws = new GardenerWatchState();
                _watch[key ?? ""] = ws;
            }
            return ws;
        }

        private static void ResetWatch(GardenerWatchState ws, string state, string order)
        {
            ws.LastState = state ?? "";
            ws.LastOrder = order ?? "";
            ws.SuspiciousSince = -1f;
            ws.WindowStarted = -1f;
            ws.ReturnCount = 0;
        }

        private int RepairAllGardeners(string reason, bool aggressive)
        {
            int count = 0;
            foreach (object gardener in ReflectionGame.EnumerateGardeners())
                if (gardener != null && RepairGardener(gardener, reason, aggressive))
                    count++;
            return count;
        }

        private bool RepairGardener(object gardener, string reason, bool aggressive)
        {
            try
            {
                string gardenerId = ReflectionGame.GetObjectGuid(gardener);
                string beforeState = ReflectionGame.GetGardenerStateName(gardener);
                object order = ReflectionGame.GetGardenerOrder(gardener);
                string orderId = ReflectionGame.GetOrderGuid(order);

                // Stop movement first so an old path callback cannot immediately re-apply stale state.
                ReflectionGame.ForceStopMovement(gardener);

                // Remove active gardener work/craft activities using the game's own cleanup routines.
                ReflectionGame.StopGardenerCraftActivity(gardener);
                ReflectionGame.StopGardenerWorkActivity(gardener);

                // Clear the executing order and release its bed worker binding through vanilla cleanup.
                ReflectionGame.StopGardenerOrder(gardener);
                if (order != null)
                    ReflectionGame.RemoveOrderFromZone(gardener, order);

                // Remove any stale worker binding owned by this gardener from garden plots.
                ReflectionGame.ClearStaleGardenWorkerBindings(gardener);

                // Clear transient target/activity fields that are serialized by the game.
                ReflectionGame.ClearGardenerTransientState(gardener);

                // Re-attach logical state to the original garden station, then ask vanilla logic
                // to return there. OnStation is used only if the station object cannot be resolved.
                bool movingHome = ReflectionGame.RestoreStationAndMoveHome(gardener, aggressive);

                Logger.LogWarning(
                    "Repaired gardener " + gardenerId +
                    " reason='" + reason + "'" +
                    " oldState=" + beforeState +
                    " oldOrder=" + orderId +
                    " result=" + (movingHome ? "returning-to-station" : "reset-on-station"));
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError("Gardener repair failed: " + ex);
                return false;
            }
        }
    }

    internal static class ReflectionGame
    {
        private static readonly Type MainGameType = Access("MainGame");
        private static readonly Type ZombieTypeType = Access("ZombieType");
        private static readonly Type SGuidType = Access("SGuid");
        private static readonly Type GardenBedNavigationType = Access("GardenBedNavigation");

        private static Type Access(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = a.GetType(name, false);
                if (t != null) return t;
            }
            return null;
        }

        private static FieldInfo F(Type t, string name)
        {
            return t == null ? null : t.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        }

        private static PropertyInfo P(Type t, string name)
        {
            return t == null ? null : t.GetProperty(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        }

        private static MethodInfo M(Type t, string name, int argc = -1)
        {
            if (t == null) return null;
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                if (m.Name == name && (argc < 0 || m.GetParameters().Length == argc))
                    return m;
            return null;
        }

        private static MethodInfo MForArg(Type t, string name, object arg)
        {
            if (t == null) return null;
            Type argType = arg == null ? null : arg.GetType();
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != name) continue;
                ParameterInfo[] p = m.GetParameters();
                if (p.Length != 1) continue;
                if (arg == null || p[0].ParameterType.IsAssignableFrom(argType))
                    return m;
            }
            return null;
        }

        internal static object TryGetWorldData()
        {
            try
            {
                object main = P(MainGameType, "Instance")?.GetValue(null, null) ?? M(MainGameType, "get_Instance", 0)?.Invoke(null, null);
                if (main == null) return null;
                return P(MainGameType, "WorldData")?.GetValue(main, null) ?? M(MainGameType, "get_WorldData", 0)?.Invoke(main, null);
            }
            catch { return null; }
        }

        internal static IEnumerable<object> EnumerateGardeners()
        {
            object world = TryGetWorldData();
            if (world == null) yield break;

            IList scenes = F(world.GetType(), "gameSceneDataList")?.GetValue(world) as IList;
            if (scenes == null) yield break;

            foreach (object scene in scenes)
            {
                if (scene == null) continue;
                IList list = F(scene.GetType(), "wgoDataList")?.GetValue(scene) as IList;
                if (list == null) continue;

                foreach (object wgo in list)
                {
                    if (wgo == null || wgo.GetType().Name != "ZombieWgoData") continue;
                    object type = P(wgo.GetType(), "ZombieType")?.GetValue(wgo, null) ?? F(wgo.GetType(), "zombieType")?.GetValue(wgo);
                    if (type != null && string.Equals(type.ToString(), "Gardener", StringComparison.Ordinal))
                        yield return wgo;
                }
            }
        }

        internal static string GetGardenerStateName(object gardener)
        {
            try
            {
                object s = P(gardener.GetType(), "GardenerState")?.GetValue(gardener, null) ?? F(gardener.GetType(), "gardenerState")?.GetValue(gardener);
                return s == null ? "" : s.ToString();
            }
            catch { return ""; }
        }

        internal static bool HasGardenerOrder(object gardener)
        {
            try
            {
                object v = P(gardener.GetType(), "HasGardenerExecutingOrder")?.GetValue(gardener, null);
                return v is bool && (bool)v;
            }
            catch { return false; }
        }

        internal static object GetGardenerOrder(object gardener)
        {
            try { return P(gardener.GetType(), "GardenerExecutingOrder")?.GetValue(gardener, null); }
            catch { return null; }
        }

        internal static string GetObjectGuid(object obj)
        {
            if (obj == null) return "";
            try
            {
                object id = P(obj.GetType(), "UniqueId")?.GetValue(obj, null) ??
                            P(obj.GetType(), "Id")?.GetValue(obj, null);
                return GuidText(id);
            }
            catch { return ""; }
        }

        internal static string GetOrderGuid(object order)
        {
            if (order == null) return "";
            try { return GuidText(P(order.GetType(), "UniqueId")?.GetValue(order, null)); }
            catch { return ""; }
        }

        private static string GuidText(object sguid)
        {
            if (sguid == null) return "";
            try
            {
                object empty = P(sguid.GetType(), "IsEmpty")?.GetValue(sguid, null);
                if (empty is bool && (bool)empty) return "";
                object id = P(sguid.GetType(), "Id")?.GetValue(sguid, null);
                return id == null ? sguid.ToString() : id.ToString();
            }
            catch { return sguid.ToString(); }
        }

        internal static void ForceStopMovement(object gardener)
        {
            try
            {
                object move = P(gardener.GetType(), "MovementComponent")?.GetValue(gardener, null) ??
                              F(gardener.GetType(), "movementComponent")?.GetValue(gardener);
                M(move?.GetType(), "ForceStop", 0)?.Invoke(move, null);
            }
            catch { }
        }

        internal static void StopGardenerCraftActivity(object gardener)
        {
            try { M(gardener.GetType(), "GardenerStopCraftActivity", 1)?.Invoke(gardener, new object[] { true }); }
            catch { }
        }

        internal static void StopGardenerWorkActivity(object gardener)
        {
            try { M(gardener.GetType(), "GardenerStopWorkActivity", 2)?.Invoke(gardener, new object[] { null, true }); }
            catch { }
        }

        internal static void StopGardenerOrder(object gardener)
        {
            try { M(gardener.GetType(), "GardenerTryStopOrderExecution", 0)?.Invoke(gardener, null); }
            catch { }
        }

        internal static void RemoveOrderFromZone(object gardener, object order)
        {
            try
            {
                object zone = P(gardener.GetType(), "WorldZoneData")?.GetValue(gardener, null) ??
                              M(gardener.GetType(), "get_WorldZoneData", 0)?.Invoke(gardener, null);
                object id = P(order.GetType(), "UniqueId")?.GetValue(order, null);
                if (zone != null && id != null)
                    M(zone.GetType(), "RemoveOrder", 1)?.Invoke(zone, new object[] { id });
            }
            catch { }
        }

        internal static void ClearStaleGardenWorkerBindings(object gardener)
        {
            string gardenerId = GetObjectGuid(gardener);
            if (string.IsNullOrEmpty(gardenerId)) return;

            object world = TryGetWorldData();
            IList scenes = world == null ? null : F(world.GetType(), "gameSceneDataList")?.GetValue(world) as IList;
            if (scenes == null) return;

            object stationGuid = F(gardener.GetType(), "gardenerStation")?.GetValue(gardener);
            string stationId = GuidText(stationGuid);

            foreach (object scene in scenes)
            {
                IList list = scene == null ? null : F(scene.GetType(), "wgoDataList")?.GetValue(scene) as IList;
                if (list == null) continue;

                foreach (object wgo in list)
                {
                    if (wgo == null) continue;
                    if (GetObjectGuid(wgo) == stationId) continue;

                    bool isGardenPlot = false;
                    try
                    {
                        MethodInfo isPlot = M(GardenBedNavigationType, "IsGardenPlot", 1);
                        if (isPlot != null)
                            isGardenPlot = (bool)isPlot.Invoke(null, new object[] { wgo });
                    }
                    catch { }

                    if (!isGardenPlot) continue;

                    object worker = null;
                    try { worker = P(wgo.GetType(), "Worker")?.GetValue(wgo, null); } catch { }
                    if (worker != null && GetWorkerId(worker) == gardenerId)
                    {
                        try { M(wgo.GetType(), "ClearWorker", 0)?.Invoke(wgo, null); } catch { }
                    }
                }
            }
        }

        private static string GetWorkerId(object worker)
        {
            if (worker == null) return "";
            try { return GuidText(P(worker.GetType(), "Id")?.GetValue(worker, null)); }
            catch { return ""; }
        }

        internal static void ClearGardenerTransientState(object gardener)
        {
            Type t = gardener.GetType();

            F(t, "currentActivity")?.SetValue(gardener, null);
            F(t, "gardenerTimeForCheckFailedPathAgain")?.SetValue(gardener, 0f);
            F(t, "gardenerPickingUpFromInventoryTime")?.SetValue(gardener, 0f);

            SetGuidFieldEmpty(gardener, "gardenerExecutingOrder");
            SetGuidFieldEmpty(gardener, "gardenerCurrentTargetUniqueId");
            SetGuidFieldEmpty(gardener, "gardenerCurrentMovementTargetUniqueId");
            SetGuidFieldEmpty(gardener, "gardenerCurrentWorkingWgo");
        }

        private static void SetGuidFieldEmpty(object obj, string fieldName)
        {
            try
            {
                FieldInfo field = F(obj.GetType(), fieldName);
                if (field == null) return;
                object empty = P(SGuidType, "Empty")?.GetValue(null, null) ?? M(SGuidType, "get_Empty", 0)?.Invoke(null, null);
                if (empty != null) field.SetValue(obj, empty);
            }
            catch { }
        }

        internal static bool RestoreStationAndMoveHome(object gardener, bool aggressive)
        {
            Type t = gardener.GetType();
            object world = TryGetWorldData();
            object stationGuid = F(t, "gardenerStation")?.GetValue(gardener);
            object station = null;

            try
            {
                if (world != null && stationGuid != null)
                    station = MForArg(world.GetType(), "GetWgoData", stationGuid)?.Invoke(world, new object[] { stationGuid });
            }
            catch { }

            if (station != null)
            {
                F(t, "attachedWgoData")?.SetValue(gardener, station);
                FieldInfo attachedGuidField = F(t, "attachedWgoDataUniqueId");
                object attachedGuid = attachedGuidField?.GetValue(gardener);
                if (attachedGuid != null && stationGuid != null)
                    M(attachedGuid.GetType(), "SetGuid", 1)?.Invoke(attachedGuid, new object[] { stationGuid });

                if (aggressive)
                {
                    try
                    {
                        object stationPos = P(station.GetType(), "Position")?.GetValue(station, null);
                        PropertyInfo pos = P(t, "Position");
                        MethodInfo setter = pos?.GetSetMethod(true);
                        if (stationPos != null && setter != null)
                            setter.Invoke(gardener, new object[] { stationPos });
                    }
                    catch { }
                }

                try
                {
                    M(t, "GardenerTryMoveToStation", 0)?.Invoke(gardener, null);
                    return true;
                }
                catch { }
            }

            // Last resort: set enum value 0 (OnStation) via the game's property setter.
            try
            {
                Type stateType = F(t, "gardenerState")?.FieldType;
                object onStation = Enum.ToObject(stateType, 0);
                P(t, "GardenerState")?.GetSetMethod(true)?.Invoke(gardener, new object[] { onStation });
            }
            catch { }

            return false;
        }
    }
}
