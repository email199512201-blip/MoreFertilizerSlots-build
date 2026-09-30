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
        public const string PluginVersion = "0.2.0";

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
    }

    internal static class GardenSlotUi
    {
        private const string GeneratedPrefix = "MFS_FertilizerSlot_";
        private static FieldInfo _perkWidgetsField;
        private static bool _warnedMissingField;

        internal static void EnsureSlots(object window)
        {
            if (window == null)
                return;

            try
            {
                if (_perkWidgetsField == null)
                    _perkWidgetsField = AccessTools.Field(window.GetType(), "perkWidgets");

                if (_perkWidgetsField == null)
                {
                    if (!_warnedMissingField && MoreFertilizerSlotsPlugin.ModLog != null)
                    {
                        _warnedMissingField = true;
                        MoreFertilizerSlotsPlugin.ModLog.LogWarning("UIGardenBedWindow.perkWidgets was not found.");
                    }
                    return;
                }

                IList widgets = _perkWidgetsField.GetValue(window) as IList;
                if (widgets == null || widgets.Count == 0)
                    return;

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

                    GameObject cloneObject =
                        UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent);
                    cloneObject.name = GeneratedPrefix + (widgets.Count + 1);

                    Component clone = cloneObject.GetComponent(template.GetType());
                    if (clone == null)
                    {
                        UnityEngine.Object.Destroy(cloneObject);
                        break;
                    }

                    widgets.Add(clone);
                }

                LayoutSlots(widgets, desired);
            }
            catch (Exception ex)
            {
                if (MoreFertilizerSlotsPlugin.ModLog != null)
                    MoreFertilizerSlotsPlugin.ModLog.LogError("Failed to expand fertilizer-slot UI: " + ex);
            }
        }

        private static void LayoutSlots(IList widgets, int desired)
        {
            int count = Math.Min(desired, widgets.Count);
            if (count <= 3)
                return;

            List<RectTransform> rects = new List<RectTransform>(count);

            for (int i = 0; i < count; i++)
            {
                Component component = widgets[i] as Component;
                if (component == null)
                    return;

                RectTransform rect = component.transform as RectTransform;
                if (rect == null)
                    return;

                rects.Add(rect);
            }

            Vector2 p0 = rects[0].anchoredPosition;
            Vector2 p1 = rects[1].anchoredPosition;
            Vector2 p2 = rects[2].anchoredPosition;

            float stepX = (p2.x - p0.x) * 0.5f;
            float baseY = (p0.y + p1.y + p2.y) / 3f;
            float centerX = (p0.x + p1.x + p2.x) / 3f;

            if (Math.Abs(stepX) < 1f)
            {
                Vector2 step = p1 - p0;
                for (int i = 3; i < count; i++)
                    rects[i].anchoredPosition = p2 + step * (i - 2);
                return;
            }

            if (count == 4)
            {
                for (int i = 0; i < count; i++)
                {
                    float offset = i - 1.5f;
                    rects[i].anchoredPosition = new Vector2(centerX + offset * stepX, baseY);
                }
                return;
            }

            int topCount = (count + 1) / 2;
            int bottomCount = count - topCount;

            float slotHeight = rects[0].rect.height;
            if (slotHeight <= 1f)
                slotHeight = Math.Abs(stepX);

            float rowStep = Math.Max(slotHeight + 4f, Math.Abs(stepX) * 0.9f);
            float topY = baseY + rowStep * 0.45f;
            float bottomY = baseY - rowStep * 0.45f;

            PositionRow(rects, 0, topCount, centerX, topY, stepX);
            PositionRow(rects, topCount, bottomCount, centerX, bottomY, stepX);
        }

        private static void PositionRow(
            List<RectTransform> rects,
            int start,
            int rowCount,
            float centerX,
            float y,
            float stepX)
        {
            if (rowCount <= 0)
                return;

            float midpoint = (rowCount - 1) * 0.5f;
            for (int i = 0; i < rowCount; i++)
            {
                float offset = i - midpoint;
                rects[start + i].anchoredPosition =
                    new Vector2(centerX + offset * stepX, y);
            }
        }
    }
}
