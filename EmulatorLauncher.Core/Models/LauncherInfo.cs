using System.Globalization;
using System.Text.Json.Serialization;

namespace EmulatorLauncher.Models;

/// <summary>実行に使うlibretroコア(ランチャー)1つ分の情報。</summary>
public class LauncherInfo
{
    /// <summary>対応するConsoleInfo.Idと同じ値("fc"、"pce"等)。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>コア名の表示用ラベル(例: "FCEUmm"、"Snes9x")。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>このコアのlibretroコア(.dll)のファイル名、または絶対パス。</summary>
    public string CorePath { get; set; } = string.Empty;

    /// <summary>外部コマンド(libretroコアではなく別プロセスのエミュレータ。例: Wii U の Cemu)で起動する場合の実行ファイルのパス。</summary>
    public string ExePath { get; set; } = string.Empty;

    /// <summary>外部コマンドへ渡す引数文字列。</summary>
    public string Args { get; set; } = string.Empty;

    /// <summary>外部コマンドの作業フォルダ。</summary>
    public string WorkingPath { get; set; } = string.Empty;

    /// <summary>ExePath が指定されている(=外部コマンドで起動する)ランチャーか。</summary>
    [JsonIgnore]
    public bool IsExternal => !string.IsNullOrWhiteSpace(ExePath);

    /// <summary>このコアで動かす実際のゲーム画面の既定アスペクト比。</summary>
    public string DefaultAspectRatio { get; set; } = "Auto";

    /// <summary>trueのコアは、ゲームを選ばずに本体(BIOS/ファームウェアのメニュー)だけを起動できる(libretroのSET_SUPPORT_NO_GAME。PS1/PS2/ドリームキャスト等)。</summary>
    public bool BiosBoot { get; set; }

    /// <summary>("Auto"、または"幅:高さ"形式の文字列)を実際の比率(幅÷高さ)へ解決する。</summary>
    public double? ResolveDefaultAspectRatio()
    {
        string raw = DefaultAspectRatio?.Trim() ?? string.Empty;
        if (raw.Length == 0 || string.Equals(raw, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string[] parts = raw.Split(':');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double width)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double height)
            && width > 0 && height > 0)
        {
            return width / height;
        }

        // "幅:高さ"として解析できない値は、起動自体を継続できるようAuto相当として扱う。
        return null;
    }
}