using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SolidEdge.Spy.McpServer.Tools;

/// <summary>
/// P2-1 证据契约标准化:可信度档位常量 + 全项目唯一的 COM 异常描述实现。
///
/// 三档语义(P3-1 定稿,2026-10-16 实机验收):
/// - observed:COM 直读的实测值
/// - inferred:读不到,值不可用——必须伴随 *Reason 字段(如 rangeBoxReason)说明原因,不猜
/// - derived :由实测值换算/推导——必须标注 derivedFrom 指明来源字段
///
/// 使用规则(踩过的坑,别再犯):
/// - 可信度只标在【会读不到或会推导】的字段旁,禁止贴在全直读的返回体上
///   (P1-2 验收教训:恒真 confidence 零信息量,反而误导)。
/// - 读不到宁可 null+原因,不填假默认值(0 会被当真);读失败与"真实返回 null"
///   必须可区分(note/Errors 字段)。
/// - 写操作的自证不在此档位体系:走 verify/readBack(se_invoke_member)与
///   体积对账(se_model_build expectvolumedelta)。
/// </summary>
internal static class EvidenceContract
{
	public const string Observed = "observed";
	public const string Inferred = "inferred";
	public const string Derived = "derived";

	/// <summary>
	/// 统一异常描述:展开 InnerException 链 + HRESULT 人话化(P1-1 码表,未收录的回退十六进制)。
	/// 全项目唯一实现——过去 7 个工具各持一份私有副本、行为漂移(有的没接码表),已全部改为委托本实现。
	/// </summary>
	public static string DescribeException(Exception ex)
	{
		if (ex == null)
		{
			return "(未知错误)";
		}
		StringBuilder sb = new StringBuilder();
		Exception cur = ex;
		int depth = 0;
		while (cur != null && depth < 6)
		{
			if (sb.Length > 0)
			{
				sb.Append(" <- 内部: ");
			}
			sb.Append(cur.GetType().Name);
			if (!string.IsNullOrEmpty(cur.Message))
			{
				sb.Append(": ").Append(cur.Message);
			}
			if (cur is COMException comEx && comEx.ErrorCode != 0)
			{
				sb.Append(" (HRESULT=").Append(CodeCatalog.DescribeHResult(comEx.ErrorCode) ?? ("0x" + comEx.ErrorCode.ToString("X8"))).Append(')');
			}
			else if (cur.HResult != 0 && cur.HResult != -2146232828)   // 后者=0x80131600 管理类基异常,没信息量
			{
				sb.Append(" (HRESULT=").Append(CodeCatalog.DescribeHResult(cur.HResult) ?? ("0x" + cur.HResult.ToString("X8"))).Append(')');
			}
			cur = cur.InnerException;
			depth++;
		}
		return sb.ToString();
	}
}
