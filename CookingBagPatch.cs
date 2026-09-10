using System;
using Bush = Il2CppSystem.Collections.Generic;
using HarmonyLib;
using GameCore.HotUpdate.Battle.Logic;
using CookingUI = GameCore.HotUpdate.ReduxUI;
using HotGame = GameCore.HotUpdate;

namespace CookingSourceExpand
{
    /// <summary>
    /// 让烹饪（灶台/火炉做饭）面板可调用的食材来源容器，从"只有冰箱/冷柜"扩展为
    /// "所有带储物背包的家具（箱子/柜子/储物架/冰箱/冷柜等）"。
    ///
    /// 经验教训（此前诊断日志验证）：
    ///   · AgentManager.GetAllBagFurnitures() 在烹饪上下文返回 0——储物家具未按
    ///     BagFurniture agent 注册，不能用它枚举。
    ///   · GetAllFurnitures() 会返回世界全部家具（含门窗墙等非储物物），把它们全当
    ///     来源会让面板崩溃。不能整表引入。
    ///   · 正确枚举是 GetFurnituresWithBag(homeMap)：只返回"带储物背包"的家具，
    ///     且自带正确的家具配置 id（作为来源的 BagConfigId）。
    ///
    /// 警告：此 mod 曾因过度枚举导致烹饪面板崩溃，已改为聚焦枚举。仍需在实际读档
    /// 中逐步验证，若还有问题优先检查 GetFurnituresWithBag 的返回范围。
    /// </summary>
    internal static class CookingBagPatch
    {
        /// <summary>
        /// 需从烹饪来源中排除的家具配置 id（诊断定位后填入）。
        /// 例如：无人机 / 门 / 车后备箱。
        /// </summary>
        private static readonly int[] ExcludedSourceConfigIds =
        {
            // 日志实测：无人机/防盗门/防盗窗/小汽车
            9054,   // 无人机
            30001,  // 防盗门
            35001,  // 防盗窗
            1027,   // 小汽车
            // 女大学生角色家实测：窗户/床/围栏/电器类储物
            30002,  // 钛合金大门
            35002,  // 防弹窗
            80064,  // 微波炉
            80066,  // 燃气灶
            80129,  // 实木床
            80143,  // 围栏
            66001,  // 酿酒桶
            9298,   // 吊篮
        };

        private static readonly string[] ExcludedSourceNames =
        {
            "防盗门",
            "防盗窗",
            "小汽车",
            "无人机",
            "房门",
            "木门",
            "铁门",
            "大门",
            "窗户",
            "木窗",
            "铁窗",
            "玻璃窗",
            "铝合金窗",
            // 打工人/女大学生都会有、不应作为来源的家具类型（名字级统一排除，跨角色生效）
            "防弹窗",
            "钛合金",
            "围栏",
            "栅栏",
            "燃气灶",
            "煤气灶",
            "微波炉",
            "酿酒桶",
            "酒桶",
            "榨汁机",
            "吊篮",
            "老鼠笼",
            "鼠笼",
            "咖啡机",
        };

        /// <summary>以这些词结尾的名字也排除（主要用于"床"，避免误伤"床头柜"这类真储物）。</summary>
        private static readonly string[] ExcludedNameSuffixes =
        {
            "床",
        };

        private static bool IsExcluded(int configId)
        {
            foreach (var id in ExcludedSourceConfigIds)
            {
                if (id == configId) return true;
            }
            return false;
        }

        /// <summary>供 TradeSourceInjection 复用的公共包装：按 configId 判断是否为需要排除的非储物来源。</summary>
        internal static bool IsStorageExcluded(int configId) => IsExcluded(configId);
        internal static bool IsStorageExcludedName(string name) => IsExcludedName(name);
        internal static string StorageResolveName(int configId) => TryResolveName(configId);

        private static bool IsExcludedName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var nm = name.Trim();
            foreach (var n in ExcludedSourceNames)
            {
                if (string.IsNullOrEmpty(n)) continue;
                if (nm.IndexOf(n.Trim(), StringComparison.Ordinal) >= 0) return true;
            }
            foreach (var s in ExcludedNameSuffixes)
            {
                if (string.IsNullOrEmpty(s)) continue;
                if (nm.EndsWith(s.Trim(), StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static string TryResolveName(int configId)
        {
            try
            {
                var cm = HotGame.ConfigManager.Instance;
                if (cm == null) return "";
                var cf = cm.Get_Config_Furniture(configId);
                if (cf == null) return "";
                try { if (!string.IsNullOrEmpty(cf.Name_Local)) return cf.Name_Local; } catch (Exception) { }
                try { if (!string.IsNullOrEmpty(cf.Name)) return cf.Name; } catch (Exception) { }
            }
            catch (Exception) { }
            return "";
        }

        private static readonly int[] BoxConfigIds = {
    // 打工人角色家（基础白名单）
    201, 202, 203, 204, 215, 872, 875,
    9056, 9057, 9092, 9095, 9096, 9099, 9084, 9159, 9168,
    9171, 9172, 9173, 15001, 307, 308, 321,
    80011, 80156, 80157, 80159, 80160,
    // 女大学生角色家实测：储物柜/架/箱/冰箱/书架/橱柜/吊篮/酿酒桶/置物架
    // 每个角色同一类家具的 configId 可能不同，故并入以保证工作台跨面板取料可用
    10001,
    80014, 80022, 80032, 80042, 80047, 80049, 80062, 80068,
    80077, 80081, 80083, 80084, 80086, 80097,
    80110, 80119, 80121, 80122, 80123, 80125, 80126, 80128,
    80130, 80132, 80140, 80141,
};

        /// <summary>
        /// 工作台"一键取料/材料不足提示"用的可用材料预缓存：ownerId -> { itemConfigId : 数量 }。
        /// 仅在打开工作台的安全上下文构建一次，杜绝在 reducer 线程枚举世界导致的死锁。
        /// 覆盖：本角色背包 + 工作台来源的全部储物家具。跨角色家的柜子不进入。
        /// </summary>
        internal static readonly System.Collections.Generic.Dictionary<long, System.Collections.Generic.Dictionary<int, int>>
            WorkbenchAvailableMaterials = new System.Collections.Generic.Dictionary<long, System.Collections.Generic.Dictionary<int, int>>();

        private static void CollectSources(HotGame.Battle.Logic.AgentManager agentManager,
                                           long excludeOwnerId, int excludeConfigId,
                                           Bush.List<HotGame.CookingFridgeInfo> targetFridge,
                                           Bush.List<CookingUI.Data_Bag> targetBag,
                                           Bush.List<Int32> targetConfigs,
                                           Bush.List<Boolean> targetLocked,
                                           HotGame.Battle.Logic.ItemManager itemManager,
                                           string tag)
        {
            if (agentManager == null) return;

            int enumerCount = 0;
            int added = 0;

            try
            {
                // 只枚举"当前角色家"（含其楼层组）的储物家具，避免取到整个世界后混入其它角色
                //（女大学生/打工人互串）家里的柜子、以及门窗等非储物物。详见 EnumerateHomeSources。
                var sources = EnumerateHomeSources(agentManager);
                enumerCount = sources.Count;
                foreach (var kv in sources)
                {
                    int cid = kv.Value;
                    // 不设有限 BoxConfigIds 白名单：GetFurnituresWithBag 已只返回"带储物背包"的家具，
                    // 下面 Excluded/名字排除会滤掉门/窗/床/电器等非储物物，故全部储物家具都能成为来源。
                    if (TryAddSource(kv.Key, cid, excludeOwnerId, excludeConfigId,
                                     targetFridge, targetBag, targetConfigs, targetLocked, itemManager))
                        added++;
                }
            }
            catch (Exception e)
            {
                CookingSourceExpandPlugin.Log.LogError($"[CookingSourceExpand] {tag} 补丁异常：{e}");
            }

            CookingSourceExpandPlugin.Log.LogInfo($"[CookingSourceExpand] ★{tag}: 储物家具枚举 {enumerCount} 个，新增来源 {added} 个。");
        }

        /// <summary>
        /// 统一枚举"当前角色家"的储物家具来源（ownerId, configId）。
        /// 优先用 am.GetFurnituresWithBag(GetHomeMapId(), …, useHomeGroup:true)——它只返回当前角色家
        /// （含其 2F/地下室楼层组）且有储物背包的家具，天然杜绝跨角色泄漏（女大学生/打工人互串）与
        /// 门窗外带物。仅在按家枚举失败/为空时退回全局枚举，但严格限定 MapConfigId==当前家，宁缺毋滥。
        /// </summary>
        private static System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<long, int>>
            EnumerateHomeSources(HotGame.Battle.Logic.AgentManager am)
        {
            var result = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<long, int>>();
            if (am == null) return result;

            int homeMap = 0;
            try { homeMap = am.GetHomeMapId(); } catch (Exception) { }

            try
            {
                if (homeMap != 0)
                {
                    var homeBag = am.GetFurnituresWithBag(homeMap, false, true);
                    if (homeBag != null && homeBag.Count > 0)
                    {
                        foreach (var f in homeBag)
                        {
                            if (f == null) continue;
                            long oid = f.InstanceId; int cid = f.AgentConfigId;
                            if (oid != 0 && cid != 0) result.Add(new System.Collections.Generic.KeyValuePair<long, int>(oid, cid));
                        }
                        CookingSourceExpandPlugin.Log.LogInfo($"[CookingSourceExpand] ★按角色家枚举：GetFurnituresWithBag(homeMap={homeMap}) -> {result.Count} 个");
                        return result;
                    }
                }
            }
            catch (Exception e)
            {
                CookingSourceExpandPlugin.Log.LogInfo($"[CookingSourceExpand] GetFurnituresWithBag 枚举失败，转全局兜底：{e.GetType().Name}");
            }

            try
            {
                var all = am.GetAllFurnitures();
                if (all != null)
                {
                    int added = 0;
                    foreach (var f in all)
                    {
                        if (f == null) continue;
                        long oid = f.InstanceId; int cid = f.AgentConfigId;
                        if (oid == 0 || cid == 0) continue;
                        // 兜底仅保留"当前家 map"的家具，隔离其它角色家的柜子
                        try { if (f.MapConfigId != homeMap) continue; } catch (Exception) { continue; }
                        result.Add(new System.Collections.Generic.KeyValuePair<long, int>(oid, cid));
                        added++;
                    }
                    CookingSourceExpandPlugin.Log.LogInfo($"[CookingSourceExpand] ★全局兜底枚举 homeMap={homeMap} -> {added} 个");
                }
            }
            catch (Exception) { }
            return result;
        }

        private static bool IsBoxConfig(int configId)
        {
            foreach (var id in BoxConfigIds)
            {
                if (id == configId) return true;
            }
            return false;
        }

        /// <summary>
        /// 在打开工作台的安全上下文构建"可用材料预缓存"。
        /// ownerIds 覆盖：本角色全部背包(Data_Bag) + 工作台来源储物家具。
        /// 只在安全上下文读 itemManager，reducer 线程的提示仅读此缓存，避免死锁。
        /// </summary>
        private static void BuildWorkbenchMaterialCache(HotGame.Battle.Logic.ItemManager itemManager,
                                                       Bush.List<CookingUI.Data_Bag> playerBags,
                                                       long[] ownerIds)
        {
            WorkbenchAvailableMaterials.Clear();
            if (itemManager == null) return;

            if (playerBags != null)
            {
                foreach (var b in playerBags)
                {
                    if (b == null || b.OwnerId == 0) continue;
                    FillOwnerItemCounts(itemManager, b.OwnerId);
                }
            }
            if (ownerIds != null)
            {
                foreach (var oid in ownerIds)
                {
                    if (oid != 0) FillOwnerItemCounts(itemManager, oid);
                }
            }

            CookingSourceExpandPlugin.Log.LogInfo($"[CookingSourceExpand] 工作台可用材料预缓存 {WorkbenchAvailableMaterials.Count} 个来源");
        }

        private static void FillOwnerItemCounts(HotGame.Battle.Logic.ItemManager itemManager, long ownerId)
        {
            try
            {
                if (WorkbenchAvailableMaterials.ContainsKey(ownerId)) return;
                var map = new System.Collections.Generic.Dictionary<int, int>();
                var items = itemManager.GetItemDataList(ownerId);
                if (items != null)
                {
                    foreach (var it in items)
                    {
                        if (it == null || it.ItemCount <= 0) continue;
                        int cid = it.ItemConfigId;
                        if (!map.ContainsKey(cid)) map[cid] = 0;
                        map[cid] += it.ItemCount;
                    }
                }
                WorkbenchAvailableMaterials[ownerId] = map;
            }
            catch (Exception) { /* 单个来源失败不影响其它来源 */ }
        }

        // 判断家具是否属于当前角色家（含其所有楼层 map）。用 IsHomeMap 归属过滤，排除其它角色
        // 家里的储物柜，同时保留自家二楼/地下室。IsHomeMap 抛异常时按白名单配置 id 兜底放行。
        private static bool IsOwnMap(HotGame.Battle.Logic.AgentManager am, Furniture f)
        {
            if (am == null || f == null) return true;
            try
            {
                return am.IsHomeMap(f.MapConfigId);
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static bool TryAddSource(long oid, int cid,
                                         long excludeOwnerId, int excludeConfigId,
                                         Bush.List<HotGame.CookingFridgeInfo> targetFridge,
                                         Bush.List<CookingUI.Data_Bag> targetBag,
                                         Bush.List<Int32> targetConfigs,
                                         Bush.List<Boolean> targetLocked,
                                         HotGame.Battle.Logic.ItemManager itemManager)
        {
            if (oid == 0 || cid == 0) return false;
            if (oid == excludeOwnerId || cid == excludeConfigId) return false;
            if (IsExcluded(cid)) return false;
            if (!SourceFilterConfig.IsAllowed(cid)) return false;

            string srcName = TryResolveName(cid);
            if (IsExcludedName(srcName))
            {
                CookingSourceExpandPlugin.Log.LogInfo($"[CookingSourceExpand] ★排除 configId={cid} name={srcName} ownerId={oid}");
                return false;
            }

            bool exists = false;
            if (targetFridge != null)
            {
                foreach (var fi in targetFridge)
                {
                    if (fi != null && fi.OwnerId == oid) { exists = true; break; }
                }
            }
            else if (targetBag != null)
            {
                foreach (var b in targetBag)
                {
                    if (b != null && b.OwnerId == oid) { exists = true; break; }
                }
            }
            if (exists) return false;

            if (targetFridge != null)
            {
                var fi = new HotGame.CookingFridgeInfo();
                fi.OwnerId = oid;
                fi.FurnitureConfigId = cid;
                fi.Locked = false;
                targetFridge.Add(fi);
            }
            else if (targetBag != null)
            {
                var bag = new CookingUI.Data_Bag();
                bag.OwnerId = oid;
                bag.BagConfigId = cid;
                bag.ExtraBurden = 0;
                bag.ItemList = BuildItemList(itemManager, oid);
                targetBag.Add(bag);
                if (targetConfigs != null) targetConfigs.Add(cid);
                if (targetLocked != null) targetLocked.Add(false);
            }
            CookingSourceExpandPlugin.Log.LogInfo($"[CookingSourceExpand] ★来源 configId={cid} name={srcName} ownerId={oid}");
            return true;
        }

        private static Bush.List<CookingUI.Data_Item> BuildItemList(HotGame.Battle.Logic.ItemManager itemManager, Int64 ownerId)
        {
            var list = new Bush.List<CookingUI.Data_Item>();
            if (itemManager == null) return list;
            try
            {
                var items = itemManager.GetItemDataList(ownerId);
                if (items == null) return list;
                foreach (var it in items)
                {
                    if (it == null || it.ItemCount <= 0) continue;
                    var di = new CookingUI.Data_Item();
                    di.LogicId = it.InstanceId;
                    di.OwnerId = ownerId;
                    di.ItemConfigId = it.ItemConfigId;
                    di.ItemCount = it.ItemCount;
                    list.Add(di);
                }
            }
            catch (Exception) { }
            return list;
        }

        internal static class AppendShelfSourcesPatch
        {
            static void Prefix(
                Int64 OwnerId,
                Int64 TargetId,
                Int32 CookFurnitureId,
                String FurnitureName,
                Single FuelSavingRatio,
                Int32 TagTierFloorRank,
                Int32 PowerCost,
                Bush.List<HotGame.CookingFridgeInfo> Fridges,
                HotGame.HotPotPanelData HotPot)
            {
                try
                {
                    if (Fridges == null) return;
                    var world = HotGame.Battle.Logic.BattleLogicWorld.Instance;
                    if (world == null) return;
                    CollectSources(world._AgentManager, TargetId, CookFurnitureId,
                                   Fridges, null, null, null, world._ItemManager, "GetCookingBagList");
                }
                catch (Exception e)
                {
                    CookingSourceExpandPlugin.Log.LogError($"[CookingSourceExpand] GetCookingBagList 外层异常：{e}");
                }
            }
        }

        internal static class AppendShelvesToCookingOpenPatch
        {
            static void Prefix(
                Bush.List<CookingUI.Data_Bag> BagList,
                Int64 TargetId,
                Int32 CookFurnitureId,
                String FurnitureName,
                Single FuelSavingRatio,
                Int32 TagTierFloorRank,
                Int32 PowerCost,
                Bush.List<CookingUI.Data_Bag> FridgeBags,
                Bush.List<Int32> FridgeConfigIds,
                Bush.List<Boolean> FridgeLocked,
                HotGame.HotPotPanelData HotPot)
            {
                try
                {
                    if (FridgeBags == null) return;
                    var world = HotGame.Battle.Logic.BattleLogicWorld.Instance;
                    if (world == null) return;
                    CollectSources(world._AgentManager, TargetId, CookFurnitureId,
                                   null, FridgeBags, FridgeConfigIds, FridgeLocked, world._ItemManager, "CookingOpen");
                }
                catch (Exception e)
                {
                    CookingSourceExpandPlugin.Log.LogError($"[CookingSourceExpand] CookingOpen 外层异常：{e}");
                }
            }
        }

        // ===== 手工制作界面材料来源扩展 =====
        // 手工制作（HandMade）的材料来源由 Ac_Item_GetHandMadeBagList 返回的
        // List<ToolCabinetInfo> Cabinets 决定（默认只有工作台抽屉/工具柜）。
        // 同样把手工艺界面的来源扩展到"所有带储物背包的家具"，并应用同一排除名单。

        private static bool TryAddToolCabinet(long oid, int cid, long excludeOwnerId,
                                              Bush.List<HotGame.ToolCabinetInfo> target,
                                              HotGame.Battle.Logic.ItemManager itemManager)
        {
            if (oid == 0 || cid == 0) return false;
            if (oid == excludeOwnerId) return false;
            if (IsExcluded(cid)) return false;
            if (!SourceFilterConfig.IsAllowed(cid)) return false;

            string srcName = TryResolveName(cid);
            if (IsExcludedName(srcName))
            {
                CookingSourceExpandPlugin.Log.LogInfo($"[CookingSourceExpand] ★排除 configId={cid} name={srcName} ownerId={oid}");
                return false;
            }

            foreach (var t in target)
            {
                if (t != null && t.OwnerId == oid) return false;
            }

            var tc = new HotGame.ToolCabinetInfo();
            tc.OwnerId = oid;
            tc.FurnitureConfigId = cid;
            target.Add(tc);

            CookingSourceExpandPlugin.Log.LogInfo($"[CookingSourceExpand] ★手工制作来源 configId={cid} name={srcName} ownerId={oid}");
            return true;
        }

        internal static class AppendShelvesToHandMadeBagListPatch
        {
            static void Prefix(
                Int64 OwnerId,
                Int64 TargetId,
                Boolean IsBtWait,
                HotGame.HandMadeState HandMadeState,
                Int32 ProductionLv,
                Bush.List<HotGame.ToolCabinetInfo> Cabinets)
            {
                try
                {
                    if (Cabinets == null) return;
                    var world = HotGame.Battle.Logic.BattleLogicWorld.Instance;
                    if (world == null) return;
                    var am = world._AgentManager;
                    if (am == null) return;
                    var im = world._ItemManager;

                    int homeMap = 0;
                    try { homeMap = am.GetHomeMapId(); } catch (Exception) { }
                    var furn = am.GetFurnituresWithBag(homeMap, false, true);
                    if (furn == null) return;

                    int added = 0;
                    foreach (var f in furn)
                    {
                        if (f == null) continue;
                        if (TryAddToolCabinet(f.InstanceId, f.AgentConfigId, TargetId, Cabinets, im))
                            added++;
                    }

                    CookingSourceExpandPlugin.Log.LogInfo($"[CookingSourceExpand] ★GetHandMadeBagList: 储物家具 {furn.Count} 个，手工制作新增来源 {added} 个。");
                }
                catch (Exception e)
                {
                    CookingSourceExpandPlugin.Log.LogError($"[CookingSourceExpand] GetHandMadeBagList 外层异常：{e}");
                }
            }
        }

        // ===== 工作台（ToolTable）材料来源扩展 =====
        // 工作台 Ac_ToolTable_Open.SendAction 的 CabinetBags(List<Data_Bag>) + CabinetConfigIds(List<Int32>)
        // 决定工作台自动填料的可用容器来源（默认只有背包 + 工作台自身，故取不到远处箱子里的料）。
        // 与烹饪/手工同一套 CollectSources 机制，扩展为所有带储物背包的家具，并应用同一排除名单。
        internal static class AppendShelvesToToolTableOpenPatch
        {
            static void Prefix(
                Bush.List<CookingUI.Data_Bag> BagList,
                HotGame.HandMadeState HandMadeState,
                Int64 TargetId,
                Int32 ProductionLv,
                Bush.List<CookingUI.Data_Bag> CabinetBags,
                Bush.List<Int32> CabinetConfigIds)
            {
                try
                {
                    var world = HotGame.Battle.Logic.BattleLogicWorld.Instance;
                    if (world == null) return;
                    var am = world._AgentManager;
                    if (am == null) return;
                    var im = world._ItemManager;

                    int homeMap = 0;
                    try { homeMap = am.GetHomeMapId(); } catch (Exception) { }

                    // —— 构建工作台跨面板取料来源缓存（仅在打开工作台的安全上下文枚举一次）——
                    // 只枚举当前角色家（含楼层组）的储物家具，杜绝跨角色泄漏。比原先 GetAllFurnitures
                    // + IsHomeMap 更严格：不再把其它角色家的柜子混进工作台来源。
                    var sources = EnumerateHomeSources(am);
                    var owners = new System.Collections.Generic.List<long>();
                    foreach (var kv in sources)
                    {
                        long oid = kv.Key; int cid = kv.Value;
                        if (oid == 0) continue;
                        // 同 CollectSources：不做有限白名单，全量储物家具 + 排除名单，覆盖所有柜子
                        if (IsExcluded(cid)) continue;
                        if (!SourceFilterConfig.IsAllowed(cid)) continue;
                        if (IsExcludedName(TryResolveName(cid))) continue;
                        if (!owners.Contains(oid)) owners.Add(oid);
                        if (owners.Count >= 60) break;
                    }
                    CookingSourceExpandPlugin.WorkbenchSourceOwners = owners.ToArray();
                    CookingSourceExpandPlugin.Log.LogInfo($"[CookingSourceExpand] ★工作台跨面板缓存 {owners.Count} 个来源");

                    // —— 构建"一键取料/材料不足提示"用的可用材料预缓存（同样只在打开工作台的安全上下文做）——
                    // BagList 是本角色背包，owners 是工作台来源储物家具；两者都纳入才不至于把背包里
                    // 已有的材料误判为"缺"。绝不在 reducer 线程重建，提示只读这份缓存。
                    BuildWorkbenchMaterialCache(im, BagList, CookingSourceExpandPlugin.WorkbenchSourceOwners);

                    // —— 原有扩展：把储物家具加入工作台 CabinetBags ——
                    if (CabinetBags == null) return;
                    CollectSources(am, TargetId, 0, null, CabinetBags, CabinetConfigIds, null, im, "ToolTableOpen");
                    CookingSourceExpandPlugin.Log.LogInfo($"[CookingSourceExpand] ★ToolTableOpen 结果：CabinetBags={CabinetBags.Count} 个，CabinetConfigIds={(CabinetConfigIds == null ? 0 : CabinetConfigIds.Count)} 个");
                }
                catch (Exception e)
                {
                    CookingSourceExpandPlugin.Log.LogError($"[CookingSourceExpand] ToolTableOpen 外层异常：{e}");
                }
            }
        }

        // ===== 工作台跨面板取料 =====
        // 工作台默认只从"当前展示面板"取料。这里在 SelectItemsForRecipe 的 Prefix 里把 ownerIds
        // 合并进"打开工作台时枚举缓存"的所有储物家具，实现不切面板也能跨箱子取料。
        // 注意：绝不能在此处调用 GetFurnituresWithBag（会在该深层数据上下文死锁导致卡死闪退），
        // 只用 AppendShelvesToToolTableOpenPatch 在安全上下文里构建的缓存。
        internal static class WorkbenchCrossSourcePatch
        {
            // 真实签名（探针实测定）：SelectItemsForRecipe(State_Data_Item, Dictionary<int,int>, Int64[])
            // ownerIds 是值传递、非 ref，无法在此扩容来源。跨柜取料实际上由 CabinetBags 扩展
            // （Ac_ToolTable_Open 的 Prefix）承担。此处 Prefix 做无条件诊断：无论是否命中都打印
            // 游戏传入的完整 ownerIds 与所需材料，确认官方取料到底在哪个环节断掉。不修改任何东西。
            static void Prefix(CookingUI.State_Data_Item itemState, Bush.Dictionary<int, int> materialNeeded, Int64[] ownerIds)
            {
                try
                {
                    var cached = CookingSourceExpandPlugin.WorkbenchSourceOwners;
                    int olen = ownerIds == null ? 0 : ownerIds.Length;
                    var sb = new System.Text.StringBuilder();
                    if (ownerIds != null)
                        foreach (var o in ownerIds) sb.Append(o).Append(',');

                    var msb = new System.Text.StringBuilder();
                    if (materialNeeded != null)
                        foreach (var kv in materialNeeded) msb.Append(kv.Key).Append('x').Append(kv.Value).Append(',');

                    int hit = 0;
                    if (cached != null && ownerIds != null)
                        foreach (var o in cached)
                            for (int i = 0; i < ownerIds.Length; i++)
                                if (ownerIds[i] == o) { hit++; break; }

                    CookingSourceExpandPlugin.Log.LogInfo($"[CookingSourceExpand] 「点配方取料」被调用 ownerIds[{olen}]={sb} 储物来源命中={hit}/{(cached == null ? 0 : cached.Length)} 需求={msb}");
                }
                catch (Exception e)
                {
                    CookingSourceExpandPlugin.Log.LogInfo($"[CookingSourceExpand] SelectItemsForRecipe Prefix 诊断 err {e.GetType().Name}");
                }
            }

            // 材料不足提示：配方所需材料在本次可用来源（玩家背包 + 全部储物家具，来自打开工作台时的预缓存）
            // 里凑不齐时，日志列出还差哪些。只读缓存、不做任何世界枚举/修改，纯提示不阻塞制作。
            static void Postfix(Bush.Dictionary<int, int> materialNeeded)
            {
                try
                {
                    if (materialNeeded == null) return;
                    var cache = CookingBagPatch.WorkbenchAvailableMaterials;
                    if (cache == null || cache.Count == 0) return;

                    var missing = new System.Collections.Generic.Dictionary<int, int>();
                    foreach (var kv in materialNeeded)
                    {
                        int need = kv.Value;
                        if (need <= 0) continue;
                        int have = 0;
                        foreach (var map in cache.Values)
                        {
                            if (map == null) continue;
                            int c;
                            if (map.TryGetValue(kv.Key, out c)) have += c;
                        }
                        int diff = need - have;
                        if (diff > 0)
                        {
                            int cur;
                            if (!missing.TryGetValue(kv.Key, out cur)) missing[kv.Key] = diff;
                            else if (diff > cur) missing[kv.Key] = diff;
                        }
                    }
                    if (missing.Count == 0) return;

                    var parts = new System.Collections.Generic.List<string>();
                    foreach (var m in missing) parts.Add($"{m.Key}(物品id)×{m.Value}");
                    CookingSourceExpandPlugin.Log.LogWarning($"[CookingSourceExpand] ⚠ 工作台材料不足，还差 {missing.Count} 项 → {string.Join("，", parts)}");
                }
                catch (Exception e)
                {
                    CookingSourceExpandPlugin.Log.LogInfo($"[CookingSourceExpand] 工作台材料不足提示 err {e.GetType().Name}");
                }
            }
        }

        // 注：储物面板跨柜互通（吊篮/柜子面板内切换其它储物容器）因受 WebView 前端面板限制
        // 未能稳定实现，已从发布版移除，仅在内部迭代中使用。
    }
}
