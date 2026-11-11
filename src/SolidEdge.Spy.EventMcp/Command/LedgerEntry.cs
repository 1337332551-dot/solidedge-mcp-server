using System;

namespace SolidEdge.Spy.EventMcp.Command
{
    /// <summary>
    /// 台账条目:用户操作习惯的一条记录。
    /// Kind 决定其余字段的含义:
    ///   Command          命令执行(AfterCommandRun) —— CommandId/CommandName 有效,推荐算法的主数据
    ///   FeatureAdded     新建特征(AfterFeatureIsAdded)
    ///   FeatureModified  修改特征(AfterFeatureIsModified)
    ///   AsmAdd           装配加零件(AfterAdd)
    ///   AsmModify        装配改零件(AfterModify)
    ///   Save             保存(AfterSave)
    ///   DocChange        切换活动文档(会话边界:此后"上一个命令"失效)
    ///   EnvChange        切换环境(零件/装配/工程图...;会话边界,且命令 ID 空间随之更换)
    /// Environment/Doc 是事件发生时 hub 维护的快照(sink 回调里零 COM 的产物)。
    /// </summary>
    public sealed class LedgerEntry
    {
        public DateTime Time { get; set; }
        public string Kind { get; set; }
        public int CommandId { get; set; }
        public string CommandName { get; set; }
        public string Environment { get; set; }
        public string Doc { get; set; }
        public string Detail { get; set; }
    }
}
