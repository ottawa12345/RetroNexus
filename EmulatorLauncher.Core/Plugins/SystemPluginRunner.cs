using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace EmulatorLauncher.Plugins;

/// <summary>プラグインのコマンド(status/action)を実行し、標準出力のJSONを結果として返す。</summary>
public static class SystemPluginRunner
{
    private const int DefaultStatusTimeoutSec = 3;
    private const int DefaultActionTimeoutSec = 10;
    private const int MaxOutputChars = 64 * 1024;

    /// <summary>status/actionを実行する。失敗(起動不可・タイムアウト・終了コード0以外・JSON不正・success=false)はSuccess=falseで返す。例外は投げない。</summary>
    /// <param name="value">actionの{value}へ入れる値(statusではnull)。</param>
    public static async Task<SystemPluginResult> RunAsync(
        SystemPluginDefinition def, SystemPluginCommand command, bool isAction, string? value, CancellationToken cancel = default)
    {
        int timeoutSec = command.TimeoutSec > 0 ? command.TimeoutSec : (isAction ? DefaultActionTimeoutSec : DefaultStatusTimeoutSec);

        var psi = new ProcessStartInfo
        {
            FileName = Expand(command.Exe, def, value),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = string.IsNullOrWhiteSpace(command.WorkingDir) ? def.Directory : Expand(command.WorkingDir, def, value),
        };
        foreach (string arg in command.Args) psi.ArgumentList.Add(Expand(arg, def, value));

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process is null) return SystemPluginResult.Fail("起動できません");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

            Task<string?> read = ReadLimitedAsync(process, timeout.Token);
            _ = process.StandardError.ReadToEndAsync(); // 詰まらないよう読み捨てる

            try
            {
                string? text = await read.ConfigureAwait(false);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                if (text is null) return SystemPluginResult.Fail("出力が大きすぎます");
                if (process.ExitCode != 0) return SystemPluginResult.Fail($"終了コード {process.ExitCode}");
                return Parse(text, isAction);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                return SystemPluginResult.Fail(cancel.IsCancellationRequested ? "中止しました" : "時間切れです");
            }
        }
        catch (Exception ex)
        {
            return SystemPluginResult.Fail(ex.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>標準出力を上限まで読む。上限を超えたらプロセスを止めてnull。</summary>
    private static async Task<string?> ReadLimitedAsync(Process process, CancellationToken token)
    {
        var sb = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            int n = await process.StandardOutput.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (n <= 0) break;
            sb.Append(buffer, 0, n);
            if (sb.Length > MaxOutputChars)
            {
                Kill(process);
                return null;
            }
        }
        return sb.ToString();
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* すでに終了している等 */ }
    }

    internal static string Expand(string text, SystemPluginDefinition def, string? value)
        => text.Replace("{pluginDir}", def.Directory, StringComparison.Ordinal)
               .Replace("{appDir}", AppContext.BaseDirectory.TrimEnd('\\', '/'), StringComparison.Ordinal)
               .Replace("{value}", value ?? string.Empty, StringComparison.Ordinal);

    internal static SystemPluginResult Parse(string text, bool isAction)
    {
        text = text.Trim();
        // actionは、終了コード0で何も出力しなければ成功とみなす(statusは必ずJSONを返す)。
        if (text.Length == 0) return isAction ? new SystemPluginResult { Success = true } : SystemPluginResult.Fail("出力がありません");

        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return SystemPluginResult.Fail("JSONがオブジェクトではありません");

            bool success = root.TryGetProperty("success", out JsonElement s) && s.ValueKind == JsonValueKind.True;
            string? message = root.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            string? detail = root.TryGetProperty("detail", out JsonElement d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;

            string? value = null;
            if (root.TryGetProperty("value", out JsonElement v))
            {
                value = v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Number => v.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => null,
                };
            }

            return new SystemPluginResult { Success = success, Value = value, Detail = detail, Message = message };
        }
        catch (JsonException)
        {
            return SystemPluginResult.Fail("出力がJSONとして読めません");
        }
    }
}
