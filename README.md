# Survival Log — Cooking Source Expand (食材来源扩展)

当前版本：**v1.3.1** · Latest: v1.3.1

一个《生存日志》(Survival Log) 的 BepInEx 6 插件，把**烹饪面板**、**手工制作界面**、**无人机交易界面**和**工作台**的食材/材料来源扩展为基地内所有带储物功能的家具。
A BepInEx 6 plugin for Survival Log that expands the **cooking panel**, **handcrafting UI**, **drone-trade UI** and **workbench** source containers to every storage-capable furniture in your base.

Compatible with Steam `v1.0.14911` (IL2CPP metadata v31). Built on BepInEx 6 (Bleeding Edge `be.785`).

---

## 功能 Features

该 Mod 同时扩展四个界面的来源容器：
This mod expands source containers for four UIs at once:

1. **烹饪面板（灶台 / 火炉）Cooking panel (stove / furnace)**
   食材来源从"仅有冰箱、冷柜"扩展为"所有带储物功能的家具"（箱子、柜子、储物架、冰箱、冷柜等）。
   Ingredient sources expand from "fridges / freezers only" to all storage furniture (boxes, cabinets, shelves, fridges, freezers, ...).

2. **手工制作界面 Handcrafting UI**
   材料来源从"仅有工作台抽屉/工具柜"扩展为"所有带储物功能的家具"。
   Material sources expand from "workbench drawer / tool cabinet only" to all storage furniture.

3. **无人机交易界面 Drone-trade UI (trade / donate / supply)**
   可调用来源从"仅有背包/无人机本体"扩展为"所有带储物功能的家具"，多个储物柜之间可直接互通取料。
   Selectable sources expand from "backpack / drone only" to all storage furniture, so multiple cabinets are freely usable across the trade interface.

4. **工作台 Workbench**
   工作台自动填料可跨面板从各处储物家具取料。
   Workbench auto-fill pulls materials across panels from any storage furniture.

做菜 / 手工 / 无人机交易 / 工作台前无需先把食材搬到特定容器，直接从各处储物柜取用即可。
No need to shuffle items into a specific container first — take them straight from any storage in your base.

### 归属过滤 Ownership filter (v1.3.0)

仅将**当前角色家**（含二楼、地下室）的储物家具作为来源，自动排除其它角色（如女大学生）家里的柜子，避免串料。
Only the **current character's home** storage (including 2nd floor & basement) is used; other characters' cabinets (e.g. a female college student) are excluded to avoid cross-contamination.

### 来源过滤配置 Source whitelist config (v1.3.1)

箱子太多、点击找箱子太慢时，编辑 `BepInEx\config\CookingSourceExpand.cfg`（首次运行自动生成）：
Too many boxes make clicking around slow? Edit `BepInEx\config\CookingSourceExpand.cfg` (auto-created on first run):

- `Sources > IncludeBoxConfigIds`：白名单。非空时仅把这些箱子类型 id 作为来源；留空=全部默认储物箱子。
- `Sources > ExtraExcludedBoxConfigIds`：额外排除这些箱子类型 id（即使命中默认白名单）。

```ini
[Sources]
# 只保留 箱子3001 和 储物柜 | keep box3001 + cabinet only
IncludeBoxConfigIds = 3001,80156
```

纯后端数据过滤，不触碰游戏前端界面，游戏更新后不失效。

### 静默无窗 Silent console (v1.3.1)

发布包预置 `BepInEx\config\BepInEx.cfg`，默认关闭启动控制台窗口；日志仍写入 `BepInEx\LogOutput.log`。
The package pre-ships `BepInEx.cfg` with the startup console disabled; logs still go to `BepInEx\LogOutput.log`.

### 排除名单 Exclusions

自动排除非储物容器：房门、木门、铁门、大门、窗户、木窗、铁窗、玻璃窗、铝合金窗、防盗门、防盗窗、小汽车、无人机、行李箱、植物灯培育箱。
Automatically excluded: doors, windows, security doors/windows, cars, drones, luggage, planter boxes.

---

## 安装 Installation

1. In Steam, right-click *Survival Log* → Manage → Browse local files (locate `SurvivalLog.exe`).
2. Extract the zip and copy **all contents** (`winhttp.dll`, `doorstop_config.ini`, `BepInEx`, `dotnet`) into the game root folder, overwrite when asked.
3. Launch the game via Steam. Open the stove/furnace cooking panel or the handcrafting UI — multiple storage sources will now be selectable.

BepInEx 6 + dotnet runtime are bundled — no extra setup needed.

## 卸载 Uninstall

- Disable the mod: delete `游戏根目录\BepInEx\plugins\CookingSourceExpand.dll`
- Fully remove BepInEx: delete `winhttp.dll`, `doorstop_config.ini`, `dotnet`, `BepInEx`.

## 交互兼容 Compatibility

- Works alongside the storage-size mod (**BagSizeExpand**) — they don't interfere.
- To add/remove exclusions, edit `ExcludedSourceNames` / `ExcludedSourceConfigIds` in `CookingBagPatch.cs` and recompile.

## 注意事项 Notes

- Game version: **v1.0.14911** (IL2CPP metadata v31).
- First launch is slower (BepInEx generates Interop assemblies, ~1–3 min) — this is normal.
- Back up your save before use.