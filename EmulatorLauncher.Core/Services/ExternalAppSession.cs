using System.Diagnostics;

namespace EmulatorLauncher.Services;

/// <summary>外部コマンド(libretroコアではない別プロセスのエミュレータ。例: Wii U の Cemu)を1回起動し、その終了を監視するセッション。</summary>
public sealed class ExternalAppSession : IDisposable
{
    private readonly object _gate = new();
    private Process? _process;
    private int _exitedRaised;

    /// <summary>プロセスが終了したとき、ちょうど1回だけ発火する(引数は終了コード。取得不能なら null)。</summary>
    public event Action<int?>? Exited;

    /// <summary>起動済みでまだ終了していないか。</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                try { return _process is { HasExited: false }; }
                catch { return false; }
            }
        }
    }

    /// <summary>プロセスを起動する。</summary>
    public void Start(string exePath, string arguments, string? workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            UseShellExecute = false,
        };

        if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
        {
            psi.WorkingDirectory = workingDirectory;
        }

        Debug.WriteLine($"[ExternalAppSession] Start: \"{exePath}\" {arguments} (cwd={psi.WorkingDirectory})");

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Exited += OnProcessExited;

        lock (_gate)
        {
            _process = process;
        }

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("プロセスを開始できませんでした。");
            }
        }
        catch
        {
            lock (_gate)
            {
                _process = null;
            }

            process.Exited -= OnProcessExited;
            process.Dispose();
            throw;
        }

        // Start直後にすでに終了していた場合、Exitedイベントが登録前に取りこぼされる可能性はEnableRaisingEvents=true(Start前に設定済み)のため実質無いが、念のため確認する。
        try
        {
            if (process.HasExited) RaiseExited(process);
        }
        catch
        {
            // 取得できなければ通常のイベントに任せる
        }
    }

    /// <summary>プロセスをツリーごと強制終了する。</summary>
    public void Kill()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
        }

        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                Debug.WriteLine("[ExternalAppSession] Kill(プロセスツリー)");
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            // 既に終了した/アクセス拒否など。
            Debug.WriteLine($"[ExternalAppSession] Kill失敗: {ex.Message}");
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (sender is Process process) RaiseExited(process);
    }

    private void RaiseExited(Process process)
    {
        if (Interlocked.Exchange(ref _exitedRaised, 1) != 0) return;

        int? exitCode = null;
        try { exitCode = process.ExitCode; } catch { }

        Debug.WriteLine($"[ExternalAppSession] Exited: code={exitCode?.ToString() ?? "?"}");
        Exited?.Invoke(exitCode);
    }

    public void Dispose()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }

        if (process is null) return;

        process.Exited -= OnProcessExited;
        process.Dispose();
    }
}