using System;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using CookingUI = GameCore.HotUpdate.ReduxUI;

namespace CookingSourceExpand
{
    [BepInPlugin(PluginInfo.GUID, PluginInfo.Name, PluginInfo.Version)]
    [BepInProcess("SurvivalLog.exe")]
    public class CookingSourceExpandPlugin : BasePlugin
    {
        internal static new ManualLogSource Log;
        internal static long[] WorkbenchSourceOwners; // 工作台跨面板取料来源缓存，仅在打开工作台的安全上下文枚举填充

        public override void Load()
        {
            Log = base.Log;
            var harmony = new Harmony(PluginInfo.GUID);
            SafePatch.ApplyAll(harmony);
            Log.LogInfo($"{PluginInfo.Name} v{PluginInfo.Version} 已加载：烹饪（灶台/火炉）面板食材来源已扩展为所有带储物背包的家具。");
        }
    }

    public static class PluginInfo
    {
        public const string GUID = "com.cookingsourceexpand.mod";
        public const string Name = "CookingSourceExpand";
        public const string Version = "v1.3.0";
    }

    /// <summary>
    /// 抗更新兜底：不用 [HarmonyPatch]+PatchAll 的硬绑定，而是逐一用 AccessTools 显式
    /// 查找目标 SendAction；找不到目标、参数变化或打补丁异常时都静默跳过并记日志，
    /// 绝不让单条补丁失败把异常抛到 BepInEx 插件加载里，避免游戏启动失败。
    /// </summary>
    internal static class SafePatch
    {
        private static Harmony _harmony;

        public static void ApplyAll(Harmony harmony)
        {
            _harmony = harmony;
            TryPatch(typeof(CookingUI.Ac_Item_GetCookingBagList), "SendAction",
                     typeof(CookingBagPatch.AppendShelfSourcesPatch), "Prefix", "烹饪来源");
            TryPatch(typeof(CookingUI.Ac_Cooking_Open), "SendAction",
                     typeof(CookingBagPatch.AppendShelvesToCookingOpenPatch), "Prefix", "烹饪面板");
            TryPatch(typeof(CookingUI.Ac_Item_GetHandMadeBagList), "SendAction",
                     typeof(CookingBagPatch.AppendShelvesToHandMadeBagListPatch), "Prefix", "手工制作来源");
            TryPatch(typeof(CookingUI.Ac_ToolTable_Open), "SendAction",
                     typeof(CookingBagPatch.AppendShelvesToToolTableOpenPatch), "Prefix", "工作台来源");
            TryPatch(typeof(CookingUI.Ac_TradeUI_SetContainerTabs), "SendAction",
                     typeof(CookingBagPatch.AppendShelvesToTradeContainerTabsPatch), "Prefix", "无人机交易来源");
            TryPatchExact(typeof(CookingUI.Reducer_Web_ToolTable), "SelectItemsForRecipe",
                     new Type[] { typeof(Il2CppSystem.Collections.Generic.Dictionary<int, int>), typeof(long[]).MakeByRefType() },
                     typeof(CookingBagPatch.WorkbenchCrossSourcePatch), "工作台跨面板取料");
        }

        private static void TryPatch(Type targetType, string methodName, Type patchType, string patchMethod, string label)
        {
            try
            {
                var patch = AccessTools.Method(patchType, patchMethod);
                if (patch == null)
                {
                    CookingSourceExpandPlugin.Log.LogWarning($"[CookingSourceExpand] 找不到补丁方法 {patchType.FullName}.{patchMethod}，跳过「{label}」。");
                    return;
                }

                // Prefix 的参数类型 == 目标 SendAction 的参数列表，精确匹配，避免多重重载误绑
                var pars = patch.GetParameters().Select(p => p.ParameterType).ToArray();
                var target = AccessTools.Method(targetType, methodName, pars);
                if (target == null)
                {
                    CookingSourceExpandPlugin.Log.LogWarning($"[CookingSourceExpand] 找不到目标 {targetType.FullName}.{methodName}（游戏可能已更新），「{label}」自动停用。");
                    return;
                }

                _harmony.Patch(target, prefix: new HarmonyMethod(patch));
                CookingSourceExpandPlugin.Log.LogInfo($"[CookingSourceExpand] ✓ 已挂载补丁：「{label}」 → {targetType.FullName}.{methodName}");
            }
            catch (Exception e)
            {
                // 任何打补丁异常都不能向外抛，避免拖垮 BepInEx 插件加载导致游戏启动失败
                CookingSourceExpandPlugin.Log.LogError($"[CookingSourceExpand] 打「{label}」补丁失败（已安全跳过）：{e.Message}");
            }
        }

        /// <summary>按方法名挂载所有同名重载（用于含 ref 参数的补丁，参数精确匹配会因 ByRef 差异误失配）。</summary>
        private static void TryPatchByNameAll(Type targetType, string methodName, Type patchType, string label)
        {
            var pre = patchType.GetMethod("Prefix",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic);
            if (pre == null)
            {
                CookingSourceExpandPlugin.Log.LogWarning($"[CookingSourceExpand] 找不到补丁方法 {patchType.FullName}.Prefix，跳过「{label}」。");
                return;
            }
            foreach (var m in targetType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                if (m.Name != methodName) continue;
                try
                {
                    _harmony.Patch(m, prefix: new HarmonyMethod(pre));
                    CookingSourceExpandPlugin.Log.LogInfo($"[CookingSourceExpand] ✓ 已挂载补丁：「{label}」 → {targetType.FullName}.{methodName}");
                }
                catch (Exception e)
                {
                    CookingSourceExpandPlugin.Log.LogWarning($"[CookingSourceExpand] 「{label}」跳过不兼容重载 {methodName}: {e.Message}");
                }
            }
        }

        /// <summary>按精确签名挂载 Prefix + Postfix（避免 ref/ByRef 差异导致失配或误挂其它重载）。</summary>
        private static void TryPatchExact(Type targetType, string methodName, Type[] argTypes, Type patchType, string label)
        {
            var pre = patchType.GetMethod("Prefix",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic);
            if (pre == null)
            {
                CookingSourceExpandPlugin.Log.LogWarning($"[CookingSourceExpand] 找不到补丁方法 {patchType.FullName}.Prefix，跳过「{label}」。");
                return;
            }
            try
            {
                var m = AccessTools.Method(targetType, methodName, argTypes);
                if (m == null)
                {
                    CookingSourceExpandPlugin.Log.LogWarning($"[CookingSourceExpand] 未找到「{label}」精确签名，跳过。");
                    return;
                }
                var post = patchType.GetMethod("Postfix",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic);
                _harmony.Patch(m,
                    prefix: new HarmonyMethod(pre),
                    postfix: post == null ? null : new HarmonyMethod(post));
                CookingSourceExpandPlugin.Log.LogInfo($"[CookingSourceExpand] ✓ 已挂载补丁：「{label}」 → {targetType.FullName}.{methodName}");
            }
            catch (Exception e)
            {
                CookingSourceExpandPlugin.Log.LogWarning($"[CookingSourceExpand] 「{label}」挂载异常：{e.Message}");
            }
        }
    }
}