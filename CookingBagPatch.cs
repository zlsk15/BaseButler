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
        };

        private static bool IsExcluded(int configId)
        {
            foreach (var id in ExcludedSourceConfigIds)
            {
                if (id == configId) return true;
            }
            return false;
        }

        private static bool IsExcludedName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var nm = name.Trim();
            foreach (var n in ExcludedSourceNames)
            {
                if (string.IsNullOrEmpty(n)) continue;
                if (nm.IndexOf(n.Trim(), StringComparison.Ordinal) >= 0) return true;
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
                int homeMap = 0;
                try { homeMap = agentManager.GetHomeMapId(); } catch (Exception) { }

                // GetFurnituresWithBag 返回 List<Furniture>；GetAllBagFurnitures 返回
                // List<BagFurniture>，两者泛型不兼容，故各自独立枚举，不能互相赋值。
                var furn = agentManager.GetFurnituresWithBag(homeMap, false, false);
                if (furn != null)
                {
                    enumerCount = furn.Count;
                    foreach (var f in furn)
                    {
                        if (f == null) continue;
                        if (TryAddSource(f.InstanceId, f.AgentConfigId, excludeOwnerId, excludeConfigId,
                                         targetFridge, targetBag, targetConfigs, targetLocked, itemManager))
                            added++;
                    }
                }
                else
                {
                    var fb = agentManager.GetAllBagFurnitures();
                    if (fb != null)
                    {
                        enumerCount = fb.Count;
                        foreach (var f in fb)
                        {
                            if (f == null) continue;
                            if (TryAddSource(f.InstanceId, f.AgentConfigId, excludeOwnerId, excludeConfigId,
                                             targetFridge, targetBag, targetConfigs, targetLocked, itemManager))
                                added++;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                CookingSourceExpandPlugin.Log.LogError($"[CookingSourceExpand] {tag} 补丁异常：{e}");
            }

            CookingSourceExpandPlugin.Log.LogInfo($"[CookingSourceExpand] ★{tag}: 储物家具枚举 {enumerCount} 个，新增来源 {added} 个。");
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

        [HarmonyPatch(typeof(CookingUI.Ac_Item_GetCookingBagList), nameof(CookingUI.Ac_Item_GetCookingBagList.SendAction))]
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

        [HarmonyPatch(typeof(CookingUI.Ac_Cooking_Open), nameof(CookingUI.Ac_Cooking_Open.SendAction))]
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

        [HarmonyPatch(typeof(CookingUI.Ac_Item_GetHandMadeBagList), nameof(CookingUI.Ac_Item_GetHandMadeBagList.SendAction))]
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
                    var furn = am.GetFurnituresWithBag(homeMap, false, false);
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
    }
}