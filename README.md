# Survival Log — Cooking Source Expand (食材来源扩展)

当前版本：**v1.6.0** · Latest: v1.6.0

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

自动排除非储物容器：房门、木门、铁门、大门、窗户、木窗、铁窗、玻璃窗、铝合金窗、防盗门、防盗窗、小汽车、无人机、行李箱、植物灯培育箱、老鼠笼、咖啡机。
Automatically excluded: doors, windows, security doors/windows, cars, drones, luggage, planter boxes, rat traps, coffee machines.

---

## 安装 Installation

> **Important — install the WHOLE zip, not just the .dll.**
> This package already bundles the full BepInEx 6 framework. If you drop only `CookingSourceExpand.dll` somewhere, the game will never load it. Extract **everything** and place it correctly in one go.

1. In Steam, right-click *Survival Log* → Manage → Browse local files. This opens the **game root folder** — the one containing `SurvivalLog.exe` (e.g. `...\Steam\steamapps\common\Survival Log\`). **Everything goes into this folder.**
2. Extract the zip and copy **all** of the following into the game root folder (merge / overwrite when asked). After installing, confirm all of these are present:
   - `winhttp.dll`
   - `doorstop_config.ini`
   - `BepInEx\` (the whole folder, incl. `plugins\CookingSourceExpand.dll`)
   - `dotnet\` (the whole folder)
   When done, the game root should show **both `SurvivalLog.exe` and the `BepInEx\` folder** side by side.
3. Launch the game via Steam. **The first launch takes 1–3 minutes longer** (BepInEx builds its interop layer) — this is normal, so wait, don't assume it failed.
4. In-game after loading a save, open the stove/furnace cooking panel or the handcrafting UI. If you see **multiple selectable storage sources** (boxes / cabinets / fridges), it works.

**How to verify it's working:** you see several storage tabs in the cooking/crafting UI **and** a `BepInEx\LogOutput.log` file appears in the game root.

**Installed it but nothing happens? Check these:**
1. Make sure `BepInEx\` and `winhttp.dll` are **in the same folder as `SurvivalLog.exe`** (the most common mistake: placed into a subfolder, or only the .dll was copied).
2. Confirm you let the first launch finish the 1–3 min interop generation (does a `BepInEx\` folder show up?).
3. Open `BepInEx\LogOutput.log` and search for `CookingSourceExpand` to check whether the plugin loaded.

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

### v1.6.0（清理优化版）
- **清理发布版诊断代码**：移除从未挂载的 `TradeRouteDebug` 探针与启动枚举的 `RuntimeProbe`，DLL 减小约 6KB，启动日志更干净，运行期行为不变。
- **修复无人机来源 IsFridge 分裂隐患**：删除从未被触发的 `SetContainerTabs` 旧补丁，无人机来源统一由实测生效的 `ShowUI → RebuildBagTabs` 注入（非冰柜来源一律 `IsFridge=false`），消除「游戏切换路径时普通储物柜被渲染成冰柜冻结模板」的隐患。

### v1.5.6（对比 v1.5.0）
- **取料来源补齐「工作台抽屉」**：此前工作台本身抽屉里的材料不会被自动取用；本版将抽屉正式纳入取料来源，抽屉里的料也会被自动搬走补齐配方。
- **新增「有料柜回头重扫」**：扫描中读取超时、或打开了却没真正取到料的柜子，会在首轮扫描后自动用更长超时二次重扫，把仍缺的材料补足，不再「粗查后跳过、缺料也不回头取」。
- **背包纳入取料来源**（带同归属保护）：玩家背包也可作为取料来源，同时避免把材料搬给自己导致计数异常。
- **无人机交易接入全屋储物并修复显示**：无人机交易来源扩展至全屋储物家具；修复 mod 注入的非冰柜来源误用「冰柜冻结模板」显示的问题，普通柜子正常显示为储物格。
- **材料判定更精准**：物品名（归一化）精确匹配 + 物品ID/configId 双重判定，避免「铁片 / 精致铁片」等相似命名误取。
- **材料足缺判定更可靠**：以实际累计搬运量判断是否足够，不依赖界面快照的滞后数据；官方判定材料全缺时插件同步取消制作。
- **来源排除更合理**：只会从真正带储物的家具取料，并新增排除老鼠笼、咖啡机。
- **发布版静默化**：去除运行期诊断浮层与提示条，不影响正常操作。
- Comparison vs v1.5.0: added workbench **drawer** as a gathering source, a slow **re-scan pass** for cabinets that had items but yielded no moves, player **backpack** as a source, drone-trade source now also normalizes non-fridge grids; **normalized-name + itemId/configId** double matching, reliable cumulative material-sufficiency checks, extra exclusions (rat traps / coffee machines), and a **silent release** (no debug overlay).

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
