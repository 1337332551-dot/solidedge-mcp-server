using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SolidEdge.Spy.EventMcp.Command
{
    /// <summary>
    /// 用户操作台账:JSONL 追加写 + 内存最近 N 条。
    ///
    /// 路径:%LOCALAPPDATA%\SolidEdgeSpy\command-ledger.jsonl(与 tool-usage.jsonl 同风格)。
    /// 线程模型:Append/Load 只在泵线程调(posted action),Snapshot 任意线程 → 全部走 _lock。
    /// 可靠性:写盘失败(磁盘满/文件被占用)只影响文件,内存台账照常;下次 Append 重开句柄重试。
    /// 增长控制:启动加载时把文件截断到最近 MaxEntries 条。
    /// </summary>
    internal sealed class CommandLedger : IDisposable
    {
        /// <summary>内存与文件都保留最近这么多条(防长期使用无限膨胀)。</summary>
        private const int MaxEntries = 5000;

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        private readonly object _lock = new object();
        private readonly List<LedgerEntry> _entries = new List<LedgerEntry>();
        private readonly string _path;
        private StreamWriter _writer;
        private bool _loaded;
        private bool _disposed;

        public CommandLedger()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SolidEdgeSpy");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "command-ledger.jsonl");
        }

        /// <summary>台账文件完整路径(浮窗"打开台账目录"用)。</summary>
        public string FilePath { get { return _path; } }

        /// <summary>内存台账条数。</summary>
        public int Count { get { lock (_lock) { return _entries.Count; } } }

        /// <summary>启动时加载历史(推荐器冷启动要用),并把文件截断到最近 MaxEntries 条。幂等。</summary>
        public void Load()
        {
            lock (_lock)
            {
                if (_loaded) return;
                _loaded = true;

                try
                {
                    if (!File.Exists(_path)) return;

                    var parsed = new List<LedgerEntry>();
                    string[] lines = File.ReadAllLines(_path, Encoding.UTF8);
                    foreach (string line in lines)
                    {
                        LedgerEntry e = TryParse(line);
                        if (e != null) parsed.Add(e);
                    }

                    if (parsed.Count > MaxEntries)
                    {
                        // 截断:只留最近 MaxEntries 条,重写文件
                        parsed.RemoveRange(0, parsed.Count - MaxEntries);
                        var rewritten = new List<string>(parsed.Count);
                        foreach (LedgerEntry e in parsed)
                        {
                            rewritten.Add(JsonSerializer.Serialize(e, JsonOpts));
                        }
                        File.WriteAllLines(_path, rewritten, Encoding.UTF8);
                    }

                    _entries.AddRange(parsed);
                }
                catch
                {
                    // 台账读不出来不致命:当作空台账重新开始
                    _entries.Clear();
                }
            }
        }

        /// <summary>追加一条(泵线程调)。入内存 + 追加写盘。</summary>
        public void Append(LedgerEntry entry)
        {
            if (entry == null) return;
            lock (_lock)
            {
                if (_disposed) return;

                _entries.Add(entry);
                if (_entries.Count > MaxEntries)
                {
                    _entries.RemoveRange(0, _entries.Count - MaxEntries);
                }

                try
                {
                    if (_writer == null)
                    {
                        _writer = new StreamWriter(_path, true, Encoding.UTF8);
                    }
                    _writer.WriteLine(JsonSerializer.Serialize(entry, JsonOpts));
                    _writer.Flush();
                }
                catch
                {
                    // 写盘失败不致命:丢句柄,内存台账照常,下次 Append 重试打开
                    try { if (_writer != null) _writer.Dispose(); } catch { }
                    _writer = null;
                }
            }
        }

        /// <summary>快照(任意线程):最近 count 条,旧的在前。</summary>
        public LedgerEntry[] Snapshot(int count)
        {
            lock (_lock)
            {
                int take = Math.Min(Math.Max(count, 0), _entries.Count);
                LedgerEntry[] copy = new LedgerEntry[take];
                _entries.CopyTo(_entries.Count - take, copy, 0, take);
                return copy;
            }
        }

        /// <summary>全量快照(推荐器启动重建用)。</summary>
        public LedgerEntry[] SnapshotAll()
        {
            return Snapshot(int.MaxValue);
        }

        private static LedgerEntry TryParse(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            try
            {
                return JsonSerializer.Deserialize<LedgerEntry>(line, JsonOpts);
            }
            catch
            {
                return null;    // 坏行跳过,不拖垮整个台账
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                if (_writer != null)
                {
                    try { _writer.Dispose(); } catch { }
                    _writer = null;
                }
            }
        }
    }
}
