using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace CookingSourceExpand
{
    [BepInPlugin(PluginInfo.GUID, PluginInfo.Name, PluginInfo.Version)]
    [BepInProcess("SurvivalLog.exe")]
    public class CookingSourceExpandPlugin : BasePlugin
    {
        internal static new ManualLogSource Log;

        public override void Load()
        {
            Log = base.Log;
            var harmony = new Harmony(PluginInfo.GUID);
            harmony.PatchAll();
            Log.LogInfo($"{PluginInfo.Name} v{PluginInfo.Version} 已加载：烹饪（灶台/火炉）面板食材来源已扩展为所有带储物背包的家具。");
        }
    }

    public static class PluginInfo
    {
        public const string GUID = "com.cookingsourceexpand.mod";
        public const string Name = "CookingSourceExpand";
        public const string Version = "1.0.0";
    }
}