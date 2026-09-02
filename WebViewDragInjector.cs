using System;
using System.IO;
using System.Text;
using BepInEx.Logging;

namespace CookingSourceExpand
{
    /// <summary>
    /// 自修复前端打补丁：mod 每次启动时，自动往烹饪/手工/交易面板的 HTML 里注入
    /// 「拖动滑动 + 可见滚动条」脚本。
    ///
    /// 为什么改成改文件而不是运行时注入：
    ///   实测确认游戏的 WebView 是代码动态创建的普通对象（非场景组件），FindObjectsOfType
    ///   三路都找不到，运行时注入走不通。改为直接给 StreamingAssets 下的 HTML 打补丁，
    ///   但用"哨兵标记"保证幂等（只打一次），并且每次启动都检查——若游戏 Steam 更新把
    ///   HTML 覆盖回原样，下次启动 mod 会重新打上，从而"自修复"，抗更新。
    ///   首次打补丁前会先备份原文件为 .cse.bak，便于回滚。
    /// </summary>
    internal static class WebViewHtmlPatcher
    {
        private const string Sentinel = "CSE_AUTOPATCH_DRAG";
        private static string UiRoot
        {
            get
            {
                try
                {
                    var dir = Path.GetDirectoryName(typeof(WebViewHtmlPatcher).Assembly.Location);
                    if (string.IsNullOrEmpty(dir)) return "";
                    var gameRoot = Path.GetFullPath(Path.Combine(dir, "..", ".."));
                    var ui = Path.Combine(gameRoot, "SurvivalLog_Data", "StreamingAssets", "WebUI", "UI");
                    return Directory.Exists(ui) ? ui : "";
                }
                catch (Exception) { return ""; }
            }
        }

        private static readonly string[] Targets = { "Cooking", "ToolTable", "TradeUI" };

        private const string Css =
            "<style id=\"cse-scrollbar\" data-cse=\"1\">" +
            ".bag-tabs-scroll,.left-tabs-scroll{overflow-x:auto!important;scrollbar-width:thin!important;scrollbar-color:rgba(251,176,52,.75) rgba(255,255,255,.08)!important;}" +
            ".bag-tabs-scroll::-webkit-scrollbar,.left-tabs-scroll::-webkit-scrollbar{display:block!important;height:8px!important;background:rgba(255,255,255,.08)!important;}" +
            ".bag-tabs-scroll::-webkit-scrollbar-thumb,.left-tabs-scroll::-webkit-scrollbar-thumb{background:rgba(251,176,52,.75)!important;border-radius:4px!important;}" +
            ".bag-tabs-scroll::-webkit-scrollbar-track,.left-tabs-scroll::-webkit-scrollbar-track{background:rgba(255,255,255,.05)!important;}" +
            "</style>";

        private const string JsHead = "<script>";
        private const string JsBody =
            "(function(){var SEL='#bagTabsScroll, #leftTabsScroll, .bag-tabs-scroll, .left-tabs-scroll';" +
            "function attach(s){if(s.__cseD)return;s.__cseD=1;var d=0,x=0,l=0;" +
            "function down(e){d=1;x=e.clientX;l=s.scrollLeft;s.style.cursor='grabbing';e.preventDefault();}" +
            "function move(e){if(!d)return;s.scrollLeft=l-(e.clientX-x);e.preventDefault();}" +
            "function up(){d=0;s.style.cursor='';}" +
            "s.addEventListener('pointerdown',down,{passive:false});window.addEventListener('pointermove',move,{passive:false});" +
            "window.addEventListener('pointerup',up);s.addEventListener('pointerleave',up);}" +
            "function go(){document.querySelectorAll(SEL).forEach(attach);}" +
            "function loop(){var n=0;var t=setInterval(function(){go();if(++n>200)clearInterval(t);},300);}" +
            "if(document.readyState!=='loading')loop();else document.addEventListener('DOMContentLoaded',loop);})();";
        private const string JsTail = "</script>";
        private const string SentinelComment = "<!-- " + Sentinel + " -->";

        internal static void ApplyAll(ManualLogSource log)
        {
            try
            {
                if (string.IsNullOrEmpty(UiRoot) || !Directory.Exists(UiRoot))
                {
                    log.LogWarning($"[CookingSourceExpand] 未定位到 WebUI 目录（{UiRoot}），前端补丁跳过。");
                    return;
                }
                int patched = 0, skipped = 0, failed = 0;
                foreach (var name in Targets)
                {
                    var path = Path.Combine(UiRoot, name, name + ".html");
                    if (!File.Exists(path)) { skipped++; continue; }
                    try
                    {
                        if (PatchFile(path, log)) patched++; else skipped++;
                    }
                    catch (Exception e)
                    {
                        failed++;
                        log.LogWarning($"[CookingSourceExpand] 补丁 {name} 失败：{e.Message}");
                    }
                }
                log.LogInfo($"[CookingSourceExpand] 前端『拖动滑动+滚动条』补丁：新打 {patched} 个，已存在跳过 {skipped} 个，失败 {failed} 个。");
            }
            catch (Exception e)
            {
                log.LogWarning($"[CookingSourceExpand] 前端打补丁异常：{e.Message}");
            }
        }

        private static bool PatchFile(string path, ManualLogSource log)
        {
            string text;
            try { text = File.ReadAllText(path, new UTF8Encoding(false)); }
            catch (Exception e) { log.LogWarning($"[CookingSourceExpand] 读取 {path} 失败：{e.Message}"); return false; }

            if (text.Contains(Sentinel))
            {
                return false; // 已打过，跳过
            }

            // 首次打：备份原文件
            var bak = path + ".cse.bak";
            if (!File.Exists(bak))
            {
                try { File.WriteAllText(bak, text, new UTF8Encoding(false)); }
                catch (Exception e) { log.LogWarning($"[CookingSourceExpand] 备份 {path} 失败：{e.Message}"); }
            }

            // 注入 CSS 到 <head>
            int headEnd = text.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
            if (headEnd >= 0)
            {
                int gt = text.IndexOf('>', headEnd);
                if (gt >= 0)
                {
                    text = text.Substring(0, gt + 1) + "\n" + Css + "\n" + text.Substring(gt + 1);
                }
            }

            // 注入 JS 到 </body> 前
            int bodyIdx = text.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (bodyIdx >= 0)
            {
                text = text.Substring(0, bodyIdx) + JsHead + JsBody + JsTail + "\n" + SentinelComment + "\n" + text.Substring(bodyIdx);
            }
            else
            {
                text = text + "\n" + SentinelComment;
            }

            File.WriteAllText(path, text, new UTF8Encoding(false));
            log.LogInfo($"[CookingSourceExpand] 已给 {Path.GetFileName(path)} 打上『拖动滑动+滚动条』补丁。");
            return true;
        }
    }
}