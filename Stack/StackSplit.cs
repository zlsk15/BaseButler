using System;
using GameCore.HotUpdate;
using GameCore.HotUpdate.Battle.Logic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace BaseButler.Stack
{
    /// <summary>
    /// 按需拆取：把储物柜里"一整堆"的物品，只取出 count 个放到目标容器（工作台），其余留在原地。
    ///
    /// 用途：一键制作（点击配方自动补料）原本整堆搬料——材料此前堆叠上限=1 时无感，给 C 模块
    /// 抬了堆叠上限后，一格能装 5 个，整堆搬会"多搬/占格/数量错"。本处理器让前端在堆超量时
    /// 改发 BB_SPLIT_MOVE，C# 按所需数量拆出，避免把整格搬空。
    ///
    /// 数据修改范式与 AutoMerge/MergeGroup 完全一致（都是改动 ItemManager 数据 + SplitHalfPatch.NotifyItem
    /// 通知 REDUX 同步），已在该路径上长期实测安全。
    /// </summary>
    public static class StackSplit
    {
        /// <summary>从 fromOwner 容器里取 count 个 itemId(InstanceId) 或 configId 指定的那一堆，放到 toOwner。</summary>
        internal static bool TrySplitMove(long fromOwner, long itemId, int configId, int count, long toOwner)
        {
            try
            {
                ItemManager im = SplitHalfPatch.GetIM();
                if (im == null) return false;

                Il2CppSystem.Collections.Generic.List<ItemData> list = null;
                try { list = im.GetItemDataList(fromOwner); } catch { return false; }
                if (list == null) return false;

                int n = 0;
                try { n = list.Count; } catch { return false; }

                ItemData src = null;
                // 优先用 InstanceId 精确定位；前端 itemId 过 JS 大数可能丢精度，此时靠 configId 回退。
                for (int i = 0; i < n && src == null && itemId > 0; i++)
                {
                    ItemData it = null;
                    try { it = list[i]; } catch { continue; }
                    if (it == null) continue;
                    long id = 0;
                    try { id = ((BaseEntity)it).InstanceId; } catch { continue; }
                    if (id != itemId) continue;
                    try { if (it.ItemCount <= 0) continue; } catch { continue; }
                    src = it;
                }
                if (src == null && configId > 0)
                {
                    for (int i = 0; i < n; i++)
                    {
                        ItemData it = null;
                        try { it = list[i]; } catch { continue; }
                        if (it == null) continue;
                        int cfgid = 0;
                        try { cfgid = it.ItemConfigId; } catch { continue; }
                        if (cfgid != configId) continue;
                        try { if (it.ItemCount <= 0) continue; } catch { continue; }
                        src = it;
                        break;
                    }
                }
                if (src == null) return false;

                int cfg = 0;
                int avail = 0;
                try { cfg = src.ItemConfigId; avail = src.ItemCount; } catch { return false; }
                if (avail <= 0) return false;

                int take = count < 1 ? 1 : (count > avail ? avail : count);
                int remain = avail - take;

                // 守恒顺序：先在目标容器确认放得下 take 个并落位，成功后才扣减源堆。
                // 之所以不能"先扣源再加目标"：若目标已满、AddItem 返回 null，源已被扣减/移除，
                // 该批物品会凭空消失（此前 catch{} 把失败吞掉 = 丢件隐患）。
                ItemData created = null;
                try
                {
                    created = im.AddItem(toOwner, cfg, take,
                        src.StartTime, src.TimeLeft, new Vector2Int(-1, -1),
                        src.UseTimes, false, src.TimeScale,
                        src.InstanceVD, src.InstanceEffectEnd,
                        src.MaxUseTimes, src.InstanceBurnValue, src.Polluted, src.IsMapPreset, src.NoPackage, src.InstanceWeight);
                }
                catch { created = null; }

                if (created == null) return false; // 放不下 → 一个不动，源堆原地保留

                try { im.SetItemCount(src, remain); } catch { }

                long sid = 0;
                try { sid = ((BaseEntity)src).InstanceId; } catch { }
                if (remain <= 0 && sid != 0)
                {
                    try { im.RemoveItem(sid); } catch { }
                }
                try { SplitHalfPatch.NotifyItem(src); } catch { }
                try { SplitHalfPatch.NotifyItem(created); } catch { }

                try { SplitHalfPatch.RefreshToolTableState(); } catch { }

                Plugin.LogSource?.LogInfo("[按需拆取] " + fromOwner + " itemId" + itemId + " x" + take + " → " + toOwner + "（原地剩 " + remain + "）");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning("[按需拆取] 失败: " + ex.Message);
                return false;
            }
        }
    }
}