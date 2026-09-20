# Survival Log — BaseButler（居家管家）

当前版本：**v2.0.1** · Latest: v2.0.1

一个《生存日志》(Survival Log) 的 **BepInEx 6** 整合插件。原「Cooking Source Expand（食材来源扩展）」已并入本插件，加上堆叠优化，两个模块共用一个 DLL。

A BepInEx 6 plugin for Survival Log. It merges the former **Cooking Source Expand** and **stacking optimization** into one DLL.

- 插件 GUID：`com.basebutler.mod`
- 依赖：BepInEx 6（IL2CPP）
- 仓储管家（原 B 模块）因未稳定触发已隔离停用，源码保留完好于此仓库。

---

## 功能 Features

### A · 来源扩展 / 一键烹饪 Cooking Source Expand

把**烹饪面板**、**手工制作界面**、**无人机交易界面**和**工作台**的食材/材料来源，从"仅有冰箱、冷柜"扩展为**基地内所有带储物功能的家具**（箱子、柜子、储物架、冰箱、冷柜等）——做菜 / 手工 / 交易 / 工作台前无需先把材料搬到特定容器，直接从各处储物柜取用即可。

- 归属过滤：仅把**当前角色家**（含二楼、地下室）的储物家具作为来源，自动排除其它角色的柜子，避免串料。
- 联动柜直取：把"玩家接入的联动储物柜"放行为**烹饪可直取袋**，原生取料链路直接认账（配置 `TreatAsCookingBag`）。
- 工作台跨柜取料：不同储物柜之间可取料互通。
- 菜谱稳定性：WebUI 注入，保持菜谱列表排序稳定并美化滚动条。
- 配置：`SelectRecipeDiagnostics`（取料诊断日志，默认关）、`IncludeBoxConfigIds` / `ExtraExcludedBoxConfigIds`（来源白名单/额外排除）。

### B · 堆叠优化 Stacking

- 自动合并：物品入包时自动把同类堆合并到一起。
- 堆叠上限扩展：把若干材料的单格堆叠上限从 1 提升到 5（肥料、种子、冰块、木板、木片、木材、石头、石块、铁片、铁皮、铁锭、金属、材料、塑料、玻璃、纸等）。
- 一键拆分：按需把堆超量的材料拆出制作所需数量到工作台，配合一键烹饪避免"整堆搬走、多搬/占格"。

> 仓储管家（原 B 模块 StorageButler）因未稳定触发已隔离停用，本版不加载。源码保留于 `StorageButler_Plugin.cs`，后续稳定后会重新开启。

---

## 安装 Installation

1. 备份 `BepInEx\plugins` 下的旧版 `CookingSourceExpand.dll`（如仍在）。
2. 将 `BaseButler.dll` 放入 `BepInEx\plugins\`。
3. 若旧版 `CookingSourceExpand.dll` 尚在，请删除它，避免与整合版冲突。
4. 启动游戏。首次运行会在 `BepInEx\config\` 生成 `BaseButler.cfg`，可按需调整来源过滤 / 堆叠开关等参数。

---

## 构建 Build

与游戏程序集互操作，`BaseButler.csproj` 通过 `..\BepInEx\core`、`..\BepInEx\interop` 引用框架与 interop 程序集。在含 `BepInEx` 的目录下：

```
dotnet build -c Release
```

产物：`bin\Release\BaseButler.dll`

---

## 兼容性 Compatibility

- 兼容 Steam `v1.0.14911`（IL2CPP metadata v31），BepInEx 6（`be.785` 及更高）。
- 使用 SafePatch（`AccessTools` + 显式查找目标方法，`try/catch` 包裹），游戏更新导致目标方法签名变化时会自动停用对应补丁并记日志，绝不拖垮插件加载导致启动失败。
- 不修改任何存档数据，兼容每日服务器热更。