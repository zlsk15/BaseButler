using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using GameCore.HotUpdate;
using GameCore.HotUpdate.Battle.Logic;
using GameCore.HotUpdate.ReduxUI;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using R3;
using UnityEngine;
using Vuplex.WebView;

namespace BaseButler.Stack
{
    public static class TelemetryProbe
    {
        public static BattleLogicWorld TryGetLiveWorld()
        {
            try
            {
                return BaseSingleton<BattleLogicWorld>.Instance;
            }
            catch
            {
                return null;
            }
        }
    }

    public static class CookingNoStack
    {
        public static bool Suppress;

        public static bool SuppressAll;
    }

    public static class CookingNoStackPatch
    {
        public static void Postfix(ItemManager __instance, long ownerId, int itemConfigId, ref ItemData __result)
        {
            try
            {
                if (__instance == null)
                {
                    return;
                }
                if (CookingNoStack.SuppressAll)
                {
                    __result = null;
                    return;
                }
                if (CookingNoStack.Suppress && __instance.IsCookingFurnitureBag(ownerId))
                {
                    __result = null;
                    return;
                }
                if (__result != null)
                {
                    return;
                }
                // 原版找不到可并的堆（多半是逐实例数据不一致）→ 对普通材料放宽，
                // 直接并进已有的同类堆，从源头避免"同种物品分成两堆、之后再也合不上"。
                __result = PreMergeSystem.TryFindRelaxedTarget(__instance, ownerId, itemConfigId);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// ItemManager.AddItem 后缀（SLTweaksSplit 1.2.0 新增）——"落袋即合并"，有保质期的物品也适用。
    ///
    /// 物品刚被放进容器后，催一次后台合并，不必等满 AutoMergeIntervalSec 的扫描间隔。
    ///
    /// 为什么不让 PreMerge 直接把新物品并进旧堆：AddItem 命中已有堆时只加数量、
    /// **保留目标堆的时效**，新物品更新的保质期会被丢掉（结果反而更短）。
    /// 走"落袋后立刻合并"这条路，用的是 AutoMerge 里那套【先把同一子组统一到最长保质期、再并堆】，
    /// 结果才是用户要的"按保质期长的来"。
    ///
    /// 本后缀只置一个时间戳、**不动任何物品数据**，不会和调用方（还在用 __result）打架。
    /// </summary>
    public static class MergeOnAddPatch
    {
        public static void Postfix()
        {
            try
            {
                if (!AutoMergeSystem.MergeOnAdd) return;
                AutoMergeSystem.RequestImmediateRun();
            }
            catch
            {
            }
        }
    }

    public static class DropOneSystem
    {
        private static bool _done;

        private static float _retryAt;

        private static Type _featType;

        private static FieldInfo _enabledField;

        internal static bool Enabled = true;

        public static void Tick()
        {
            if (_done || !Enabled || !SplitConfig.Enabled || Time.unscaledTime < _retryAt)
            {
                return;
            }
            try
            {
                TryEnableDropOne();
                _done = true;
            }
            catch (Exception ex)
            {
                _retryAt = Time.unscaledTime + 5f;
                Plugin.LogSource.LogError("[堆叠拆分] 设置失败，5秒后重试: " + ex.Message);
            }
        }

        // 直接写 DropOneFeature.Enabled 会在方法首次执行时于 IL2CPP trampoline 阶段解析类型；
        // 游戏版本若删/改了该类型，TypeLoadException 会发生在 try/catch 之外直接崩。
        // 改用反射安全查找：类型存在才设置，找不到就静默跳过。
        private static void TryEnableDropOne()
        {
            if (_featType == null)
            {
                Assembly hotUpdate = null;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name == "HotUpdate")
                    {
                        hotUpdate = asm;
                        break;
                    }
                }
                if (hotUpdate != null)
                {
                    _featType = hotUpdate.GetType("GameCore.HotUpdate.DropOneFeature");
                }
            }
            if (_featType == null)
            {
                Plugin.LogSource.LogInfo("[堆叠拆分] 未找到 DropOneFeature（当前游戏版本无该类型），丢弃键只丢一个功能跳过");
                return;
            }
            if (_enabledField == null)
            {
                _enabledField = _featType.GetField("Enabled", BindingFlags.Static | BindingFlags.Public);
            }
            if (_enabledField == null)
            {
                Plugin.LogSource.LogInfo("[堆叠拆分] 未找到 DropOneFeature.Enabled 字段，丢弃键只丢一个功能跳过");
                return;
            }
            _enabledField.SetValue(null, true);
            Plugin.LogSource.LogInfo("[堆叠拆分] DropOneFeature.Enabled = true（丢弃键只丢一个）");
        }
    }

    internal static class Plugin
    {
        public static ManualLogSource LogSource;

        public static void Init(BepInEx.Configuration.ConfigFile cfg, ManualLogSource log, Harmony harmony)
        {
            LogSource = log;
            var Log = log; // 兼容原 Load() 内基类 base.Log 的 Log.xxx 引用
            try
            {
                SplitConfig.Enabled = cfg.Bind("Split", "Enabled", true, "堆叠拆分总开关（关闭后 Shift+丢弃键丢一个、右键拆分转移均失效）。开启后通用拆分引擎覆盖所有容器 UI（背包/工作台/烹饪/酿造/鼠笼/压缩机/粉碎机/加热器/燃料发电机等），无法识别的 UI 自动放行原版转移").Value;
                SplitConfig.HalfKey = ParseKey(cfg.Bind("Split", "HalfModifier", "LeftShift", "「拆一半」修饰键（Unity KeyCode 名，如 LeftShift/LeftControl/Tab；右键固定，按住该键+右键=转移一半）").Value, (KeyCode)304, "HalfModifier");
                SplitConfig.OneKey = ParseKey(cfg.Bind("Split", "OneModifier", "LeftAlt", "「转移 1 个」修饰键（Unity KeyCode 名，如 LeftAlt/LeftControl/Tab；右键固定，按住该键+右键=转移 1 个）").Value, (KeyCode)308, "OneModifier");
                SplitConfig.CookingNoStack = cfg.Bind("Split", "CookingNoStack", true, "烹饪家具内不堆叠：拆分转移进烹饪家具的物品逐件独立存放（烹饪按格子/实例计份，故可识别多份）。关闭后按堆叠整体放入").Value;
                // 堆叠优化总开关（[ItemStack] Enabled）：false 时整模块停用 —— 不抬堆叠上限、
                // 不自动合并/落袋合并、不启用丢弃键丢一；右键拆分转移由 [Split] Enabled 单独控制
                // （可临时开它来拆开历史遗留的超量堆）；模块A 一键制作依赖的 BB_SPLIT_MOVE 拆取
                // 通道始终保留，不受本开关影响。
                // ★ v2.0.11：支持游戏内可视化开关（工作台右上角「堆叠:开/关」按钮，随时切换、写回 cfg）。
                //   因此所有子开关改为无条件绑定为 ConfigEntry 字段引用，Init 末尾统一 SetStackEnabled 刷新；
                //   不再用 if (!stackEnabled) return 提前退出——合并类补丁与后台行为常驻，运行时即可双向切换。
                _stackEntry = cfg.Bind("ItemStack", "Enabled", true,
                    "堆叠优化总开关（false=停用：不抬堆叠上限、不自动合并/落袋合并、丢弃键丢一停用；右键拆分由 [Split] Enabled 单独控制；一键制作的拆取通道始终可用）");
                StackLimitSystem.NameKeywords = cfg.Bind("ItemStack", "NameKeywords", "肥料,种子,冰块,木板,木片,木材,石头,石块,铁片,铁皮,铁锭,金属,塑料,玻璃", "关键字（包含匹配）规则：`关键字` 或 `关键字=数值`，多个用逗号分隔；对全部物品（含自定义物品）生效，优先级低于 ExactNames").Value;
                StackLimitSystem.ExactNames = cfg.Bind("ItemStack", "ExactNames", "铁丝,电器元件,容器,卡式气瓶,门板修补材料,窗户修补材料,纸片,硬纸,报纸=1,纸条=1,纸牌=1", "精确名称规则（优先级高于关键字）：`名称` / `名称@ID` / `名称=数值` / `名称@ID=数值`，多个用逗号分隔").Value;
                StackLimitSystem.NewStackLimit = cfg.Bind("ItemStack", "StackLimit", 5, "未在规则中单独写数值时的默认堆叠上限").Value;
                StackLimitSystem.AllStackableLimit = cfg.Bind("ItemStack", "AllStackableLimit", 0, "全局兜底：所有原本可堆叠（当前上限>=2）且未命中 ExactNames/NameKeywords 规则的物品，统一改为该上限；0 = 关闭").Value;
                _autoMergeEntry = cfg.Bind("ItemStack", "AutoMerge", true, "自动合并（true=启用）：后台把同一容器内【游戏自己认为可合并】的同种物品多堆自动并成一堆，无需任何操作/按键。仅合并逐实例数据完全一致的堆（如木片/木材），烹饪家具内的逐件独立存放不受影响");
                AutoMergeSystem.IntervalSec = cfg.Bind("ItemStack", "AutoMergeIntervalSec", 5f, "自动合并扫描间隔（秒）。默认 5 秒，幂等，没得合时几乎无开销").Value;
                AutoMergeSystem.ForceMerge = cfg.Bind("ItemStack", "AutoMergeForce", true, "强制合并（true=启用）：对【无保质期且无品质/耐久附加数据】的普通材料（木片/木板/铁皮等），即使两堆的逐实例数据不完全一致也并成一堆（以目标堆数据为准）。食物与带品质/耐久的物品不受影响").Value;
                AutoMergeSystem.LogDiff = cfg.Bind("ItemStack", "AutoMergeLogDiff", true, "打印\"同种物品有多堆却合不了\"的实例字段差异（排查用，每个物品只打一次）").Value;
                _preMergeEntry = cfg.Bind("ItemStack", "PreMerge", true, "落袋即合并（true=启用，推荐）：物品被放进容器时，若原版因为逐实例数据不一致而找不到可并的堆，就对普通材料放宽判定、直接并进已有的同类堆，从源头避免同种物品分堆。食物（有保质期）、带品质/耐久、以及烹饪家具袋内不受影响");
                AutoMergeSystem.MergePerishable = cfg.Bind("ItemStack", "AutoMergePerishable", true, "有时效物品也自动合并（true=启用）：同种食物的多堆会先按【剩余保质期最长的那堆】统一保质期，再并成一堆 —— 结果按最高保质期生效。发霉(污染)与新鲜分属不同子组，不会混在一起").Value;
                AutoMergeSystem.MergeFlagDiff = cfg.Bind("ItemStack", "MergeFlagDiff", true, "标签差异也合并（true=启用，推荐）：允许【只差标签类字段（地图预置 preset / 已拆包 nopkg）】的同种物品并成一堆。这两项只记录物品来源，不改变新鲜度/污染/耐久/品质。可乐那种「一堆2一堆3怎么都合不上」就是被它卡住的。关掉则回到严格判定（只并逐实例字段完全一致的堆）").Value;
                _mergeOnAddEntry = cfg.Bind("ItemStack", "MergeOnPickup", true, "落袋即合并（true=启用，推荐）：物品刚放进容器就立刻催一次合并，有保质期的食物/饮料也一样（不必等满 AutoMergeIntervalSec）。保质期不一致时按【最长的那堆】统一生效。关掉则只按固定间隔扫描");
                // 按 cfg 初值刷新运行时状态（之后游戏内可视化开关可随时切换，无需重启）
                SetStackEnabled(_stackEntry.Value);
                // 使用 BaseButler 传入的共享 harmony；拆分补丁用 CreateClassProcessor 精确挂载（避免 PatchAll 波及程序集内其它补丁）。
                // 若 OnMessageFromJS 的 Vuplex 签名解析被系统策略拦截（见 BaseButlerPlugin.PreloadVuplexInterop），
                // 只停用拆分，绝不让它连累堆叠扩展/自动合并等其余功能。
                try
                {
                    harmony.CreateClassProcessor(typeof(SplitHalfPatch)).Patch();
                }
                catch (Exception exSplit)
                {
                    Log.LogError("[堆叠拆分] SplitHalfPatch(OnMessageFromJS) 注册失败，Shift/Ctrl+右键拆分停用（堆叠扩展/自动合并等继续加载）：" + exSplit.Message);
                }
                // 不再用总开关提前 return：合并类补丁与后台行为常驻，运行时由 SetStackEnabled 双向切换。
                try
                {
                    MethodInfo findStackable = AccessTools.Method(typeof(ItemManager), "FindStackableItem", null, null);
                    MethodInfo postfix = AccessTools.Method(typeof(CookingNoStackPatch), "Postfix", null, null);
                    harmony.Patch(findStackable, null, new HarmonyMethod(postfix), null, null, null);
                    Log.LogInfo("[烹饪探针] FindStackableItem patch OK");
                }
                catch (Exception ex)
                {
                    Log.LogError("[烹饪探针] FindStackableItem patch failed: " + ex.Message);
                }
                // 落袋即合并（有保质期的物品也适用）：挂 ItemManager.AddItem 后缀。
                // AddItem 有 6 个重载，按"名字 = AddItem 且参数个数 = 17"精确定位"物品入包"那一个。
                try
                {
                    MethodInfo addItem = null;
                    MethodInfo[] candidates = typeof(ItemManager).GetMethods(BindingFlags.Public | BindingFlags.Instance);
                    foreach (MethodInfo m in candidates)
                    {
                        if (m.Name != "AddItem")
                        {
                            continue;
                        }
                        if (m.GetParameters().Length != 17)
                        {
                            continue;
                        }
                        addItem = m;
                        break;
                    }
                    if (addItem == null)
                    {
                        Log.LogWarning("[落袋合并] 未找到 17 参数的 ItemManager.AddItem，跳过挂钩（有保质期的物品仍按 " + AutoMergeSystem.IntervalSec + " 秒间隔合并）");
                    }
                    else
                    {
                        MethodInfo post = AccessTools.Method(typeof(MergeOnAddPatch), "Postfix", null, null);
                        harmony.Patch(addItem, null, new HarmonyMethod(post), null, null, null);
                        Log.LogInfo("[落袋合并] AddItem 后缀补丁 OK（有保质期的物品也落袋即合并，取最长保质期生效）");
                    }
                }
                catch (Exception ex)
                {
                    Log.LogWarning("[落袋合并] AddItem 挂钩失败: " + ex.Message);
                }
                ClassInjector.RegisterTypeInIl2Cpp<SLTweaksSplitBehaviour>();
                GameObject obj = new GameObject("BaseButlerStack");
                UnityEngine.Object.DontDestroyOnLoad(obj);
                obj.AddComponent<SLTweaksSplitBehaviour>();
                // v2.0.11：游戏内可视化开关——向工作台 ToolTable.html 注入右上角「堆叠:开/关」按钮脚本
                // （独立哨兵、幂等、抗 Steam 更新；初始状态与 cfg 同步）。注入失败不影响堆叠功能本体。
                try
                {
                    BaseButler.SourceExpand.WebViewHtmlPatcher.ApplyStackToggle(Log, ItemStackEnabled);
                }
                catch (Exception exToggle)
                {
                    Log.LogWarning("[堆叠] 游戏内可视化开关注入失败（不影响堆叠功能本体）：" + exToggle.Message);
                }
                log.LogInfo("=== BaseButler Stack（堆叠优化）loaded ===");
            }
            catch (Exception ex2)
            {
                log.LogError("BaseButler Stack load failed: " + ex2);
            }
        }

        // ===== 游戏内可视化开关（v2.0.11）=====
        // 运行时总开关状态；工作台右上角「堆叠:开/关」按钮可随时切换，无需改 cfg / 重启游戏。
        internal static bool ItemStackEnabled = true;

        // 各子开关的 ConfigEntry 引用：切换总开关时同步写回 cfg 持久化（下次启动仍保持）。
        private static BepInEx.Configuration.ConfigEntry<bool> _stackEntry;
        private static BepInEx.Configuration.ConfigEntry<bool> _autoMergeEntry;
        private static BepInEx.Configuration.ConfigEntry<bool> _preMergeEntry;
        private static BepInEx.Configuration.ConfigEntry<bool> _mergeOnAddEntry;

        /// <summary>运行时切换堆叠优化总开关：刷新各系统标志 + 写回 cfg + 日志。
        /// 关闭时 StackLimitSystem 会在下一拍 Tick 里把已抬高的上限恢复原版值（见 RestoreRaisedLimits）。</summary>
        internal static void SetStackEnabled(bool on)
        {
            ItemStackEnabled = on;
            try { if (_stackEntry != null) _stackEntry.Value = on; } catch { }
            if (on)
            {
                // 开启：以各子开关当前 cfg 值为准（用户在 cfg 单独关掉过的子开关保持关闭）
                StackLimitSystem.Enabled = true;
                DropOneSystem.Enabled = true;
                AutoMergeSystem.Enabled = (_autoMergeEntry != null) ? _autoMergeEntry.Value : true;
                PreMergeSystem.Enabled = (_preMergeEntry != null) ? _preMergeEntry.Value : true;
                AutoMergeSystem.MergeOnAdd = (_mergeOnAddEntry != null) ? _mergeOnAddEntry.Value : true;
            }
            else
            {
                // 关闭：整模块停用；SplitConfig（右键拆分转移）仍按 [Split] Enabled 独立生效
                StackLimitSystem.Enabled = false;
                DropOneSystem.Enabled = false;
                AutoMergeSystem.Enabled = false;
                PreMergeSystem.Enabled = false;
                AutoMergeSystem.MergeOnAdd = false;
            }
            LogSource.LogMessage("[BaseButler] [ItemStack] 游戏内开关 → " + (on ? "开启" : "关闭") +
                "（堆叠上限/自动合并/落袋合并/丢一" + (on ? "启用" : "停用") + "；右键拆分不受影响）。");
        }

        private static KeyCode ParseKey(string value, KeyCode fallback, string keyName)
        {
            if (string.IsNullOrEmpty(value))
            {
                return fallback;
            }
            try
            {
                return (KeyCode)Enum.Parse(typeof(KeyCode), value.Trim(), ignoreCase: true);
            }
            catch
            {
                LogSource.LogError("[堆叠拆分] 无效按键 '" + value + "' (" + keyName + ")，回退 " + fallback);
                return fallback;
            }
        }
    }

    public class SLTweaksSplitBehaviour : MonoBehaviour
    {
        private void Update()
        {
            StackLimitSystem.Tick();
            DropOneSystem.Tick();
            AutoMergeSystem.Tick();
        }
    }

    public static class SplitConfig
    {
        internal static bool Enabled = true;

        internal static KeyCode HalfKey = (KeyCode)304;

        internal static KeyCode OneKey = (KeyCode)308;

        internal static bool CookingNoStack = true;
    }

    [HarmonyPatch(typeof(WebUILayer), "OnMessageFromJS")]
    public static class SplitHalfPatch
    {
        private enum RefreshKind
        {
            Unsupported,
            Action,
            Backpack,
            ToolTable
        }

        private sealed class OwnerField
        {
            public string Name;

            public long Value;
        }

        private sealed class Resolved
        {
            public Type StateType;

            public string StateName;

            public long TargetOwner;

            public RefreshKind Kind;

            public MethodInfo Send;

            public string Detail;
        }

        private static Assembly _reduxAsm;

        private static Type[] _stateWebTypes;

        private static MethodInfo _getStateMi;

        public static bool Prefix(WebUILayer __instance, EventArgs<string> eventArgs)
        {
            try
            {
                if (eventArgs == null)
                {
                    return true;
                }
                string value = eventArgs.Value;
                if (string.IsNullOrEmpty(value))
                {
                    return true;
                }
                string[] array = value.Split('\u001e');
                string page = ((array.Length > 1) ? array[1] : "");
                string ev = ((array.Length > 2) ? array[2] : "");
                string json = ((array.Length > 3) ? array[3] : value);
                // 前端诊断日志桥：一键制作的 dbg() 走 core.UnitySendEvent('CSE_DEBUG',{text})，
                // 实际汇入 OnMessageFromJS（UnitySendEvent 的自定义事件到不了 OnPageMessage）。
                // 此处原文转储到 BepInEx 日志，纯诊断、放行不打扰前端流程。
                if (ev == "CSE_DEBUG")
                {
                    string txt = json;
                    if (!string.IsNullOrEmpty(json))
                    {
                        try
                        {
                            using (var dd = System.Text.Json.JsonDocument.Parse(json))
                            {
                                if (dd.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                                    && dd.RootElement.TryGetProperty("text", out var t)
                                    && t.ValueKind == System.Text.Json.JsonValueKind.String)
                                    txt = t.GetString();
                            }
                        }
                        catch { }
                    }
                    Plugin.LogSource.LogMessage("[CSE前端] " + txt);
                    return true;
                }
                // 游戏内可视化开关：工作台右上角「堆叠:开/关」按钮点击后发 core.UnitySendEvent('CSE_TOGGLE_STACK',{on:true/false})。
                // 本分支直接切换运行时总开关并写回 cfg 持久化，同时刷新 ToolTable.html 里的初始状态 meta（下次打开面板按钮与开关一致）。
                if (ev == "CSE_TOGGLE_STACK")
                {
                    bool pOn = true;
                    if (!string.IsNullOrEmpty(json))
                    {
                        try
                        {
                            using (var td = System.Text.Json.JsonDocument.Parse(json))
                            {
                                if (td.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                                    && td.RootElement.TryGetProperty("on", out var v))
                                {
                                    if (v.ValueKind == System.Text.Json.JsonValueKind.True) pOn = true;
                                    else if (v.ValueKind == System.Text.Json.JsonValueKind.False) pOn = false;
                                    else if (v.ValueKind == System.Text.Json.JsonValueKind.String)
                                    {
                                        string s = v.GetString();
                                        pOn = (s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase));
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                    Plugin.SetStackEnabled(pOn);
                    try { BaseButler.SourceExpand.WebViewHtmlPatcher.ApplyStackToggle(Plugin.LogSource, pOn); }
                    catch (Exception exToggle) { Plugin.LogSource.LogWarning("[堆叠] 刷新开关状态失败: " + exToggle.Message); }
                    return false; // 已处理，拦截
                }
                // 一键制作按需拆取：前端在"堆超量"时改发 BB_SPLIT_MOVE，走本监听的已验证 JS→C# 通道
                // (OnMessageFromJS，SplitHalfPatch 挂这里并成功拦截原生拆分)。此前误挂 OnPageMessage，
                // 而 UnitySendEvent 实际汇入 OnMessageFromJS，导致拆取请求从未到达 C#——
                // 整堆搬(ITEM_MOVE)能通、按需拆分却一直无效。此分支把请求交给 StackSplit.TrySplitMove。
                // ★ 该拆取通道是模块A 一键制作的前置依赖，必须无条件放行，不受 [Split] Enabled / [ItemStack] Enabled 影响。
                if (ev == "BB_SPLIT_MOVE")
                {
                    bool handled = false;
                    try { handled = TryHandleSplitMove(json); } catch (Exception ex) { Plugin.LogSource.LogWarning("[按需拆取] 内部异常: " + ex.Message); }
                    return !handled; // 处理成功则拦下(false)，失败则放行(true)交回游戏兜底
                }
                // 以下右键拆分转移 / 交易拆分受 [Split] Enabled 控制；关闭时全部放行原版行为
                if (!SplitConfig.Enabled)
                {
                    return true;
                }
                if (TradeSystem.IsTradeClick(page, ev, json) && TradeSystem.Handle(__instance, page, json))
                {
                    return false;
                }
                bool isQuick = ev == "QUICK_TRANSFER_ITEM";
                bool isItemClick = ev == "ITEM_CLICK" && json.IndexOf("\"rightClick\":true", StringComparison.Ordinal) >= 0;
                if (!isQuick && !isItemClick)
                {
                    return true;
                }
                bool halfKey = Input.GetKey(SplitConfig.HalfKey);
                bool oneKey = Input.GetKey(SplitConfig.OneKey);
                if (!halfKey && !oneKey)
                {
                    return true;
                }
                Match match = Regex.Match(json, "\"itemId\"\\s*:\\s*(-?\\d+)");
                if (!match.Success || !long.TryParse(match.Groups[1].Value, out long result))
                {
                    return true;
                }
                BattleLogicWorld world = TelemetryProbe.TryGetLiveWorld();
                ItemManager im = ((world == null) ? null : world._ItemManager);
                if (im == null)
                {
                    return true;
                }
                ItemData src = im.GetItemData(result);
                if (src == null || src.ItemCount < 2)
                {
                    return true;
                }
                long srcOwner = src.OwnerId;
                Resolved resolved = Resolve(page, srcOwner);
                if (resolved == null)
                {
                    Plugin.LogSource.LogInfo("[堆叠拆分] 未识别 UI，放行原版转移：page=" + page + " ev=" + ev + " srcOwner=" + srcOwner);
                    return true;
                }
                int total = src.ItemCount;
                int num = (oneKey ? 1 : (total / 2));
                if (num < 1)
                {
                    num = 1;
                }
                im.SetItemCount(src, total - num);
                bool cooking = SplitConfig.CookingNoStack && IsCookingTarget(im, resolved.TargetOwner);
                int moved;
                if (cooking)
                {
                    moved = AddSeparateUnits(im, src, resolved.TargetOwner, num);
                    if (moved <= 0)
                    {
                        im.SetItemCount(src, total);
                        Plugin.LogSource.LogError("[堆叠拆分] 烹饪逐件添加失败，交还原版转移");
                        return true;
                    }
                    if (moved < num)
                    {
                        im.SetItemCount(src, total - moved);
                    }
                }
                else
                {
                    ItemData created = im.AddItem(resolved.TargetOwner, src.ItemConfigId, num, src.StartTime, src.TimeLeft, new Vector2Int(-1, -1), src.UseTimes, false, src.TimeScale, src.InstanceVD, src.InstanceEffectEnd, src.MaxUseTimes, src.InstanceBurnValue, src.Polluted, src.IsMapPreset, src.NoPackage, src.InstanceWeight);
                    if (created == null)
                    {
                        im.SetItemCount(src, total);
                        Plugin.LogSource.LogError("[堆叠拆分] 目标容器 AddItem 失败，交还原版转移");
                        return true;
                    }
                    NotifyItem(created);
                    moved = num;
                }
                NotifyItem(src);
                ApplyRefresh(__instance, resolved, page);
                Plugin.LogSource.LogInfo("[堆叠拆分] 拆分成功(" + (oneKey ? "-1个" : "-一半") + ") page=" + page + " state=" + resolved.StateName + " kind=" + resolved.Kind.ToString() + " 总数=" + total + " 源保留=" + (total - moved) + " 目标增加=" + moved + (cooking ? " [烹饪逐件]" : "") + " | " + resolved.Detail);
                return false;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError("[堆叠拆分] 拆分异常: " + ex.Message);
                return true;
            }
        }

        private static bool TryHandleSplitMove(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return false;
            }
            long from = 0, to = 0; long id = 0; int cfg = 0, cnt = 0;
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    var r = doc.RootElement;
                    if (r.TryGetProperty("fromOwnerId", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Number) from = a.GetInt64();
                    if (r.TryGetProperty("toOwnerId", out var b) && b.ValueKind == System.Text.Json.JsonValueKind.Number) to = b.GetInt64();
                    if (r.TryGetProperty("itemId", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Number) id = c.GetInt64();
                    if (r.TryGetProperty("configId", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.Number) cfg = e.GetInt32();
                    if (r.TryGetProperty("count", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.Number) cnt = d.GetInt32();
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning("[按需拆取] JSON 解析失败: " + ex.Message);
                return false;
            }
            if (cnt <= 0 || from == 0 || (id == 0 && cfg == 0))
            {
                Plugin.LogSource.LogWarning("[按需拆取] 参数无效: from=" + from + " id=" + id + " cfg=" + cfg + " cnt=" + cnt);
                return false;
            }
            bool ok = StackSplit.TrySplitMove(from, id, cfg, cnt, to);
            Plugin.LogSource.LogInfo("[按需拆取] 通道=OnMessageFromJS from=" + from + " id=" + id + " cfg=" + cfg + " x" + cnt + " → " + to + " 结果=" + ok);
            return ok;
        }

        private static Resolved Resolve(string page, long srcOwner)
        {
            Resolved best = null;
            int bestScore = 0;
            Type[] array = StateWebTypes();
            foreach (Type type in array)
            {
                int score = ScorePage(type.Name, page);
                if (score <= 0 || score <= bestScore)
                {
                    continue;
                }
                object obj = TryGetState(type);
                if (obj != null && TryResolveTarget(obj, srcOwner, out long target, out string detail))
                {
                    Resolved resolved = MakeResolved(type, target, detail);
                    if (resolved != null)
                    {
                        best = resolved;
                        bestScore = score;
                    }
                }
            }
            if (best != null)
            {
                return best;
            }
            Resolved found = null;
            Type[] array2 = StateWebTypes();
            foreach (Type type2 in array2)
            {
                object obj2 = TryGetState(type2);
                if (obj2 == null || !TryResolveTarget(obj2, srcOwner, out long target2, out string detail2))
                {
                    continue;
                }
                Resolved resolved2 = MakeResolved(type2, target2, detail2);
                if (resolved2 != null)
                {
                    if (found != null)
                    {
                        Plugin.LogSource.LogInfo("[堆叠拆分] 目标状态不唯一，放行原版。page=" + page + " 命中=" + found.StateName + " 与 " + resolved2.StateName);
                        return null;
                    }
                    found = resolved2;
                }
            }
            return found;
        }

        private static Resolved MakeResolved(Type stateType, long target, string detail)
        {
            Resolved resolved = new Resolved
            {
                StateType = stateType,
                StateName = stateType.Name,
                TargetOwner = target,
                Detail = detail,
                Kind = RefreshKind.Unsupported
            };
            if (stateType == typeof(State_Web_BackpackUI))
            {
                resolved.Kind = RefreshKind.Backpack;
                return resolved;
            }
            if (stateType == typeof(State_Web_ToolTable))
            {
                resolved.Kind = RefreshKind.ToolTable;
                return resolved;
            }
            Type type = ReduxAsm().GetType(stateType.Namespace + ".Ac_" + Strip(stateType.Name) + "_RefreshBag");
            if (type != null)
            {
                MethodInfo method = type.GetMethod("SendAction", BindingFlags.Static | BindingFlags.Public, null, Type.EmptyTypes, null);
                if (method != null)
                {
                    resolved.Kind = RefreshKind.Action;
                    resolved.Send = method;
                    return resolved;
                }
            }
            return null;
        }

        private static int ScorePage(string typeName, string page)
        {
            if (string.IsNullOrEmpty(page))
            {
                return 0;
            }
            string text = Strip(typeName);
            if (string.Equals(text, page, StringComparison.OrdinalIgnoreCase))
            {
                return 100;
            }
            if (string.Equals(text, page + "UI", StringComparison.OrdinalIgnoreCase))
            {
                return 90;
            }
            if (text.EndsWith("UI", StringComparison.OrdinalIgnoreCase) && string.Equals(text.Substring(0, text.Length - 2), page, StringComparison.OrdinalIgnoreCase))
            {
                return 90;
            }
            if (text.StartsWith(page, StringComparison.OrdinalIgnoreCase) || page.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            {
                return 40;
            }
            return 0;
        }

        private static string Strip(string typeName)
        {
            return typeName.StartsWith("State_Web_", StringComparison.Ordinal) ? typeName.Substring("State_Web_".Length) : typeName;
        }

        private static bool TryResolveTarget(object state, long srcOwner, out long target, out string detail)
        {
            target = 0L;
            detail = null;
            List<OwnerField> list = OwnerFields(state);
            if (list.Count < 2)
            {
                return false;
            }
            List<OwnerField> sources = list.Where((OwnerField f) => f.Value == srcOwner && f.Value != 0).ToList();
            if (sources.Count == 0)
            {
                return false;
            }
            List<OwnerField> others = (from f in list
                where f.Value != 0L && f.Value != srcOwner
                group f by f.Value into g
                select g.First()).ToList();
            if (others.Count == 0)
            {
                return false;
            }
            OwnerField chosen = null;
            if (sources.Any((OwnerField s) => s.Name.IndexOf("Bag", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                chosen = others.FirstOrDefault((OwnerField c) => c.Name.IndexOf("Workbench", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            if (chosen == null && sources.Any((OwnerField s) => s.Name.EndsWith("_A", StringComparison.OrdinalIgnoreCase)))
            {
                chosen = others.FirstOrDefault((OwnerField c) => c.Name.EndsWith("_B", StringComparison.OrdinalIgnoreCase));
            }
            if (chosen == null && sources.Any((OwnerField s) => s.Name.EndsWith("_B", StringComparison.OrdinalIgnoreCase)))
            {
                chosen = others.FirstOrDefault((OwnerField c) => c.Name.EndsWith("_A", StringComparison.OrdinalIgnoreCase));
            }
            if (chosen == null)
            {
                chosen = others[0];
            }
            target = chosen.Value;
            detail = "src{" + string.Join(",", sources.Select((OwnerField s) => s.Name + "=" + s.Value)) + "} -> " + chosen.Name + "=" + chosen.Value + " | all{" + string.Join(",", list.Select((OwnerField f) => f.Name + "=" + f.Value)) + "}";
            return true;
        }

        private static List<OwnerField> OwnerFields(object state)
        {
            List<OwnerField> list = new List<OwnerField>();
            Type type = state.GetType();
            PropertyInfo[] properties;
            try
            {
                properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public);
            }
            catch
            {
                return list;
            }
            PropertyInfo[] array = properties;
            foreach (PropertyInfo propertyInfo in array)
            {
                try
                {
                    if (!propertyInfo.CanRead)
                    {
                        continue;
                    }
                    string name = propertyInfo.Name;
                    if (name.IndexOf("OwnerId", StringComparison.OrdinalIgnoreCase) < 0 || name.IndexOf("RP", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Tab", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Tag", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        continue;
                    }
                    Type propertyType = propertyInfo.PropertyType;
                    long value;
                    if (propertyType == typeof(long))
                    {
                        value = (long)propertyInfo.GetValue(state, null);
                    }
                    else
                    {
                        if (!IsReactiveLong(propertyType))
                        {
                            continue;
                        }
                        object rp = propertyInfo.GetValue(state, null);
                        if (rp == null)
                        {
                            continue;
                        }
                        PropertyInfo property = rp.GetType().GetProperty("CurrentValue", BindingFlags.Instance | BindingFlags.Public);
                        if (property == null)
                        {
                            continue;
                        }
                        value = (long)property.GetValue(rp, null);
                    }
                    list.Add(new OwnerField
                    {
                        Name = name,
                        Value = value
                    });
                }
                catch
                {
                }
            }
            return list;
        }

        private static bool IsReactiveLong(Type pt)
        {
            try
            {
                return pt.IsGenericType && pt.Name.StartsWith("ReactiveProperty", StringComparison.Ordinal) && pt.GetGenericArguments()[0] == typeof(long);
            }
            catch
            {
                return false;
            }
        }

        private static Type[] StateWebTypes()
        {
            if (_stateWebTypes != null)
            {
                return _stateWebTypes;
            }
            List<Type> list = new List<Type>();
            try
            {
                Type[] types = ReduxAsm().GetTypes();
                foreach (Type type in types)
                {
                    if (type.IsClass && !type.IsAbstract && type.Name.StartsWith("State_Web_", StringComparison.Ordinal))
                    {
                        list.Add(type);
                    }
                }
            }
            catch (ReflectionTypeLoadException ex)
            {
                Type[] types2 = ex.Types;
                foreach (Type type2 in types2)
                {
                    if (type2 != null && type2.IsClass && !type2.IsAbstract && type2.Name.StartsWith("State_Web_", StringComparison.Ordinal))
                    {
                        list.Add(type2);
                    }
                }
            }
            catch
            {
            }
            _stateWebTypes = list.ToArray();
            return _stateWebTypes;
        }

        private static Assembly ReduxAsm()
        {
            if (_reduxAsm == null)
            {
                _reduxAsm = typeof(State_Web_BackpackUI).Assembly;
            }
            return _reduxAsm;
        }

        private static object TryGetState(Type t)
        {
            try
            {
                ReduxUISystem instance = BaseSingleton<ReduxUISystem>.Instance;
                ReduxStoreLayer store = ((instance == null) ? null : instance.reduxStoreLayer);
                if (store == null)
                {
                    return null;
                }
                if (_getStateMi == null)
                {
                    _getStateMi = typeof(SplitHalfPatch).GetMethod("GetStateImpl", BindingFlags.Static | BindingFlags.NonPublic);
                }
                return _getStateMi.MakeGenericMethod(t).Invoke(null, new object[1] { store });
            }
            catch
            {
                return null;
            }
        }

        private static T GetStateImpl<T>(ReduxStoreLayer store) where T : class
        {
            return store.GetState<T>(Il2CppType.Of<T>());
        }

        private static void ApplyRefresh(WebUILayer layer, Resolved r, string page)
        {
            try
            {
                switch (r.Kind)
                {
                    case RefreshKind.Backpack:
                        RefreshBackpackState(layer);
                        break;
                    case RefreshKind.ToolTable:
                        RefreshToolTableState();
                        break;
                    case RefreshKind.Action:
                        r.Send.Invoke(null, null);
                        break;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError("[堆叠拆分] 刷新失败(" + r.StateName + "): " + ex.Message);
            }
            MarkDirty(layer, page);
        }

        public static void MarkDirty(WebUILayer layer, string page)
        {
            try
            {
                if (layer != null && !string.IsNullOrEmpty(page))
                {
                    var pageMap = layer.PageMap;
                    WebUIVm vm = null;
                    if (pageMap != null && pageMap.TryGetValue(page, out vm) && vm != null)
                    {
                        vm.MarkAllMsgDirty();
                    }
                }
            }
            catch
            {
            }
        }

        public static void NotifyItem(ItemData it)
        {
            if (it != null)
            {
                Ac_Item_AddOrNotify.SendAction(((BaseEntity)it).InstanceId, it.OwnerId, it.ItemConfigId, it.ItemCount, it.StartTime, it.TimeLeft, it.ItemSize, it.BagPos, it.UseTimes, it.MaxUseTimes, it.TimeScale, it.OriginalTimeLeft, it.InstanceVD, it.InstanceEffectEnd, it.InstanceBurnValue, it.Polluted, it.IsMapPreset, it.PresetShelfScale, it.InstanceWeight);
            }
        }

        public static ItemManager GetIM()
        {
            BattleLogicWorld world = TelemetryProbe.TryGetLiveWorld();
            return (world == null) ? null : world._ItemManager;
        }

        private static bool IsCookingTarget(ItemManager im, long owner)
        {
            try
            {
                return im.IsCookingFurnitureBag(owner);
            }
            catch
            {
                return false;
            }
        }

        private static int AddSeparateUnits(ItemManager im, ItemData src, long owner, int count)
        {
            int num = 0;
            CookingNoStack.Suppress = true;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    ItemData created = im.AddItem(owner, src.ItemConfigId, 1, src.StartTime, src.TimeLeft, new Vector2Int(-1, -1), src.UseTimes, false, src.TimeScale, src.InstanceVD, src.InstanceEffectEnd, src.MaxUseTimes, src.InstanceBurnValue, src.Polluted, src.IsMapPreset, src.NoPackage, src.InstanceWeight);
                    if (created == null)
                    {
                        break;
                    }
                    NotifyItem(created);
                    num++;
                }
            }
            finally
            {
                CookingNoStack.Suppress = false;
            }
            return num;
        }

        public static void RefreshBackpackState(WebUILayer layer)
        {
            ReduxUISystem instance = BaseSingleton<ReduxUISystem>.Instance;
            ReduxStoreLayer store = ((instance == null) ? null : instance.reduxStoreLayer);
            if (store == null)
            {
                return;
            }
            State_Web_BackpackUI state = store.GetState<State_Web_BackpackUI>(Il2CppType.Of<State_Web_BackpackUI>());
            ItemManager im = GetIM();
            if (state != null && im != null)
            {
                RebuildSide(im, state.ItemDataList_A, state.OwnerId_A);
                RebuildSide(im, state.ItemDataList_B, state.OwnerId_B);
                if (state.OwnerId_A != 0)
                {
                    Ac_BackpackUI_RefreshBag.SendAction(state.OwnerId_A, state.ItemDataList_A);
                }
                if (state.OwnerId_B != 0)
                {
                    Ac_BackpackUI_RefreshBag.SendAction(state.OwnerId_B, state.ItemDataList_B);
                }
                WebUIVm vm = null;
                if (layer != null && layer.GetWebUI(Il2CppType.Of<WebUI_BackpackUI>(), out vm) && vm != null)
                {
                    vm.MarkAllMsgDirty();
                }
            }
        }

        public static void RefreshToolTableState()
        {
            ReduxUISystem instance = BaseSingleton<ReduxUISystem>.Instance;
            ReduxStoreLayer store = ((instance == null) ? null : instance.reduxStoreLayer);
            if (store == null)
            {
                return;
            }
            State_Web_ToolTable state = store.GetState<State_Web_ToolTable>(Il2CppType.Of<State_Web_ToolTable>());
            ItemManager im = GetIM();
            if (state != null && im != null)
            {
                long bagOwner = ((ReadOnlyReactiveProperty<long>)(object)state.BagOwnerId).CurrentValue;
                long workbenchOwner = ((ReadOnlyReactiveProperty<long>)(object)state.WorkbenchOwnerId).CurrentValue;
                var storyIds = new Il2CppSystem.Collections.Generic.List<int>();
                if (bagOwner != 0)
                {
                    // 游戏 v1.0.14911+ 更新后 RefreshBagItems 新增 float currentHours 参数（工作台物品时间/倒计时显示用）。
                    // mod 侧是"拆分/合并后同步网格"，时间字段后续由官方正常刷新覆盖，传 0f 安全兼容、不崩不丢物。
                    Reducer_Web_ToolTable.RefreshBagItems(state, BuildDataItems(im, bagOwner), state.BagItems, storyIds, 0f);
                }
                if (workbenchOwner != 0)
                {
                    Reducer_Web_ToolTable.RefreshBagItems(state, BuildDataItems(im, workbenchOwner), state.WorkbenchItems, storyIds, 0f);
                }
            }
        }

        private static void RebuildSide(ItemManager im, Il2CppSystem.Collections.Generic.List<Data_Item> list, long ownerId)
        {
            if (list == null || ownerId == 0)
            {
                return;
            }
            var itemDataList = im.GetItemDataList(ownerId);
            list.Clear();
            if (itemDataList == null)
            {
                return;
            }
            for (int i = 0; i < itemDataList.Count; i++)
            {
                ItemData val = itemDataList[i];
                if (val != null)
                {
                    list.Add(MakeDataItem(val));
                }
            }
        }

        private static Il2CppSystem.Collections.Generic.List<Data_Item> BuildDataItems(ItemManager im, long ownerId)
        {
            var list = new Il2CppSystem.Collections.Generic.List<Data_Item>();
            var itemDataList = im.GetItemDataList(ownerId);
            if (itemDataList != null)
            {
                for (int i = 0; i < itemDataList.Count; i++)
                {
                    ItemData val = itemDataList[i];
                    if (val != null)
                    {
                        list.Add(MakeDataItem(val));
                    }
                }
            }
            return list;
        }

        private static Data_Item MakeDataItem(ItemData it)
        {
            Data_Item val = new Data_Item();
            val.LogicId = ((BaseEntity)it).InstanceId;
            val.OwnerId = it.OwnerId;
            val.ItemCount = it.ItemCount;
            val.ItemConfigId = it.ItemConfigId;
            val.StartTime = it.StartTime;
            val.TimeLeft = it.TimeLeft;
            val.ItemSize = it.ItemSize;
            val.BagPos = it.BagPos;
            val.UseTimes = it.UseTimes;
            val.MaxUseTimes = it.MaxUseTimes;
            val.TimeScale = it.TimeScale;
            val.OriginalTimeLeft = it.OriginalTimeLeft;
            val.InstanceVD = it.InstanceVD;
            val.InstanceEffectEnd = it.InstanceEffectEnd;
            val.InstanceBurnValue = it.InstanceBurnValue;
            val.InstanceWeight = it.InstanceWeight;
            val.Polluted = it.Polluted;
            val.IsMapPreset = it.IsMapPreset;
            val.PresetShelfScale = it.PresetShelfScale;
            return val;
        }
    }

    public static class StackLimitSystem
    {
        private sealed class Rule
        {
            public string Text;

            public string Norm;

            public int Id;

            public bool HasValue;

            public int Value;
        }

        internal static string NameKeywords = "肥料,种子,冰块,木板,木片,木材,石头,石块,铁片,铁皮,铁锭,金属,塑料,玻璃";

        internal static string ExactNames = "铁丝,电器元件,容器,卡式气瓶,门板修补材料,窗户修补材料,纸片,硬纸,报纸=1,纸条=1,纸牌=1";

        internal static int NewStackLimit = 5;

        internal static int AllStackableLimit = 0;

        internal static bool Enabled = true;

        // 总开关关闭时恢复原始上限用：configId → 本 mod 首次抬高前的原始 StackLimit。
        private static readonly Dictionary<int, int> _raisedOrig = new Dictionary<int, int>();

        private static Rule[] _exact;

        private static Rule[] _keywords;

        private static float _nextAt;

        public static void Tick()
        {
            try
            {
                if (!Enabled)
                {
                    // 总开关被游戏内可视化开关关闭：把已抬高的堆叠上限恢复原版值
                    RestoreRaisedLimits();
                    return;
                }
                if (Time.unscaledTime < _nextAt)
                {
                    return;
                }
                _nextAt = Time.unscaledTime + 5f;
                ConfigManager instance = BaseSingleton<ConfigManager>.Instance;
                if (instance == null)
                {
                    return;
                }
                // 启动刚进游戏的那一拍，配置表里还是空的，Get_Config_Item_All() 内部会抛
                // ArgumentNullException（dictionary = null）。这不是故障，静静跳过、1.5 秒后再来，
                // 免得每次启动都在日志里刷一条 [Error]。
                Il2CppSystem.Collections.Generic.Dictionary<int, Config_Item> configItemAll = null;
                try { configItemAll = instance.Get_Config_Item_All(); }
                catch
                {
                    _nextAt = Time.unscaledTime + 1.5f;
                    return;
                }
                if (configItemAll == null || configItemAll.Count == 0)
                {
                    return;
                }
                if (_exact == null)
                {
                    _exact = ParseRules(ExactNames);
                }
                if (_keywords == null)
                {
                    _keywords = ParseRules(NameKeywords);
                }
                if (_exact.Length == 0 && _keywords.Length == 0 && AllStackableLimit < 2)
                {
                    return;
                }
                int num = 0;
                var enumerator = configItemAll.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    Config_Item item = enumerator.Current.Value;
                    if (item == null)
                    {
                        continue;
                    }
                    int limit;
                    string how;
                    if (TryMatch(item, _exact, "精确", exact: true, out limit, out how) || TryMatch(item, _keywords, "关键字", exact: false, out limit, out how))
                    {
                        if (limit < 1)
                        {
                            limit = 1;
                        }
                        if (item.StackLimit != limit)
                        {
                            int oldLimit = item.StackLimit;
                            if (!_raisedOrig.ContainsKey(item.ID)) _raisedOrig[item.ID] = oldLimit;
                            item.StackLimit = limit;
                            num++;
                            Log("堆叠 '" + Display(item) + "' " + oldLimit + " -> " + limit + " (" + how + ")");
                        }
                    }
                    else if (AllStackableLimit >= 2 && item.StackLimit >= 2 && item.StackLimit != AllStackableLimit)
                    {
                        int oldLimit2 = item.StackLimit;
                        if (!_raisedOrig.ContainsKey(item.ID)) _raisedOrig[item.ID] = oldLimit2;
                        item.StackLimit = AllStackableLimit;
                        num++;
                        Log("堆叠 '" + Display(item) + "' " + oldLimit2 + " -> " + AllStackableLimit + " (全局可堆叠)");
                    }
                }
                if (num > 0)
                {
                    Log("堆叠上限修改完成，本次 " + num + " 处");
                }
            }
            catch (Exception ex)
            {
                LogErr("堆叠修改失败: " + ex.Message);
            }
        }

        /// <summary>总开关被游戏内可视化开关关闭时调用：把之前抬高的堆叠上限逐一恢复为原始值（按 configId 缓存）。</summary>
        private static void RestoreRaisedLimits()
        {
            if (_raisedOrig.Count == 0) return;
            try
            {
                ConfigManager instance = BaseSingleton<ConfigManager>.Instance;
                if (instance == null) return;
                Il2CppSystem.Collections.Generic.Dictionary<int, Config_Item> configItemAll = null;
                try { configItemAll = instance.Get_Config_Item_All(); }
                catch { return; }
                if (configItemAll == null || configItemAll.Count == 0) return;
                int n = 0;
                var enumerator = configItemAll.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    Config_Item item = enumerator.Current.Value;
                    if (item == null) continue;
                    if (_raisedOrig.TryGetValue(item.ID, out int origLimit))
                    {
                        if (item.StackLimit != origLimit)
                        {
                            item.StackLimit = origLimit;
                            n++;
                        }
                    }
                }
                if (n > 0) Log("堆叠优化已关闭：恢复 " + n + " 处物品的原始堆叠上限");
                _raisedOrig.Clear();
            }
            catch (Exception ex)
            {
                LogErr("恢复原始堆叠上限失败: " + ex.Message);
            }
        }

        private static bool TryMatch(Config_Item item, Rule[] rules, string kind, bool exact, out int value, out string how)
        {
            value = 0;
            how = null;
            if (rules == null || rules.Length == 0)
            {
                return false;
            }
            string text = Normalize(Display(item));
            foreach (Rule rule in rules)
            {
                if ((rule.Id != 0) ? (item.ID == rule.Id) : ((!exact) ? (rule.Norm.Length > 0 && text.Contains(rule.Norm)) : (text.Length > 0 && text == rule.Norm)))
                {
                    value = (rule.HasValue ? rule.Value : NewStackLimit);
                    how = kind + ":" + ((rule.Id != 0) ? ("ID=" + rule.Id) : rule.Text) + (rule.HasValue ? ("=" + rule.Value) : "");
                    return true;
                }
            }
            return false;
        }

        private static Rule[] ParseRules(string cfg)
        {
            if (string.IsNullOrEmpty(cfg))
            {
                return new Rule[0];
            }
            List<Rule> list = new List<Rule>();
            string[] array = cfg.Split(new char[4] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries);
            string[] array2 = array;
            foreach (string text in array2)
            {
                string text2 = text.Trim();
                if (text2.Length != 0)
                {
                    Rule rule = new Rule();
                    int num = text2.LastIndexOf('=');
                    if (num > 0 && int.TryParse(text2.Substring(num + 1).Trim(), out int result))
                    {
                        rule.HasValue = true;
                        rule.Value = result;
                        text2 = text2.Substring(0, num).Trim();
                    }
                    int num2 = text2.LastIndexOf('@');
                    if (num2 > 0 && int.TryParse(text2.Substring(num2 + 1).Trim(), out int result2))
                    {
                        rule.Id = result2;
                        text2 = text2.Substring(0, num2).Trim();
                    }
                    rule.Text = text2;
                    rule.Norm = Normalize(text2);
                    if (rule.Id != 0 || rule.Norm.Length > 0)
                    {
                        list.Add(rule);
                    }
                }
            }
            return list.ToArray();
        }

        private static string Display(Config_Item item)
        {
            return item.ItemName_Local ?? item.ItemName;
        }

        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }
            return s.Replace("\u200b", "").Replace("\ufeff", "").Replace("\u200c", "")
                .Replace("\u00a0", " ")
                .Trim();
        }

        private static void Log(string msg)
        {
            Plugin.LogSource.LogInfo(msg);
        }

        private static void LogErr(string msg)
        {
            Plugin.LogSource.LogError(msg);
        }
    }

    public static class TradeSystem
    {
        public static bool IsTradeClick(string page, string ev, string json)
        {
            if (!string.Equals(page, "Trade", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (ev != "CLICK_ITEM")
            {
                return false;
            }
            string text = json ?? "";
            if (text.IndexOf("\"rightClick\":true", StringComparison.Ordinal) < 0)
            {
                return false;
            }
            Match match = Regex.Match(text, "\"fromGrid\"\\s*:\\s*\"([^\"]*)\"");
            return match.Success && string.Equals(match.Groups[1].Value, "bag", StringComparison.OrdinalIgnoreCase);
        }

        public static bool Handle(WebUILayer layer, string page, string json)
        {
            try
            {
                bool halfKey = Input.GetKey(SplitConfig.HalfKey);
                bool oneKey = Input.GetKey(SplitConfig.OneKey);
                if (!halfKey && !oneKey)
                {
                    return false;
                }
                Match match = Regex.Match(json ?? "", "\"itemKey\"\\s*:\\s*\"?(-?\\d+)\"?");
                if (!match.Success || !long.TryParse(match.Groups[1].Value, out long result))
                {
                    return false;
                }
                ItemManager im = SplitHalfPatch.GetIM();
                if (im == null)
                {
                    return false;
                }
                ItemData src = im.GetItemData(result);
                if (src == null || src.ItemCount < 2)
                {
                    return false;
                }
                int total = src.ItemCount;
                int num = (oneKey ? 1 : (total / 2));
                if (num < 1)
                {
                    num = 1;
                }
                im.SetItemCount(src, total - num);
                CookingNoStack.SuppressAll = true;
                ItemData created;
                try
                {
                    created = im.AddItem(src.OwnerId, src.ItemConfigId, num, src.StartTime, src.TimeLeft, new Vector2Int(-1, -1), src.UseTimes, false, src.TimeScale, src.InstanceVD, src.InstanceEffectEnd, src.MaxUseTimes, src.InstanceBurnValue, src.Polluted, src.IsMapPreset, src.NoPackage, src.InstanceWeight);
                }
                finally
                {
                    CookingNoStack.SuppressAll = false;
                }
                if (created == null)
                {
                    im.SetItemCount(src, total);
                    Plugin.LogSource.LogError("[交易拆分] 生成拆出件失败，交还原版");
                    return false;
                }
                SplitHalfPatch.NotifyItem(src);
                SplitHalfPatch.NotifyItem(created);
                Ac_TradeUI_ClickItem.SendAction(((BaseEntity)created).InstanceId.ToString(), "bag", true);
                SplitHalfPatch.MarkDirty(layer, page);
                Plugin.LogSource.LogInfo("[交易拆分] 成功(" + (oneKey ? "-1个" : "-一半") + ") itemKey=" + result + " 总数=" + total + " 保留=" + (total - num) + " 报价=" + num + " 新件=" + ((BaseEntity)created).InstanceId);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError("[交易拆分] 异常: " + ex.Message);
                return false;
            }
        }
    }
}
