using System.Text.Json;
using System.Text.RegularExpressions;

namespace EmulatorLauncher.Plugins;

/// <summary>実行フォルダの Plugins\ にある *.json を読み込み、書式を確認して返す。読めないものは警告を出して無視する。</summary>
public static class SystemPluginLoader
{
    private static readonly Regex IdPattern = new("^[a-z0-9_-]+$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string PluginsFolder => Path.Combine(AppContext.BaseDirectory, "Plugins");

    /// <summary>全プラグイン(order、次にファイル名の順)。フォルダが無ければ空。</summary>
    public static List<SystemPluginDefinition> LoadAll(Action<string>? warn = null)
    {
        var result = new List<SystemPluginDefinition>();
        string folder = PluginsFolder;
        if (!Directory.Exists(folder)) return result;

        var ids = new HashSet<string>(StringComparer.Ordinal);
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            warn?.Invoke($"プラグインのフォルダを読めません: {ex.Message}");
            return result;
        }

        foreach (string file in files)
        {
            try
            {
                SystemPluginDefinition? def = JsonSerializer.Deserialize<SystemPluginDefinition>(File.ReadAllText(file), JsonOptions);
                if (def is null) { warn?.Invoke($"{Path.GetFileName(file)}: 空のJSONです"); continue; }

                def.FilePath = file;
                def.Directory = Path.GetDirectoryName(file) ?? folder;
                string? error = Validate(def);
                if (error is not null) { warn?.Invoke($"{Path.GetFileName(file)}: {error}"); continue; }
                if (!ids.Add(def.Id)) { warn?.Invoke($"{Path.GetFileName(file)}: idが重複しています({def.Id})"); continue; }

                result.Add(def);
            }
            catch (Exception ex)
            {
                warn?.Invoke($"{Path.GetFileName(file)}: 読み込めません({ex.Message})");
            }
        }

        return result.OrderBy(d => d.Order).ThenBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>書式の誤りがあればその説明、問題なければnull。</summary>
    public static string? Validate(SystemPluginDefinition d)
    {
        if (d.Schema != 1) return "schemaは1にしてください";
        if (!IdPattern.IsMatch(d.Id ?? string.Empty)) return "idは半角の小文字・数字・-・_だけで指定してください";
        if (string.IsNullOrWhiteSpace(d.Name)) return "nameがありません";
        if (d.Width is < 0 or > 3) return "widthは1〜3で指定してください";
        if (d.Status is { } s && string.IsNullOrWhiteSpace(s.Exe)) return "statusのexeがありません";
        if (d.Action is { } a && string.IsNullOrWhiteSpace(a.Exe)) return "actionのexeがありません";

        switch (d.Display)
        {
            case "stat":
                if (d.Status is null) return "statにはstatusが必要です";
                if (d.Bar is { } bar && bar.Max <= bar.Min) return "barのmaxはminより大きくしてください";
                break;

            case "slider":
                if (d.Action is null) return "sliderにはactionが必要です";
                if (d.Options is not { Type: "range" } r) return "sliderにはoptionsのtype=rangeが必要です";
                if (r.Max <= r.Min || r.Step <= 0) return "rangeはmin<max、step>0にしてください";
                break;

            case "tile":
                if (d.Action is null) return "tileにはactionが必要です";
                if (d.Options is { } o)
                {
                    if (o.Type == "choice")
                    {
                        if (o.Items.Count == 0 || o.Items.Any(i => string.IsNullOrEmpty(i.Value) || string.IsNullOrEmpty(i.Label)))
                            return "choiceのitemsには、valueとlabelが必要です";
                    }
                    else if (o.Type != "toggle")
                    {
                        return "tileのoptionsはtype=choice(選択肢)かtoggle(オン/オフ)にしてください";
                    }
                }
                break;

            default:
                return "displayはstat・tile・sliderのどれかにしてください";
        }

        return null;
    }
}
