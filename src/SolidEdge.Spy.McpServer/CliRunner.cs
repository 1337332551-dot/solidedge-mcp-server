using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using SolidEdge.Spy.McpServer.Telemetry;
using SolidEdge.Spy.McpServer.Tools;

namespace SolidEdge.Spy.McpServer;

internal static class CliRunner
{
	public static int Run(string[] args)
	{
		if (args == null || args.Length == 0)
		{
			PrintUsage();
			return 0;
		}
		string text = args[0].ToLowerInvariant();

		// CLI 与 MCP server 是两个进程:这里独立读同一组环境变量,让写入口过同一张档位表(ToolRisk)。
		// 只读模式下 --newpart/--model/--set/--cs 等写命令在连 SE 之前就被拒(见 GateWriteCommand)。
		ToolRisk.Configure(
			Environment.GetEnvironmentVariable("SE_MCP_MODE"),
			Environment.GetEnvironmentVariable("SE_MCP_READONLY"));

		// 写命令门禁:先于建 SolidEdgeContext 执行,只读模式下不碰 COM
		string gateError = GateWriteCommand(text);
		if (gateError != null)
		{
			Console.WriteLine(gateError);
			return 1;
		}

		// --usage 只读本地计量文件,不需要连接 Solid Edge(所以放在 using(context) 之前)
		if (text == "--usage" || text == "-u")
		{
			Console.WriteLine(RunUsageReport(args));
			return 0;
		}

		// 配方清单/校验同样只读本地文件、不碰 COM,也放在 using(context) 之前
		if (text == "--recipes")
		{
			Console.WriteLine(RecipeTools.FormatRecipeList());
			return 0;
		}
		if (text == "--recipe-validate")
		{
			Console.WriteLine(RecipeTools.ValidateForCli((args.Length > 1) ? args[1] : null));
			return 0;
		}
		// 把沙箱里跑通的脚本固化成配方:同样是纯本地文件操作(读 .cs / 写 .json),不碰 COM
		if (text == "--recipe-save")
		{
			Console.WriteLine(RecipeTools.SaveFromScriptForCli(args));
			return 0;
		}

		// 弹窗探针同样不碰 COM(纯 Win32 窗口枚举),放前面:
		// SE 卡在对话框上时,这一条照样能跑出来告诉你"卡在哪个框"。
		if (text == "--dialogs")
		{
			long closeHwnd = 0L;
			bool probeConfirm = false;
			for (int i = 1; i < args.Length; i++)
			{
				if ((args[i] == "--close" || args[i] == "-c") && i + 1 < args.Length)
				{
					long.TryParse(args[++i], out closeHwnd);
				}
				else if (args[i] == "--confirm")
				{
					probeConfirm = true;
				}
			}
			Console.WriteLine(DialogTools.Probe());
			if (closeHwnd > 0)
			{
				if (!probeConfirm)
				{
					Console.WriteLine("拒绝关闭:需同时传 --confirm(关闭对话框有副作用,先确认 buttons/texts 的语义)。");
				}
				else if (ToolRisk.Mode == McpMode.ReadOnly)
				{
					Console.WriteLine("拒绝关闭:当前处于只读模式(SE_MCP_MODE=readonly)。需要时把环境变量 SE_MCP_MODE 设为 full(或删除该变量)后重跑。");
				}
				else
				{
					Console.WriteLine(DialogTools.CloseByHandle(closeHwnd)
						? ("已向 hwnd=" + closeHwnd + " 投递关闭消息,复查如下。")
						: ("投递关闭消息失败(hwnd=" + closeHwnd + " 可能已消失)。"));
					System.Threading.Thread.Sleep(300);
					Console.WriteLine(DialogTools.Probe());
				}
			}
			return 0;
		}

		int result;
		using (SolidEdgeContext context = new SolidEdgeContext())
		{
			try
			{
				// 写命令执行前自动打还原点(2026-10-06;与 MCP 侧 PermissionTap→RestorePointHook 同源)。
				// BeforeWrite 内部按档位过滤(Session/Read 档直接跳过),best-effort 失败不阻断。
				string writeTool = ToolNameOfWriteCommand(text);
				if (writeTool != null)
				{
					try
					{
						new SolidEdge.Spy.McpServer.Tools.RestorePointHook(context).BeforeWrite(writeTool, default);
					}
					catch (Exception rpEx)
					{
						Console.Error.WriteLine("还原点失败(不阻断): " + rpEx.Message);
					}
				}

				switch (text)
				{
				case "--doc":
				case "-d":
					Console.WriteLine(DocumentTools.se_get_document(context));
					result = 0;
					break;
				case "-s":
				case "--selection":
					Console.WriteLine(SelectionTools.se_get_selection(context));
					result = 0;
					break;
				case "--vars":
					Console.WriteLine(VariableTools.se_get_variables(context));
					result = 0;
					break;
				case "-w":
				case "--walk":
					if (args.Length < 2)
					{
						Console.WriteLine("缺少路径参数。用法: solidedge-mcp --walk <路径>");
						result = 1;
					}
					else
					{
						Console.WriteLine(WalkTools.se_walk_object(context, args[1]));
						result = 0;
					}
					break;
				case "-desc":
				case "--describe":
					if (args.Length < 2)
					{
						Console.WriteLine("缺少对象编号。用法: solidedge-mcp --describe <objId>");
						result = 1;
					}
					else
					{
						Console.WriteLine(SelectionTools.se_describe_object(context, args[1]));
						result = 0;
					}
					break;
				case "-p":
				case "--paths":
					if (args.Length < 2)
					{
						Console.WriteLine("缺少参数。用法: solidedge-mcp --paths <objId|对象名>(obj- 开头按句柄查,否则按 Name/Key 全局反查)");
						result = 1;
					}
					else if (args[1].StartsWith("obj-", StringComparison.OrdinalIgnoreCase))
					{
						Console.WriteLine(SelectionTools.se_find_paths(context, args[1]));
						result = 0;
					}
					else
					{
						Console.WriteLine(SelectionTools.se_find_paths(context, null, args[1]));
						result = 0;
					}
					break;
				case "--newpart":
					Console.WriteLine(DocumentTools.se_new_document(context, "part"));
					result = 0;
					break;
				case "--newclose":
				{
					string docTypeArg = (args.Length >= 2) ? args[1] : "part";
					string createdJson = DocumentTools.se_new_document(context, docTypeArg);
					Console.WriteLine(createdJson);
					string handleId = ExtractHandle(createdJson);
					if (handleId == null)
					{
						Console.WriteLine("创建响应里没有句柄,跳过关闭自检。");
						result = 1;
						break;
					}
					Console.WriteLine(DocumentTools.se_close_document(context, handleId, confirm: true));
					result = 0;
					break;
				}
				case "--probe":
					Console.WriteLine(Probe(context));
					result = 0;
					break;
				case "--preview":
				{
					string text4 = null;
					string text5 = null;
					for (int i = 1; i < args.Length; i++)
					{
						string text6 = args[i];
						int result2;
						if (text6 == "-o" || text6 == "--out")
						{
							if (i + 1 < args.Length)
							{
								text5 = args[++i];
							}
						}
						else if (GeometryTools.IsOrientationName(text6) || int.TryParse(text6, out result2))
						{
							if (text4 == null)
							{
								text4 = text6;
							}
						}
						else if (text5 == null)
						{
							text5 = text6;
						}
					}
					Console.WriteLine(GeometryTools.CliCaptureViewport(context, text4 ?? "iso", fit: true, null, 1600, 1200, restoreCamera: true, text5));
					result = 0;
					break;
				}
				case "--geometry":
					Console.WriteLine(GeometryTools.se_read_geometry(context, (args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)) ? args[1] : "refplanes"));
					result = 0;
					break;
				case "--modelsnap":
				{
					// 用法: solidedge-mcp --modelsnap <save|diff|list|drop> <名字> [--tol 1.0]
					string msAction = (args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)) ? args[1] : "list";
					string msName = (args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal)) ? args[2] : null;
					double msTol = 1.0;
					for (int i = 1; i + 1 < args.Length; i++)
					{
						if (args[i].Equals("--tol", StringComparison.Ordinal) && double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double tv))
						{
							msTol = tv;
						}
					}
					Console.WriteLine(ModelStateSnapshotTools.se_model_state_snapshot(context, msAction, msName, msTol, 50, null));
					result = 0;
					break;
				}
				case "--modelstate":
				{
					// 用法: solidedge-mcp --modelstate [summary|faces] [--nofeatures] [--noarea] [--maxfaces N]
					string msDetail = "summary";
					if (args.Length > 1 && (args[1].Equals("faces", StringComparison.OrdinalIgnoreCase)
						|| args[1].Equals("summary", StringComparison.OrdinalIgnoreCase)))
					{
						msDetail = args[1];
					}
					int msMax = 300;
					for (int i = 1; i + 1 < args.Length; i++)
					{
						if (args[i].Equals("--maxfaces", StringComparison.Ordinal) && int.TryParse(args[i + 1], out int mf) && mf > 0)
						{
							msMax = mf;
						}
					}
					Console.WriteLine(ModelStateTools.se_read_model_state(
						context, msDetail, !HasFlag(args, "--noarea"), !HasFlag(args, "--nofeatures"), null, msMax,
						HasFlag(args, "--edges")));
					result = 0;
					break;
				}
				case "--validate":
				{
					// P0:features 文件静态校验冒烟入口(纯函数,不碰 COM——SE 没启动也能跑)。
					// 用法: solidedge-mcp --validate <features.json> [--assumeStock]
					if (args.Length < 2)
					{
						Console.WriteLine("缺少 JSON 文件路径。用法: solidedge-mcp --validate <features.json> [--assumeStock]");
						result = 1;
						break;
					}
					Console.WriteLine(ModelingTools.se_validate_features(context, null, HasFlag(args, "--assumeStock"), args[1],
						ParseSetArgs(args)));
					result = 0;
					break;
				}
				case "--model":
				{
					if (args.Length < 2)
					{
						Console.WriteLine("缺少 JSON 文件路径。用法: solidedge-mcp --model <features.json> [objId]");
						result = 1;
						break;
					}
					// P0:文件读取/格式校验与 MCP 侧 featuresFile 共用同一份实现,错误文案也一致。
					string loadError;
					JsonElement[] fileFeatures = ModelingTools.LoadFeaturesFile(args[1], out loadError);
					if (loadError != null)
					{
						Console.WriteLine(loadError);
						result = 1;
						break;
					}
					// 用法: --model <features.json> [objId] [--assumeStock] [--topodiff]
					string modelObjId = null;
					for (int ma = 2; ma < args.Length; ma++)
					{
						if (!args[ma].StartsWith("--", StringComparison.Ordinal)) { modelObjId = args[ma]; break; }
					}
					Console.WriteLine(ModelingTools.se_model_build(context, fileFeatures, modelObjId,
						null, HasFlag(args, "--assumeStock"), false, false, HasFlag(args, "--topodiff"),
						null, ParseSetArgs(args), HasFlag(args, "--slim"),
						IntArg(args, "--limit"), IntArg(args, "--cursor")));
					result = 0;
					break;
				}
				case "--assembly":
				{
					// 装配批量操作冒烟入口,支持 --assembly <ops.json> [objId] 与 --assembly --validate <ops.json>
					if (args.Length < 2)
					{
						Console.WriteLine("缺少参数。用法: solidedge-mcp --assembly <ops.json> [objId]  或  solidedge-mcp --assembly --validate <ops.json>(纯校验不碰 COM)");
						result = 1;
						break;
					}
					int fileArg = 1;
					bool validateOnly = args.Length > 1 && args[1].Equals("--validate", StringComparison.Ordinal);
					if (validateOnly)
					{
						fileArg = 2;
						if (args.Length < 3)
						{
							Console.WriteLine("缺少 JSON 文件路径。用法: solidedge-mcp --assembly --validate <ops.json>");
							result = 1;
							break;
						}
					}
					if (fileArg >= args.Length)
					{
						Console.WriteLine("缺少 JSON 文件路径。");
						result = 1;
						break;
					}
					JsonElement asmRoot = JsonDocument.Parse(File.ReadAllText(args[fileArg])).RootElement;
					JsonElement asmOps = asmRoot;
					if (asmRoot.ValueKind == JsonValueKind.Object && asmRoot.TryGetProperty("ops", out var opsValue))
					{
						asmOps = opsValue;
					}
					if (asmOps.ValueKind != JsonValueKind.Array)
					{
						Console.WriteLine("JSON 文件必须是数组或 {\"ops\":[...]}。");
						result = 1;
						break;
					}
					List<JsonElement> asmList = new List<JsonElement>();
					foreach (JsonElement item in asmOps.EnumerateArray())
					{
						asmList.Add(item);
					}
					if (validateOnly)
					{
						// 纯本地校验(不碰 COM):复用 AssemblySpec,输出与 dryRun 一致
						var asmReport = AssemblySpec.Validate(asmList.ToArray());
						Console.WriteLine(asmReport.ToJson());
						result = asmReport.HasError ? 1 : 0;
						break;
					}
					string asmObjId = null;
					int objArg = fileArg + 1;
					if (objArg < args.Length && !args[objArg].StartsWith("--", StringComparison.Ordinal))
					{
						asmObjId = args[objArg];
					}
					Console.WriteLine(AssemblyBuildTools.se_assembly_build(context, asmList.ToArray(), asmObjId, false, HasFlag(args, "--confirm")));
					result = 0;
					break;
				}
				case "--assemblyquery":
				{
					// 装配只读查询冒烟:--assemblyquery [mode] [objId]
					string asmMode = (args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)) ? args[1] : "occurrences";
					string asmQObjId = null;
					for (int a = 2; a < args.Length; a++)
					{
						if (!args[a].StartsWith("--", StringComparison.Ordinal)) { asmQObjId = args[a]; break; }
					}
					Console.WriteLine(AssemblyQueryTools.se_assembly_query(context, asmMode, asmQObjId));
					result = 0;
					break;
				}
				case "--set":
				{
					if (args.Length < 3)
					{
						Console.WriteLine("缺少参数。用法: solidedge-mcp --set <member> <value>");
						result = 1;
						break;
					}
					string text2 = SelectionTools.se_get_selection(context);
					string text3 = ExtractFirstObjId(text2);
					if (text3 == null)
					{
						Console.WriteLine("未选中对象: " + text2);
						result = 1;
					}
					else
					{
						Console.WriteLine(InvokeTools.se_invoke_member(context, text3, args[1], new string[1] { args[2] }, propertySet: true));
						result = 0;
					}
					break;
				}
				case "--vars-set":
				{
					// 用法: solidedge-mcp --vars-set <变量名> <公式(必须带单位)>
					if (args.Length < 3)
					{
						Console.WriteLine("用法: solidedge-mcp --vars-set <变量名> <公式>   例: --vars-set Flange_OD \"240 mm\"");
						result = 1;
						break;
					}
					Console.WriteLine(VariableTools.se_get_variables(context, "set", args[1], args[2]));
					result = 0;
					break;
				}
				case "--feature-param":
				{
					// 用法: solidedge-mcp --feature-param <特征名|obj-N> <参数> <值>
					if (args.Length < 4)
					{
						Console.WriteLine("用法: solidedge-mcp --feature-param <特征名|obj-N> <参数> <值>   例: --feature-param Pattern_1 count 12");
						result = 1;
						break;
					}
					Console.WriteLine(FeatureParameterTools.se_set_feature_parameter(context, args[1], args[2], args[3]));
					result = 0;
					break;
				}
				case "--verify":
				{
					// 用法: solidedge-mcp --verify od=220,bore=116,holeCount=8
					if (args.Length < 2)
					{
						Console.WriteLine("用法: solidedge-mcp --verify od=220,bore=116,thickness=25,pcd=182,holeCount=8,holeDia=18");
						result = 1;
						break;
					}
					Console.WriteLine(VerificationTools.se_verify_model(context, BuildVerifyJson(args[1]), 0.05, 1));
					result = 0;
					break;
				}
				case "--save":
				{
					// 用法: solidedge-mcp --save <完整路径> [overwrite]
					if (args.Length < 2)
					{
						Console.WriteLine("用法: solidedge-mcp --save <完整路径> [overwrite]");
						result = 1;
						break;
					}
					bool cliOverwrite = args.Length > 2 && args[2].Equals("overwrite", StringComparison.OrdinalIgnoreCase);
					Console.WriteLine(DocumentTools.se_save_document(context, args[1], null, cliOverwrite));
					result = 0;
					break;
				}
				case "--newsave":
				{
					// 用法: solidedge-mcp --newsave <完整路径> [overwrite]
					// 为什么要 new+save 合一条:追踪表是本进程内存的,CLI 每次独立进程,
					// 单独 --save 永远看不到"另一个进程里建的文档"(会被安全策略拒绝)。
					if (args.Length < 2)
					{
						Console.WriteLine("用法: solidedge-mcp --newsave <完整路径> [overwrite]");
						result = 1;
						break;
					}
					bool nsOverwrite = args.Length > 2 && args[2].Equals("overwrite", StringComparison.OrdinalIgnoreCase);
					Console.WriteLine(DocumentTools.se_new_document(context, "part"));
					Console.WriteLine(DocumentTools.se_save_document(context, args[1], null, nsOverwrite));
					result = 0;
					break;
				}
				case "--openclose":
					if (args.Length < 2)
					{
						Console.WriteLine("缺少路径参数。用法: solidedge-mcp --openclose <文件路径>");
						result = 1;
						break;
					}
					Console.WriteLine(DocumentTools.se_open_document(context, args[1]));
					Console.WriteLine(DocumentTools.se_close_document(context, args[1]));
					result = 0;
					break;
				case "--cs":
			{
				if (args.Length < 2)
				{
					Console.WriteLine("缺少脚本路径。用法: solidedge-mcp --cs <脚本.cs> [超时秒] [脚本参数 k=v ...]");
					result = 1;
					break;
				}
				string csFile = args[1];
				if (!File.Exists(csFile))
				{
					Console.WriteLine("脚本文件不存在: " + csFile);
					result = 1;
					break;
				}
				string scriptName = System.Text.RegularExpressions.Regex.Replace(
					Path.GetFileNameWithoutExtension(csFile), "[^A-Za-z0-9_\\-]", "_");
				string scriptCode = File.ReadAllText(csFile);
				int scriptTimeout = (args.Length >= 3 && int.TryParse(args[2], out int t) && t > 0) ? t : 60;
				// 超时秒之后的参数原样透传给脚本 Main(string[] args)(脚本库的 params: 约定靠它)
				string[] scriptArgs = null;
				if (args.Length > 3)
				{
					scriptArgs = new string[args.Length - 3];
					Array.Copy(args, 3, scriptArgs, 0, scriptArgs.Length);
				}
				Console.WriteLine(ScriptTools.se_script_run(context, scriptName, scriptCode, timeoutSec: scriptTimeout, scriptArgs: scriptArgs));
				result = 0;
				break;
			}
			case "--viewctx":
					Console.WriteLine(ViewContextTools.se_view_context(context, "map", (args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)) ? args[1] : null, null, "view", null, null));
					result = 0;
					break;
				case "--batchread":
				{
					// 用法: solidedge-mcp --batchread <成员1,成员2,...> [objId]   (objId 缺省=当前选中对象/活动文档)
					string membersArg = (args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)) ? args[1] : null;
					if (string.IsNullOrWhiteSpace(membersArg))
					{
						Console.WriteLine("缺少成员名。用法: solidedge-mcp --batchread <成员1,成员2,...> [objId]");
						result = 1;
						break;
					}
					string batchTarget = (args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal)) ? args[2] : ExtractFirstObjId(SelectionTools.se_get_selection(context));
					if (string.IsNullOrWhiteSpace(batchTarget))
					{
						Console.WriteLine("没拿到对象句柄(选择集为空且没有活动文档?)。");
						result = 1;
						break;
					}
					Console.WriteLine(BatchReadTools.se_batch_read(context, new string[1] { batchTarget }, membersArg.Split(','), includeFingerprint: true, unitNote: true));
					result = 0;
					break;
				}
				case "--snap":
				{
					// 用法: solidedge-mcp --snap <save|diff|list|drop> <名字> [objId]
					string snapAction = (args.Length > 1) ? args[1] : null;
					string snapName = (args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal)) ? args[2] : null;
					string snapTarget = (args.Length > 3 && !args[3].StartsWith("--", StringComparison.Ordinal)) ? args[3] : null;
					if (string.Equals(snapAction, "list", StringComparison.OrdinalIgnoreCase))
					{
						Console.WriteLine(SnapshotTools.se_snapshot_diff(context, "list", null, "objects", null, null, 1.0, 500, null));
						result = 0;
						break;
					}
					if (string.IsNullOrWhiteSpace(snapAction) || string.IsNullOrWhiteSpace(snapName))
					{
						Console.WriteLine("用法: solidedge-mcp --snap <save|diff|list|drop> <名字> [objId]");
						result = 1;
						break;
					}
					if (snapTarget == null)
					{
						snapTarget = ExtractFirstObjId(SelectionTools.se_get_selection(context));
					}
					Console.WriteLine(SnapshotTools.se_snapshot_diff(context, snapAction, snapName, "objects", snapTarget, null, 1.0, 500, null));
					result = 0;
					break;
				}
				case "--recipe-run":
				{
					// 用法: solidedge-mcp --recipe-run <配方名> [k=v ...] [--confirm]
					// 未显式给 target= 时,自动取当前选中对象(空选中时 se_get_selection 会回退登记 ActiveDocument)。
					if (args.Length < 2)
					{
						Console.WriteLine("用法: solidedge-mcp --recipe-run <配方名> [参数 k=v ...] [--confirm]");
						result = 1;
						break;
					}
					string recipeName = args[1];
					List<string> recipeArgs = new List<string>();
					bool hasTarget = false;
					bool recipeConfirm = false;
					for (int i = 2; i < args.Length; i++)
					{
						string a = args[i];
						if (string.Equals(a, "--confirm", StringComparison.OrdinalIgnoreCase))
						{
							recipeConfirm = true;
							continue;
						}
						if (a.StartsWith("--", StringComparison.Ordinal))
						{
							continue;
						}
						recipeArgs.Add(a);
						if (a.StartsWith("target=", StringComparison.OrdinalIgnoreCase))
						{
							hasTarget = true;
						}
					}
					if (!hasTarget)
					{
						string autoTarget = ExtractFirstObjId(SelectionTools.se_get_selection(context));
						if (!string.IsNullOrWhiteSpace(autoTarget))
						{
							recipeArgs.Insert(0, "target=" + autoTarget);
						}
					}
					Console.WriteLine(RecipeTools.se_recipe_run(context, recipeName, recipeArgs.ToArray(), recipeConfirm));
					result = 0;
					break;
				}
				case "/?":
				case "-h":
				case "--help":
					PrintUsage();
					result = 0;
					break;
				default:
					Console.WriteLine("未知命令: " + args[0]);
					PrintUsage();
					result = 1;
					break;
				}
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine("CLI 执行失败: " + ex.Message);
				result = 1;
			}
		}
		return result;
	}

	private static string ExtractHandle(string json)
	{
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(json);
			if (jsonDocument.RootElement.TryGetProperty("handle", out var value) && value.ValueKind == JsonValueKind.String)
			{
				return value.GetString();
			}
		}
		catch
		{
		}
		return null;
	}

	/// <summary>
	/// CLI 写命令 → 工具档位门禁。只读模式下返回拒绝说明(带切换指路),放行返回 null。
	/// 与 MCP 侧共用 ToolRisk 登记表;--dialogs 的关闭分支在 Run 内单独处理(有副作用但不是注册工具)。
	/// </summary>
	private static string GateWriteCommand(string command)
	{
		if (ToolRisk.Mode == McpMode.Full)
		{
			return null;
		}
		string tool = ToolNameOfWriteCommand(command);
		if (tool == null)
		{
			return null; // 其余 CLI 命令均为只读查询或本地文件操作
		}
		// ⚠️ Check() 放行时返回 null;直接拼字符串会把 null 变成 "[CLI] " 这个【非 null】结果,
		//    被上层当成"有拒绝理由",于是 engineer 模式下所有写命令都被空消息误拒(2026-10-01 实测)。
		string reason = ToolRisk.Check(tool);
		return (reason == null) ? null : "[CLI] " + reason;
	}

	/// <summary>CLI 写命令 → 对应 MCP 工具名(还原点 hook 与档位门禁共用);非写命令返回 null。</summary>
	internal static string ToolNameOfWriteCommand(string command)
	{
		switch (command)
		{
		case "--newpart":
			return "se_new_document";
		case "--newclose":
			return "se_close_document";
		case "--model":
			return "se_model_build";
		case "--set":
			return "se_invoke_member";
		case "--openclose":
			return "se_open_document";
		case "--vars-set":
			return "se_get_variables";
		case "--feature-param":
			return "se_set_feature_parameter";
		case "--verify":
			return "se_verify_model";
		case "--save":
		case "--newsave":
			return "se_save_document";
		case "--cs":
			return "se_script_run";
		case "--recipe-run":
			return "se_recipe_run";
		// 2026-10-25 审计(F-09):--assembly 是装配写命令,补映射——否则还原点不打、readonly 的 CLI 档位门禁会跳过(此前仅靠 AssemblyBuildTools 内层 ReadOnlyEnabled 兜底)。
		case "--assembly":
			return "se_assembly_build";
		default:
			return null;
		}
	}

	private static string Probe(SolidEdgeContext context)
	{
		try
		{
			return context.Invoke(delegate
			{
				dynamic val = ((dynamic)context.GetApplication()).ActiveDocument;
				dynamic val2 = val.RefPlanes;
				int num = Convert.ToInt32(val2.Count);
				List<object> list = new List<object>();
				for (int i = 1; i <= num; i++)
				{
					object obj = val2.Item(i);
					string name = "(参考面)";
					try
					{
						name = (string)obj.GetType().InvokeMember("DisplayName", BindingFlags.GetProperty, null, obj, null);
					}
					catch
					{
					}
					try
					{
						object obj3 = obj.GetType().InvokeMember("RootPoint", BindingFlags.GetProperty, null, obj, null);
						double x = Convert.ToDouble(obj3.GetType().InvokeMember("X", BindingFlags.GetProperty, null, obj3, null));
						double y = Convert.ToDouble(obj3.GetType().InvokeMember("Y", BindingFlags.GetProperty, null, obj3, null));
						double z = Convert.ToDouble(obj3.GetType().InvokeMember("Z", BindingFlags.GetProperty, null, obj3, null));
						list.Add(new { name, x, y, z });
					}
					catch (Exception ex2)
					{
						list.Add(new
						{
							name = name,
							error = ex2.Message
						});
					}
				}
				double[] modelRangeBox = null;
				try
				{
					dynamic val3 = val.Models;
					object obj4 = val3.Item(1);
					double[] array = (double[])obj4.GetType().InvokeMember("RangeBox", BindingFlags.GetProperty, null, obj4, null);
					modelRangeBox = new double[6]
					{
						array[0],
						array[1],
						array[2],
						array[3],
						array[4],
						array[5]
					};
				}
				catch
				{
				}
				return JsonSerializer.Serialize(new
				{
					status = "ok",
					refPlanes = list,
					modelRangeBox = modelRangeBox
				});
			});
		}
		catch (Exception ex)
		{
			return JsonSerializer.Serialize(new
			{
				status = "error",
				message = ex.Message
			});
		}
	}

	/// <summary>
	/// CLI 的 --set name=value(可重复)→ sets 对象。值可以是数字,也可以是表达式(含 {{名字}} 引用),
	/// 与 MCP 侧 sets 参数同语义——CLI 只是把字符串原样收进来,求值仍由 BuildPipeline 统一负责。
	/// </summary>
	private static JsonElement? ParseSetArgs(string[] args)
	{
		if (args == null) return null;

		// --sets-file <path>:整份参数表(对象或行数组)直接从文件读——大表不必挤在命令行里。
		for (int i = 0; i < args.Length; i++)
		{
			if (!string.Equals(args[i], "--sets-file", StringComparison.Ordinal) || i + 1 >= args.Length) continue;
			string path = args[i + 1];
			if (!File.Exists(path))
			{
				Console.WriteLine("找不到 sets 文件: " + path);
				return null;
			}
			using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
			{
				return doc.RootElement.Clone();
			}
		}

		var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		for (int i = 0; i + 1 < args.Length; i++)
		{
			if (!string.Equals(args[i], "--set", StringComparison.Ordinal)) continue;
			string kv = args[i + 1];
			int eq = kv.IndexOf('=');
			if (eq <= 0) continue;
			dict[kv.Substring(0, eq)] = kv.Substring(eq + 1);
		}
		if (dict.Count == 0) return null;
		return JsonSerializer.SerializeToElement(dict);
	}

	/// <summary>--xxx N 形式的整数参数(缺省 0)。</summary>
	private static int IntArg(string[] args, string flag)
	{
		if (args == null) return 0;
		for (int i = 0; i + 1 < args.Length; i++)
		{
			if (string.Equals(args[i], flag, StringComparison.Ordinal) && int.TryParse(args[i + 1], out int v))
				return v;
		}
		return 0;
	}

	private static bool HasFlag(string[] args, string flag)
	{
		if (args == null) return false;
		foreach (string a in args)
		{
			if (string.Equals(a, flag, StringComparison.Ordinal)) return true;
		}
		return false;
	}

	private static string ExtractFirstObjId(string json)
	{
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(json);
			if (jsonDocument.RootElement.TryGetProperty("items", out var value))
			{
				foreach (JsonElement item in value.EnumerateArray())
				{
					if (item.TryGetProperty("id", out var value2) && value2.ValueKind == JsonValueKind.String)
					{
						return value2.GetString();
					}
				}
			}
			if (jsonDocument.RootElement.TryGetProperty("objectId", out var value3) && value3.ValueKind == JsonValueKind.String)
			{
				return value3.GetString();
			}
		}
		catch
		{
		}
		return null;
	}

	/// <summary>
	/// `--usage [天数] [--all] [--by day] [--sort calls|ms|last|fail]`
	/// 读 %LOCALAPPDATA%\SolidEdgeSpy\tool-usage.jsonl 聚合输出(纯本地,不连 Solid Edge)。
	/// </summary>
	private static string RunUsageReport(string[] args)
	{
		int days = 7;
		bool byDay = false;
		string sort = "calls";
		for (int i = 1; i < args.Length; i++)
		{
			string a = args[i];
			if (a == "--by" && i + 1 < args.Length)
			{
				byDay = args[++i].Equals("day", StringComparison.OrdinalIgnoreCase);
			}
			else if (a == "--sort" && i + 1 < args.Length)
			{
				sort = args[++i];
			}
			else if (a == "--all")
			{
				days = 0;
			}
			else
			{
				int d;
				if (int.TryParse(a, out d))
				{
					days = d;
				}
			}
		}
		return ToolUsage.Report(days, sort, byDay);
	}

	/// <summary>
	/// 把 "od=220,bore=116,holeCount=8" 转成 JsonElement(CLI 冒烟用,免得为一次自检写 JSON 文件)。
	/// 数字按数字写,其它按字符串;非法值原样带引号传下去,由工具侧报错。
	/// </summary>
	private static JsonElement BuildVerifyJson(string spec)
	{
		var sb = new System.Text.StringBuilder("{");
		bool first = true;
		if (!string.IsNullOrWhiteSpace(spec))
		{
			foreach (string part in spec.Split(','))
			{
				if (string.IsNullOrWhiteSpace(part)) continue;
				int eq = part.IndexOf('=');
				if (eq <= 0) continue;
				string key = part.Substring(0, eq).Trim().Replace("\"", "");
				string val = part.Substring(eq + 1).Trim();
				if (!first) sb.Append(',');
				first = false;
				sb.Append('"').Append(key).Append("\":");
				double d;
				if (double.TryParse(val, System.Globalization.NumberStyles.Float,
						System.Globalization.CultureInfo.InvariantCulture, out d))
				{
					sb.Append(val);
				}
				else
				{
					sb.Append('"').Append(val.Replace("\"", "")).Append('"');
				}
			}
		}
		sb.Append('}');
		using (JsonDocument document = JsonDocument.Parse(sb.ToString()))
		{
			return document.RootElement.Clone();
		}
	}

	private static void PrintUsage()
	{
		Console.WriteLine("Solid Edge Spy MCP - 命令行查询工具");
		Console.WriteLine("用法:");
		Console.WriteLine("  solidedge-mcp --doc                         取当前活动文档信息");
		Console.WriteLine("  solidedge-mcp --selection                   取当前选中对象列表");
		Console.WriteLine("  solidedge-mcp --walk <路径>                  按对象结构路径取对象");
		Console.WriteLine("  solidedge-mcp --describe <objId>             详细描述对象");
		Console.WriteLine("  solidedge-mcp --validate <features.json> [--assumeStock]  声明式建模的静态校验(纯本地,不碰 COM)");
		Console.WriteLine("  solidedge-mcp --model <json> [objId] [--assumeStock]  声明式建模(features JSON;已有实体上只除料时加 --assumeStock)");
		Console.WriteLine("  solidedge-mcp --assembly <ops.json> [objId] [--confirm]  装配批量操作(ops JSON;加 --validate 只校验不碰 COM)");
		Console.WriteLine("  solidedge-mcp --assemblyquery [mode] [objId]  装配只读查询(mode: occurrences/relations/bom/all)");
		Console.WriteLine("  solidedge-mcp --paths <objId>                查找对象可达路径");
		Console.WriteLine("  solidedge-mcp --geometry [target]            读几何定位(默认 refplanes;支持 model / obj-N)");
			Console.WriteLine("  solidedge-mcp --modelstate [summary|faces] [--nofeatures] [--noarea] [--maxfaces N] [--edges]  读模型状态(拓扑结构+特征树,替代截图)");
		Console.WriteLine("  solidedge-mcp --modelsnap <save|diff|list|drop> <名字> [--tol 1.0]  拓扑快照与面级差异对比");
		Console.WriteLine("  solidedge-mcp --preview [视角] [-o 路径]      视口截图(视角: iso/top/front/back/left/right/bottom/current,默认 iso)");
		Console.WriteLine("  solidedge-mcp --vars-set <变量名> <公式>      改变量并回读校验(se_get_variables action=set 冒烟,公式须带单位)");
		Console.WriteLine("  solidedge-mcp --feature-param <特征> <参数> <值>  改特征参数如阵列孔数(se_set_feature_parameter 冒烟)");
		Console.WriteLine("  solidedge-mcp --verify od=220,bore=116,...    尺寸自检(se_verify_model 冒烟,逗号分隔 kv)");
		Console.WriteLine("  solidedge-mcp --save <路径> [overwrite]       落盘并回读确认(se_save_document 冒烟)");
		Console.WriteLine("  solidedge-mcp --newsave <路径> [overwrite]    新建+立即落盘(同进程内完成,CLI 追踪表自检)");
		Console.WriteLine("  solidedge-mcp --openclose <文件路径>          打开并立即关闭文档(se_open/close_document 自检)");
		Console.WriteLine("  solidedge-mcp --newpart                       新建零件文档(进追踪表)");
		Console.WriteLine("  solidedge-mcp --newclose [类型]                新建并立即关闭文档(创建追踪自检)");
		Console.WriteLine("  solidedge-mcp --cs <脚本.cs> [超时秒]           编译并运行一次性 C# 脚本(se_script_run 冒烟)");
		Console.WriteLine("  solidedge-mcp --viewctx [图页名]               视图↔编辑上下文映射判定(se_view_context 冒烟)");
		Console.WriteLine("  solidedge-mcp --batchread <成员1,成员2,...> [objId]  批量读属性(se_batch_read 冒烟,objId 缺省=选中对象)");
		Console.WriteLine("  solidedge-mcp --snap <save|diff|list|drop> <名字> [objId]  对象快照/差异(se_snapshot_diff 冒烟)");
		Console.WriteLine("  solidedge-mcp --usage [天数] [--all] [--by day] [--sort calls|ms|last|fail]  工具调用统计(默认近 7 天,按工具)");
		Console.WriteLine("  solidedge-mcp --recipes                      列出全部配方(名称/状态/参数/来源目录,纯本地)");
		Console.WriteLine("  solidedge-mcp --recipe-validate <名字|路径>   静态校验一条配方(不碰 COM)");
		Console.WriteLine("  solidedge-mcp --recipe-save <脚本名> [--input name:type[:default][:desc]] [--dry]  把沙箱里跑通的脚本固化成配方(不碰 COM)");
		Console.WriteLine("  solidedge-mcp --recipe-run <配方名> [k=v ...] [--confirm]  执行一条配方(未给 target= 时自动取当前选中/活动文档)");
		Console.WriteLine("  solidedge-mcp --dialogs [--close <hwnd> --confirm]   检测 SE 当前挂着的对话框(不碰 COM,SE 卡住也能跑)");
		Console.WriteLine("示例:");
		Console.WriteLine("  solidedge-mcp --walk \"Application.ActiveDocument.ProfileSets.Item(\\\"ProfileSet_1\\\")\"");
	}
}
