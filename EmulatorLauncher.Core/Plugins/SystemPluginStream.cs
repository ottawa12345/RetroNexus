using System.Diagnostics;
using System.Text;

namespace EmulatorLauncher.Plugins;

/// <summary>
/// 常駐型のstatus(<c>"stream": true</c>)。コマンドを1回だけ起動して動かし続け、標準出力の1行ごとのJSONを結果として通知する。
/// 止めるときは標準入力を閉じて終了を促し、一定時間待っても終わらなければ強制終了する。
/// コールバックはスレッドプールから呼ばれる(UIへ渡すときは呼び出し側でスレッドを切り替える)。
/// </summary>
public sealed class SystemPluginStream
{
    private const int DefaultFirstLineTimeoutSec = 5;
    private const int MaxLineChars = 64 * 1024;
    private const int GracefulExitMilliseconds = 1500;

    private readonly CancellationTokenSource _stop = new();
    private Process? _process;
    private int _stopped;
    private volatile bool _stopOwnsProcess;   // Stop()がプロセスの後始末(終了待ち・強制終了・破棄)を引き受けた

    private SystemPluginStream() { }

    /// <summary>コマンドを起動して読み取りを始める。onResultは1行届くたび、onEndedはコマンドが終わった(または起動できなかった)とき1回だけ呼ばれる。</summary>
    public static SystemPluginStream Start(
        SystemPluginDefinition def, SystemPluginCommand command, Action<SystemPluginResult> onResult, Action<string> onEnded)
    {
        var stream = new SystemPluginStream();
        _ = Task.Run(() => stream.RunAsync(def, command, onResult, onEnded));
        return stream;
    }

    private async Task RunAsync(SystemPluginDefinition def, SystemPluginCommand command, Action<SystemPluginResult> onResult, Action<string> onEnded)
    {
        string reason = "終了しました";
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = SystemPluginRunner.Expand(command.Exe, def, null),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = string.IsNullOrWhiteSpace(command.WorkingDir) ? def.Directory : SystemPluginRunner.Expand(command.WorkingDir, def, null),
            };
            foreach (string arg in command.Args) psi.ArgumentList.Add(SystemPluginRunner.Expand(arg, def, null));

            process = Process.Start(psi);
            if (process is null)
            {
                onEnded("起動できません");
                return;
            }
            _process = process;
            if (Volatile.Read(ref _stopped) != 0 && !_stopOwnsProcess) { Kill(process); return; }   // 起動中に止められた

            _ = process.StandardError.ReadToEndAsync();   // 詰まらないよう読み捨てる

            int firstTimeout = command.TimeoutSec > 0 ? command.TimeoutSec : DefaultFirstLineTimeoutSec;
            bool first = true;
            while (true)
            {
                string? line;
                if (first)
                {
                    // 最初の1行だけは、時間内に出なければ失敗とみなす。
                    using var firstCts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    firstCts.CancelAfter(TimeSpan.FromSeconds(firstTimeout));
                    try { line = await process.StandardOutput.ReadLineAsync(firstCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException)
                    {
                        if (!_stop.IsCancellationRequested) { reason = "最初の出力がありません(時間切れ)"; Kill(process); }
                        break;
                    }
                }
                else
                {
                    try { line = await process.StandardOutput.ReadLineAsync(_stop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }

                if (line is null)
                {
                    reason = "コマンドが終了しました";
                    break;
                }
                if (line.Length > MaxLineChars)
                {
                    reason = "出力が大きすぎます";
                    Kill(process);
                    break;
                }
                if (line.Trim().Length == 0) continue;

                first = false;
                onResult(SystemPluginRunner.Parse(line, isAction: false));
            }
        }
        catch (Exception ex)
        {
            reason = ex.Message;
        }
        finally
        {
            if (process is not null && !_stopOwnsProcess)
            {
                Kill(process);   // 読み取りが終わったのにまだ生きているものは残さない
                process.Dispose();
            }
        }

        if (!_stop.IsCancellationRequested) onEnded(reason);
    }

    /// <summary>終了を促す。標準入力を閉じ、少し待っても終わらなければ強制終了する。何度呼んでもよい。</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;

        Process? process = _process;
        if (process is not null) _stopOwnsProcess = true;
        try { _stop.Cancel(); } catch { }
        if (process is null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                try { process.StandardInput.Close(); } catch { /* すでに閉じている等 */ }
                using var wait = new CancellationTokenSource(GracefulExitMilliseconds);
                try { await process.WaitForExitAsync(wait.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { Kill(process); }
            }
            catch
            {
                // すでに終わっている
            }
            finally
            {
                try { process.Dispose(); } catch { }
            }
        });
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* すでに終了している等 */ }
    }
}
