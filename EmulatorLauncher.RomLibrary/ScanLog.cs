using System.Text;

namespace EmulatorLauncher.RomLibrary;

/// <summary>フォルダのスキャン(新規登録・再スキャン)1回分の動作ログ。</summary>
internal sealed class ScanLog : IDisposable
{
    private const int KeepLogCount = 10;

    private static readonly AsyncLocal<ScanLog?> CurrentLog = new();

    private readonly object _gate = new();
    private readonly HashSet<string> _onceKeys = new(StringComparer.Ordinal);
    private readonly ScanLog? _previous;
    private StreamWriter? _writer;
    private readonly DateTime _startedAt = DateTime.Now;

    private int _registered, _recognized, _unrecognized, _skipped, _guessed;
    private int _artDownloaded, _artExisting, _artPlaceholder, _artDeferred, _artNone;

    public string? LogPath { get; private set; }

    private ScanLog(string folderPath)
    {
        _previous = CurrentLog.Value;
        try
        {
            string directory = Path.Combine(folderPath, "RetroNexus", "log");
            Directory.CreateDirectory(directory);
            LogPath = Path.Combine(directory, $"scan_{_startedAt:yyyyMMdd_HHmmss}.log");
            _writer = new StreamWriter(LogPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))
            {
                AutoFlush = true,
            };
            TrimOldLogs(directory);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ScanLog] ログファイルを作成できませんでした: {ex.Message}");
            _writer = null;
        }
    }

    /// <summary>スキャンの開始。</summary>
    public static ScanLog Begin(string folderPath)
    {
        var log = new ScanLog(folderPath);
        CurrentLog.Value = log;
        return log;
    }

    private static void TrimOldLogs(string directory)
    {
        try
        {
            foreach (string old in Directory.EnumerateFiles(directory, "scan_*.log")
                         .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                         .Skip(KeepLogCount))
            {
                try { File.Delete(old); } catch { /* 古いログの削除失敗は無視 */ }
            }
        }
        catch { /* 無視 */ }
    }

    private void Write(string kind, string message)
    {
        lock (_gate)
        {
            if (_writer is null) return;
            try
            {
                _writer.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{kind}] {message}");
            }
            catch
            {
                _writer = null; // 以後は書かない
            }
        }
    }

    // 静的ヘルパ(Beginされていなければ何もしない)

    public static void Info(string message) => CurrentLog.Value?.Write("情報", message);

    public static void Warn(string message) => CurrentLog.Value?.Write("警告", message);

    /// <summary>同じkeyのメッセージは1回だけ書く(画像一覧が取れない等、全ゲームで同じになる内容用)。</summary>
    public static void Once(string key, string message)
    {
        ScanLog? log = CurrentLog.Value;
        if (log is null) return;
        lock (log._gate)
        {
            if (!log._onceKeys.Add(key)) return;
        }
        log.Write("警告", message);
    }

    public static void Registered() { ScanLog? l = CurrentLog.Value; if (l is not null) Interlocked.Increment(ref l._registered); }

    public static void Skipped(string message)
    {
        ScanLog? l = CurrentLog.Value;
        if (l is null) return;
        Interlocked.Increment(ref l._skipped);
        l.Write("対象外", message);
    }

    public static void Recognized(string fileName, string title, string key)
    {
        ScanLog? l = CurrentLog.Value;
        if (l is null) return;
        // 成功は件数だけ数える(行は書かない)。
        Interlocked.Increment(ref l._recognized);
    }

    /// <summary>CRC/シリアルは合わなかったが、名前(簡略キー)の一致でタイトルを推定した。</summary>
    public static void Guessed(string fileName, string title, string reason)
    {
        ScanLog? l = CurrentLog.Value;
        if (l is null) return;
        Interlocked.Increment(ref l._guessed);
        l.Write("推定", $"{fileName} → {title} (名前の一致で推定。{reason}。別の版の可能性があります)");
    }

    public static void Unrecognized(string fileName, string reason)
    {
        ScanLog? l = CurrentLog.Value;
        if (l is null) return;
        Interlocked.Increment(ref l._unrecognized);
        l.Write("未認識", $"{fileName} → {reason}");
    }

    public enum ArtResult { Downloaded, Existing, Placeholder, Deferred, None }

    public static void Art(ArtResult result, string title, string detail)
    {
        ScanLog? l = CurrentLog.Value;
        if (l is null) return;
        string kind;
        switch (result)
        {
            // 成功(取得・既存)は件数だけ数える(行は書かない)。
            case ArtResult.Downloaded: Interlocked.Increment(ref l._artDownloaded); return;
            case ArtResult.Existing: Interlocked.Increment(ref l._artExisting); return;
            case ArtResult.Placeholder: Interlocked.Increment(ref l._artPlaceholder); kind = "画像:ダミー"; break;
            case ArtResult.Deferred: Interlocked.Increment(ref l._artDeferred); kind = "画像:保留"; break;
            default: Interlocked.Increment(ref l._artNone); kind = "画像:なし"; break;
        }
        l.Write(kind, $"{title} - {detail}");
    }

    public void Dispose()
    {
        Write("集計",
            $"登録 {_registered}件 / 認識 {_recognized}件 / 名前で推定 {_guessed}件 / 未認識 {_unrecognized}件 / 対象外 {_skipped}件");
        Write("集計",
            $"画像: 取得 {_artDownloaded} / 既存 {_artExisting} / ダミー {_artPlaceholder} / 保留(次回再試行) {_artDeferred} / なし {_artNone}");
        Write("情報", $"スキャン終了(所要 {(DateTime.Now - _startedAt).TotalSeconds:F1}秒)");

        lock (_gate)
        {
            try { _writer?.Dispose(); } catch { /* 無視 */ }
            _writer = null;
        }

        CurrentLog.Value = _previous;
    }
}
