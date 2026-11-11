using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using ModelContextProtocol.Server;

namespace SolidEdge.Spy.EventMcp.Tools
{
    /// <summary>
    /// 监视 MCP 的 4 个工具。全部走纯托管读路径(环形缓冲/快照字段 / 事件通知),
    /// 不做同步 COM 调用 —— 泵线程忙于订阅/重连时工具也立刻返回。
    /// se_wait_event 是唯一的"阻塞"工具,但它阻塞的是自己的 MTA 线程池线程
    /// (轮询环形缓冲 + 等 EventHub.EventRecorded 信号),不占泵线程、不碰 COM。
    /// </summary>
    [McpServerToolType]
    public static class EventTools
    {
        [McpServerTool, Description("增量读取 Solid Edge 事件(本 server 常驻订阅 4 类事件源:应用级/文档级/零件重算/装配重算;文件UI 接口因 Advise 会吞另存为/打开对话框,自 2026-09-22 起不订阅)。返回 seq 大于入参 seq 的事件;首次调用传 seq=0 取最近事件,之后把返回的 latestSeq 作为下次入参增量轮询。典型用法:①用执行 MCP(solidedge)的 se_invoke_member 改参数/建特征后,轮询本工具等待 AfterRecompute(重算完成)或 AfterSave(保存完成)确认信号;②等待用户双击视图进入/退出编辑环境:轮询等 ISEApplicationEvents.AfterEnvironmentActivate(进入)或 BeforeEnvironmentDeactivate(退出),收到后用 solidedge 的 se_get_document 查 environment 字段确认具体环境名(如 DrawingViewEdit=视图编辑、Detail=图页)。返回的 lost>0 表示事件太多被环形缓冲覆盖,需加大轮询频率或用 se_set_event_filter 关闭噪声源。")]
        public static string se_get_events(
            EventHub hub,
            [Description("游标:上次返回的 latestSeq;首次调用传 0")] int seq,
            [Description("本次最多返回条数,建议 50,上限 200")] int limit)
        {
            try
            {
                int clamped = Math.Max(1, Math.Min(limit, 200));
                RingReadResult rr = hub.GetEvents(seq, clamped);

                var payload = new
                {
                    status = "ok",
                    latestSeq = rr.LatestSeq,
                    oldestSeq = rr.OldestSeq,
                    lost = rr.Lost,
                    nextSeq = rr.LatestSeq,
                    events = rr.Events.Select(r => new
                    {
                        seq = r.Seq,
                        time = r.Time.ToString("HH:mm:ss.fff"),
                        source = r.Source,
                        @event = r.Event,
                        detail = r.Detail,
                        doc = r.Doc,
                    }).ToArray(),
                };
                return JsonSerializer.Serialize(payload);
            }
            catch (Exception ex)
            {
                return Error("读取事件失败: " + ex.Message);
            }
        }

        [McpServerTool, Description("查看监视状态诊断:Solid Edge 连接是否正常、各事件接口订阅结果(宿主路径)、当前文档、环形缓冲统计、被禁用的事件源。事件不触发时先用本工具诊断:若 connected=false 是 SE 未启动或已关闭(会自动重连);若某接口显示'当前文档类型不适用'属正常(零件级事件源只在零件文档存在,装配级同理)。")]
        public static string se_event_status(EventHub hub)
        {
            try
            {
                RingStats ring = hub.GetRingStats();
                var payload = new
                {
                    status = "ok",
                    connected = hub.IsConnected,
                    document = hub.CurrentDocument,
                    uptimeSeconds = hub.UptimeSeconds,
                    subscriptions = hub.SubscriptionStatus.Select(s => new
                    {
                        @interface = s.Interface,
                        host = s.Host,
                        result = s.Result,
                    }).ToArray(),
                    ring = new
                    {
                        capacity = ring.Capacity,
                        buffered = ring.Buffered,
                        latestSeq = ring.LatestSeq,
                        oldestSeq = ring.OldestSeq,
                    },
                    disabledEvents = hub.DisabledEvents,
                    hint = hub.IsConnected
                        ? null
                        : "尚未连接 Solid Edge(未启动或已关闭)。连接恢复后自动重订,无需干预;期间的事件不可见。",
                };
                return JsonSerializer.Serialize(payload);
            }
            catch (Exception ex)
            {
                return Error("获取状态失败: " + ex.Message);
            }
        }

        [McpServerTool, Description("开关某个事件源(默认全部开启,仅 ISEDocumentEvents.SelectSetChanged 因高频噪声默认关闭)。event 可用短名(如 SelectSetChanged,须在全部事件中唯一)或全名(如 ISEDocumentEvents.SelectSetChanged)。典型用法:等待重算完成时关闭 AfterCommandRun/BeforeCommandRun 等命令噪声,减少轮询干扰。")]
        public static string se_set_event_filter(
            EventHub hub,
            [Description("事件名:短名 SelectSetChanged 或全名 ISEDocumentEvents.SelectSetChanged")] string eventName,
            [Description("true=启用,false=禁用")] bool enabled)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(eventName))
                {
                    return Error("事件名不能为空。可用事件键: " + string.Join(", ", hub.AllEventKeys().ToArray()));
                }

                string resolved;
                string error;
                if (!hub.TrySetFilter(eventName, enabled, out resolved, out error))
                {
                    return Error(error + "。可用事件键: " + string.Join(", ", hub.AllEventKeys().ToArray()));
                }

                var payload = new
                {
                    status = "ok",
                    resolved = resolved,
                    enabled = enabled,
                    disabledEvents = hub.DisabledEvents,
                };
                return JsonSerializer.Serialize(payload);
            }
            catch (Exception ex)
            {
                return Error("设置过滤器失败: " + ex.Message);
            }
        }

        [McpServerTool, Description("阻塞等待某个事件出现(最多 timeoutMs 毫秒),替代'反复轮询 se_get_events 直到看见目标事件'。典型用法:①用执行 MCP(solidedge)改参数/建特征后,等重算完成 —— waitFor=\"AfterRecompute\"(短名)或 \"ISEModelRecomputeEvents.AfterRecompute\"(全名,零件级/装配级同名事件必须用全名);②等用户双击进入/退出视图编辑环境 —— \"ISEApplicationEvents.AfterEnvironmentActivate\" / \"BeforeEnvironmentDeactivate\",命中后再用 solidedge 的 se_get_document 看 environment 字段;③等保存完成 —— \"ISEDocumentEvents.AfterSave\"。waitFor 支持逗号分隔多个(任一命中即返回)与 \"*\"(任意事件)。返回 matched=true 时带命中事件与本次等待期间观察到的其它事件(便于诊断'只看到 BeforeRecompute 没看到 AfterRecompute');matched=false 表示等到超时都没出现 —— 超时不是错误,可用返回的 observed 看期间到底发生了什么,再决定调大 timeoutMs 还是换判据。注意:本调用会占用一次 MCP 请求时长(最长 300 秒),期间 Solid Edge 卡住/未触发该事件就等满超时才返回。")]
        public static string se_wait_event(
            EventHub hub,
            [Description("要等待的事件:短名(全局唯一时,如 AfterRecompute)或全名(接口.事件,如 ISEDocumentEvents.AfterSave);可逗号分隔多个(任一命中即返回);\"*\"=任意事件")] string waitFor,
            [Description("超时毫秒,默认 30000,夹取到 200~300000")] int timeoutMs = 30000,
            [Description("可选:从该 seq 之后开始等(把上次 se_get_events 返回的 latestSeq 传进来,可覆盖'刚刚已发生'的事件,避免因调用间隙漏掉);传 0 或不传=只等从现在起的新事件")] int afterSeq = 0,
            [Description("可选:命中后是否返回该事件的详情,默认 true")] bool includeEvent = true)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(waitFor))
                {
                    return Error("waitFor 不能为空。可用事件键: " + string.Join(", ", hub.AllEventKeys().ToArray()));
                }
                int timeout = Math.Max(200, Math.Min(timeoutMs, 300000));
                string[] tokens = waitFor.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < tokens.Length; i++)
                {
                    tokens[i] = tokens[i].Trim();
                }

                long cursor = (afterSeq > 0) ? (long)afterSeq : (long)hub.GetRingStats().LatestSeq;
                long startCursor = cursor;
                var observed = new List<EventRecord>();
                var stopwatch = Stopwatch.StartNew();

                using (var signal = new ManualResetEventSlim(false))
                {
                    Action<EventRecord> handler = delegate (EventRecord rec) { signal.Set(); };
                    hub.EventRecorded += handler;
                    try
                    {
                        while (true)
                        {
                            // 先订阅、后读缓冲:保证"读完之后"到"下一次读之前"发生的事件也能被唤醒(不会漏)
                            RingReadResult rr = hub.GetEvents(cursor, 200);
                            EventRecord hit = null;
                            long newest = cursor;
                            foreach (EventRecord rec in rr.Events)
                            {
                                if (rec.Seq > newest)
                                {
                                    newest = rec.Seq;
                                }
                                if (observed.Count < 40)
                                {
                                    observed.Add(rec);
                                }
                                if (hit == null && Matches(tokens, rec))
                                {
                                    hit = rec;
                                }
                            }
                            cursor = newest;

                            if (hit != null)
                            {
                                return JsonSerializer.Serialize(new
                                {
                                    status = "ok",
                                    matched = true,
                                    waitFor = waitFor,
                                    waitedMs = stopwatch.ElapsedMilliseconds,
                                    cursor = cursor,
                                    @event = (includeEvent ? EventView(hit) : null),
                                    observedCount = observed.Count,
                                    observed = ObservedView(observed, hit),
                                    hint = "已命中。接着可用执行 MCP(solidedge)查询结果(如 se_get_document 看 environment、se_batch_read 回读属性)。"
                                });
                            }

                            long elapsed = stopwatch.ElapsedMilliseconds;
                            if (elapsed >= timeout)
                            {
                                return JsonSerializer.Serialize(new
                                {
                                    status = "ok",
                                    matched = false,
                                    waitFor = waitFor,
                                    waitedMs = elapsed,
                                    cursor = cursor,
                                    observedCount = observed.Count,
                                    observed = ObservedView(observed, null),
                                    hint = (observed.Count == 0
                                        ? "超时:期间没有任何事件。可能 SE 未连接(先 se_event_status 看 connected)、该操作本就不触发事件、或被 se_set_event_filter 过滤掉了。"
                                        : "超时:期间有事件但没有目标事件 —— 请对照 observed 里的真实事件名(接口名/事件名可能与预期不同),用 se_event_status 看可用事件键。")
                                });
                            }

                            signal.Reset();
                            int waitMs = (int)Math.Min(200, Math.Max(20, timeout - elapsed));
                            signal.Wait(waitMs);
                        }
                    }
                    finally
                    {
                        hub.EventRecorded -= handler;
                    }
                }
            }
            catch (Exception ex)
            {
                return Error("等待事件失败: " + ex.Message);
            }
        }

        /// <summary>事件是否命中等待列表:支持 "*"、全名(接口.事件)、短名(事件名)。全名与短名都不区分大小写。</summary>
        private static bool Matches(string[] tokens, EventRecord rec)
        {
            string full = rec.Source + "." + rec.Event;
            foreach (string t in tokens)
            {
                if (t == "*")
                {
                    return true;
                }
                if (string.Equals(full, t, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (string.Equals(rec.Event, t, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static object EventView(EventRecord r)
        {
            return new
            {
                seq = r.Seq,
                time = r.Time.ToString("HH:mm:ss.fff"),
                source = r.Source,
                @event = r.Event,
                detail = r.Detail,
                doc = r.Doc,
            };
        }

        private static object[] ObservedView(List<EventRecord> observed, EventRecord hit)
        {
            var list = new List<object>();
            foreach (EventRecord r in observed)
            {
                if (hit != null && r.Seq == hit.Seq)
                {
                    continue;
                }
                list.Add(EventView(r));
            }
            return list.ToArray();
        }

        private static string Error(string message)
        {
            return JsonSerializer.Serialize(new { status = "error", message = message });
        }
    }
}
