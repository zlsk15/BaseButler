using System;
using System.Collections.Generic;
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
            SourceFilterConfig.Load(Config);
            WebViewHtmlPatcher.ApplyAll(Log);
            var harmony = new Harmony(PluginInfo.GUID);
            SafePatch.ApplyAll(harmony);
            RuntimeProbe.Dump();
            Log.LogInfo($"{PluginInfo.Name} v{PluginInfo.Version} 已加载：烹饪（灶台/火炉）面板食材来源已扩展为所有带储物背包的家具。");
        }
    }

    public static class PluginInfo
    {
        public const string GUID = "com.cookingsourceexpand.mod";
        public const string Name = "CookingSourceExpand";
        public const string Version = "v1.5.6";
    }

    /// <summary>
    /// 来源箱子过滤配置（玩家可调，纯后端数据过滤）。
    /// 箱子太多导致来源列表过长时，可在 BepInEx\config\CookingSourceExpand.cfg 里：
    ///   · IncludeBoxConfigIds      非空时仅把这些箱子类型(configId)作为来源（白名单）
    ///   · ExtraExcludedBoxConfigIds 这些箱子类型(configId)总是被排除，即使命中默认白名单
    /// 留空即维持 v1.3.0 的"全部默认储物箱子"行为。
    /// </summary>
    internal static class SourceFilterConfig
    {
        public static readonly HashSet<int> IncludeSet = new HashSet<int>();
        public static readonly HashSet<int> ExtraExcludeSet = new HashSet<int>();

        public static void Load(BepInEx.Configuration.ConfigFile config)
        {
            if (config == null) return;
            IncludeSet.Clear();
            ExtraExcludeSet.Clear();
            try
            {
                var inc = config.Bind<string>("Sources", "IncludeBoxConfigIds", string.Empty,
                    "仅把这些箱子类型(configId)作为来源，多个用英文逗号分隔；留空=使用全部默认储物箱子。");
                var exc = config.Bind<string>("Sources", "ExtraExcludedBoxConfigIds", string.Empty,
                    "额外排除这些箱子类型(configId)，多个用英文逗号分隔；即使命中默认白名单也会被排除。");
                Fill(IncludeSet, inc.Value);
                Fill(ExtraExcludeSet, exc.Value);
                CookingSourceExpandPlugin.Log.LogInfo(
                    $"[CookingSourceExpand] 来源过滤配置：白名单[{string.Join(",", IncludeSet)}] 额外排除[{string.Join(",", ExtraExcludeSet)}]");
            }
            catch (Exception e)
            {
                CookingSourceExpandPlugin.Log.LogError($"[CookingSourceExpand] 读取来源过滤配置失败：{e.Message}");
            }
        }

        private static void Fill(HashSet<int> set, string raw)
        {
            if (string.IsNullOrEmpty(raw)) return;
            foreach (var part in raw.Split(new[] { ',', '，', ';', '；', ' ', '\t', '\n' },
                                           StringSplitOptions.RemoveEmptyEntries))
            {
                int v;
                if (int.TryParse(part.Trim(), out v)) set.Add(v);
            }
        }

        /// <summary>箱子是否允许作为来源：先看额外排除，再看白名单（白名单非空时必须命中）。</summary>
        public static bool IsAllowed(int configId)
        {
            if (IncludeSet.Count == 0 && ExtraExcludeSet.Count == 0) return true;
            if (ExtraExcludeSet.Contains(configId)) return false;
            if (IncludeSet.Count > 0 && !IncludeSet.Contains(configId)) return false;
            return true;
        }
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
            TryPatch(typeof(CookingUI.WebUILayer), "OnPageMessage",
                     typeof(LogBridgePatch), "Prefix", "前端诊断日志桥");
            // 无人机 TradeUI 走 ShowUI→RebuildBagTabs（实测不从 SetContainerTabs 进入）
            TryPatchByNameAll(typeof(CookingUI.Ac_TradeUI_ShowUI), "SendAction",
                     typeof(TradeSourceInjection.ShowUICachePatch), "无人机来源缓存");
            TryPatchByNameAll(typeof(CookingUI.Reducer_Web_TradeUI), "RebuildBagTabs",
                     typeof(TradeSourceInjection.RebuildTabsPatch), "无人机来源注入");
            // SelectItemsForRecipe 位于 reducer（配方选择热路径），注入读写 Il2Cpp 字典会冻结面板，
            // 已整体撤销该路径的补丁；跨柜取料由 CabinetBags 扩展（工作台来源）承担。
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
            var post = patchType.GetMethod("Postfix",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic);
            if (pre == null && post == null)
            {
                CookingSourceExpandPlugin.Log.LogWarning($"[CookingSourceExpand] 找不到补丁方法 {patchType.FullName}.Prefix/Postfix，跳过「{label}」。");
                return;
            }
            int attached = 0;
            foreach (var m in targetType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                if (m.Name != methodName) continue;
                try
                {
                    _harmony.Patch(m,
                        prefix: pre == null ? null : new HarmonyMethod(pre),
                        postfix: post == null ? null : new HarmonyMethod(post));
                    attached++;
                    CookingSourceExpandPlugin.Log.LogInfo($"[CookingSourceExpand] ✓ 已挂载补丁：「{label}」 → {targetType.FullName}.{methodName}（重载 {m}）");
                }
                catch (Exception e)
                {
                    CookingSourceExpandPlugin.Log.LogWarning($"[CookingSourceExpand] 「{label}」跳过不兼容重载 {methodName}: {e.Message}");
                }
            }
            if (attached == 0) CookingSourceExpandPlugin.Log.LogWarning($"[CookingSourceExpand] 「{label}」未找到任何可挂载重载，自动停用。");
        }
    }
}