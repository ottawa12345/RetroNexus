using System.Text.Json.Serialization;

namespace EmulatorLauncher.Plugins;

/// <summary>プラグインのコマンド1つ(status/action)。シェルは通さず、exeとargsで直接起動する。</summary>
public sealed class SystemPluginCommand
{
    public string Exe { get; set; } = string.Empty;
    public List<string> Args { get; set; } = new();
    public string? WorkingDir { get; set; }

    /// <summary>この秒数で強制終了する。0なら既定(status 3秒 / action 10秒)。</summary>
    public int TimeoutSec { get; set; }

    /// <summary>statusだけ。パネルを開いている間、この秒数ごとに取得する(0なら開いたときと操作後だけ。下限2秒)。</summary>
    public int IntervalSec { get; set; }

    /// <summary>statusだけ。trueなら、パネルを開いている間コマンドを動かし続け、標準出力に1行1JSONで値を受け取る(IntervalSecは無視)。
    /// パネルを閉じるとき、コマンドの標準入力を閉じて終了を促す(終わらなければ強制終了)。</summary>
    public bool Stream { get; set; }
}

public sealed class SystemPluginChoice
{
    public string Value { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

/// <summary>actionに渡す値の指定。Typeは range / choice / toggle。</summary>
public sealed class SystemPluginOptions
{
    public string Type { get; set; } = string.Empty;
    public int Min { get; set; }
    public int Max { get; set; }
    public int Step { get; set; } = 1;
    public string? Unit { get; set; }
    public List<SystemPluginChoice> Items { get; set; } = new();
}

/// <summary>statの細いバー(値をmin〜maxの割合で塗る)。</summary>
public sealed class SystemPluginBar
{
    public int Min { get; set; }
    public int Max { get; set; } = 100;
}

/// <summary>Plugins\*.json 1つぶん(システム設定パネルの1項目)。書式はPROJECT_NOTES.mdの「システムプラグイン」参照。</summary>
public sealed class SystemPluginDefinition
{
    public int Schema { get; set; }
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>stat / tile / slider。</summary>
    public string Display { get; set; } = string.Empty;

    /// <summary>パネルの横3マスのうちの幅(1〜3)。0なら表示タイプの既定(slider=3、他=1)。</summary>
    public int Width { get; set; }

    public int Order { get; set; }
    public string? Icon { get; set; }
    public SystemPluginCommand? Status { get; set; }
    public SystemPluginCommand? Action { get; set; }
    public SystemPluginOptions? Options { get; set; }

    /// <summary>statの表示文字。{value}を現在値に置き換える(省略時は値そのまま)。</summary>
    public string? Format { get; set; }

    public SystemPluginBar? Bar { get; set; }

    /// <summary>falseなら、ゲーム中はグレーアウトして操作できない。</summary>
    public bool AllowInGame { get; set; } = true;

    [JsonIgnore] public string Directory { get; set; } = string.Empty;
    [JsonIgnore] public string FilePath { get; set; } = string.Empty;

    /// <summary>システム項目の一覧(表示/非表示・並び順)で使う識別子。</summary>
    [JsonIgnore] public string ItemId => "plugin:" + Id;

    [JsonIgnore] public int EffectiveWidth => Width is >= 1 and <= 3 ? Width : (Display == "slider" ? 3 : 1);
}

/// <summary>コマンドが標準出力へ返すJSON({"success":true,"value":15,"detail":"..."})と、実行の結果。</summary>
public sealed class SystemPluginResult
{
    public bool Success { get; init; }
    public string? Value { get; init; }
    public string? Detail { get; init; }
    public string? Message { get; init; }

    public static SystemPluginResult Fail(string message) => new() { Success = false, Message = message };
}
