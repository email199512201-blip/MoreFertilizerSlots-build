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
        public const string PluginVersion = "0.3.0";

        internal const string FertilizerSlotsKey = "g_garden_fertilizer_slots";
        internal const int VanillaMaxSlots = 2;

        internal static ConfigEntry<int> FertilizerSlots;
        internal static ConfigEntry<bool> AllowDuplicateFertilizers;
        internal static ConfigEntry<bool> StackDuplicateEffects;
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

            AllowDuplicateFertilizers = Config.Bind(
                "General",
                "AllowDuplicateFertilizers",
                true,
                "Allow the same fertilizer to occupy multiple fertilizer slots.");

            StackDuplicateEffects = Config.Bind(
                "General",
                "StackDuplicateEffects",
                true,
                "When duplicate fertilizers are allowed, apply each copy's mastery/start/progress effect.");

            try
            {
                new Harmony(PluginGuid).PatchAll(Assembly.GetExecutingAssembly());
                Logger.LogInfo(
                    PluginName + " " + PluginVersion +
                    " loaded. FertilizerSlots=" + MaxSlots +
                    ", AllowDuplicateFertilizers=" + AllowDuplicateFertilizers.Value +
                    ", StackDuplicateEffects=" + StackDuplicateEffects.Value);
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to apply Harmony patches: " + ex);
            }
        }
    }

    internal static class ReflectionUtil
    {
        internal static object GetProperty(object instance, string name)
        {
            if (instance == null) return null;
            PropertyInfo p = AccessTools.Property(instance.GetType(), name);
            return p == null ? null : p.GetValue(instance, null);
        }

        internal static object GetField(object instance, string name)
        {
            if (instance == null) return null;
            FieldInfo f = AccessTools.Field(instance.GetType(), name);
            return f == null ? null : f.GetValue(instance);
        }

        internal static object GetDefinition(object perk)
        {
            if (perk == null) return null;
            MethodInfo getter = AccessTools.Method(perk.GetType(), "get_Definition");
            if (getter != null) return getter.Invoke(perk, null);
            return GetProperty(perk, "Definition");
        }

        internal static string GetDefinitionId(object perk)
        {
            object def = GetDefinition(perk);
            if (def == null) return null;
            FieldInfo idField = AccessTools.Field(def.GetType(), "id");
            return idField == null ? null : idField.GetValue(def) as string;
        }

        internal static bool IsFertilizerPerk(object perk)
        {
            object def = GetDefinition(perk);
            if (def == null) return false;
            MethodInfo m = AccessTools.Method(def.GetType(), "get_IsFertilizerPerk");
            if (m == null) return false;
            object result = m.Invoke(def, null);
            return result is bool && (bool)result;
        }

        internal static IList GetActivePerks(object wgo)
        {
            return GetProperty(wgo, "ActivePerks") as IList;
        }

        internal static IList GetGardenPerks(object window)
        {
            object data = GetField(window, "data");
            if (data == null) return null;
            return GetProperty(data, "GardenPerks") as IList;
        }

        internal static object GetWindowWgo(object window)
        {
            object data = GetField(window, "data");
            if (data == null) return null;
            return GetProperty(data, "WgoData");
        }

        internal static bool GetWindowIsGrowing(object window)
        {
            object data = GetField(window, "data");
            if (data == null) return false;
            object value = GetProperty(data, "IsGrowing");
            return value is bool && (bool)value;
        }

        internal static int GetGameResInt(object wgo, string key)
        {
            if (wgo == null) return 0;
            MethodInfo m = AccessTools.Method(wgo.GetType(), "GetGameResInt", new Type[] { typeof(string) });
            if (m == null) return 0;
            object result = m.Invoke(wgo, new object[] { key });
            return result is int ? (int)result : 0;
        }

        internal static int GetIntField(object instance, string fieldName)
        {
            if (instance == null) return 0;
            FieldInfo f = AccessTools.Field(instance.GetType(), fieldName);
            if (f == null) return 0;
            object v = f.GetValue(instance);
            return v is int ? (int)v : 0;
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
                return codes;

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

    // Allow selecting a fertilizer even if another copy is already active.
    [HarmonyPatch]
    internal static class GardenItemValidityPatch
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("UIGardenBedWindow");
            return type == null ? null : AccessTools.Method(type, "IsItemValid");
        }

        private static bool Prefix(object __0, ref bool __result)
        {
            if (MoreFertilizerSlotsPlugin.AllowDuplicateFertilizers == null ||
                !MoreFertilizerSlotsPlugin.AllowDuplicateFertilizers.Value)
                return true;

            if (__0 == null)
                return true;

            MethodInfo m = AccessTools.Method(__0.GetType(), "get_IsFertilizer");
            if (m == null)
                return true;

            object value = m.Invoke(__0, null);
            if (value is bool && (bool)value)
            {
                __result = true;
                return false;
            }

            return true;
        }
    }

    // Force a second/third/etc. fertilizer perk to be a real additional PerkData object.
    [HarmonyPatch]
    internal static class WgoAddPerkDuplicatePatch
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("WgoData");
            return type == null ? null : AccessTools.Method(type, "AddPerk", new Type[] { typeof(string) });
        }

        private static bool Prefix(object __instance, string __0)
        {
            if (MoreFertilizerSlotsPlugin.AllowDuplicateFertilizers == null ||
                !MoreFertilizerSlotsPlugin.AllowDuplicateFertilizers.Value)
                return true;

            IList active = ReflectionUtil.GetActivePerks(__instance);
            if (active == null)
                return true;

            object matching = null;
            for (int i = 0; i < active.Count; i++)
            {
                object perk = active[i];
                if (string.Equals(ReflectionUtil.GetDefinitionId(perk), __0, StringComparison.Ordinal))
                {
                    matching = perk;
                    break;
                }
            }

            if (matching == null || !ReflectionUtil.IsFertilizerPerk(matching))
                return true;

            Type perkType = AccessTools.TypeByName("PerkData");
            if (perkType == null)
                return true;

            object newPerk = Activator.CreateInstance(perkType, new object[] { __0 });
            MethodInfo addNew = AccessTools.Method(__instance.GetType(), "AddNewPerk");
            if (newPerk == null || addNew == null)
                return true;

            addNew.Invoke(__instance, new object[] { newPerk });
            return false;
        }
    }

    // The vanilla slot lookup uses one game-res key per perk ID, so duplicate IDs can only occupy
    // one visible slot. Replace that lookup with the actual GardenPerks list order when duplicates
    // are enabled. When duplicates are disabled, emulate the original mapping.
    [HarmonyPatch]
    internal static class GardenHasPerkForSlotPatch
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("UIGardenBedWindow");
            return type == null ? null : AccessTools.Method(type, "HasPerkForSlot");
        }

        private static object ResolveSlotPerk(object window, int uiSlotIndex)
        {
            IList perks = ReflectionUtil.GetGardenPerks(window);
            if (perks == null)
                return null;

            bool duplicates =
                MoreFertilizerSlotsPlugin.AllowDuplicateFertilizers != null &&
                MoreFertilizerSlotsPlugin.AllowDuplicateFertilizers.Value;

            if (duplicates)
            {
                if (uiSlotIndex >= 0 && uiSlotIndex < perks.Count)
                    return perks[uiSlotIndex];
                return null;
            }

            object wgo = ReflectionUtil.GetWindowWgo(window);
            for (int i = 0; i < perks.Count; i++)
            {
                object perk = perks[i];
                string id = ReflectionUtil.GetDefinitionId(perk);
                if (string.IsNullOrEmpty(id))
                    continue;

                int slot = ReflectionUtil.GetGameResInt(wgo, "perk_fertilize_" + id);
                if (slot > 0 && slot - 1 == uiSlotIndex)
                    return perk;
            }

            return null;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            Type perkType = AccessTools.TypeByName("PerkData");
            MethodInfo resolver = AccessTools.Method(typeof(GardenHasPerkForSlotPatch), "ResolveSlotPerk");

            if (perkType == null || resolver == null)
                return instructions;

            return new CodeInstruction[]
            {
                new CodeInstruction(OpCodes.Ldarg_2),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Call, resolver),
                new CodeInstruction(OpCodes.Castclass, perkType),
                new CodeInstruction(OpCodes.Stind_Ref),
                new CodeInstruction(OpCodes.Ldarg_2),
                new CodeInstruction(OpCodes.Ldind_Ref),
                new CodeInstruction(OpCodes.Ldnull),
                new CodeInstruction(OpCodes.Cgt_Un),
                new CodeInstruction(OpCodes.Ret)
            };
        }
    }

    internal static class DuplicateEffectHelper
    {
        internal static int GetExtraBonus(object craftParams, string bonusField)
        {
            if (MoreFertilizerSlotsPlugin.AllowDuplicateFertilizers == null ||
                !MoreFertilizerSlotsPlugin.AllowDuplicateFertilizers.Value ||
                MoreFertilizerSlotsPlugin.StackDuplicateEffects == null ||
                !MoreFertilizerSlotsPlugin.StackDuplicateEffects.Value)
                return 0;

            object wgo = ReflectionUtil.GetProperty(craftParams, "WgoData");
            object craftDef = ReflectionUtil.GetProperty(craftParams, "CraftDef");
            if (wgo == null || craftDef == null)
                return 0;

            IList active = ReflectionUtil.GetActivePerks(wgo);
            IList linked = ReflectionUtil.GetField(craftDef, "linkedPerks") as IList;
            if (active == null || linked == null)
                return 0;

            int extra = 0;

            for (int li = 0; li < linked.Count; li++)
            {
                string linkedId = linked[li] as string;
                if (string.IsNullOrEmpty(linkedId))
                    continue;

                int count = 0;
                object firstMatching = null;

                for (int ai = 0; ai < active.Count; ai++)
                {
                    object perk = active[ai];
                    if (string.Equals(ReflectionUtil.GetDefinitionId(perk), linkedId, StringComparison.Ordinal))
                    {
                        count++;
                        if (firstMatching == null)
                            firstMatching = perk;
                    }
                }

                if (count <= 1 || firstMatching == null)
                    continue;

                object def = ReflectionUtil.GetDefinition(firstMatching);
                if (def == null)
                    continue;

                int perCopy = ReflectionUtil.GetIntField(def, bonusField);
                extra += (count - 1) * perCopy;
            }

            return extra;
        }
    }

    [HarmonyPatch]
    internal static class MasteryStackPatch
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("CraftParamsData");
            return type == null ? null : AccessTools.Method(type, "GetWgoPerksCraftMasteryBonusValue");
        }

        private static void Postfix(object __instance, ref int __result)
        {
            __result += DuplicateEffectHelper.GetExtraBonus(__instance, "craftMasteryBonus");
        }
    }

    [HarmonyPatch]
    internal static class StartTicksStackPatch
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("CraftParamsData");
            return type == null ? null : AccessTools.Method(type, "GetWgoPerksCraftStartTicksBonusValue");
        }

        private static void Postfix(object __instance, ref int __result)
        {
            __result += DuplicateEffectHelper.GetExtraBonus(__instance, "craftStartTicks");
        }
    }

    [HarmonyPatch]
    internal static class TotalProgressStackPatch
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("CraftParamsData");
            return type == null ? null : AccessTools.Method(type, "GetWgoPerksCraftAddTotalProgressTicks");
        }

        private static void Postfix(object __instance, ref int __result)
        {
            __result += DuplicateEffectHelper.GetExtraBonus(__instance, "craftTotalProgressTicksBonus");
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

        private static void Postfix(object __instance)
        {
            GardenSlotUi.LayoutSlots(__instance);
        }
    }

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
        private const float EmptyStateVerticalGap = 8f;
        private const float GrowingScale = 0.76f;
        private const float GrowingHorizontalFactor = 0.82f;
        private const float GrowingRowGapFactor = 0.10f;
        private const float ProgressSafetyGapFactor = 0.20f;

        private static FieldInfo _perkWidgetsField;
        private static FieldInfo _craftProgressObjField;
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
                if (widgets == null || widgets.Count < 3)
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

                    SetIgnoreParentLayout(cloneObject, true);
                    widgets.Add(clone);
                }

                // Before vanilla UpdatePerks runs, give the original three widgets back to
                // the game's own LayoutGroup so we can read a fresh native reference layout.
                for (int i = 0; i < Math.Min(3, widgets.Count); i++)
                {
                    Component vanilla = widgets[i] as Component;
                    if (vanilla != null && vanilla.gameObject != null)
                    {
                        SetIgnoreParentLayout(vanilla.gameObject, false);
                        vanilla.transform.localScale = Vector3.one;
                    }
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
                ResolveFields(window.GetType());

                IList widgets = _perkWidgetsField == null ? null : _perkWidgetsField.GetValue(window) as IList;
                if (widgets == null || widgets.Count < 3)
                    return;

                int desired = Math.Min(MoreFertilizerSlotsPlugin.MaxSlots, widgets.Count);
                if (desired <= 3)
                    return;

                if (ReflectionUtil.GetWindowIsGrowing(window))
                    LayoutGrowing(window, widgets, desired);
                else
                    LayoutEmpty(widgets, desired);
            }
            catch (Exception ex)
            {
                if (MoreFertilizerSlotsPlugin.ModLog != null)
                    MoreFertilizerSlotsPlugin.ModLog.LogError("Failed to lay out fertilizer slots: " + ex);
            }
        }

        private static void LayoutEmpty(IList widgets, int desired)
        {
            RectTransform r0 = GetRect(widgets[0]);
            RectTransform r1 = GetRect(widgets[1]);
            RectTransform r2 = GetRect(widgets[2]);
            if (r0 == null || r1 == null || r2 == null)
                return;

            Vector3 w0 = r0.position;
            Vector3 w1 = r1.position;
            Vector3 w2 = r2.position;
            Vector3 step = (w2 - w0) * 0.5f;
            Vector3 rowCenter = (w0 + w1 + w2) / 3f;

            Vector3[] corners = new Vector3[4];
            r0.GetWorldCorners(corners);
            Vector3 down = corners[0] - corners[1];
            float renderedHeight = down.magnitude;
            if (renderedHeight < 0.0001f)
                down = Vector3.down;
            else
                down /= renderedHeight;

            Vector3 lowerCenter = rowCenter + down * (renderedHeight + renderedHeight * 0.10f);
            int extra = desired - 3;

            if (extra == 1)
            {
                PlaceWorld(widgets, 3, lowerCenter, Vector3.one);
            }
            else if (extra == 2)
            {
                PlaceWorld(widgets, 3, lowerCenter - step * 0.5f, Vector3.one);
                PlaceWorld(widgets, 4, lowerCenter + step * 0.5f, Vector3.one);
            }
            else if (extra == 3)
            {
                PlaceWorld(widgets, 3, lowerCenter - step, Vector3.one);
                PlaceWorld(widgets, 4, lowerCenter, Vector3.one);
                PlaceWorld(widgets, 5, lowerCenter + step, Vector3.one);
            }
            else
            {
                PlaceWorld(widgets, 3, lowerCenter - step * 2f, Vector3.one);
                PlaceWorld(widgets, 4, lowerCenter - step, Vector3.one);
                PlaceWorld(widgets, 5, lowerCenter, Vector3.one);
                PlaceWorld(widgets, 6, lowerCenter + step, Vector3.one);
            }
        }

        private static void LayoutGrowing(object window, IList widgets, int desired)
        {
            RectTransform r0 = GetRect(widgets[0]);
            RectTransform r1 = GetRect(widgets[1]);
            RectTransform r2 = GetRect(widgets[2]);
            if (r0 == null || r1 == null || r2 == null)
                return;

            // In the growing screen there is a progress UI directly below the fertilizer area.
            // Compact ALL fertilizer slots into two rows so the total height stays within the
            // original fertilizer zone.
            for (int i = 0; i < desired; i++)
            {
                Component c = widgets[i] as Component;
                if (c != null && c.gameObject != null)
                    SetIgnoreParentLayout(c.gameObject, true);
            }

            Vector3 w0 = r0.position;
            Vector3 w1 = r1.position;
            Vector3 w2 = r2.position;
            Vector3 nativeStep = (w2 - w0) * 0.5f;
            Vector3 horizontalStep = nativeStep * GrowingHorizontalFactor;
            Vector3 center = (w0 + w1 + w2) / 3f;

            Vector3[] corners = new Vector3[4];
            r0.GetWorldCorners(corners);

            Vector3 down = corners[0] - corners[1];
            float nativeHeight = down.magnitude;
            if (nativeHeight < 0.0001f)
            {
                down = Vector3.down;
                nativeHeight = 1f;
            }
            else
            {
                down /= nativeHeight;
            }

            float scaledHeight = nativeHeight * GrowingScale;
            float rowDistance = scaledHeight * (1f + GrowingRowGapFactor);

            // Keep the top row close to the vanilla fertilizer row and the lower row immediately below.
            Vector3 topCenter = center - down * (nativeHeight * 0.08f);
            Vector3 bottomCenter = topCenter + down * rowDistance;

            // If the progress widget is active, treat its top edge as a hard lower boundary.
            RectTransform progress = GetCraftProgressRect(window);
            if (progress != null && progress.gameObject.activeInHierarchy)
            {
                Vector3[] pc = new Vector3[4];
                progress.GetWorldCorners(pc);

                float progressTopDistance = float.MaxValue;
                for (int i = 0; i < 4; i++)
                {
                    float d = Vector3.Dot(pc[i] - center, down);
                    if (d < progressTopDistance)
                        progressTopDistance = d;
                }

                float bottomCenterDistance = Vector3.Dot(bottomCenter - center, down);
                float bottomEdgeDistance = bottomCenterDistance + scaledHeight * 0.5f;
                float safeDistance = progressTopDistance - nativeHeight * ProgressSafetyGapFactor;

                if (bottomEdgeDistance > safeDistance)
                {
                    float shiftUp = bottomEdgeDistance - safeDistance;
                    topCenter -= down * shiftUp;
                    bottomCenter -= down * shiftUp;
                }
            }

            int topCount = desired <= 4 ? desired : 4;
            int bottomCount = desired - topCount;

            PlaceCenteredRow(widgets, 0, topCount, topCenter, horizontalStep, GrowingScale);
            if (bottomCount > 0)
                PlaceCenteredRow(widgets, topCount, bottomCount, bottomCenter, horizontalStep, GrowingScale);
        }

        private static void PlaceCenteredRow(
            IList widgets, int start, int count, Vector3 center, Vector3 step, float scale)
        {
            if (count <= 0) return;

            float mid = (count - 1) * 0.5f;
            Vector3 s = new Vector3(scale, scale, 1f);

            for (int i = 0; i < count; i++)
            {
                Vector3 pos = center + step * (i - mid);
                PlaceWorld(widgets, start + i, pos, s);
            }
        }

        private static RectTransform GetCraftProgressRect(object window)
        {
            if (_craftProgressObjField == null || window == null)
                return null;

            GameObject go = _craftProgressObjField.GetValue(window) as GameObject;
            return go == null ? null : go.transform as RectTransform;
        }

        private static RectTransform GetRect(object widget)
        {
            Component component = widget as Component;
            return component == null ? null : component.transform as RectTransform;
        }

        private static void PlaceWorld(IList widgets, int index, Vector3 worldPosition, Vector3 scale)
        {
            if (index < 0 || index >= widgets.Count)
                return;

            RectTransform rect = GetRect(widgets[index]);
            if (rect == null)
                return;

            SetIgnoreParentLayout(rect.gameObject, true);
            rect.localScale = scale;
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

        private static void ResolveFields(Type type)
        {
            if (_perkWidgetsField == null)
                _perkWidgetsField = AccessTools.Field(type, "perkWidgets");
            if (_craftProgressObjField == null)
                _craftProgressObjField = AccessTools.Field(type, "craftProgressObj");
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
