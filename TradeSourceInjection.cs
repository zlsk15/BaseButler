using System;
using System.Collections.Generic;
using System.Reflection;
using Bush = Il2CppSystem.Collections.Generic;
using HarmonyLib;
using HotGame = GameCore.HotUpdate;
using CookingUI = GameCore.HotUpdate.ReduxUI;

namespace CookingSourceExpand
{
    /// <summary>
    /// 无人机 TradeUI（ShowUI → RebuildBagTabs 路径）来源扩展。
    /// 实测：打开无人机走 Ac_TradeUI_ShowUI → RebuildBagTabs(state,itemState)，从不走
    /// Ac_TradeUI_SetContainerTabs；面板从 state.Containers 构建来源标签，官方只放冰柜。
    /// 因此：在 ShowUI（安全打开上下文）用 GetFurnituresWithBag 预缓存全部储物家具，
    /// 再在 RebuildBagTabs（reducer）里从缓存追加到 state.Containers —— 绝不在 reducer 里枚举世界。
    /// </summary>
    internal static class TradeSourceInjection
    {
        internal static List<HotGame.TradeContainerInfo> CachedSources = new List<HotGame.TradeContainerInfo>();

        // 暂存无人机 UI 的真实 reducer state 与 itemState（RebuildBagTabs 能拿到）。
        // 因为实测 RebuildBagTabs 先跑、ShowUI 后跑（缓存当时为空），所以注入必须推迟到
        // ShowUI（缓存刚填好）时进行：注入后重算官方 RebuildBagTabs，让 BagTabsJson 推给前端。
        private static CookingUI.State_Web_TradeUI _liveState;
        private static CookingUI.State_Data_Item _liveItem;

        // ===== 安全上下文：Ac_TradeUI_ShowUI.SendAction（无参）=====
        // Prefix 预缓存家庭储物来源（安全上下文，实测 28 个）；
        // Postfix（无参，可挂载）注入到暂存的 state.Containers 并重算 BagTabs。
        internal static class ShowUICachePatch
        {
            static void Prefix()
            {
                try { BuildCache(); }
                catch (Exception e)
                {
                    CookingSourceExpandPlugin.Log.LogError($"[TradeSource] ShowUI 缓存失败：{e}");
                }
            }

            static void Postfix()
            {
                try
                {
                    if (CachedSources.Count == 0 || _liveState == null) return;
                    object list = null;
                    try { list = _liveState.Containers; } catch (Exception) { }
                    Inject(list, "ShowUI.Postfix");
                    TryRebuildTabs();
                }
                catch (Exception e)
                {
                    CookingSourceExpandPlugin.Log.LogInfo($"[TradeSource] ShowUI 注入异常：{e}");
                }
            }
        }

        // ===== RebuildBagTabs：暂存真实 state/itemState；若缓存已就绪也先注入一次 =====
        internal static class RebuildTabsPatch
        {
            static void Prefix(CookingUI.State_Web_TradeUI state, CookingUI.State_Data_Item itemState)
            {
                try
                {
                    _liveState = state;
                    _liveItem = itemState;
                    if (state == null) return;
                    object list = null;
                    try { list = state.Containers; } catch (Exception) { }
                    Inject(list, "RebuildBagTabs");
                }
                catch (Exception e)
                {
                    CookingSourceExpandPlugin.Log.LogInfo($"[TradeSource] RebuildBagTabs 暂存异常：{e}");
                }
            }
        }

        // ===== 注入后重算官方 RebuildBagTabs，让 BagTabs/BagTabsJson 反映全部来源 =====
        private static void TryRebuildTabs()
        {
            if (_liveState == null || _liveItem == null) return;
            try
            {
                var m = typeof(CookingUI.Reducer_Web_TradeUI).GetMethod("RebuildBagTabs",
                    new[] { typeof(CookingUI.State_Web_TradeUI), typeof(CookingUI.State_Data_Item) });
                if (m == null)
                {
                    CookingSourceExpandPlugin.Log.LogInfo("[TradeSource] 未找到 RebuildBagTabs 方法用于刷新");
                    return;
                }
                m.Invoke(null, new object[] { _liveState, _liveItem });
                CookingSourceExpandPlugin.Log.LogInfo("[TradeSource] 注入后已重算 BagTabsJson");
            }
            catch (Exception e)
            {
                CookingSourceExpandPlugin.Log.LogInfo($"[TradeSource] 重算 BagTabsJson 异常：{e}");
            }
        }

        // ===== 把缓存来源注入目标容器列表（去重）=====
        private static void Inject(object list, string tag)
        {
            try
            {
                int before = CountOf(list);
                CookingSourceExpandPlugin.Log.LogInfo(
                    $"[TradeSource] {tag}: state.Containers={(list == null ? "null" : list.GetType().FullName)} 已有 {before} 个，缓存 {CachedSources.Count} 个");
                DiagnosticsEach(list);
                if (list == null || CachedSources == null || CachedSources.Count == 0) return;
                if (before < 0) return;
                if (before >= CachedSources.Count) return; // 已含全部，避免重复追加

                int added = AppendTo(list, CachedSources, before);
                CookingSourceExpandPlugin.Log.LogInfo($"[TradeSource] {tag} 注入来源 {added} 个 → 现 {CountOf(list)} 个");
            }
            catch (Exception) { }
        }

        // ===== 构建缓存：枚举家庭储物家具（仅在 ShowUI 安全上下文调用）=====
        private static void BuildCache()
        {
            var list = new List<HotGame.TradeContainerInfo>();
            var world = HotGame.Battle.Logic.BattleLogicWorld.Instance;
            var am = world == null ? null : world._AgentManager;
            if (am == null) return;
            int homeMap = 0;
            try { homeMap = am.GetHomeMapId(); } catch (Exception) { }
            var furn = am.GetFurnituresWithBag(homeMap, false, true);
            if (furn == null || furn.Count == 0) return;

            foreach (var f in furn)
            {
                try
                {
                    if (f == null) continue;
                    long oid = f.InstanceId; int cid = f.AgentConfigId;
                    if (oid == 0 || cid == 0) continue;
                    if (CookingBagPatch.IsStorageExcluded(cid)) continue;
                    string name = CookingBagPatch.StorageResolveName(cid);
                    if (CookingBagPatch.IsStorageExcludedName(name)) continue;
                    bool dup = false;
                    foreach (var s in list) { if (s != null && s.OwnerId == oid) { dup = true; break; } }
                    if (dup) continue;
                    var tc = new HotGame.TradeContainerInfo();
                    // IsFridge 只影响前端渲染（is-frozen 冻结样式/霜花层），不影响"是否作为来源"。
                    // RebuildBagTabs 路径不过滤 IsFridge（实测读取全部 28 个），所以注入来源一律
                    // 设 false，避免所有来源都被渲染成冰柜模板；真正的冰柜由官方条目保持 true。
                    tc.OwnerId = oid; tc.FurnitureConfigId = cid; tc.IsFridge = false;
                    list.Add(tc);
                }
                catch (Exception) { }
            }
            CachedSources = list;
            CookingSourceExpandPlugin.Log.LogInfo($"[TradeSource] ShowUI 缓存家庭储物来源 {list.Count} 个");
        }

        // ===== 工具：统计 List 长度（兼容 Il2Cpp / .NET 列表）=====
        private static int CountOf(object list)
        {
            if (list == null) return 0;
            try
            {
                var p = list.GetType().GetProperty("Count") ?? list.GetType().GetProperty("_size");
                if (p != null && p.PropertyType == typeof(int))
                {
                    var v = p.GetValue(list);
                    if (v != null) return (int)v;
                }
                var m = list.GetType().GetMethod("get_Count", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (m != null && m.ReturnType == typeof(int))
                {
                    var v = m.Invoke(list, null);
                    if (v != null) return (int)v;
                }
            }
            catch (Exception) { }
            return -1;
        }

        // ===== 工具：把 TradeContainerInfo 追加进任意 List<TradeContainerInfo via reflection>，去重 =====
        private static int AppendTo(object list, List<HotGame.TradeContainerInfo> src, int before)
        {
            int added = 0;
            try
            {
                // 该 List 的 Add 方法（容纳 TradeContainerInfo 基类/接口）
                MethodInfo add = null;
                foreach (var m in list.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name == "Add" || m.Name == "AddUnique")
                    {
                        var ps = m.GetParameters();
                        if (ps.Length == 1)
                        {
                            var pt = ps[0].ParameterType;
                            if (pt.IsAssignableFrom(typeof(HotGame.TradeContainerInfo)) || typeof(HotGame.TradeContainerInfo).IsAssignableFrom(pt))
                            { add = m; break; }
                        }
                    }
                }
                if (add == null) { CookingSourceExpandPlugin.Log.LogInfo("[TradeSource] 未找到 Add 方法，无法注入"); return 0; }

                foreach (var tc in src)
                {
                    if (tc == null) continue;
                    bool dup = Exists(list, tc.OwnerId, before);
                    if (dup) continue;
                    try { add.Invoke(list, new object[] { tc }); added++; }
                    catch (Exception) { }
                }
            }
            catch (Exception e)
            {
                CookingSourceExpandPlugin.Log.LogInfo($"[TradeSource] AppendTo 异常：{e}");
            }
            return added;
        }

        private static bool Exists(object list, long ownerId, int count)
        {
            if (count <= 0) return false;
            try
            {
                var gt = list.GetType();
                // item/this[] 或 Item 属性
                var prop = gt.GetProperty("Item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var meth = prop == null ? gt.GetMethod("get_Item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
                object idx = prop != null ? (object)prop : meth;
                for (int i = 0; i < count; i++)
                {
                    object it = null;
                    if (prop != null && prop.GetIndexParameters().Length == 1)
                        it = prop.GetValue(list, new object[] { i });
                    else if (meth != null && meth.GetParameters().Length == 1)
                        it = meth.Invoke(list, new object[] { i });
                    if (it == null) continue;
                    var oid = GetProp(it, "OwnerId") ?? GetProp(it, "ownerId");
                    if (oid != null && (long)oid == ownerId) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        private static object GetProp(object o, string name)
        {
            try
            {
                var p = o.GetType().GetProperty(name);
                if (p != null) return p.GetValue(o);
            }
            catch (Exception) { }
            return null;
        }

        private static void DiagnosticsEach(object list)
        {
            int c = CountOf(list);
            if (c <= 0 || c > 8) return;
            try
            {
                var sb = new System.Text.StringBuilder();
                var gt = list.GetType();
                var idx = getIndexer(gt);
                for (int i = 0; i < c; i++)
                {
                    object it = getAt(list, idx, i);
                    if (it == null) { sb.Append("#").Append(i).Append("(null) "); continue; }
                    sb.Append("#").Append(i).Append("(oid=").Append(GetProp(it, "OwnerId"))
                      .Append(",cid=").Append(GetProp(it, "FurnitureConfigId"))
                      .Append(",fr=").Append(GetProp(it, "IsFridge")).Append(") ");
                }
                CookingSourceExpandPlugin.Log.LogInfo("[TradeSource] Containers内容: " + sb);
            }
            catch (Exception) { }
        }

        private static object getIndexer(Type t)
        {
            try
            {
                var p = t.GetProperty("Item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.GetIndexParameters().Length == 1) return p;
                var m = t.GetMethod("get_Item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (m != null && m.GetParameters().Length == 1) return m;
            }
            catch (Exception) { }
            return null;
        }
        private static object getAt(object list, object idx, int i)
        {
            try
            {
                if (idx is PropertyInfo pi) return pi.GetValue(list, new object[] { i });
                if (idx is MethodInfo mi) return mi.Invoke(list, new object[] { i });
            }
            catch (Exception) { }
            return null;
        }
    }
}