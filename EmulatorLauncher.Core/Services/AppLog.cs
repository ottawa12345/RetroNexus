using System.Runtime.InteropServices;
using System.Text;

namespace EmulatorLauncher.Services;

/// <summary>アプリ全体の動作ログ。</summary>
public static class AppLog
{
    private const int KeepLogCount = 10;

    /// <summary>コアが出すログの、1回のゲーム起動あたりの最大行数(それ以降は数だけ数える)。</summary>
    private const int CoreLineLimit = 3000;

    private static readonly object Gate = new();
    private static readonly HashSet<string> OnceKeys = new(StringComparer.Ordinal);
    private static StreamWriter? _writer;
    private static bool _initialized;
    private static string? _lastLine;
    private static int _repeatCount;
    private static int _coreLines;
    private static int _coreLinesDropped;

    /// <summary>今回の起動のログファイルのパス(書き込めなかったときはnull)。</summary>
    public static string? LogPath { get; private set; }

    /// <summary>コアのログのうち INFO/DEBUG も残すか。</summary>
    public static volatile bool CoreVerbose;

    /// <summary>ログの出力先フォルダ。</summary>
    public static string LogFolder => Path.Combine(AppContext.BaseDirectory, "data", "log");

    /// <summary>ログファイルを開き、環境情報(アプリのバージョン・OS・.NET)を先頭に書く。</summary>
    public static void Start(string appVersion)
    {
        lock (Gate)
        {
            if (_initialized) return;
            OpenUnlocked();
            WriteRaw("INFO", "App", $"RetroNexus {appVersion} を起動しました");
            WriteRaw("INFO", "App", $"OS: {RuntimeInformation.OSDescription} / {RuntimeInformation.OSArchitecture}");
            WriteRaw("INFO", "App", $".NET: {RuntimeInformation.FrameworkDescription}");
            WriteRaw("INFO", "App", $"実行フォルダ: {AppContext.BaseDirectory}");
        }
    }

    public static void Info(string source, string message) => Write("INFO", source, message, null);

    public static void Warn(string source, string message, Exception? ex = null) => Write("WARN", source, message, ex);

    public static void Error(string source, string message, Exception? ex = null) => Write("ERROR", source, message, ex);

    /// <summary>同じ key では最初の1回だけ警告を書く(読み込みのたびに呼ばれる処理の失敗を、何度も書かないため)。</summary>
    public static void WarnOnce(string key, string source, string message, Exception? ex = null)
    {
        lock (Gate)
        {
            if (!OnceKeys.Add(key)) return;
        }

        Write("WARN", source, message, ex);
    }

    /// <summary>ゲーム(コア)を起動するたびに呼ぶ。</summary>
    public static void BeginCoreSession()
    {
        lock (Gate)
        {
            FlushRepeatUnlocked();
            _coreLines = 0;
            _coreLinesDropped = 0;
        }

        CoreVerbose = true;
    }

    /// <summary>コアのロードが終わったとき(以降は WARN/ERROR だけ残す)、または停止したときに呼ぶ。</summary>
    public static void EndCoreLoading()
    {
        CoreVerbose = false;
    }

    /// <summary>コア(libretro)のログ。</summary>
    public static void Core(int level, string message)
    {
        // DEBUG(0)は量が多く、原因調査にはほとんど役立たないので残さない(FBNeoは1回のロードで数十行出る)。
        if (level < 1 || (level < 2 && !CoreVerbose)) return;

        lock (Gate)
        {
            if (_coreLines >= CoreLineLimit)
            {
                _coreLinesDropped++;
                return;
            }

            _coreLines++;
        }

        string kind = level switch { >= 3 => "ERROR", 2 => "WARN", 1 => "INFO", _ => "DEBUG" };
        Write(kind, "Core", message.Trim(), null);
    }

    /// <summary>コアのログを上限で打ち切った場合、その行数を書く(ゲーム終了時に呼ぶ)。</summary>
    public static void EndCoreSession()
    {
        int dropped;
        lock (Gate)
        {
            dropped = _coreLinesDropped;
            _coreLinesDropped = 0;
        }

        if (dropped > 0) Write("INFO", "Core", $"コアのログが多すぎるため、{dropped} 行を省略しました", null);
    }

    /// <summary>ログファイルを書き切る(アプリ終了時)。</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            FlushRepeatUnlocked();
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }

    private static void Write(string kind, string source, string message, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                if (!_initialized) OpenUnlocked();
                if (_writer is null) return;

                string line = $"[{kind}] [{source}] {message}";
                if (ex is not null) line += Environment.NewLine + ex;

                // 直前と同じ内容(時刻以外)はまとめる。
                if (line == _lastLine)
                {
                    _repeatCount++;
                    return;
                }

                FlushRepeatUnlocked();
                _lastLine = line;
                WriteLineUnlocked(line);
            }
        }
        catch
        {
            // ログのせいでアプリを止めない。
        }
    }

    // 以下は Gate を取得済みの状態で呼ぶ

    private static void WriteRaw(string kind, string source, string message)
        => WriteLineUnlocked($"[{kind}] [{source}] {message}");

    private static void WriteLineUnlocked(string line)
    {
        try
        {
            _writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
        }
        catch
        {
            // 書き込めなくなったら以降は何もしない。
            _writer = null;
        }
    }

    private static void FlushRepeatUnlocked()
    {
        if (_repeatCount > 0 && _lastLine is not null)
        {
            WriteLineUnlocked($"[INFO] [Log] (直前と同じ内容が、さらに {_repeatCount} 回続きました)");
        }

        _repeatCount = 0;
        _lastLine = null;
    }

    private static void OpenUnlocked()
    {
        _initialized = true;
        try
        {
            string folder = LogFolder;
            Directory.CreateDirectory(folder);
            LogPath = Path.Combine(folder, $"app_{DateTime.Now:yyyyMMdd_HHmmss}.log");

            // 別のプロセスや、ユーザーがメモ帳などで開いていても書き込めるよう共有を許可する。
            var stream = new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))
            {
                AutoFlush = true,
            };

            TrimOldLogs(folder);
        }
        catch
        {
            _writer = null;
            LogPath = null;
        }
    }

    private static void TrimOldLogs(string folder)
    {
        try
        {
            foreach (string old in Directory.EnumerateFiles(folder, "app_*.log")
                         .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                         .Skip(KeepLogCount))
            {
                try { File.Delete(old); } catch { }
            }
        }
        catch
        {
        }
    }
}
