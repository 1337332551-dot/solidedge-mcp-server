using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SolidEdgeFramework;

namespace SolidEdge.Spy.EventMcp
{
    /// <summary>
    /// 事件目录:全部可过滤事件键(来源.事件名)与默认开关。
    /// 键清单与 EventSinks 实现的接口成员一一对应。
    /// </summary>
    internal static class EventCatalog
    {
        public static readonly Dictionary<string, string[]> Events = new Dictionary<string, string[]>
        {
            ["ISEApplicationEvents"] = new[]
            {
                "AfterActiveDocumentChange", "AfterCommandRun", "AfterDocumentOpen", "AfterDocumentPrint",
                "AfterDocumentSave", "AfterEnvironmentActivate", "AfterNewDocumentOpen", "AfterNewWindow",
                "AfterWindowActivate", "BeforeCommandRun", "BeforeDocumentClose", "BeforeDocumentPrint",
                "BeforeDocumentSave", "BeforeEnvironmentDeactivate", "BeforeQuit", "BeforeWindowDeactivate",
            },
            ["ISEDocumentEvents"] = new[]
            {
                "BeforeClose", "BeforeSave", "AfterSave", "SelectSetChanged",
            },
            ["ISEModelRecomputeEvents"] = new[]
            {
                "BeforeRecompute", "AfterRecompute", "AfterFeatureIsAdded",
                "AfterFeatureIsModified", "BeforeFeatureIsDeleted", "BeforeModelIsDeleted",
            },
            ["ISEAssemblyRecomputeEvents"] = new[]
            {
                "BeforeRecompute", "AfterRecompute", "AfterAdd", "AfterModify", "BeforeDelete",
            },
            ["ISEFileUIEvents"] = new[]
            {
                "OnFileOpenUI", "OnFileSaveAsUI", "OnFileNewUI",
                "OnFileSaveAsImageUI", "OnPlacePartUI", "OnCreateInPlacePartUI",
            },
        };

        /// <summary>合成事件源名(连接/断开/重订等运维信号,不受过滤)。</summary>
        public const string SyntheticSource = "EventRecorder";

        /// <summary>全部事件键(来源.事件名)。</summary>
        public static List<string> AllKeys()
        {
            var keys = new List<string>();
            foreach (var kv in Events)
            {
                foreach (string e in kv.Value)
                {
                    keys.Add(kv.Key + "." + e);
                }
            }
            return keys;
        }

        /// <summary>默认过滤器:全部启用,仅 SelectSetChanged 高频噪声默认关(调研记录 §2.1)。</summary>
        public static Dictionary<string, bool> BuildDefaultFilters()
        {
            var filters = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in AllKeys())
            {
                filters[key] = true;
            }
            filters["ISEDocumentEvents.SelectSetChanged"] = false;
            return filters;
        }

        /// <summary>
        /// 解析事件名:全名("来源.事件")直接匹配;短名("事件")须唯一,
        /// 如 "BeforeRecompute" 在零件级/装配级都存在 → 歧义返回 null。
        /// </summary>
        public static string ResolveKey(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            input = input.Trim();

            // 全名匹配
            foreach (var kv in Events)
            {
                foreach (string e in kv.Value)
                {
                    string full = kv.Key + "." + e;
                    if (string.Equals(full, input, StringComparison.OrdinalIgnoreCase)) return full;
                }
            }

            // 短名唯一匹配
            string hit = null;
            foreach (var kv in Events)
            {
                foreach (string e in kv.Value)
                {
                    if (string.Equals(e, input, StringComparison.OrdinalIgnoreCase))
                    {
                        if (hit != null) return null; // 歧义
                        hit = kv.Key + "." + e;
                    }
                }
            }
            return hit;
        }
    }

    /// <summary>sink 公用工具:参数安全描述(只取托管类型名/值,绝不触发 COM 调用)。</summary>
    internal static class SinkUtil
    {
        public static string Describe(object o)
        {
            if (o == null) return "null";
            Type t = o.GetType();
            // 裸 RCW(__ComObject)拿不到类型名,回调内禁止 COM 调用(如读 Name),标为不透明
            if (t == typeof(object) || t.Name == "__ComObject") return "COM对象(不透明)";
            if (t.IsPrimitive || t == typeof(string) || t.IsEnum) return o.ToString();
            return t.Name;
        }
    }

    // ------------------------------------------------------------------
    // 5 个事件 sink。铁律(EventTester 验证):回调里零 COM 调用、零阻塞,
    // 只调 EventHub.Record(纯托管入环形缓冲);需要 COM 的动作(文档级重订)
    // 通过 hub.RequestDocumentRescan() → PostMessage 延迟到泵线程干净上下文执行。
    // ------------------------------------------------------------------

    /// <summary>文档级事件(宿主:ActiveDocument.DocumentEvents 属性)。</summary>
    internal sealed class DocumentEventsSink : ISEDocumentEvents
    {
        private readonly EventHub _hub;
        public DocumentEventsSink(EventHub hub) { _hub = hub; }

        public void BeforeClose() { _hub.Record("ISEDocumentEvents", "BeforeClose", ""); }
        public void BeforeSave() { _hub.Record("ISEDocumentEvents", "BeforeSave", ""); }
        public void AfterSave() { _hub.Record("ISEDocumentEvents", "AfterSave", ""); _hub.NoteMilestone("Save", ""); }
        public void SelectSetChanged(object SelectSet)
        {
            _hub.Record("ISEDocumentEvents", "SelectSetChanged", "SelectSet=" + SinkUtil.Describe(SelectSet));
        }
    }

    /// <summary>零件级模型重算事件(宿主:ActiveDocument.Models.Item(1).ModelRecomputeEvents)。</summary>
    internal sealed class ModelRecomputeEventsSink : ISEModelRecomputeEvents
    {
        private readonly EventHub _hub;
        public ModelRecomputeEventsSink(EventHub hub) { _hub = hub; }

        public void BeforeRecompute() { _hub.Record("ISEModelRecomputeEvents", "BeforeRecompute", ""); }
        public void AfterRecompute() { _hub.Record("ISEModelRecomputeEvents", "AfterRecompute", ""); }
        public void AfterFeatureIsAdded(SeFeatureAddFlag AddFlag, object Feature)
        {
            _hub.Record("ISEModelRecomputeEvents", "AfterFeatureIsAdded",
                "AddFlag=" + AddFlag + " Feature=" + SinkUtil.Describe(Feature));
            _hub.NoteMilestone("FeatureAdded", "AddFlag=" + AddFlag);
        }
        public void BeforeFeatureIsDeleted(SeFeatureDeleteFlag DeleteFlag, object Feature)
        {
            _hub.Record("ISEModelRecomputeEvents", "BeforeFeatureIsDeleted",
                "DeleteFlag=" + DeleteFlag + " Feature=" + SinkUtil.Describe(Feature));
        }
        public void AfterFeatureIsModified(SeFeatureModifyFlag ModifyFlag, object Feature)
        {
            _hub.Record("ISEModelRecomputeEvents", "AfterFeatureIsModified",
                "ModifyFlag=" + ModifyFlag + " Feature=" + SinkUtil.Describe(Feature));
            _hub.NoteMilestone("FeatureModified", "ModifyFlag=" + ModifyFlag);
        }
        public void BeforeModelIsDeleted(object Model)
        {
            _hub.Record("ISEModelRecomputeEvents", "BeforeModelIsDeleted",
                "Model=" + SinkUtil.Describe(Model));
        }
    }

    /// <summary>装配级重算事件(宿主:装配文档 AssemblyRecomputeEvents 直属性)。</summary>
    internal sealed class AssemblyRecomputeEventsSink : ISEAssemblyRecomputeEvents
    {
        private readonly EventHub _hub;
        public AssemblyRecomputeEventsSink(EventHub hub) { _hub = hub; }

        public void BeforeRecompute(object theDocument)
        {
            _hub.Record("ISEAssemblyRecomputeEvents", "BeforeRecompute", "");
        }
        public void AfterRecompute(object theDocument)
        {
            _hub.Record("ISEAssemblyRecomputeEvents", "AfterRecompute", "");
        }
        public void AfterAdd(object theDocument, object obj, ObjectType Type)
        {
            _hub.Record("ISEAssemblyRecomputeEvents", "AfterAdd",
                "Type=" + Type + " Obj=" + SinkUtil.Describe(obj));
            _hub.NoteMilestone("AsmAdd", "Type=" + Type);
        }
        public void BeforeDelete(object theDocument, object obj, ObjectType Type)
        {
            _hub.Record("ISEAssemblyRecomputeEvents", "BeforeDelete",
                "Type=" + Type + " Obj=" + SinkUtil.Describe(obj));
        }
        public void AfterModify(object theDocument, object obj, ObjectType Type, seAssemblyEventConstants ModifyType)
        {
            _hub.Record("ISEAssemblyRecomputeEvents", "AfterModify",
                "Type=" + Type + " ModifyType=" + ModifyType + " Obj=" + SinkUtil.Describe(obj));
            _hub.NoteMilestone("AsmModify", "ModifyType=" + ModifyType);
        }
    }

    /// <summary>
    /// 文件 UI 事件的"非侵入"接口声明(IID 与 interop 的 ISEFileUIEvents 完全一致)。
    ///
    /// 为什么不直接用 interop 的 ISEFileUIEvents:PIA 把这 6 个方法声明为 void,
    /// 托管侧无法返回 HRESULT,CLR 自动回 S_OK。而 SE 对这几个"查询事件"的契约是
    /// (官方 SolidEdgeFramework~ISEFileUIEvents~OnFileSaveAsUI.html 的 Remarks 原文):
    ///   · E_NOTIMPL                      → SE 当没人监听,照常弹它自己的对话框
    ///   · S_OK / S_FALSE 且字符串全 NULL → SE 取消命令并且不弹对话框
    ///   · 其它错误码                     → SE 直接中止命令
    /// 旧实现"out 置 null 即不干预"正好落在第二种:等于替用户点了取消,
    /// 表现为"另存为/打开/新建点了没反应"(2026-09-20 实机确诊)。
    ///
    /// 2026-09-22 更正:上表"E_NOTIMPL→放行"对 SE 无效——修复部署确认后实机复测,另存为仍被拦。
    /// SE 的实际行为是"只要 Advise 了就接管",与返回值无关。最终解 = 不订阅
    /// (SubscriptionManager 中 ISEFileUIEvents 已标 Skip=true)。本 sink 仅留作历史/实验用途。
    ///
    /// 本接口取自 Interop.SolidEdge 108.0.0 反射结果:InterfaceIsIUnknown(虚表接口),
    /// 成员顺序即虚表顺序,不可调整;字符串参数为 BSTR。
    /// 仅把返回类型改为 [PreserveSig] int,以便把 E_NOTIMPL 真正还给 SE。
    /// </summary>
    [ComImport]
    [Guid("ecc667a1-a4aa-11d1-aecc-08003616ce02")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileUIEventsNonIntrusive
    {
        [PreserveSig] int OnFileOpenUI(
            [MarshalAs(UnmanagedType.BStr)] out string Filename,
            [MarshalAs(UnmanagedType.BStr)] out string AppendToTitle);

        [PreserveSig] int OnFileSaveAsUI(
            [MarshalAs(UnmanagedType.BStr)] out string Filename,
            [MarshalAs(UnmanagedType.BStr)] out string AppendToTitle);

        [PreserveSig] int OnFileNewUI(
            [MarshalAs(UnmanagedType.BStr)] out string Filename,
            [MarshalAs(UnmanagedType.BStr)] out string AppendToTitle);

        [PreserveSig] int OnFileSaveAsImageUI(
            [MarshalAs(UnmanagedType.BStr)] out string Filename,
            [MarshalAs(UnmanagedType.BStr)] out string AppendToTitle,
            ref int Width, ref int Height, ref SeImageQualityType ImageQuality);

        [PreserveSig] int OnPlacePartUI(
            [MarshalAs(UnmanagedType.BStr)] out string Filename,
            [MarshalAs(UnmanagedType.BStr)] out string AppendToTitle);

        [PreserveSig] int OnCreateInPlacePartUI(
            [MarshalAs(UnmanagedType.BStr)] out string Filename,
            [MarshalAs(UnmanagedType.BStr)] out string AppendToTitle,
            [MarshalAs(UnmanagedType.BStr)] out string Template);
    }

    /// <summary>
    /// 文件 UI 事件 sink(宿主:Application 连接点 / FileUIEvents 属性)。
    /// 守则:只观察、不干预——记录事件后一律返回 E_NOTIMPL,让 SE 照常弹它自己的对话框。
    /// 这是同步回调链路(SE 弹框前会等这个返回值),回调里零 COM、零阻塞。
    /// 2026-09-20 修正:旧实现返回 S_OK+null,实际效果是取消用户命令。
    /// </summary>
    internal sealed class FileUIEventsSink : IFileUIEventsNonIntrusive
    {
        /// <summary>E_NOTIMPL:SE 视作"没有应用监听该事件",照常弹自己的对话框。</summary>
        private const int ENotImpl = unchecked((int)0x80004001);

        private readonly EventHub _hub;
        public FileUIEventsSink(EventHub hub) { _hub = hub; }

        public int OnFileOpenUI(out string Filename, out string AppendToTitle)
        {
            _hub.Record("ISEFileUIEvents", "OnFileOpenUI", "");
            return PassThrough(out Filename, out AppendToTitle);
        }

        public int OnFileSaveAsUI(out string Filename, out string AppendToTitle)
        {
            _hub.Record("ISEFileUIEvents", "OnFileSaveAsUI", "");
            return PassThrough(out Filename, out AppendToTitle);
        }

        public int OnFileNewUI(out string Filename, out string AppendToTitle)
        {
            _hub.Record("ISEFileUIEvents", "OnFileNewUI", "");
            return PassThrough(out Filename, out AppendToTitle);
        }

        public int OnFileSaveAsImageUI(out string Filename, out string AppendToTitle, ref int Width, ref int Height, ref SeImageQualityType ImageQuality)
        {
            _hub.Record("ISEFileUIEvents", "OnFileSaveAsImageUI",
                "W=" + Width + " H=" + Height + " Quality=" + ImageQuality);
            return PassThrough(out Filename, out AppendToTitle);
        }

        public int OnPlacePartUI(out string Filename, out string AppendToTitle)
        {
            _hub.Record("ISEFileUIEvents", "OnPlacePartUI", "");
            return PassThrough(out Filename, out AppendToTitle);
        }

        public int OnCreateInPlacePartUI(out string Filename, out string AppendToTitle, out string Template)
        {
            _hub.Record("ISEFileUIEvents", "OnCreateInPlacePartUI", "");
            Template = null;
            return PassThrough(out Filename, out AppendToTitle);
        }

        /// <summary>统一出口:字符串留空 + E_NOTIMPL = "我不处理,请 SE 走自己的对话框"。</summary>
        private static int PassThrough(out string Filename, out string AppendToTitle)
        {
            Filename = null;
            AppendToTitle = null;
            return ENotImpl;
        }
    }

    /// <summary>应用级事件(宿主:Application 直接连接点)。文档切换时联动触发文档级重订。</summary>
    internal sealed class ApplicationEventsSink : ISEApplicationEvents
    {
        private readonly EventHub _hub;
        public ApplicationEventsSink(EventHub hub) { _hub = hub; }

        public void AfterActiveDocumentChange(object theDocument)
        {
            _hub.Record("ISEApplicationEvents", "AfterActiveDocumentChange", "");
            _hub.NoteMilestone("DocChange", "");   // 台账:会话边界("上一个命令"失效)
            // "每文档一订阅":切文档必须重订文档级事件源(调研记录 §2.1)。
            // 回调铁律:此处零 COM —— 只 PostMessage,由泵线程延迟重订。
            _hub.RequestDocumentRescan();
        }

        public void AfterCommandRun(int theCommandID)
        {
            _hub.Record("ISEApplicationEvents", "AfterCommandRun", "CommandID=" + theCommandID);
            _hub.NoteCommand(theCommandID);   // 台账:命令名反解+落盘 Post 到泵线程(回调零 COM)
        }

        public void AfterDocumentOpen(object theDocument)
        {
            _hub.Record("ISEApplicationEvents", "AfterDocumentOpen", "");
        }

        public void AfterDocumentPrint(object theDocument, int hDC, ref double ModelToDC, ref int Rect)
        {
            _hub.Record("ISEApplicationEvents", "AfterDocumentPrint", "");
        }

        public void AfterDocumentSave(object theDocument)
        {
            _hub.Record("ISEApplicationEvents", "AfterDocumentSave", "");
        }

        public void AfterEnvironmentActivate(object theEnvironment)
        {
            _hub.Record("ISEApplicationEvents", "AfterEnvironmentActivate", "");
            _hub.NoteMilestone("EnvChange", "");   // 台账:会话边界(命令 ID 空间随之更换)
        }

        public void AfterNewDocumentOpen(object theDocument)
        {
            _hub.Record("ISEApplicationEvents", "AfterNewDocumentOpen", "");
        }

        public void AfterNewWindow(object theWindow)
        {
            _hub.Record("ISEApplicationEvents", "AfterNewWindow", "");
        }

        public void AfterWindowActivate(object theWindow)
        {
            _hub.Record("ISEApplicationEvents", "AfterWindowActivate", "");
        }

        public void BeforeCommandRun(int theCommandID)
        {
            _hub.Record("ISEApplicationEvents", "BeforeCommandRun", "CommandID=" + theCommandID);
        }

        public void BeforeDocumentClose(object theDocument)
        {
            _hub.Record("ISEApplicationEvents", "BeforeDocumentClose", "");
        }

        public void BeforeDocumentPrint(object theDocument, int hDC, ref double ModelToDC, ref int Rect)
        {
            _hub.Record("ISEApplicationEvents", "BeforeDocumentPrint", "");
        }

        public void BeforeDocumentSave(object theDocument)
        {
            _hub.Record("ISEApplicationEvents", "BeforeDocumentSave", "");
        }

        public void BeforeEnvironmentDeactivate(object theEnvironment)
        {
            _hub.Record("ISEApplicationEvents", "BeforeEnvironmentDeactivate", "");
        }

        public void BeforeQuit()
        {
            _hub.Record("ISEApplicationEvents", "BeforeQuit", "");
            // SE 要退了;活性检查(WM_TIMER)会探测到并转入重连等待模式
        }

        public void BeforeWindowDeactivate(object theWindow)
        {
            _hub.Record("ISEApplicationEvents", "BeforeWindowDeactivate", "");
        }
    }
}
