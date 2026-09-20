using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using CookingUI = GameCore.HotUpdate.ReduxUI;
using HotGame = GameCore.HotUpdate;
using BaseButler.SourceExpand;

namespace BaseButler.StorageButler
{
    /// <summary>
    /// 模块 B：仓储自动管家（Phase2）。
    ///
    /// 决策门 P2/P6 已在 _probe_move.log 实测 PASS（任意柜→柜 Ac_Item_ChangePos 可落位、守恒可对账），
    /// 据此从"只读预览"升级为"可真正搬运"。默认 ExecMode=0（仅预览整理清单，不动玩家物资）。
    ///
    /// 触发：空闲自动整理（AutoOrganize=true 时，角色空闲 IsUnableToControl()==false 且冷却届满 → 跑一轮）。
    ///
    /// 规则（[StorageRules] 区，TargetRuleText）："关键词1/关键词2:目标柜configId; 分类B:configId"
    ///   关键词可为物品名片段或 ItemConfigId；未命中规则→默认柜 DefaultCabinetConfigId；
    ///   命中 ProtectedItemText（物品名关键词/ItemConfigId）的绝不移动。
    ///
    /// 移动（ExecMode=1）硬边界：
    ///   · 只经官方 reducer Ac_Item_ChangePos.SendAction 驱动，绝不直调 ItemManager.AddItem（§8 硬约束 #3）。
    ///   · 目标空位 = 读取目标柜现有物品的 BagPos，从 (1,1) 起选取首个未占格（probe 已验证该坐标系落位）。
    ///   · 单 tick 至多推进一件（MoveIntervalMs 节流），不卡 reducer/主线程。
    ///   · 一轮结束后整局重新对账总量守恒，不丢件/不重复/不破坏既有堆叠。
    /// </summary>
    public static class StorageButlerPlugin
    {
        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> ExecMode;
        internal static ConfigEntry<bool> AutoOrganize;
        internal static ConfigEntry<float> ScanCooldownSec;
        internal static ConfigEntry<bool> PreviewOnBoot;
        internal static ConfigEntry<int> TargetPickPolicy;
        internal static ConfigEntry<int> MoveIntervalMs;
        internal static ConfigEntry<string> ProtectedItemText;
        internal static ConfigEntry<string> TargetRuleText;
        internal static ConfigEntry<string> DefaultCabinetConfigId;
        internal static ConfigEntry<string> ExcludeCabinetText;

        private const int PROTECT = -1;

        private static float _lastScan;
        private static float _nextMoveAt;

        // 解析后的规则 / 保护集
        private static readonly List<Rule> Rules = new List<Rule>();
        private static readonly HashSet<int> ProtIds = new HashSet<int>();
        private static readonly List<string> ProtWords = new List<string>();
        private static int _defaultCfg;
        private static readonly HashSet<int> ExcludedCabIds = new HashSet<int>();
        private static readonly List<string> ExcludedCabWords = new List<string>();

        // 搬运队列
        private static List<ItemMove> _queue;
        private static readonly Dictionary<long, HashSet<Vector2Int>> _occ = new Dictionary<long, HashSet<Vector2Int>>();
        private static long _total0;

        /// <summary>由 BaseButler 主入口调用。</summary>
        public static void Init(ConfigFile cfg, ManualLogSource log, Harmony harmony)
        {
            Log = log;

            Enabled = cfg.Bind("StorageRules", "Enabled", true, "仓储管家总开关。");
            ExecMode = cfg.Bind("StorageRules", "ExecMode", 0, "0=仅预览整理清单（默认，不动玩家物资）；1=按规则执行移动。");
            AutoOrganize = cfg.Bind("StorageRules", "AutoOrganize", true, "true 时角色空闲且冷却届满自动跑一轮整理；false 仅 PreviewOnBoot/手动。");
            ScanCooldownSec = cfg.Bind("StorageRules", "ScanCooldownSec", 8f, "空闲自动整理的最小冷却间隔（秒），避免频繁扫描。");
            PreviewOnBoot = cfg.Bind("StorageRules", "PreviewOnBoot", true, "true 时开档后自动打印一次整理清单预览，便于核对规则。");
            TargetPickPolicy = cfg.Bind("StorageRules", "TargetPickPolicy", 1, "同一目标柜型命中多柜时选哪柜：0=顺序 1=最空优先。");
            MoveIntervalMs = cfg.Bind("StorageRules", "MoveIntervalMs", 60, "ExecMode=1 时每次移动的墙钟间隔毫秒（节流）。");
            ProtectedItemText = cfg.Bind("StorageRules", "ProtectedItemText", "任务, 绑定, 锁定, 关键道具, 传家宝, 未鉴定", "受保护物品关键词/ItemConfigId，绝不移动。");
            TargetRuleText = cfg.Bind("StorageRules", "TargetRuleText",
                "木板/木头/木材:80014; 金属/矿石/铁:80157; 食物/食材/蔬菜/肉:80156; 成品/装备/工具:80155",
                "分类规则：'关键词1/关键词2:目标柜configId; 分类B:configId'，关键词可为物品名片段或 ItemConfigId。");
            DefaultCabinetConfigId = cfg.Bind("StorageRules", "DefaultCabinetConfigId", "80156", "未命中任何规则时的默认落点柜 configId。");
            ExcludeCabinetText = cfg.Bind("StorageRules", "ExcludeCabinetText",
                "材料堆, 资源堆, 木料堆, 石头堆, 燃料堆",
                "不纳入仓储整理的容器 configId 或名字关键词（逗号分隔）。用于屏蔽玩家无法正常存取的隐藏/临时容器（如探图带回的箱子、不可拆除的材料堆），避免其被当作源/目标。");

            ParseRules();
            ParseCabinetBlacklist();

            var update = AccessTools.Method(typeof(HotGame.Battle.Logic.AgentManager), "AgentUpdate");
            if (update != null)
                harmony.Patch(update, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(StorageButlerPlugin), nameof(OnAgentTick))));
            else
                Log.LogWarning("[BaseButler][Storage] 未找到 AgentManager.AgentUpdate，空闲自动整理不可用。");

            Log.LogInfo($"[BaseButler][Storage] 仓储管家 Phase2 已就绪：ExecMode={(ExecMode.Value == 0 ? "预览" : "移动")}，自动={AutoOrganize.Value}，规则 {Rules.Count} 条。");
        }

        /// <summary>AgentUpdate Postfix：推进搬运队列 + 空闲自动扫描。任何异常只记日志，绝不影响游戏。</summary>
        internal static void OnAgentTick()
        {
            try
            {
                if (!Enabled.Value) return;
                var now = Time.realtimeSinceStartup;

                var world = HotGame.Battle.Logic.BattleLogicWorld.Instance;
                if (world == null) return;
                var am = world._AgentManager;
                var im = world._ItemManager;
                if (am == null || im == null) return;

                // 1) 进行中的搬运队列优先推进（仅空闲时，忙则暂停，符合不变式）
                if (_queue != null && _queue.Count > 0)
                {
                    if (IsBusy(am)) return;
                    AdvanceMover(world, am, im);
                    return;
                }

                // 2) 冷却节流
                if (now - _lastScan < ScanCooldownSec.Value) return;
                _lastScan = now;

                // 3) 空闲才扫描；AutoOrganize 周期跑，或 PreviewOnBoot 至少打一次预览
                if (!AutoOrganize.Value && !PreviewOnBoot.Value) return;
                if (IsBusy(am)) return;

                RunOrganizeCycle(world, am, im);
            }
            catch (Exception e)
            {
                Log.LogWarning("[BaseButler][Storage] 轮询异常：" + e.Message);
            }
        }

        private static bool IsBusy(HotGame.Battle.Logic.AgentManager am)
        {
            try
            {
                var role = am.GetLeadingRole();
                if (role == null) return false;
                var st = HotGame.Battle.Logic.AgentTools
                    .GetAgentComponent<HotGame.Battle.Logic.AgentStateComponent>(role);
                if (st == null) return false;
                return st.IsUnableToControl();
            }
            catch (Exception) { return false; }
        }

        // ------------------------------------------------------------------ 计划构建

        private sealed class Cabinet
        {
            public long Owner;
            public int Cfg;
        }

        private sealed class ItemMove
        {
            public long ItemId;
            public int Cfg;
            public int Count;
            public long SrcOwner;
            public long TargetOwner;
        }

        private sealed class Rule
        {
            public readonly HashSet<int> ConfigIds = new HashSet<int>();
            public readonly List<string> Keywords = new List<string>();
            public int Target;
        }

        private static void ParseRules()
        {
            Rules.Clear(); ProtIds.Clear(); ProtWords.Clear();
            try
            {
                foreach (var tok in Split(ProtectedItemText.Value))
                {
                    if (int.TryParse(tok, out var id)) ProtIds.Add(id);
                    else if (!string.IsNullOrEmpty(tok)) ProtWords.Add(Norm(tok));
                }
                int.TryParse(DefaultCabinetConfigId.Value.Trim(), out _defaultCfg);

                foreach (var entry in TargetRuleText.Value.Split(new[] { ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var e = entry.Trim();
                    if (e.Length == 0) continue;
                    var idx = e.IndexOf(':');
                    if (idx < 0) idx = e.IndexOf('：');
                    if (idx < 0) continue;
                    var keyPart = e.Substring(0, idx).Trim();
                    if (!int.TryParse(e.Substring(idx + 1).Trim(), out var target)) continue;
                    if (keyPart.Length == 0) continue;

                    var rule = new Rule { Target = target };
                    foreach (var tok in keyPart.Split(new[] { '/', '\\', ',', '，' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var t = tok.Trim();
                        if (t.Length == 0) continue;
                        if (int.TryParse(t, out var id)) rule.ConfigIds.Add(id);
                        else rule.Keywords.Add(Norm(t));
                    }
                    if (rule.ConfigIds.Count > 0 || rule.Keywords.Count > 0) Rules.Add(rule);
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("[BaseButler][Storage] 规则解析失败（用默认）：" + e.Message);
            }
        }

        /// <summary>
        /// 解析"排除容器"黑名单（configId 或名字关键词）。玩家反馈存在一类"有 Bag 但无法拆除/无法正常存取"
        /// 的隐藏/临时容器（如探图带回的箱子、不可拆除的材料堆）会被 GetFurnituresWithBag 一并返回，
        /// 且其 id 无法用外部 mod 读到。这里提供一个可配置的黑名单把它们从"源/目标"双双剔除。
        /// </summary>
        private static void ParseCabinetBlacklist()
        {
            ExcludedCabIds.Clear();
            ExcludedCabWords.Clear();
            try
            {
                foreach (var tok in Split(ExcludeCabinetText.Value))
                {
                    if (int.TryParse(tok, out var id)) ExcludedCabIds.Add(id);
                    else if (tok.Length > 0) ExcludedCabWords.Add(Norm(tok));
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 该家具 configId 是否不应作为仓储整理的源/目标：
        ///   1) 命中玩家配置的 ExcludeCabinetText 黑名单；
        ///   2) 命中 A 模块（CookingSourceExpand）已确认的"非储物家具"排除名单（门/窗/床/电器/无人机/小汽车等）
        ///      ——与烹饪来源扩展保持同一把尺子，避免这些带 Bag 的物也被当成柜子。
        /// </summary>
        private static bool IsCabinetExcluded(int cid)
        {
            if (cid != 0 && ExcludedCabIds.Contains(cid)) return true;
            if (CookingBagPatch.IsStorageExcluded(cid)) return true;              // A 模块 configId 排除名单
            string name = CookingBagPatch.StorageResolveName(cid);
            if (name.Length > 0)
            {
                if (CookingBagPatch.IsStorageExcludedName(name)) return true;      // A 模块名字排除名单
                var n = NormName(name);
                foreach (var w in ExcludedCabWords)
                    if (w.Length > 0 && n.Contains(w)) return true;
            }
            return false;
        }

        private static IEnumerable<string> Split(string s)
        {
            if (string.IsNullOrEmpty(s) || _rulesSplitter == null) return Array.Empty<string>();
            return s.Split(_rulesSplitter, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0);
        }

        private static readonly char[] _rulesSplitter = { ',', '，', ';', '；', '/', '、', ' ', '\t' };

        private static string Norm(string s) => new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
        private static string NormName(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace(" ", "").Replace("　", "").ToLowerInvariant();
        }

        // ------------------------------------------------------------------ 物品名解析（动态反射，失败则退化为 configId 匹配）

        private static MethodInfo _getItemCfg;
        private static PropertyInfo _nLocal, _nName;
        private static readonly Dictionary<int, string> _nameCache = new Dictionary<int, string>();

        private static string ItemName(int cfgId)
        {
            if (_nameCache.TryGetValue(cfgId, out var c)) return c;
            var s = ResolveName(cfgId);
            _nameCache[cfgId] = s;
            return s;
        }

        private static string ResolveName(int cfgId)
        {
            try
            {
                if (_getItemCfg == null)
                {
                    var cmT = typeof(HotGame.ConfigManager);
                    _getItemCfg = AccessTools.Method(cmT, "Get_Config_Item", new[] { typeof(int) });
                    if (_getItemCfg == null) return "";
                }
                var cm = HotGame.ConfigManager.Instance;
                if (cm == null) return "";
                var cf = _getItemCfg.Invoke(cm, new object[] { cfgId });
                if (cf == null) return "";
                var t = cf.GetType();
                if (_nLocal == null) _nLocal = t.GetProperty("Name_Local");
                if (_nName == null) _nName = t.GetProperty("Name");
                string s = null;
                try { s = _nLocal?.GetValue(cf)?.ToString(); } catch (Exception) { }
                if (string.IsNullOrEmpty(s)) { try { s = _nName?.GetValue(cf)?.ToString(); } catch (Exception) { } }
                return s ?? "";
            }
            catch (Exception) { return ""; }
        }

        // ------------------------------------------------------------------ 分类

        private static bool IsProtected(int cfgId, string name)
        {
            if (ProtIds.Contains(cfgId)) return true;
            if (name.Length == 0) return false;
            var n = NormName(name);
            foreach (var w in ProtWords)
                if (n.Contains(w)) return true;
            return false;
        }

        private static int PickTargetCfg(int cfgId, string name)
        {
            if (IsProtected(cfgId, name)) return PROTECT;
            foreach (var r in Rules)
            {
                if (r.ConfigIds.Contains(cfgId)) return r.Target;
                if (name.Length > 0)
                {
                    var n = NormName(name);
                    foreach (var k in r.Keywords)
                        if (k.Length > 0 && n.Contains(k)) return r.Target;
                }
            }
            return _defaultCfg;
        }

        private static List<ItemMove> BuildPlan(HotGame.Battle.Logic.ItemManager im, List<Cabinet> cabinets)
        {
            // 每柜物品件数只算一遍，供选柜策略复用，避免 O(物品×柜) 重复读柜
            var counts = cabinets.ToDictionary(c => c.Owner, c => SafeSum(im, c.Owner));

            var plan = new List<ItemMove>();
            foreach (var src in cabinets)
            {
                try
                {
                    var items = im.GetItemDataList(src.Owner);
                    if (items == null) continue;
                    foreach (var it in items)
                    {
                        if (it == null || it.ItemCount <= 0) continue;
                        int cfgId = it.ItemConfigId;
                        string name = ItemName(cfgId);
                        int targetCfg = PickTargetCfg(cfgId, name);
                        if (targetCfg == PROTECT) continue; // 受保护物品不搬
                        long targetOwner = PickTargetOwner(cabinets, targetCfg, counts);
                        if (targetOwner == 0 || targetOwner == src.Owner) continue; // 无目标柜 / 已在目标位 → 跳过
                        plan.Add(new ItemMove
                        {
                            ItemId = it.InstanceId,
                            Cfg = cfgId,
                            Count = (int)it.ItemCount,
                            SrcOwner = src.Owner,
                            TargetOwner = targetOwner
                        });
                    }
                }
                catch (Exception) { /* 单柜读取失败只影响该柜 */ }
            }
            return plan;
        }

        private static long PickTargetOwner(List<Cabinet> cabinets, int targetCfg, IReadOnlyDictionary<long, long> counts)
        {
            long chosen = 0; long chosenItems = long.MaxValue;
            foreach (var c in cabinets)
            {
                if (c.Cfg != targetCfg) continue;
                if (TargetPickPolicy.Value == 0)
                {
                    if (chosen == 0) chosen = c.Owner;
                    continue;
                }
                long n = counts.TryGetValue(c.Owner, out var v) ? v : 0;
                if (n < chosenItems) { chosenItems = n; chosen = c.Owner; }
            }
            return chosen;
        }

        private static long SafeSum(HotGame.Battle.Logic.ItemManager im, long owner)
        {
            long s = 0;
            try
            {
                var items = im.GetItemDataList(owner);
                if (items != null)
                    foreach (var it in items)
                        if (it != null && it.ItemCount > 0) s += it.ItemCount;
            }
            catch (Exception) { }
            return s;
        }

        // ------------------------------------------------------------------ 整理循环

        private static void RunOrganizeCycle(HotGame.Battle.Logic.BattleLogicWorld world,
            HotGame.Battle.Logic.AgentManager am, HotGame.Battle.Logic.ItemManager im)
        {
            int homeMap = 0;
            try { homeMap = am.GetHomeMapId(); } catch (Exception) { }
            if (homeMap == 0) return;

            var cabinets = new List<Cabinet>();
            int enumTotal = 0, filtered = 0;
            try
            {
                var hb = am.GetFurnituresWithBag(homeMap, false, true);
                if (hb != null)
                    foreach (var f in hb)
                    {
                        if (f == null) continue;
                        enumTotal++;
                        long oid = f.InstanceId; int cid = f.AgentConfigId;
                        if (oid == 0) { filtered++; continue; }
                        if (IsCabinetExcluded(cid)) { filtered++; continue; } // 屏蔽非储物家具 / 玩家无法存取的隐藏容器
                        cabinets.Add(new Cabinet { Owner = oid, Cfg = cid });
                    }
                Log.LogInfo($"[BaseButler][Storage] 枚举储物家居 {enumTotal} 个，过滤剔除 {filtered} 个，纳入整理 {cabinets.Count} 个。");
            }
            catch (Exception e) { Log.LogWarning("[BaseButler][Storage] 枚举储物柜失败：" + e.Message); return; }

            if (cabinets.Count == 0) return;
            var plan = BuildPlan(im, cabinets);

            // 预览模式：清单未变化则不重复刷屏
            if (ExecMode.Value != 1)
            {
                string sig = PlanSig(plan);
                if (sig == _prevSig) return;
                _prevSig = sig;
            }

            LogPlan(im, cabinets.Count, plan, out long totalMoved);

            if (ExecMode.Value != 1 || plan.Count == 0) return;

            // 进入移动模式：预登记守恒基线 + 建队列
            _total0 = 0;
            foreach (var c in cabinets) _total0 += SafeSum(im, c.Owner);
            _occ.Clear();
            _queue = plan;
            _qTotal = plan.Count; _qt = 0;
            _nextMoveAt = Time.realtimeSinceStartup;
            Log.LogInfo($"[BaseButler][Storage] 开始执行搬运 {plan.Count} 项，守恒基线 total={_total0}。");
        }

        private static string _prevSig = string.Empty; // 预览模式：上次清单签名，避免重复刷屏

        private static string PlanSig(List<ItemMove> plan)
        {
            var parts = plan.OrderBy(p => p.Cfg).ThenBy(p => p.TargetOwner)
                .Select(p => $"{p.Cfg}@{p.TargetOwner}").Distinct();
            return string.Join(",", parts);
        }

        private static void LogPlan(HotGame.Battle.Logic.ItemManager im, int cabinetCount, List<ItemMove> plan, out long totalMoved)
        {
            totalMoved = 0;
            var sb = new StringBuilder();
            sb.AppendLine($"========== BaseButler 仓储整理（{(ExecMode.Value == 1 ? "执行" : "预览，不移动")}） =========");
            sb.AppendLine($"时间: {DateTime.Now:HH:mm:ss}   储物柜 {cabinetCount} 个   待搬物品项 {plan.Count}");

            var byTarget = plan.GroupBy(p => p.TargetOwner);
            foreach (var g in byTarget)
            {
                long cnt = g.Sum(p => p.Count);
                totalMoved += cnt;
                string cfgIds = string.Join(",", g.Select(p => p.Cfg).Distinct());
                sb.AppendLine($"→ 柜ownerId={g.Key}：{cnt} 件（configIds: {cfgIds}）");
            }
            if (plan.Count == 0)
                sb.AppendLine("（无待搬物品：要么已归位、要么未命中规则、要么全受保护）");
            if (ExecMode.Value != 1)
                sb.AppendLine("提示：当前为预览模式(ExecMode=0)，未移动任何物品。");
            Log.LogInfo(sb.ToString());
        }

        // ------------------------------------------------------------------ 搬运执行

        private static void AdvanceMover(HotGame.Battle.Logic.BattleLogicWorld world,
            HotGame.Battle.Logic.AgentManager am, HotGame.Battle.Logic.ItemManager im)
        {
            var now = Time.realtimeSinceStartup;
            if (now < _nextMoveAt) return;
            _nextMoveAt = now + MoveIntervalMs.Value * 0.001f;

            var item = _queue[0];
            _queue.RemoveAt(0);
            _qt++;
            try
            {
                Vector2Int cell = FirstFreeCell(im, item.TargetOwner);
                if (cell.x < 0)
                {
                    Log.LogWarning($"[BaseButler][Storage] 目标柜ownerId={item.TargetOwner} 已满，跳过 configId={item.Cfg}。");
                    return;
                }
                CookingUI.Ac_Item_ChangePos.SendAction(item.ItemId, item.SrcOwner, item.TargetOwner, cell);
                Occupy(im, item.TargetOwner, cell);
                if (_qt % 20 == 0)
                    Log.LogInfo($"[BaseButler][Storage] 移动中 {_qt}/{_qTotal}: configId={item.Cfg} ×{item.Count} → 柜{item.TargetOwner}@{cell}");
            }
            catch (Exception e)
            {
                Log.LogWarning("[BaseButler][Storage] 搬移单件失败：" + e.Message);
            }

            if (_queue.Count == 0)
                FinishMove(im);
        }

        private static int _qt, _qTotal;

        private static Vector2Int FirstFreeCell(HotGame.Battle.Logic.ItemManager im, long targetOwner)
        {
            if (!_occ.TryGetValue(targetOwner, out var occ))
            {
                occ = new HashSet<Vector2Int>();
                _occ[targetOwner] = occ;
                try
                {
                    var items = im.GetItemDataList(targetOwner);
                    if (items != null)
                        foreach (var it in items)
                        {
                            if (it == null || it.ItemCount <= 0) continue;
                            try { occ.Add(it.BagPos); } catch (Exception) { }
                        }
                }
                catch (Exception) { }
            }
            for (int x = 1; x <= 30; x++)
                for (int y = 1; y <= 14; y++)
                {
                    var c = new Vector2Int(x, y);
                    if (!occ.Contains(c)) return c;
                }
            return new Vector2Int(-1, -1);
        }

        private static void Occupy(HotGame.Battle.Logic.ItemManager im, long targetOwner, Vector2Int cell)
        {
            if (_occ.TryGetValue(targetOwner, out var occ)) occ.Add(cell);
        }

        private static void FinishMove(HotGame.Battle.Logic.ItemManager im)
        {
            var world = HotGame.Battle.Logic.BattleLogicWorld.Instance;
            long total1 = 0;
            if (world != null)
            {
                var am = world._AgentManager;
                int homeMap = 0;
                try { homeMap = am.GetHomeMapId(); } catch (Exception) { }
                if (homeMap != 0)
                {
                    var hb = am.GetFurnituresWithBag(homeMap, false, true);
                    if (hb != null)
                        foreach (var f in hb)
                        {
                            if (f == null) continue;
                            total1 += SafeSum(im, f.InstanceId);
                        }
                }
            }
            Log.LogInfo($"[BaseButler][Storage] 搬运结束：守恒基准={_total0} 实际={total1} => {(total1 == _total0 ? "✅ 守恒" : "⚠ 请人工核对")}。");
            _queue = null;
        }
    }
}