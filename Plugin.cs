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
        public const string Version = "1.0.0";
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
    }
}