using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace MoreFertilizerSlots
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class MoreFertilizerSlotsPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.gk2.morefertilizerslots";
        public const string PluginName = "More Fertilizer Slots";
        public const string PluginVersion = "0.2.2";

        internal const string FertilizerSlotsKey = "g_garden_fertilizer_slots";
        internal const int VanillaMaxSlots = 2;

        internal static ConfigEntry<int> FertilizerSlots;
        internal static ManualLogSource ModLog;

        internal static int MaxSlots
        {
            get
            {
                int value = FertilizerSlots == null ? 4 : FertilizerSlots.Value;
                if (value < 3) return 3;
                if (value > 7) return 7;
                return value;
            }
        }

        private void Awake()
        {
            ModLog = Logger;
            FertilizerSlots = Config.Bind(
                "General",
                "FertilizerSlots",
                4,
                new ConfigDescription(
                    "Maximum fertilizer slots after the vanilla slot progression is fully unlocked. Range: 3-7.",
                    new AcceptableValueRange<int>(3, 7)));

            try
            {
                new Harmony(PluginGuid).PatchAll(Assembly.GetExecutingAssembly());
                Logger.LogInfo(PluginName + " " + PluginVersion + " loaded. FertilizerSlots=" + MaxSlots);
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to apply Harmony patches: " + ex);
            }
        }
    }

    [HarmonyPatch]
    internal static class PlayerDataGetResIntPatch
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("PlayerData");
            return type == null ? null : AccessTools.Method(type, "GetResInt", new Type[] { typeof(string) });
        }

        private static void Postfix(string __0, ref int __result)
        {
            if (!string.Equals(__0, MoreFertilizerSlotsPlugin.FertilizerSlotsKey, StringComparison.Ordinal))
                return;

            if (__result <= 0)
                return;

            int extraSlots = MoreFertilizerSlotsPlugin.MaxSlots - MoreFertilizerSlotsPlugin.VanillaMaxSlots;
            int effective = __result + extraSlots;
            if (effective > MoreFertilizerSlotsPlugin.MaxSlots)
                effective = MoreFertilizerSlotsPlugin.MaxSlots;
            __result = effective;
        }
    }

    [HarmonyPatch]
    internal static class GardenSlotAssignmentPatch
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("GardenInteractionHandler");
            return type == null ? null : AccessTools.Method(type, "TryAssignPerkSlotForNewestAddedPerk");
        }

        private static List<int> BuildSlotList()
        {
            List<int> result = new List<int>(MoreFertilizerSlotsPlugin.MaxSlots);
            for (int i = 1; i <= MoreFertilizerSlotsPlugin.MaxSlots; i++)
                result.Add(i);
            return result;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            int firstStoreLocal0 = -1;

            int searchLimit = Math.Min(codes.Count, 24);
            for (int i = 0; i < searchLimit; i++)
            {
                if (codes[i].opcode == OpCodes.Stloc_0)
                {
                    firstStoreLocal0 = i;
                    break;
                }
            }

            if (firstStoreLocal0 < 0)
            {
                if (MoreFertilizerSlotsPlugin.ModLog != null)
                    MoreFertilizerSlotsPlugin.ModLog.LogError("Could not patch fertilizer slot assignment list; game method layout changed.");
                return codes;
            }

            MethodInfo builder = AccessTools.Method(typeof(GardenSlotAssignmentPatch), "BuildSlotList");
            CodeInstruction callBuilder = new CodeInstruction(OpCodes.Call, builder);

            if (codes.Count > 0 && codes[0].labels != null)
                callBuilder.labels.AddRange(codes[0].labels);

            codes.RemoveRange(0, firstStoreLocal0 + 1);
            codes.Insert(0, new CodeInstruction(OpCodes.Stloc_0));
            codes.Insert(0, callBuilder);
            return codes;
        }
    }

    // Create additional widgets before vanilla UpdatePerks iterates perkWidgets.Count.
    [HarmonyPatch]
    internal static class GardenBedWindowUpdatePerksPatch
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("UIGardenBedWindow");
            return type == null ? null : AccessTools.Method(type, "UpdatePerks");
        }

        private static void Prefix(object __instance)
        {
            GardenSlotUi.EnsureSlots(__instance);
        }

        private static void Postfix(object __instance)
        {
            GardenSlotUi.LayoutSlots(__instance);
        }
    }

    // Redraw moves slotsObj between slotsPos1/slotsPos2 and finishes updating seed/plant controls.
    // Re-run our layout at the very end so the safe boundary uses the final live geometry.
    [HarmonyPatch]
    internal static class GardenBedWindowRedrawPatch
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("UIGardenBedWindow");
            return type == null ? null : AccessTools.Method(type, "Redraw");
        }

        private static void Postfix(object __instance)
        {
            GardenSlotUi.LayoutSlots(__instance);
        }
    }

    internal static class GardenSlotUi
    {
        private const string GeneratedPrefix = "MFS_FertilizerSlot_";
        private const float EdgePadding = 8f;
        private const float RightPanelGap = 12f;
        private const float MinScale = 0.72f;

        private static FieldInfo _perkWidgetsField;
        private static FieldInfo _seedItemCellField;
        private static FieldInfo _plantButtonField;
        private static FieldInfo _slotsObjField;

        private static readonly Dictionary<int, Vector3> OriginalPositions = new Dictionary<int, Vector3>();
        private static readonly Dictionary<int, Vector3> OriginalScales = new Dictionary<int, Vector3>();

        private static bool _warnedMissingField;

        internal static void EnsureSlots(object window)
        {
            if (window == null)
                return;

            try
            {
                ResolveFields(window.GetType());

                if (_perkWidgetsField == null)
                {
                    WarnOnce("UIGardenBedWindow.perkWidgets was not found.");
                    return;
                }

                IList widgets = _perkWidgetsField.GetValue(window) as IList;
                if (widgets == null || widgets.Count == 0)
                    return;

                RememberOriginalPrefabSlots(widgets);

                int desired = MoreFertilizerSlotsPlugin.MaxSlots;

                while (widgets.Count > desired)
                {
                    int last = widgets.Count - 1;
                    Component component = widgets[last] as Component;
                    if (component == null || component.gameObject == null ||
                        !component.gameObject.name.StartsWith(GeneratedPrefix, StringComparison.Ordinal))
                        break;

                    widgets.RemoveAt(last);
                    UnityEngine.Object.Destroy(component.gameObject);
                }

                while (widgets.Count < desired)
                {
                    Component template = widgets[widgets.Count - 1] as Component;
                    if (template == null || template.gameObject == null)
                        break;

                    GameObject cloneObject = (GameObject)UnityEngine.Object.Instantiate(
                        template.gameObject, template.transform.parent);
                    cloneObject.name = GeneratedPrefix + (widgets.Count + 1);

                    Component clone = cloneObject.GetComponent(template.GetType());
                    if (clone == null)
                    {
                        UnityEngine.Object.Destroy(cloneObject);
                        break;
                    }

                    widgets.Add(clone);
                }

                SetIgnoreParentLayout(widgets, desired > 3);
            }
            catch (Exception ex)
            {
                if (MoreFertilizerSlotsPlugin.ModLog != null)
                    MoreFertilizerSlotsPlugin.ModLog.LogError("Failed to expand fertilizer-slot UI: " + ex);
            }
        }

        internal static void LayoutSlots(object window)
        {
            if (window == null)
                return;

            try
            {
                ResolveFields(window.GetType());

                IList widgets = _perkWidgetsField == null ? null : _perkWidgetsField.GetValue(window) as IList;
                if (widgets == null || widgets.Count < 3)
                    return;

                int desired = Math.Min(MoreFertilizerSlotsPlugin.MaxSlots, widgets.Count);
                RememberOriginalPrefabSlots(widgets);

                if (desired <= 3)
                {
                    SetIgnoreParentLayout(widgets, false);
                    RestoreOriginalSlots(widgets);
                    return;
                }

                SetIgnoreParentLayout(widgets, true);

                List<RectTransform> rects = new List<RectTransform>(desired);
                for (int i = 0; i < desired; i++)
                {
                    Component component = widgets[i] as Component;
                    if (component == null)
                        return;

                    RectTransform rect = component.transform as RectTransform;
                    if (rect == null)
                        return;

                    rects.Add(rect);
                }

                RectTransform parent = rects[0].parent as RectTransform;
                if (parent == null)
                    return;

                float slotW = Mathf.Max(1f, rects[0].rect.width);
                float slotH = Mathf.Max(1f, rects[0].rect.height);

                Vector3 p0 = GetOriginalPosition(rects[0]);
                Vector3 p1 = GetOriginalPosition(rects[1]);
                Vector3 p2 = GetOriginalPosition(rects[2]);

                float originalStep = Mathf.Abs((p2.x - p0.x) * 0.5f);
                if (originalStep < 1f)
                    originalStep = Mathf.Max(slotW + 6f, 1f);

                float originalCenterX = (p0.x + p1.x + p2.x) / 3f;
                float baseY = (p0.y + p1.y + p2.y) / 3f;

                Rect safe = GetSafeRect(window, parent, rects, originalCenterX, slotW);
                if (safe.width <= 1f)
                    return;

                // Prefer the most compact readable grid that fits entirely to the left of the planting area.
                int preferredCols = desired <= 4 ? desired : (desired == 5 ? 3 : desired == 6 ? 3 : 4);
                int cols = preferredCols;
                float gapX = Mathf.Max(4f, Mathf.Min(10f, originalStep - slotW));
                float scale = 1f;

                while (cols >= 2)
                {
                    float widthAtOne = cols * slotW + (cols - 1) * gapX;
                    if (widthAtOne <= safe.width)
                        break;
                    cols--;
                }

                if (cols < 2)
                    cols = 2;

                float unscaledWidth = cols * slotW + (cols - 1) * gapX;
                if (unscaledWidth > safe.width)
                {
                    scale = Mathf.Clamp(safe.width / unscaledWidth, MinScale, 1f);
                }

                float scaledW = slotW * scale;
                float scaledH = slotH * scale;
                float scaledGapX = gapX * scale;
                float groupWidth = cols * scaledW + (cols - 1) * scaledGapX;

                float centerX = Mathf.Clamp(
                    originalCenterX,
                    safe.xMin + groupWidth * 0.5f,
                    safe.xMax - groupWidth * 0.5f);

                int rows = (int)Math.Ceiling((double)desired / cols);
                float rowGap = Mathf.Max(6f, scaledH * 0.16f);
                float rowStep = scaledH + rowGap;
                float totalHeight = rows * scaledH + (rows - 1) * rowGap;

                // Keep the group close to the vanilla row, but clamp it to slotsObj/parent bounds.
                float centerY = Mathf.Clamp(
                    baseY,
                    safe.yMin + totalHeight * 0.5f,
                    safe.yMax - totalHeight * 0.5f);

                int index = 0;
                for (int row = 0; row < rows; row++)
                {
                    int remaining = desired - index;
                    int rowCount = Math.Min(cols, remaining);
                    float rowWidth = rowCount * scaledW + (rowCount - 1) * scaledGapX;
                    float rowStartX = centerX - rowWidth * 0.5f + scaledW * 0.5f;
                    float y = centerY + totalHeight * 0.5f - scaledH * 0.5f - row * rowStep;

                    for (int col = 0; col < rowCount; col++, index++)
                    {
                        RectTransform rect = rects[index];
                        rect.localScale = new Vector3(scale, scale, rect.localScale.z);
                        Vector3 currentLocal = rect.localPosition;
                        rect.localPosition = new Vector3(
                            rowStartX + col * (scaledW + scaledGapX),
                            y,
                            currentLocal.z);
                    }
                }
            }
            catch (Exception ex)
            {
                if (MoreFertilizerSlotsPlugin.ModLog != null)
                    MoreFertilizerSlotsPlugin.ModLog.LogError("Failed to lay out fertilizer slots: " + ex);
            }
        }

        private static Rect GetSafeRect(object window, RectTransform parent, List<RectTransform> rects, float originalCenterX, float slotW)
        {
            Rect parentRect = parent.rect;
            float minX = parentRect.xMin + EdgePadding;
            float maxX = parentRect.xMax - EdgePadding;
            float minY = parentRect.yMin + EdgePadding;
            float maxY = parentRect.yMax - EdgePadding;

            // slotsObj is the game's own movable fertilizer-area transform (Redraw places it at slotsPos1/slotsPos2).
            // If it has a meaningful rect, use it as the primary vertical/left-side envelope.
            RectTransform slotsObj = GetRectTransformField(window, _slotsObjField);
            if (slotsObj != null && slotsObj != parent && slotsObj.rect.width > 1f && slotsObj.rect.height > 1f)
            {
                Rect b;
                if (TryGetBoundsInParent(slotsObj, parent, out b))
                {
                    minX = Mathf.Max(minX, b.xMin + EdgePadding);
                    minY = Mathf.Max(minY, b.yMin + EdgePadding);
                    maxY = Mathf.Min(maxY, b.yMax - EdgePadding);
                }
            }

            // Treat the seed selector and plant button as a hard exclusion zone on the right.
            Component seed = GetComponentField(window, _seedItemCellField);
            Component plantButton = GetComponentField(window, _plantButtonField);

            ApplyRightBoundary(seed, parent, originalCenterX, ref maxX);
            ApplyRightBoundary(plantButton, parent, originalCenterX, ref maxX);

            // Never let the safe region collapse behind the vanilla fertilizer row.
            float vanillaLeft = float.MaxValue;
            for (int i = 0; i < Math.Min(3, rects.Count); i++)
            {
                Rect b;
                if (TryGetBoundsInParent(rects[i], parent, out b))
                    vanillaLeft = Mathf.Min(vanillaLeft, b.xMin);
            }
            if (vanillaLeft != float.MaxValue)
                minX = Mathf.Min(minX, vanillaLeft - EdgePadding);

            // If the right-side fields are inactive/unavailable, keep a conservative reserve equal
            // to roughly one slot so expanded fertilizer UI cannot invade the planting column.
            if (maxX >= parentRect.xMax - EdgePadding - 0.5f)
                maxX -= slotW + RightPanelGap;

            if (maxX <= minX)
                return new Rect(minX, minY, 0f, Mathf.Max(0f, maxY - minY));

            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        private static void ApplyRightBoundary(Component blocker, RectTransform parent, float originalCenterX, ref float maxX)
        {
            if (blocker == null || blocker.gameObject == null || !blocker.gameObject.activeInHierarchy)
                return;

            RectTransform blockerRect = blocker.transform as RectTransform;
            if (blockerRect == null)
                return;

            Rect b;
            if (!TryGetBoundsInParent(blockerRect, parent, out b))
                return;

            // Only use controls that are actually on the right side of the vanilla fertilizer row.
            if (b.center.x > originalCenterX)
                maxX = Mathf.Min(maxX, b.xMin - RightPanelGap);
        }

        private static bool TryGetBoundsInParent(RectTransform target, RectTransform parent, out Rect result)
        {
            result = new Rect();
            if (target == null || parent == null)
                return false;

            Vector3[] corners = new Vector3[4];
            target.GetWorldCorners(corners);

            Vector3 p0 = parent.InverseTransformPoint(corners[0]);
            float minX = p0.x;
            float maxX = p0.x;
            float minY = p0.y;
            float maxY = p0.y;

            for (int i = 1; i < 4; i++)
            {
                Vector3 p = parent.InverseTransformPoint(corners[i]);
                minX = Mathf.Min(minX, p.x);
                maxX = Mathf.Max(maxX, p.x);
                minY = Mathf.Min(minY, p.y);
                maxY = Mathf.Max(maxY, p.y);
            }

            result = Rect.MinMaxRect(minX, minY, maxX, maxY);
            return true;
        }

        private static void SetIgnoreParentLayout(IList widgets, bool ignore)
        {
            Type layoutElementType = Type.GetType("UnityEngine.UI.LayoutElement, UnityEngine.UI");
            if (layoutElementType == null)
                return;

            PropertyInfo ignoreLayoutProperty = layoutElementType.GetProperty("ignoreLayout", BindingFlags.Instance | BindingFlags.Public);
            if (ignoreLayoutProperty == null)
                return;

            for (int i = 0; i < widgets.Count; i++)
            {
                Component component = widgets[i] as Component;
                if (component == null || component.gameObject == null)
                    continue;

                Component layoutElement = component.gameObject.GetComponent(layoutElementType);
                if (layoutElement == null)
                    layoutElement = component.gameObject.AddComponent(layoutElementType);

                ignoreLayoutProperty.SetValue(layoutElement, ignore, null);
            }
        }

        private static void RememberOriginalPrefabSlots(IList widgets)
        {
            for (int i = 0; i < Math.Min(3, widgets.Count); i++)
            {
                Component component = widgets[i] as Component;
                RectTransform rect = component == null ? null : component.transform as RectTransform;
                if (rect == null)
                    continue;

                int id = rect.GetInstanceID();
                if (!OriginalPositions.ContainsKey(id))
                    OriginalPositions[id] = rect.localPosition;
                if (!OriginalScales.ContainsKey(id))
                    OriginalScales[id] = rect.localScale;
            }
        }

        private static Vector3 GetOriginalPosition(RectTransform rect)
        {
            Vector3 value;
            if (rect != null && OriginalPositions.TryGetValue(rect.GetInstanceID(), out value))
                return value;
            return rect == null ? Vector3.zero : rect.localPosition;
        }

        private static void RestoreOriginalSlots(IList widgets)
        {
            for (int i = 0; i < Math.Min(3, widgets.Count); i++)
            {
                Component component = widgets[i] as Component;
                RectTransform rect = component == null ? null : component.transform as RectTransform;
                if (rect == null)
                    continue;

                Vector3 pos;
                Vector3 scale;
                if (OriginalPositions.TryGetValue(rect.GetInstanceID(), out pos))
                    rect.localPosition = pos;
                if (OriginalScales.TryGetValue(rect.GetInstanceID(), out scale))
                    rect.localScale = scale;
            }
        }

        private static void ResolveFields(Type type)
        {
            if (_perkWidgetsField == null) _perkWidgetsField = AccessTools.Field(type, "perkWidgets");
            if (_seedItemCellField == null) _seedItemCellField = AccessTools.Field(type, "seedItemCell");
            if (_plantButtonField == null) _plantButtonField = AccessTools.Field(type, "plantButton");
            if (_slotsObjField == null) _slotsObjField = AccessTools.Field(type, "slotsObj");
        }

        private static Component GetComponentField(object instance, FieldInfo field)
        {
            if (instance == null || field == null)
                return null;
            return field.GetValue(instance) as Component;
        }

        private static RectTransform GetRectTransformField(object instance, FieldInfo field)
        {
            if (instance == null || field == null)
                return null;
            return field.GetValue(instance) as RectTransform;
        }

        private static void WarnOnce(string message)
        {
            if (_warnedMissingField)
                return;
            _warnedMissingField = true;
            if (MoreFertilizerSlotsPlugin.ModLog != null)
                MoreFertilizerSlotsPlugin.ModLog.LogWarning(message);
        }
    }
}
