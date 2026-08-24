# Survival Log — Cooking Source Expand (食材来源扩展)

一个《生存日志》(Survival Log) 的 BepInEx 6 插件，把**烹饪面板**和**手工制作界面**的食材/材料来源扩展为基地内所有带储物功能的家具。
A BepInEx 6 plugin for Survival Log that expands the **cooking panel** and **handcrafting UI** source containers to every storage-capable furniture in your base.

Compatible with Steam `v1.0.14911` (IL2CPP metadata v31). Built on BepInEx 6 (Bleeding Edge `be.785`).

---

## 功能 Features

该 Mod 同时扩展两个制作界面的来源容器：
This mod expands source containers for two crafting UIs at once:

1. **烹饪面板（灶台 / 火炉）Cooking panel (stove / furnace)**
   食材来源从"仅有冰箱、冷柜"扩展为"所有带储物功能的家具"（箱子、柜子、储物架、冰箱、冷柜等）。
   Ingredient sources expand from "fridges / freezers only" to all storage furniture (boxes, cabinets, shelves, fridges, freezers, ...).

2. **手工制作界面 Handcrafting UI**
   材料来源从"仅有工作台抽屉/工具柜"扩展为"所有带储物功能的家具"。
   Material sources expand from "workbench drawer / tool cabinet only" to all storage furniture.

做菜 / 手工前无需先把食材搬到特定容器，直接从各处储物柜取用即可。
No need to shuffle ingredients into a specific container first — take them straight from any storage in your base.

### 排除名单 Exclusions

自动排除非储物容器：房门、木门、铁门、大门、窗户、木窗、铁窗、玻璃窗、铝合金窗、防盗门、防盗窗、小汽车、无人机。
Automatically excluded: doors, windows, security doors/windows, cars, drones.

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