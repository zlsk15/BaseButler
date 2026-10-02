using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace BaseButler
{
    /// <summary>
    /// BaseButler（基地管家）v2.0.12 唯一入口，统一加载 A/B 模块：
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

            // Windows 智能应用控制(SAC)会拦截 interop 程序集的按路径加载(0x800711C7)：
            // Harmony 解析补丁签名 / 补丁方法体 JIT 时，AssemblyResolve→LoadFrom 被拦，
            // 轻则模块注册失败，重则异常直接抛在游戏 UI 的消息 trampoline(OnMessageFromJS)里。
            // 修复：模块注册前先用内存加载把所需程序集注册进进程——Load(byte[]) 不携带文件映像路径，
            // 不触发 SAC 的文件策略检查；程序集名一经注册，后续解析不再走 LoadFrom 路径。
            PreloadInteropAssemblies();

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

        /// <summary>
        /// 预加载必要的 interop 程序集：把程序集名提前注册进进程，避免 Harmony 打补丁/补丁方法体 JIT
        /// 时经 AssemblyResolve→LoadFrom 触发 Smart App Control 的文件拦截(0x800711C7)。
        /// 清单覆盖当前已确认的冷依赖：Vuplex.WebView(OnMessageFromJS 签名)、UnityEngine.InputLegacyModule(Input.GetKey)、
        /// UnityEngine.CoreModule(UnityEngine 基础)。每种先查已加载，未加载则内存加载，失败回退 LoadFile，
        /// 再失败仅记日志（对应功能降级，不影响其它模块）。
        /// </summary>
        private static void PreloadInteropAssemblies()
        {
            string[] names = { "Vuplex.WebView", "UnityEngine.InputLegacyModule", "UnityEngine.CoreModule" };
            foreach (var name in names)
            {
                try
                {
                    bool loaded = false;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name == name) { loaded = true; break; }
                    }
                    if (loaded) continue;

                    string path = Path.Combine(Paths.BepInExRootPath, "interop", name + ".dll");
                    if (!File.Exists(path))
                    {
                        Log.LogWarning($"[BaseButler] 未找到 interop\\{name}.dll，跳过预加载。");
                        continue;
                    }

                    try
                    {
                        Assembly.Load(File.ReadAllBytes(path));
                        Log.LogInfo($"[BaseButler] {name} 内存预加载成功（绕过应用控制策略文件拦截）。");
                    }
                    catch (Exception e1)
                    {
                        try
                        {
                            Assembly.LoadFile(path);
                            Log.LogInfo($"[BaseButler] {name} LoadFile 预加载成功。");
                        }
                        catch (Exception e2)
                        {
                            Log.LogWarning($"[BaseButler] {name} 预加载失败：Load(byte[])={e1.Message} | LoadFile={e2.Message}");
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.LogWarning($"[BaseButler] {name} 预加载异常：{e.Message}");
                }
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

        /// <summary>
        /// v2.0.12：按 N 网玩家反馈移除【首次启动自动配置 Windows Defender 白名单】功能——
        /// 自动加白名单会使 Defender 跳过整个游戏/BepInEx 目录的扫描、排除项在卸载后仍残留，
        /// 风险大于收益。防误杀改为在 FAQ / 描述中引导玩家手动加白名单。
        /// </summary>
    }

    public static class PluginInfo
    {
        public const string GUID = "com.basebutler.mod";
        public const string Name = "BaseButler";
        public const string Version = "2.0.12";
    }
}