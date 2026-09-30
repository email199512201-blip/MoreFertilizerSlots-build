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
        public const string PluginVersion = "0.2.4";

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
        private const float VerticalGap = 8f;

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
                    WarnOnce("UIGardenBedWindow.perkWidgets was not found.");
                    return;
                }

                IList widgets = _perkWidgetsField.GetValue(window) as IList;
                if (widgets == null || widgets.Count < 3)
                    return;

                int desired = MoreFertilizerSlotsPlugin.MaxSlots;

                // Remove only slots created by this mod. Never touch the three vanilla widgets.
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

                // Always clone the third VANILLA slot, not a previously generated clone.
                Component template = widgets[2] as Component;
                if (template == null || template.gameObject == null)
                    return;

                while (widgets.Count < desired)
                {
                    GameObject cloneObject = (GameObject)UnityEngine.Object.Instantiate(
                        template.gameObject, template.transform.parent);
                    cloneObject.name = GeneratedPrefix + (widgets.Count + 1);

                    Component clone = cloneObject.GetComponent(template.GetType());
                    if (clone == null)
                    {
                        UnityEngine.Object.Destroy(cloneObject);
                        break;
                    }

                    // Only generated slots leave the game's LayoutGroup.
                    // The original three remain fully vanilla-controlled.
                    SetIgnoreParentLayout(cloneObject, true);
                    widgets.Add(clone);
                }

                // Make absolutely sure vanilla slots are still governed by the original LayoutGroup.
                for (int i = 0; i < Math.Min(3, widgets.Count); i++)
                {
                    Component vanilla = widgets[i] as Component;
                    if (vanilla != null && vanilla.gameObject != null)
                        SetIgnoreParentLayout(vanilla.gameObject, false);
                }
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
                if (_perkWidgetsField == null)
                    _perkWidgetsField = AccessTools.Field(window.GetType(), "perkWidgets");

                IList widgets = _perkWidgetsField == null ? null : _perkWidgetsField.GetValue(window) as IList;
                if (widgets == null || widgets.Count < 3)
                    return;

                int desired = Math.Min(MoreFertilizerSlotsPlugin.MaxSlots, widgets.Count);
                if (desired <= 3)
                    return;

                RectTransform r0 = GetRect(widgets[0]);
                RectTransform r1 = GetRect(widgets[1]);
                RectTransform r2 = GetRect(widgets[2]);
                if (r0 == null || r1 == null || r2 == null)
                    return;

                // Let the game's original LayoutGroup own the first three slots completely.
                // Read their FINAL world positions and use those as a visual ruler.
                Vector3 w0 = r0.position;
                Vector3 w1 = r1.position;
                Vector3 w2 = r2.position;

                Vector3 step = (w2 - w0) * 0.5f;
                Vector3 rowCenter = (w0 + w1 + w2) / 3f;

                // Derive one visual slot-height in world space from the actual rendered rectangle.
                Vector3[] corners = new Vector3[4];
                r0.GetWorldCorners(corners);
                Vector3 down = corners[0] - corners[1];
                float renderedHeight = down.magnitude;
                if (renderedHeight < 0.0001f)
                    down = Vector3.down;
                else
                    down /= renderedHeight;

                // One compact second row only. This keeps every configuration (4-7)
                // inside the window vertically.
                float rowGap = renderedHeight * 0.12f;
                Vector3 lowerCenter = rowCenter + down * (renderedHeight + rowGap);

                int extra = desired - 3;

                // All extra slots stay in ONE lower row.
                // For seven total slots, the four lower slots use:
                //   one extrapolated column to the left + the three vanilla fertilizer columns.
                // That leftmost column occupies the empty area below the crop/seed preview, while
                // the rightmost column never extends beyond vanilla slot #3. Therefore the planting
                // controls on the right cannot be covered.
                if (extra == 1)
                {
                    PlaceWorld(widgets, 3, lowerCenter);
                }
                else if (extra == 2)
                {
                    PlaceWorld(widgets, 3, lowerCenter - step * 0.5f);
                    PlaceWorld(widgets, 4, lowerCenter + step * 0.5f);
                }
                else if (extra == 3)
                {
                    PlaceWorld(widgets, 3, lowerCenter - step);
                    PlaceWorld(widgets, 4, lowerCenter);
                    PlaceWorld(widgets, 5, lowerCenter + step);
                }
                else
                {
                    PlaceWorld(widgets, 3, lowerCenter - step * 2f);
                    PlaceWorld(widgets, 4, lowerCenter - step);
                    PlaceWorld(widgets, 5, lowerCenter);
                    PlaceWorld(widgets, 6, lowerCenter + step);
                }
            }
            catch (Exception ex)
            {
                if (MoreFertilizerSlotsPlugin.ModLog != null)
                    MoreFertilizerSlotsPlugin.ModLog.LogError("Failed to lay out fertilizer slots: " + ex);
            }
        }

        private static RectTransform GetRect(object widget)
        {
            Component component = widget as Component;
            return component == null ? null : component.transform as RectTransform;
        }

        private static void PlaceWorld(IList widgets, int index, Vector3 worldPosition)
        {
            if (index < 0 || index >= widgets.Count)
                return;

            RectTransform rect = GetRect(widgets[index]);
            if (rect == null)
                return;

            SetIgnoreParentLayout(rect.gameObject, true);

            // World-space placement deliberately avoids RectTransform anchor/pivot differences.
            // The position is derived from the already-correct vanilla slots, so it scales with
            // resolution and Canvas scaling automatically.
            rect.position = worldPosition;
        }

        private static void SetIgnoreParentLayout(GameObject obj, bool ignore)
        {
            if (obj == null)
                return;

            Type layoutElementType = Type.GetType("UnityEngine.UI.LayoutElement, UnityEngine.UI");
            if (layoutElementType == null)
                return;

            PropertyInfo ignoreLayoutProperty =
                layoutElementType.GetProperty("ignoreLayout", BindingFlags.Instance | BindingFlags.Public);
            if (ignoreLayoutProperty == null)
                return;

            Component layoutElement = obj.GetComponent(layoutElementType);
            if (layoutElement == null && ignore)
                layoutElement = obj.AddComponent(layoutElementType);

            if (layoutElement != null)
                ignoreLayoutProperty.SetValue(layoutElement, ignore, null);
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
