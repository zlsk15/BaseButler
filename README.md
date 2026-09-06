# Survival Log — Cooking Source Expand (食材来源扩展)

当前版本：**v1.5.6** · Latest: v1.5.6

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

### 按当前角色家枚举 (v1.3.2)

四个界面统一改用 `GetFurnituresWithBag(当前家)` 枚举，多角色存档里其它角色家的家具绝不会混入本角色面板。
All four UIs enumerate via `GetFurnituresWithBag(current home)`, so other characters' furniture never leaks into your panel in co-op saves.

### 名字级排除 Name-level exclusions (v1.3.2)

门窗、床（以“床”结尾，不误伤“床头柜”）、围栏、栅栏、燃气灶、微波炉等名字一律排除，跨角色统一生效，不再依赖逐字符固定的 configId。
Furniture named as door/window, anything ending in “床” (bed; “床头柜” bedside cabinet unaffected), fence, stove, microwave are all excluded by name across every character.

### 拖动定位储物处 Drag-to-scroll (v1.4.3)

烹饪/手工/无人机交易面板的箱子来源条**支持鼠标按住左右拖动**来滑动查找箱子（不再只靠左右箭头点按），并显示一条**细横向滚动条**辅助定位。
Driving the source bar in cooking / handcraft / drone-trade: **hold and drag with the mouse** to scroll and find the right box (no more tapping the arrow buttons), with a thin scrollbar for positioning.

- 实现方式：mod 启动时自动给三个面板的 HTML 打一份**干净补丁**；游戏更新若覆盖 HTML，下次启动 mod 会自动重新打上，**抗更新**。
  Implemented via a self-healing HTML auto-patch applied at mod startup; if a game update overwrites the HTML, the mod re-applies it on next launch (update-resilient).
- 同时兼容烹饪的 `#bagTabsScroll` 和工作台/交易的 `#leftTabsScroll` 两种来源条容器。

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

## 更新日志 Changelog

### v1.5.6
- **工作台一键取料修复**：取料源列表补上「工作台抽屉」（此前抽屉里的材料从不被取）；并新增「有料柜回头重扫」——扫过但有物品却没取到料的柜子，首轮结束后用更长超时回头再扫，把还缺的材料补足。已验证跨柜+抽屉自动取料并成功制作多配方。
- 无人机交易来源修复：mod 注入的非冰柜来源正确显示为普通储物格（不再误用冰柜冻结模板）。
- 来源排除新增：**老鼠笼/鼠笼、咖啡机** 不再作为烹饪/手工/无人机交易/工作台的存货来源。
- 前端新增游戏内调试覆盖层（右上角黄色面板）便于定位取料过程。
- Fixed one-click workbench gathering: workbench drawer now included as a material source, plus a slow re-scan pass revisits cabinets that had items but yielded no moves until shortages are covered. Verified automated cross-cabinet/drawer gathering crafts recipes successfully.
- Added source exclusions for **rat traps / coffee machines** across cooking / handcraft / drone-trade / workbench.
- Drone-trade injected non-fridge sources now render as normal storage grids (no more frozen-template style).

### v1.4.3
- 前端增强：烹饪/手工/无人机交易面板的箱子来源条支持鼠标拖动滑动查找，并显示细滚动条（mod 启动时自动给三个面板 HTML 打干净补丁，游戏更新自动重打）。
- 前端 smooth：drag-to-scroll + thin scrollbar on the source bar, applied via a self-healing HTML auto-patch.

### v1.3.2
- 按当前角色家枚举、名字级跨角色排除、白名单扩充（详见上文“按当前角色家枚举”等小节）。

### v1.3.1
- 来源过滤配置（`CookingSourceExpand.cfg`）、静默无窗（`BepInEx.cfg`）。

### v1.3.0
- 归属过滤：仅当前角色家的储物家具作为来源。

### v1.1.1
- 首个稳定发布版。
