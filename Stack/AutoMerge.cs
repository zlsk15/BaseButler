using System;
using System.Collections.Generic;
using GameCore.HotUpdate;
using GameCore.HotUpdate.Battle.Logic;
using GameCore.HotUpdate.ReduxUI;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace BaseButler.Stack
{
    /// <summary>
    /// 自动合并（SLTweaksSplit 1.1.1 新增）
    ///
    /// 问题：物品的堆叠上限决定"一堆能装几个"。原版绝大多数物品上限很低（日志显示大量物品原生上限=1，
    /// 木料类常见 4/5）。上限满了以后游戏只能再开一堆 —— 于是出现"一堆 4、一堆 2 怎么拖都合不到一起"
    /// （其实不是不给你合，是 4+2 超过了该物品的 StackLimit，游戏判定无处可放）。
    ///
    /// 做法：
    ///   1. 堆叠上限由 StackLimitSystem 统一抬高（规则词命中即可，见 cfg 的 NameKeywords/ExactNames）；
    ///   2. 本系统在后台每 N 秒扫一遍所有背包/家具容器，把【逐实例数据完全一致】的同种物品多堆并成一堆，
    ///      全程无需玩家任何按键或额外操作。
    ///
    /// 安全边界：
    ///   - 只合并逐实例字段完全一致的堆（StartTime/TimeLeft/UseTimes/TimeScale/InstanceBurnValue/
    ///     InstanceWeight/Polluted/IsMapPreset/InstanceVD 全等），不会把不同新鲜度/污染/耐久的东西揉一起；
    ///   - 烹饪家具袋内跳过（本模组的"逐件独立存放"特性必须保留）；
    ///   - 拆分操作进行中（Suppress/SuppressAll）时跳过该拍；
    ///   - 改动 ItemManager 后按老规矩补 redux 同步（Ac_Item_AddOrNotify + 重建列表 + MarkAllMsgDirty）。
    /// </summary>
    public static class AutoMergeSystem
    {
        internal static bool Enabled = true;
        internal static float IntervalSec = 5f;
        internal static bool ForceMerge = true;
        internal static bool LogDiff = true;
        internal static bool MergePerishable = true;

        /// <summary>
        /// 允许"只差标签类字段"的堆互相合并。
        /// 标签类字段 = IsMapPreset（地图预置刷出来的）/ NoPackage（已拆包）：它们只记录物品"哪来的"，
        /// 不改变物品本身的价值（新鲜度 / 污染 / 耐久 / 品质 / 使用次数）。合并后取目标堆的标记。
        /// 可乐那两堆（一堆 2 一堆 3、其余字段逐位相同、只有 preset 不同）就是被这一项卡住的。
        /// </summary>
        internal static bool MergeFlagDiff = true;

        /// <summary>
        /// 落袋即合并（有保质期的物品也适用）：物品刚被 AddItem 放进容器后立刻催一次合并。
        /// 关掉则回到"最多等 IntervalSec 才合并"。
        /// </summary>
        internal static bool MergeOnAdd = true;

        private static float _nextAt;
        private static float _lastFastAt;
        private static bool _busy;
        private static bool _loggedError;
        private static bool _loggedSuppress;
        private static readonly HashSet<string> _diffLogged = new HashSet<string>();

        public static void Tick()
        {
            try
            {
                if (!Enabled) return;
                // 未进入存档 / 世界已销毁时不要碰世界对象（主菜单也会跑 Update）
                try
                {
                    if (!BaseSingleton<BattleLogicWorld>.IsInstanceCreated) return;
                }
                catch
                {
                    return;
                }
                if (CookingNoStack.Suppress || CookingNoStack.SuppressAll)
                {
                    if (!_loggedSuppress)
                    {
                        _loggedSuppress = true;
                        Plugin.LogSource?.LogWarning("[自动合并] 检测到拆分抑制标志处于 true（Suppress=" +
                            CookingNoStack.Suppress + ", SuppressAll=" + CookingNoStack.SuppressAll + "），本拍跳过");
                    }
                    return; // 拆分流程进行中
                }
                if (Time.unscaledTime < _nextAt) return;
                float iv = IntervalSec;
                if (iv < 1f) iv = 1f;
                _nextAt = Time.unscaledTime + iv;

                if (_busy) return;
                _busy = true;
                try { Run(); }
                finally { _busy = false; }
            }
            catch (Exception ex)
            {
                if (!_loggedError)
                {
                    _loggedError = true;
                    Plugin.LogSource?.LogWarning("[自动合并] Tick 异常: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// 请求"尽快跑一次合并"。
        /// 用途：物品刚被放进容器（ItemManager.AddItem 之后）时催一下，让"有保质期的物品"也表现为
        /// 【落袋即合并】—— 不必等满 IntervalSec 的扫描间隔。
        /// 节流：最多 0.5 秒催一次，避免一次拾取几十件时反复全表扫描。
        /// 注意：这里只改一个时间戳，**不动任何物品数据**，所以放在 AddItem 后缀里是安全的。
        /// </summary>
        internal static void RequestImmediateRun()
        {
            if (!Enabled || _busy) return;
            float now;
            try { now = Time.unscaledTime; } catch { return; }
            if (now >= _nextAt) return;                 // 本来就到点了，不用催
            if (now - _lastFastAt < 0.5f) return;       // 节流
            _lastFastAt = now;
            _nextAt = 0f;                               // 让下一拍立刻跑
        }

        private static void Run()
        {
            ItemManager im = SplitHalfPatch.GetIM();
            if (im == null) return;

            var cache = im.Cache;
            if (cache == null) return;

            // 1) 快照：合并过程会改字典（RemoveItem），不能边遍历边改
            var items = new List<ItemData>();
            var e = cache.GetEnumerator();
            while (e.MoveNext())
            {
                ItemData it = e.Current.Value;
                if (it == null) continue;
                if (it.ItemCount <= 0) continue;
                items.Add(it);
            }
            if (items.Count < 2) return;

            // 2) 按 (owner, configId) 分组
            var groups = new Dictionary<long, Dictionary<int, List<ItemData>>>();
            foreach (ItemData it in items)
            {
                long owner = it.OwnerId;
                if (owner == 0) continue;
                int cfg = it.ItemConfigId;
                if (cfg <= 0) continue;

                Dictionary<int, List<ItemData>> byCfg;
                if (!groups.TryGetValue(owner, out byCfg))
                {
                    byCfg = new Dictionary<int, List<ItemData>>();
                    groups[owner] = byCfg;
                }
                List<ItemData> list;
                if (!byCfg.TryGetValue(cfg, out list))
                {
                    list = new List<ItemData>();
                    byCfg[cfg] = list;
                }
                list.Add(it);
            }

            ConfigManager cm = BaseSingleton<ConfigManager>.Instance;
            if (cm == null) return;

            int mergedGroups = 0;
            int movedTotal = 0;
            int unifiedTotal = 0;
            var report = new System.Text.StringBuilder();

            foreach (var ownerKv in groups)
            {
                long owner = ownerKv.Key;
                if (IsCookingBag(im, owner)) continue;

                foreach (var cfgKv in ownerKv.Value)
                {
                    List<ItemData> stacks = cfgKv.Value;
                    if (stacks.Count < 2) continue;

                    int limit = 0;
                    string name = null;
                    Config_Item ci = null;
                    try
                    {
                        ci = cm.Get_Config_Item(cfgKv.Key);
                        if (ci != null)
                        {
                            limit = ci.StackLimit;
                            name = Display(ci);
                        }
                    }
                    catch { }
                    if (limit < 2) continue;

                    // 有时效的物品：先把"同污染状态"子组内的保质期统一成组内最长的那堆，
                    // 再交给合并逻辑（合并只是搬数量，堆上挂的是谁的时效，结果就是谁的时效）。
                    int unified = 0;
                    if (MergePerishable && SafeLife(ci) > 0)
                    {
                        unified = UnifyBestShelfLife(stacks);
                        unifiedTotal += unified;
                    }

                    int moved = MergeGroup(im, owner, stacks, limit, ci);
                    if (moved > 0)
                    {
                        mergedGroups++;
                        movedTotal += moved;
                        if (report.Length < 400)
                        {
                            report.Append(name ?? ("cfg" + cfgKv.Key)).Append("(x").Append(moved).Append(") ");
                        }
                    }
                    else
                    {
                        MaybeLogDiff(owner, cfgKv.Key, stacks, ci, limit);
                    }
                }
            }

            if (movedTotal > 0 || unifiedTotal > 0)
            {
                Plugin.LogSource?.LogInfo("[自动合并] 后台并堆 " + mergedGroups + " 组，共移动 " + movedTotal +
                    " 个" + (unifiedTotal > 0 ? ("，其中统一保质期 " + unifiedTotal + " 堆（按最高时效生效）") : "") +
                    "：" + report.ToString());
                RefreshOpenUi();
            }
        }

        /// <summary>把一组同种物品的多个堆尽量并起来，返回移动的数量。</summary>
        private static int MergeGroup(ItemManager im, long owner, List<ItemData> stacks, int limit, Config_Item ci)
        {
            var dead = new HashSet<long>();
            int movedTotal = 0;
            bool forceOk = ForceMerge && ci != null && SafeLife(ci) <= 0;

            for (int guard = 0; guard < 200; guard++)
            {
                bool acted = false;

                foreach (ItemData target in stacks)
                {
                    long tid = IdOf(target);
                    if (tid == 0 || dead.Contains(tid)) continue;
                    if (target.ItemCount <= 0) continue;
                    int space = limit - target.ItemCount;
                    if (space <= 0) continue;

                    foreach (ItemData src in stacks)
                    {
                        if (src == target) continue;
                        long sid = IdOf(src);
                        if (sid == 0 || dead.Contains(sid)) continue;
                        if (src.ItemCount <= 0) continue;
                        if (!CanMergePair(src, target, forceOk)) continue;

                        // 只搬"整堆都塞得下"的：这样每次合并都真正少掉一堆（腾出一个格子），
                        // 且算法单调收敛、绝不空转。部分搬运（比如 6+6 拼成 10+2）并不减少堆数、
                        // 不省格子，还会每 5 秒反复搬一次 —— 那正是 1.1.4 之前日志里
                        // "后台并堆 8 组，共移动 9800 个" 反复刷屏的原因，必须禁止。
                        int move = src.ItemCount;
                        if (move <= 0 || move > space) continue;

                        try
                        {
                            im.SetItemCount(target, target.ItemCount + move);
                            im.SetItemCount(src, src.ItemCount - move);
                        }
                        catch
                        {
                            continue;
                        }

                        SplitHalfPatch.NotifyItem(target);

                        if (src.ItemCount <= 0)
                        {
                            try { im.RemoveItem(sid); } catch { }
                            dead.Add(sid);
                        }
                        else
                        {
                            SplitHalfPatch.NotifyItem(src);
                        }

                        movedTotal += move;
                        acted = true;
                        break;
                    }

                    if (acted) break;
                }

                if (!acted) break;
            }

            return movedTotal;
        }

        private static int SafeLife(Config_Item ci)
        {
            try { return ci.Life; } catch { return 0; }
        }

        /// <summary>
        /// 有时效物品：按 (污染状态 + 是否带逐实例附加数据) 分子组，组内把每一堆的
        /// StartTime / TimeLeft / OriginalTimeLeft / TimeLeftFrac 统一成【剩余保质期最长】那堆的值。
        /// 统一之后它们就是"同源"了，合并逻辑会把它们并成一堆 —— 结果这堆按最高的保质期生效。
        /// 发霉（Polluted）与新鲜的分属不同子组，不会被混到一起。
        /// 返回被改写的堆数。
        /// </summary>
        private static int UnifyBestShelfLife(List<ItemData> stacks)
        {
            var subgroups = new Dictionary<string, List<ItemData>>();
            foreach (ItemData it in stacks)
            {
                if (it == null) continue;
                if (it.ItemCount <= 0) continue;
                string key;
                try { key = (it.Polluted ? "1" : "0") + "|" + (HasInstanceArray(it) ? "1" : "0"); }
                catch { continue; }
                List<ItemData> sub;
                if (!subgroups.TryGetValue(key, out sub)) { sub = new List<ItemData>(); subgroups[key] = sub; }
                sub.Add(it);
            }

            int changed = 0;
            foreach (var kv in subgroups)
            {
                List<ItemData> sub = kv.Value;
                if (sub.Count < 2) continue;

                ItemData best = null;
                foreach (ItemData it in sub)
                {
                    if (best == null || IsFresher(it, best)) best = it;
                }
                if (best == null) continue;

                foreach (ItemData it in sub)
                {
                    if (it == best) continue;
                    try
                    {
                        if (it.TimeLeft == best.TimeLeft
                            && it.OriginalTimeLeft == best.OriginalTimeLeft
                            && it.StartTime == best.StartTime
                            && Math.Abs(it.TimeLeftFrac - best.TimeLeftFrac) < 0.0001f)
                        {
                            continue;
                        }
                        CopyTime(it, best);
                        SplitHalfPatch.NotifyItem(it);
                        changed++;
                    }
                    catch { }
                }
            }
            return changed;
        }

        private static bool IsFresher(ItemData a, ItemData b)
        {
            try
            {
                if (a.TimeLeft != b.TimeLeft) return a.TimeLeft > b.TimeLeft;
                if (Math.Abs(a.TimeLeftFrac - b.TimeLeftFrac) > 0.0001f) return a.TimeLeftFrac > b.TimeLeftFrac;
                return a.OriginalTimeLeft > b.OriginalTimeLeft;
            }
            catch
            {
                return false;
            }
        }

        private static void CopyTime(ItemData dst, ItemData src)
        {
            dst.StartTime = src.StartTime;
            dst.TimeLeft = src.TimeLeft;
            dst.OriginalTimeLeft = src.OriginalTimeLeft;
            dst.TimeLeftFrac = src.TimeLeftFrac;
        }

        /// <summary>
        /// 两堆能不能并。三条放行路径（从严到宽）：
        ///   ① 逐实例字段完全一致（原判定）；
        ///   ② 只差"标签类字段"（IsMapPreset / NoPackage）—— 这两项只记录物品来源，不改变物品价值；
        ///   ③ 普通材料（配置无保质期、无附加数组）的强制合并。
        /// 新鲜度不同、污染不同、带品质/耐久的，一律仍然不放行（要并也得先由
        /// UnifyBestShelfLife 把同一子组的保质期统一到最长的那堆，再靠 ① 并）。
        /// </summary>
        private static bool CanMergePair(ItemData a, ItemData b, bool forceOk)
        {
            if (SameInstance(a, b)) return true;
            if (MergeFlagDiff && SameInstance(a, b, false)) return true;
            return forceOk && CanForcePair(a, b);
        }

        /// <summary>
        /// 强制合并的适用范围：只是普通材料（配置无保质期）+ 两堆都没有逐实例附加数组（品质/耐久等）。
        /// 这样食物（有保质期）和带品质/耐久的物品不会被硬合，避免抹平差异。
        /// </summary>
        private static bool CanForcePair(ItemData a, ItemData b)
        {
            try
            {
                if (HasInstanceArray(a) || HasInstanceArray(b)) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool HasInstanceArray(ItemData it)
        {
            try
            {
                if (it.InstanceVD != null && it.InstanceVD.Length > 0) return true;
                if (it.InstanceEffectEnd != null && it.InstanceEffectEnd.Length > 0) return true;
                return false;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>同一容器里同种物品有多堆却合不了时，把每堆的实例字段打出来（每个物品只打一次）。</summary>
        private static void MaybeLogDiff(long owner, int cfgId, List<ItemData> stacks, Config_Item ci, int limit)
        {
            if (!LogDiff) return;
            string key = owner + ":" + cfgId;
            if (_diffLogged.Contains(key)) return;
            _diffLogged.Add(key);

            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(Display(ci)).Append(" 上限=").Append(limit).Append(" 共 ").Append(stacks.Count).Append(" 堆: ");
                foreach (ItemData it in stacks)
                {
                    if (it == null) continue;
                    sb.Append('#').Append(it.ItemCount).Append('{');
                    sb.Append("start=").Append(it.StartTime);
                    sb.Append(",left=").Append(it.TimeLeft);
                    sb.Append(",use=").Append(it.UseTimes).Append('/').Append(it.MaxUseTimes);
                    sb.Append(",ts=").Append(it.TimeScale.ToString("0.###"));
                    sb.Append(",burn=").Append(it.InstanceBurnValue);
                    sb.Append(",w=").Append(it.InstanceWeight);
                    sb.Append(",pol=").Append(it.Polluted ? 1 : 0);
                    sb.Append(",preset=").Append(it.IsMapPreset ? 1 : 0);
                    sb.Append(",nopkg=").Append(it.NoPackage ? 1 : 0);
                    sb.Append(",vd=").Append(ArrLen(it.InstanceVD));
                    sb.Append(",eff=").Append(ArrLen(it.InstanceEffectEnd));
                    sb.Append("} ");
                }
                Plugin.LogSource?.LogInfo("[自动合并] 未能合并（实例数据差异）: " + sb.ToString());

                // ★ 直接点名"差在哪"，免得那一长串字段要人肉比对（"其他物品以后也会不会这样"的兜底诊断）
                ItemData first = null;
                foreach (ItemData it in stacks)
                {
                    if (it == null || it.ItemCount <= 0) continue;
                    first = it;
                    break;
                }
                if (first != null)
                {
                    var d = new System.Text.StringBuilder();
                    foreach (ItemData it in stacks)
                    {
                        if (it == null || it == first || it.ItemCount <= 0) continue;
                        d.Append(" #").Append(it.ItemCount).Append(" 差[").Append(DiffFields(first, it)).Append(']');
                    }
                    if (d.Length > 0)
                    {
                        Plugin.LogSource?.LogInfo("[自动合并] 差在哪（相对第 1 堆）: " + Display(ci) + d);
                    }
                }
            }
            catch { }
        }

        /// <summary>列出两堆之间"哪些逐实例字段不同"，用于一眼定位"为什么合不了"。</summary>
        private static string DiffFields(ItemData a, ItemData b)
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                void Add(string name, bool diff)
                {
                    if (!diff) return;
                    if (sb.Length > 0) sb.Append(',');
                    sb.Append(name);
                }
                Add("start", a.StartTime != b.StartTime);
                Add("left", a.TimeLeft != b.TimeLeft);
                Add("origLeft", a.OriginalTimeLeft != b.OriginalTimeLeft);
                Add("leftFrac", Math.Abs(a.TimeLeftFrac - b.TimeLeftFrac) > 0.0001f);
                Add("use", a.UseTimes != b.UseTimes || a.MaxUseTimes != b.MaxUseTimes);
                Add("ts", Math.Abs(a.TimeScale - b.TimeScale) > 0.0001f);
                Add("burn", a.InstanceBurnValue != b.InstanceBurnValue);
                Add("weight", a.InstanceWeight != b.InstanceWeight);
                Add("polluted", a.Polluted != b.Polluted);
                Add("preset", a.IsMapPreset != b.IsMapPreset);
                Add("nopkg", a.NoPackage != b.NoPackage);
                Add("vd", !SameFloats(a.InstanceVD, b.InstanceVD));
                Add("eff", !SameFloats(a.InstanceEffectEnd, b.InstanceEffectEnd));
            }
            catch { return "?"; }
            return sb.Length == 0 ? "无" : sb.ToString();
        }

        private static int ArrLen(Il2CppStructArray<float> a)
        {
            try { return a == null ? 0 : a.Length; } catch { return -1; }
        }

        /// <summary>逐实例字段完全一致才算"同一种东西"，避免把不同新鲜度/污染/耐久的揉一起。</summary>
        private static bool SameInstance(ItemData a, ItemData b)
        {
            return SameInstance(a, b, true);
        }

        /// <summary>
        /// compareFlags = false 时跳过"标签类字段"（IsMapPreset / NoPackage）的比较：
        /// 这两项只记录物品来源（地图预置刷出来的 / 已拆包），不改变物品本身的价值。
        /// 其余字段（保质期 / 使用次数 / 时间倍率 / 热值 / 重量 / 污染 / 附加数组）一项不减。
        /// </summary>
        private static bool SameInstance(ItemData a, ItemData b, bool compareFlags)
        {
            if (a == null || b == null) return false;
            try
            {
                if (a.ItemConfigId != b.ItemConfigId) return false;
                if (a.StartTime != b.StartTime) return false;
                if (a.TimeLeft != b.TimeLeft) return false;
                if (a.UseTimes != b.UseTimes) return false;
                if (a.MaxUseTimes != b.MaxUseTimes) return false;
                if (Math.Abs(a.TimeScale - b.TimeScale) > 0.0001f) return false;
                if (a.InstanceBurnValue != b.InstanceBurnValue) return false;
                if (a.InstanceWeight != b.InstanceWeight) return false;
                if (a.Polluted != b.Polluted) return false;
                if (compareFlags)
                {
                    if (a.IsMapPreset != b.IsMapPreset) return false;
                    if (a.NoPackage != b.NoPackage) return false;
                }
                if (!SameFloats(a.InstanceVD, b.InstanceVD)) return false;
                if (!SameFloats(a.InstanceEffectEnd, b.InstanceEffectEnd)) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool SameFloats(Il2CppStructArray<float> a, Il2CppStructArray<float> b)
        {
            try
            {
                bool an = a == null;
                bool bn = b == null;
                if (an || bn) return an == bn;
                int la = a.Length;
                int lb = b.Length;
                if (la != lb) return false;
                for (int i = 0; i < la; i++)
                {
                    if (Math.Abs(a[i] - b[i]) > 0.0001f) return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static long IdOf(ItemData it)
        {
            try { return ((BaseEntity)it).InstanceId; } catch { return 0L; }
        }

        private static bool IsCookingBag(ItemManager im, long owner)
        {
            try { return im.IsCookingFurnitureBag(owner); } catch { return false; }
        }

        private static string Display(Config_Item ci)
        {
            try
            {
                string s = ci.ItemName_Local;
                if (string.IsNullOrEmpty(s)) s = ci.ItemName;
                return string.IsNullOrEmpty(s) ? ("ID" + ci.ID) : s;
            }
            catch
            {
                return "?";
            }
        }

        /// <summary>
        /// 「不可标脏」页面集合：这些页面前端持有本地推进 / 本地演出状态，后端消息只给锚点或旧快照，
        /// 一旦被 MarkAllMsgDirty 重推，界面就会被拽回锚点（时钟/倒计时/进度回跳、浮层"诈尸"、演出重播）。
        /// 证据（各页面脚本自己的引擎与消息处理器）：
        ///   CoreUI1          createClockEngine             → WebUI_CoreUI1_ClockSyncMsg 用 clockTotalSeconds 覆盖当日秒数（时钟回跳）
        ///   CoreUI0          createCountdown/ProgressEngine → 用 percent+duration 覆盖本地进度
        ///   WishTipsUI       createCountdownEngine         → start(remainSeconds) 覆盖剩余时间
        ///   PlantingDetails  createCountdownEngine         → start(remainSeconds, scale) 覆盖剩余时间
        ///   EventChoice      自建 rAF 倒计时                → 跳回旧剩余值，且会再排一次 TIMEEND
        ///   DailySettlement  本地结算演出                   → 重推等于整段重播
        ///   Cooking          setInterval 本地推进出锅进度    → 合并也不会改动锅/烹饪袋内容，无需刷新它
        ///   TrapFloatPopup / GeneratorFloatPopup / FuelGeneratorFloatPopup
        ///                    InfoMsg 会把 visible 置 true  → 重推会让已关掉的浮层"诈尸"
        /// </summary>
        private static readonly HashSet<string> NoDirtyPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CoreUI0", "CoreUI1", "WishTipsUI", "PlantingDetails", "EventChoice",
            "DailySettlement", "Cooking",
            "TrapFloatPopup", "GeneratorFloatPopup", "FuelGeneratorFloatPopup",
        };

        private static bool _skippedNoDirtyLogged;

        private static bool IsNoDirtyPage(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            try { return NoDirtyPages.Contains(key); } catch { return false; }
        }

        /// <summary>合并后刷新正开着的容器界面（背包界面 / 工作台）。</summary>
        private static void RefreshOpenUi()
        {
            try
            {
                ReduxUISystem rus = BaseSingleton<ReduxUISystem>.Instance;
                if (rus == null) return;
                WebUILayer layer = rus.GetWebUILayer();
                if (layer == null) return;

                if (SafeIsPageActive(layer, "BackpackUI"))
                {
                    SplitHalfPatch.RefreshBackpackState(layer);
                }
                if (SafeIsPageActive(layer, "ToolTable"))
                {
                    SplitHalfPatch.RefreshToolTableState();
                }

                // 其余容器页面（冰箱/置物架/无人机等）没有专用重建入口：
                // 把已注册页面标脏，让前端重新拉一次数据，避免显示旧数量。
                // ⚠ 但必须跳过「本地推进」页面（见 NoDirtyPages）：这些页的时钟/倒计时/进度是本地在跑、
                //    后端消息只给锚点或旧快照，重推会把界面拽回锚点（典型：HUD 时钟跳回进游戏时刻 / 当日 00:00）。
                try
                {
                    var pageMap = layer.PageMap;
                    if (pageMap != null)
                    {
                        var pe = pageMap.GetEnumerator();
                        System.Text.StringBuilder skipped = null;
                        while (pe.MoveNext())
                        {
                            try
                            {
                                string key = pe.Current.Key;
                                if (IsNoDirtyPage(key))
                                {
                                    if (skipped == null) skipped = new System.Text.StringBuilder();
                                    if (skipped.Length > 0) skipped.Append(',');
                                    skipped.Append(key);
                                    continue;
                                }
                                SplitHalfPatch.MarkDirty(layer, key);
                            }
                            catch { }
                        }
                        if (skipped != null && !_skippedNoDirtyLogged)
                        {
                            _skippedNoDirtyLogged = true;
                            Plugin.LogSource?.LogInfo("[自动合并] 界面刷新已跳过「本地推进」页面（不重推，避免时钟/倒计时回跳）：" + skipped);
                        }
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning("[自动合并] 刷新界面失败: " + ex.Message);
            }
        }

        private static bool SafeIsPageActive(WebUILayer layer, string page)
        {
            try { return layer.IsPageActive(page); } catch { return false; }
        }
    }

    /// <summary>
    /// 落袋即合并（预防层，SLTweaksSplit 1.1.3 新增）
    ///
    /// 分堆是怎么产生的：物品入库时游戏调 ItemManager.FindStackableItem 找"能并进去的那堆"。
    /// 只要参与比较的逐实例字段（时间/使用次数/重量/污染/地图预设/附加数据…）有一项不同，
    /// 即使目标堆还有空位，游戏也会判定"不是同一种东西"，于是**另开一堆** —— 之后再想手动合，
    /// 走的还是同一套判定，永远合不上（这就是木片那两堆的来历）。
    ///
    /// 本层在 FindStackableItem 找不到目标时，对【普通材料】放宽判定，返回一个已有同类堆，
    /// 让游戏自己把它并进去 —— 从源头不再产生第二堆，而不是事后修补。
    ///
    /// 放宽范围（保守）：配置无保质期（Life&lt;=0）、堆未满、该堆没有逐实例附加数据/热值、
    /// 且不在烹饪家具袋内。食物、带品质耐久的物品、发电/烹饪等特殊容器都不受影响。
    /// </summary>
    public static class PreMergeSystem
    {
        internal static bool Enabled = true;

        private static readonly HashSet<int> _reported = new HashSet<int>();

        internal static ItemData TryFindRelaxedTarget(ItemManager im, long ownerId, int itemConfigId)
        {
            if (!Enabled) return null;
            if (im == null || ownerId == 0 || itemConfigId <= 0) return null;

            // 烹饪家具袋内不放宽（本模组的"逐件独立存放才能一锅多份"必须保留）
            try { if (im.IsCookingFurnitureBag(ownerId)) return null; } catch { return null; }

            ConfigManager cm = BaseSingleton<ConfigManager>.Instance;
            if (cm == null) return null;

            Config_Item ci = null;
            try { ci = cm.Get_Config_Item(itemConfigId); } catch { }
            if (ci == null) return null;

            int limit = 0;
            int life = 0;
            try
            {
                limit = ci.StackLimit;
                life = ci.Life;
            }
            catch { }
            if (limit < 2) return null;   // 上限 1 的物品本来就是一堆一个，放宽没有意义
            if (life > 0) return null;    // 有保质期的（食物等）不混新鲜度

            Il2CppSystem.Collections.Generic.List<ItemData> list = null;
            try { list = im.GetItemDataList(ownerId); } catch { }
            if (list == null) return null;

            int count = 0;
            try { count = list.Count; } catch { }

            for (int i = 0; i < count; i++)
            {
                ItemData it = null;
                try { it = list[i]; } catch { continue; }
                if (it == null) continue;
                if (it.ItemConfigId != itemConfigId) continue;
                if (it.ItemCount <= 0) continue;
                if (it.ItemCount >= limit) continue;
                if (HasExtraData(it)) continue;

                if (_reported.Add(itemConfigId))
                {
                    Plugin.LogSource?.LogInfo("[落袋合并] '" + Display(ci) + "' 将并进已有堆（原版判定不同源），以后不会再分成两堆");
                }
                return it;
            }
            return null;
        }

        /// <summary>带逐实例附加数据（品质/耐久/热值）的堆不放宽，避免抹平差异。</summary>
        private static bool HasExtraData(ItemData it)
        {
            try
            {
                if (it.InstanceVD != null && it.InstanceVD.Length > 0) return true;
                if (it.InstanceEffectEnd != null && it.InstanceEffectEnd.Length > 0) return true;
                if (it.InstanceBurnValue > 0) return true;
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static string Display(Config_Item ci)
        {
            try
            {
                string s = ci.ItemName_Local;
                if (string.IsNullOrEmpty(s)) s = ci.ItemName;
                return string.IsNullOrEmpty(s) ? ("ID" + ci.ID) : s;
            }
            catch
            {
                return "?";
            }
        }
    }
}
