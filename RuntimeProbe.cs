using System;
using System.Linq;
using System.Reflection;
using CookingUI = GameCore.HotUpdate.ReduxUI;

namespace CookingSourceExpand
{
    /// <summary>
    /// 进程内反射探针：在游戏运行时（所有 IL2CPP 类型都能被正常解析的上下文）枚举
    /// 与无人机 / 交易 / 救援相关的 ReduxUI 动作类型与方法，把方法签名打进 BepInEx 日志。
    /// 用途：定位"无人机救助界面只列冰箱"背后真正调用的后端来源枚举方法，
    /// 以便把它纳入 CookingSourceExpand 的来源扩展，而不是挂错的 Ac_TradeUI_SetContainerTabs。
    /// </summary>
    internal static class RuntimeProbe
    {
        private static readonly string[] Keywords =
            { "drone", "trade", "rescue", "rescu", "救助", "救援", "互助", "deliver", "transport" };

        internal static void Dump()
        {
            try
            {
                var asm = typeof(CookingUI.Ac_TradeUI_SetContainerTabs).Assembly;
                var types = asm.GetTypes();
                var hits = types
                    .Where(t =>
                        (t.Namespace ?? "").Contains("ReduxUI")
                        && (t.Name.StartsWith("Ac_") || t.Name.Contains("Bag") || t.Name.Contains("Container") || t.Name.Contains("Trade") || t.Name.Contains("Drone") || t.Name.Contains("Rescue"))
                        && Keywords.Any(k => t.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                    .OrderBy(t => t.Name)
                    .ToArray();

                CookingSourceExpandPlugin.Log.LogInfo($"[RuntimeProbe] 命中动作类型 {hits.Length} 个：");
                foreach (var t in hits)
                {
                    var full = t.FullName ?? t.Name;
                    CookingSourceExpandPlugin.Log.LogInfo($"[RuntimeProbe]  TYPE {full}");
                    try
                    {
                        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                        {
                            if (Keywords.Any(k => m.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                                || m.Name.Contains("Bag") || m.Name.Contains("Container") || m.Name.Contains("Fridge")
                                || m.Name.Contains("Source") || m.Name.StartsWith("Get") || m.Name.StartsWith("Set"))
                            {
                                var ps = string.Join(",", m.GetParameters().Select(p => (p.ParameterType.Name) + " " + (p.Name ?? "")));
                                CookingSourceExpandPlugin.Log.LogInfo($"[RuntimeProbe]    M {m.ReturnType.Name} {m.Name}({ps})");
                            }
                        }
                    }
                    catch (Exception mex)
                    {
                        CookingSourceExpandPlugin.Log.LogInfo($"[RuntimeProbe]    (method enum err {mex.Message})");
                    }
                }
            }
            catch (Exception ex)
            {
                CookingSourceExpandPlugin.Log.LogError($"[RuntimeProbe] 枚举失败：{ex}");
            }
        }
    }
}