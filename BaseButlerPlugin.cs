using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace BaseButler
{
    /// <summary>
    /// BaseButler（基地管家）v2.0.1 唯一入口，统一加载 A/B 模块：
    ///   A 来源扩展   SourceExpand（原 CookingSourceExpand 全部行为继承，
    ///                            吸收 IsCookingFurnitureBag 直取放行 + 菜谱稳定排序）
    ///   B 堆叠优化   Stack（搬运自 SLTweaksSplit：入包自动合并 + 右键拆分 + 单格堆叠上限扩展）
    /// 两个模块共享同一 Harmony 实例、同一 Config 文件与同一日志，共用一个 DLL。
    /// 仓储管家（原 B 模块 StorageButler）因未稳定触发，已隔离停用（源码保留，不再加载）。
    /// 时间档位（原 AutoTimeBoost 模块）已整体移除，不再提供切档能力。
    /// </summary>
    [BepInPlugin(PluginInfo.GUID, PluginInfo.Name, PluginInfo.Version)]
    [BepInProcess("SurvivalLog.exe")]
    public sealed class BaseButlerPlugin : BasePlugin
    {
        internal static new ManualLogSource Log;
        private Harmony _harmony;

        public override void Load()
        {
            Log = base.Log;

            // 旧独立 DLL 仍在 plugins 目录 → 提示删除，避免 A/B 被双 patch / 重复加载
            DetectLegacyDlls();

            _harmony = new Harmony(PluginInfo.GUID);

            // 每个模块独立装载：任一模 Init 内意外异常，只记日志并继续，绝不连累其它模块
            Safe("A 来源扩展(SourceExpand)", () => SourceExpand.CookingSourceExpandPlugin.Init(Config, Log, _harmony));
            Safe("B 堆叠优化(Stack)",          () => Stack.Plugin.Init(Config, Log, _harmony));
            // 仓储管家（原 B）因未稳定触发已隔离停用：源码保留在 StorageButler_Plugin.cs，不再在入口装载。

            Log.LogMessage($"{PluginInfo.Name} v{PluginInfo.Version} 已加载：A 来源扩展 + B 堆叠优化，共用一个 DLL（仓储管家隔离、时间档位移除）。");
        }

        private static void Safe(string name, Action init)
        {
            try { init(); }
            catch (Exception e)
            {
                Log.LogError($"[BaseButler] 模块 {name} 初始化失败（不影响其它模块）：{e}");
            }
        }

        public override bool Unload()
        {
            try { _harmony?.UnpatchSelf(); }
            catch (Exception) { }
            return true;
        }

        private static void DetectLegacyDlls()
        {
            try
            {
                var pluginsDir = Path.Combine(Paths.BepInExRootPath, "plugins");
                string[] legacy = { "CookingSourceExpand.dll", "AutoTimeBoost.dll", "SLCookLink.dll", "SLTweaksSplit.dll",
                        "SLCraftLink.dll", "SLCraftFix.dll", "SLBagTweak.dll", "SLPowerTweak.dll", "SLSpeedTweak.dll", "BaseButlerProbe.dll" };
                foreach (var name in legacy)
                {
                    if (File.Exists(Path.Combine(pluginsDir, name)))
                        Log.LogWarning($"[BaseButler] 检测到旧插件 {name} 仍在 BepInEx\\plugins，为避免重复 patch，建议删除后重进游戏。");
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("[BaseButler] 检测旧插件失败：" + e.Message);
            }
        }
    }

    public static class PluginInfo
    {
        public const string GUID = "com.basebutler.mod";
        public const string Name = "BaseButler";
        public const string Version = "2.0.1";
    }
}