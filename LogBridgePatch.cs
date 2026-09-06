using System;
using System.Text.Json;

namespace CookingSourceExpand
{
    /// <summary>
    /// 前端诊断日志桥：把工作台 HTML 里 JS 的 dbg() 诊断行，经 WebUI 层收敛的 JS→C# 消息入口
    /// (GameCore.HotUpdate.ReduxUI.WebUILayer.OnPageMessage) 接回 BepInEx 日志，便于直接查 LogOutput.log。
    ///
    /// 前端 contract：core.UnitySendEvent('CSE_DEBUG', {text:'...'}) →
    /// postMessage({type:'CSE_DEBUG', data:{text}}) → 游戏在 WebUILayer.OnPageMessage(source, messageType, jsonData) 收到。
    /// 注意：此前误挂 ActionDispatcher.DispatchReduxAction（reducer 分发在消息筛选之后，未知事件不会到达），
    /// 故这里改挂更上游的 OnPageMessage——它能看到所有 JS 消息的原始 messageType。
    /// 本 Prefix 只拦截 messageType == "CSE_DEBUG"，记录后 return false（无需继续走 reducer 路由），其余放行。
    /// </summary>
    internal static class LogBridgePatch
    {
        private const int MaxLen = 800;
        private static long _lastBridgeTick;
        private static int _bridgeCount;
        // 每 2 秒最多转储 2 条非 CSE_DEBUG 消息，避免高频消息刷屏
        private const long ThrottleMs = 2000;
        private const int ThrottleMax = 2;

        internal static bool Prefix(string source, string messageType, string jsonData)
        {
            try
            {
                string page = source ?? "";
                bool isDebug = messageType == "CSE_DEBUG";

                // CSE_DEBUG 无条件转储（这是我们的诊断正文）
                var text = jsonData;
                if (!string.IsNullOrEmpty(jsonData) && jsonData.Length > MaxLen)
                    text = jsonData.Substring(0, MaxLen) + "…(" + jsonData.Length + ")";

                if (isDebug)
                {
                    if (!string.IsNullOrEmpty(jsonData))
                    {
                        try
                        {
                            using (var doc = JsonDocument.Parse(jsonData))
                            {
                                if (doc.RootElement.ValueKind == JsonValueKind.Object
                                    && doc.RootElement.TryGetProperty("text", out var t))
                                {
                                    var s = t.GetString();
                                    if (s != null) text = s;
                                }
                            }
                        }
                        catch { }
                    }
                    CookingSourceExpandPlugin.Log.LogMessage("[CSE前端] " + text);
                    return true; // 放行，不干扰前端后续流程
                }

                // 非 CSE_DEBUG：节流转储，用于确认 OnPageMessage 是否真的被调用、
                // 以及确认 ToolTable 前端的消息通道到底长什么样。
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (now - _lastBridgeTick >= ThrottleMs * 10000)
                {
                    _lastBridgeTick = now;
                    _bridgeCount = 0;
                }
                if (_bridgeCount < ThrottleMax)
                {
                    _bridgeCount++;
                    CookingSourceExpandPlugin.Log.LogMessage($"[CSE桥] src={page} type={messageType} json={text}");
                }
                return true;
            }
            catch (Exception)
            {
                return true; // 任何异常都不能影响正常分发
            }
        }
    }
}