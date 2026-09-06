using System;
using System.Reflection;
using HarmonyLib;
using CookingUI = GameCore.HotUpdate.ReduxUI;

namespace CookingSourceExpand
{
    /// <summary>
    /// 无人机 TradeUI 路由探测：暂不作为修复，只把"打开无人机界面时到底调用了哪个
    /// Ac_TradeUI_* 动作 / reducer"打进日志。据截图确认该界面是 TradeUI，但
    /// Ac_TradeUI_SetContainerTabs 始终未触发，说明面板走的是 OpenUI/OpenSupply 等入口。
    /// 用 __originalMethod 特殊参数写 Prefix 可挂到任意签名的方法上，逐个打印调用点。
    /// </summary>
    internal static class TradeRouteDebug
    {
        internal static void Attach(Harmony harmony)
        {
            var pre = new HarmonyMethod(typeof(TradeRouteDebug).GetMethod("Prefix",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));
            // 所有无人机交易相关动作类（均在 GameCore.HotUpdate.ReduxUI）
            Type[] actionTypes =
            {
                typeof(CookingUI.Ac_TradeUI_OpenUI), typeof(CookingUI.Ac_TradeUI_ShowUI),
                typeof(CookingUI.Ac_TradeUI_OpenSupply), typeof(CookingUI.Ac_TradeUI_OpenDonate),
                typeof(CookingUI.Ac_TradeUI_OpenRoster), typeof(CookingUI.Ac_TradeUI_Reset),
                typeof(CookingUI.Ac_TradeUI_SetContainerTabs), typeof(CookingUI.Ac_TradeUI_SwitchBag),
                typeof(CookingUI.Ac_TradeUI_SwitchTabByKey), typeof(CookingUI.Ac_TradeUI_Sort),
                typeof(CookingUI.Ac_TradeUI_ClickItem), typeof(CookingUI.Ac_TradeUI_Deal),
                typeof(CookingUI.Ac_TradeUI_PickQty), typeof(CookingUI.Ac_TradeUI_PickRoster),
                typeof(CookingUI.Ac_TradeUI_SelectSupplyTarget),
            };
            foreach (var t in actionTypes)
                TryPatchAction(harmony, t, "SendAction", pre);
            TryPatchNamed(harmony, typeof(CookingUI.Reducer_Web_TradeUI), "RA_SetContainerTabs", pre, "ra_settabs");
            TryPatchNamed(harmony, typeof(CookingUI.Reducer_Web_TradeUI), "RebuildBagTabs", pre, "rebuild_tabs");
            TryPatchNamed(harmony, typeof(CookingUI.Reducer_Web_TradeUI), "RefreshBagTabsView", pre, "refresh_tabs");
        }

        private static void TryPatchAction(Harmony harmony, Type t, string methodName, HarmonyMethod pre)
        {
            try
            {
                if (t == null) { LogSkip("null"); return; }
                var m = AccessTools.Method(t, methodName);
                if (m == null) { LogSkip(t.Name + "." + methodName); return; }
                harmony.Patch(m, prefix: pre);
                CookingSourceExpandPlugin.Log.LogInfo($"[TradeRoute] ✓ probe → {t.FullName}.{methodName}");
            }
            catch (Exception e)
            {
                CookingSourceExpandPlugin.Log.LogInfo($"[TradeRoute] 挂载 {t?.Name} 失败：{e.Message}");
            }
        }

        private static void TryPatchNamed(Harmony harmony, Type t, string methodName, HarmonyMethod pre, string tag)
        {
            try
            {
                if (t == null) { LogSkip(tag); return; }
                int n = 0;
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    if (m.Name != methodName) continue;
                    try { harmony.Patch(m, prefix: pre); n++; }
                    catch (Exception) { }
                }
                CookingSourceExpandPlugin.Log.LogInfo($"[TradeRoute] ✓ probe {tag} ({n} overloads) → {t.FullName}.{methodName}");
            }
            catch (Exception e)
            {
                CookingSourceExpandPlugin.Log.LogInfo($"[TradeRoute] 挂载 {tag} 失败：{e.Message}");
            }
        }

        private static void LogSkip(string what)
        {
            CookingSourceExpandPlugin.Log.LogInfo($"[TradeRoute] 跳过（找不到） {what}");
        }

        private static void Prefix(MethodBase __originalMethod)
        {
            try
            {
                var t = __originalMethod?.DeclaringType;
                CookingSourceExpandPlugin.Log.LogInfo($"[TradeRoute] ★CALL {t?.FullName ?? "?"}.{__originalMethod?.Name}");
            }
            catch (Exception) { }
        }
    }
}