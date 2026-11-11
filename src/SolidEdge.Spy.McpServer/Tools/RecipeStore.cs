using System;
using System.Collections.Generic;
using System.IO;

namespace SolidEdge.Spy.McpServer.Tools;

/// <summary>一条已定位的配方文件。</summary>
internal sealed class RecipeRef
{
	internal string Name;

	internal string Path;

	internal string Directory;
}

/// <summary>
/// 配方定位器:按搜索路径顺序查找 &lt;配方名&gt;.json。
///
/// 搜索路径(优先级从高到低):
///   ① 环境变量 SE_MCP_RECIPES_DIR(分号分隔,可多个)
///   ② 从 exe 所在目录向上找到的 recipes 目录(本仓库布局开箱即用,见 FindAdjacentRecipesDir)
///   ③ 兜底 %LOCALAPPDATA%\SolidEdgeSpy\recipes
///
/// 刻意【不】靠当前工作目录(而不是 exe 目录)去找:MCP 是常驻进程,它的 CWD 由客户端决定,不可靠。
/// 同名配方按目录顺序"先命中优先",命中结果自带来源目录 —— 避免"改了 A 目录没生效,实际跑的是 B 目录"的排查地狱。
///
/// 以 "_" 开头的文件视为草稿/模板,不登记、不可按名执行(但 --recipe-validate 可以直接传路径校验)。
/// </summary>
internal static class RecipeStore
{
	private static readonly object Sync = new object();

	private static string[] _dirs;

	internal static string DefaultDir()
	{
		return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolidEdgeSpy", "recipes");
	}

	/// <summary>
	/// 新配方落盘目录。默认写用户兜底目录(不污染 git 仓库);
	/// project=true 且能找到仓库 recipes 目录时才写仓库。
	///
	/// 注意搜索优先级是"仓库 → 用户目录,先命中优先",所以仓库里已有同名配方时,
	/// 写到用户目录的那条【不会生效】—— 调用方需要自己处理覆盖(见 --recipe-save --force)。
	/// </summary>
	internal static string SaveDir(bool project)
	{
		if (project)
		{
			string adjacent = FindAdjacentRecipesDir();
			if (adjacent != null)
			{
				return adjacent;
			}
		}
		return DefaultDir();
	}

	/// <summary>
	/// 从 exe 所在目录向上找名为 recipes 的目录(本仓库布局:src/…/bin/Release/net8.0-windows/ → 仓库根/recipes)。
	/// 这样本仓库开箱即用,不必去改 MCP 客户端配置。
	///
	/// 注意:依据是 <see cref="AppContext.BaseDirectory"/>(exe 位置,可靠),**不是**当前工作目录 ——
	/// MCP 是常驻进程,CWD 由客户端决定,不可靠。
	/// 只在目录内确实存在 *.json 时才采用,避免误认无关目录。
	/// </summary>
	private static string FindAdjacentRecipesDir()
	{
		try
		{
			DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);
			for (int i = 0; i < 8 && dir != null; i++)
			{
				string candidate = System.IO.Path.Combine(dir.FullName, "recipes");
				if (Directory.Exists(candidate))
				{
					string[] files;
					try
					{
						files = Directory.GetFiles(candidate, "*.json");
					}
					catch
					{
						files = new string[0];
					}
					if (files.Length > 0)
					{
						return candidate;
					}
				}
				dir = dir.Parent;
			}
		}
		catch
		{
		}
		return null;
	}

	private static bool ContainsDir(List<string> dirs, string dir)
	{
		foreach (string d in dirs)
		{
			if (string.Equals(d, dir, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	internal static string[] SearchDirs()
	{
		lock (Sync)
		{
			if (_dirs != null)
			{
				return _dirs;
			}
			List<string> list = new List<string>();
			string env = Environment.GetEnvironmentVariable("SE_MCP_RECIPES_DIR");
			if (!string.IsNullOrWhiteSpace(env))
			{
				foreach (string d in env.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
				{
					string t = d.Trim();
					if (t.Length > 0)
					{
						list.Add(t);
					}
				}
			}
			string adjacent = FindAdjacentRecipesDir();
			if (adjacent != null && !ContainsDir(list, adjacent))
			{
				list.Add(adjacent);
			}
			list.Add(DefaultDir());
			_dirs = list.ToArray();
			return _dirs;
		}
	}

	/// <summary>
	/// 测试专用:清空 <see cref="SearchDirs"/> 的静态缓存(_dirs),使下一次调用重新读环境变量。
	/// 单元测试在同一 xUnit 进程里逐用例切换 SE_MCP_RECIPES_DIR 时必须先调这个,
	/// 否则第一次调用后缓存永不过期,后续用例拿到旧目录(假失败)。运行期无调用方。
	/// </summary>
	internal static void ResetForTests()
	{
		lock (Sync)
		{
			_dirs = null;
		}
	}

	/// <summary>列出全部可执行配方(同名先命中优先)。</summary>
	internal static List<RecipeRef> List()
	{
		List<RecipeRef> result = new List<RecipeRef>();
		HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string dir in SearchDirs())
		{
			if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
			{
				continue;
			}
			string[] files;
			try
			{
				files = Directory.GetFiles(dir, "*.json");
			}
			catch
			{
				continue;
			}
			Array.Sort(files, StringComparer.OrdinalIgnoreCase);
			foreach (string f in files)
			{
				string name = System.IO.Path.GetFileNameWithoutExtension(f);
				if (name.StartsWith("_", StringComparison.Ordinal))
				{
					continue;
				}
				if (!seen.Add(name))
				{
					continue;
				}
				result.Add(new RecipeRef
				{
					Name = name,
					Path = f,
					Directory = dir
				});
			}
		}
		return result;
	}

	/// <summary>按名查找;传的是已存在的文件路径时也接受。</summary>
	internal static RecipeRef Find(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return null;
		}
		string want = name.Trim();
		if (want.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
		{
			want = want.Substring(0, want.Length - 5);
		}
		foreach (RecipeRef r in List())
		{
			if (string.Equals(r.Name, want, StringComparison.OrdinalIgnoreCase))
			{
				return r;
			}
		}
		if (File.Exists(name))
		{
			return new RecipeRef
			{
				Name = System.IO.Path.GetFileNameWithoutExtension(name),
				Path = name,
				Directory = System.IO.Path.GetDirectoryName(name)
			};
		}
		return null;
	}
}
