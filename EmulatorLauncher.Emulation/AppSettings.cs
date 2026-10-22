using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace EmulatorLauncher.Emulation;

/// <summary>
/// エミュレータ画面のアスペクト比。中断メニューでは常にAuto/16:9の2つに加え、
/// LauncherDefault(そのコアがLauncherInfo.DefaultAspectRatioで指定している値。例:
/// ファミコンなら4:3、GBAなら3:2)が(値がAuto扱い、または既に16:9と同じ場合を除いて)
/// 選べるようになる。実際に選べる組み合わせの決定はGamePlayPage.BuildScreenLayoutSequence参照。
///
/// System.Text.Jsonの既定設定(JsonStringEnumConverter未使用)ではenumは数値としてそのまま
/// config/&lt;コア&gt;.dll.jsonへ保存される。このアプリはまだ実機ビルド未検証で既存の保存済み
/// 設定は存在しない想定だが、今後値を追加する場合は末尾に追加し、既存メンバーの並び替え・
/// 削除は避けること。
/// </summary>
public enum ScreenLayoutMode
{
    /// <summary>コアが実行時に申告する値(EmulationHost.CoreReportedAspectRatio)をそのまま使う。</summary>
    Auto,

    /// <summary>
    /// LauncherInfo.DefaultAspectRatio(Launchers.jsonの、そのコアが動かす実機の既定画面比率)を
    /// そのまま使う。まだそのコア専用の設定ファイルが無い(=一度もこの中断メニューで
    /// アスペクト比を変更していない)場合の既定値としてCoreSettings.ScreenLayoutに使われる
    /// (GamePlayPage.GetDisplayAspectRatio参照)。DefaultAspectRatioが"Auto"の機種(MAME、
    /// ニンテンドーDS等コア設定次第で画面比率が変わるもの)では、Autoと同じ挙動になる。
    /// </summary>
    LauncherDefault,

    Ratio16x9,
}

/// <summary>
/// 中断メニュー「設定」内の「画質」で選べる映像フィルター。
/// ソフトウェアレンダリングのコア(生ピクセルバッファが取れるコア)でのみ選択可能。
/// HWレンダリングのコア(N64等、SET_HW_RENDERを申告するコア)には適用できない。
/// </summary>
public enum PictureQualityMode
{
    /// <summary>フィルター無し(コアの生ピクセルバッファをそのまま等倍アップスケール)。</summary>
    None,

    /// <summary>
    /// 独自実装のScale3x(AdvMAME3x)系アルゴリズムによる3倍角の高品質スケーラー。
    /// 隣接ピクセルの一致/不一致パターンから出力先9マスの値を決めるため、色の
    /// ブレンドを行わずドット絵特有の輪郭を保ったまま斜め線を滑らかに見せる。
    /// </summary>
    HighQuality3x,

    /// <summary>
    /// 独自実装のHQ2x風2倍角スケーラー。Scale3xとは異なり、近傍ピクセルの比較を
    /// (ビット完全一致ではなく)RGB空間での知覚的な近さで判定し、輪郭部分は単純コピー
    /// ではなく近傍色とのブレンドで滑らかな階調を作る(本家hq2xの256パターン参照テーブル
    /// そのものではなく、その考え方を踏まえて独自に設計した簡易版)。
    /// </summary>
    Hq2x,
}

/// <summary>
/// コア(DLL)ごとに保存する設定。「設定」メニューのオートセーブ/巻き戻し/操作説明の
/// オンオフと、画面レイアウト(4:3 / 16:9)を保持する。
/// </summary>
public sealed class CoreSettings
{
    public bool AutoSaveEnabled { get; set; } = true;
    public bool RewindEnabled { get; set; } = true;

    /// <summary>
    /// ゲーム画面下部の操作説明バー(GamePlayPage.xamlの「通常時 下部ガイドバー」、
    /// x:Name="PlayHintBar" — 中断メニュー/決定ボタンの案内)を表示するかどうか。
    /// 中断メニュー「設定」内の「操作説明表示」トグルに対応(GamePlayPage.xaml.cs の
    /// ToggleShowControlGuide/ApplyShowControlGuide参照)。オンなら表示、オフなら非表示。
    /// (以前の説明。現在は使われない)
    /// </summary>
    /// 【廃止】操作説明表示は全体設定になった(AppPreferences.ShowControlGuide)。読み込み互換のため項目だけ残す。
    public bool ShowControlGuide { get; set; } = true;

    /// <summary>
    /// 【廃止】連射はコントローラー設定画面(コンソール毎)に移った。以下は旧仕様の説明:
    /// 常に連射(1秒間に約15回)にするRetroPadのボタン名("A"/"B"/"X"/"Y"/"L"/"R"/"L2"/"R2")。
    /// 空文字は連射なし。中断メニュー「設定」内の「デフォルトで連射割り当て」に対応する。
    /// そのボタンを押している間だけ、押す/離すを自動で繰り返す。
    /// </summary>
    public string TurboButton { get; set; } = string.Empty;  // 廃止(使われない)

    /// <summary>
    /// 既定値はLauncherDefault(=LauncherInfo.DefaultAspectRatioをそのまま使う)。まだ一度も
    /// 中断メニューでアスペクト比を変更していない(=このコアのconfig/*.jsonが存在しない)
    /// 新規コアはこの値からスタートし、GamePlayPage.GetDisplayAspectRatioがLauncherInfo側の
    /// 既定アスペクト比を反映する。ユーザーが変更すればその選択(Auto/ランチャー既定/16:9)が
    /// 以後は優先される(CycleScreenLayout参照)。
    /// </summary>
    public ScreenLayoutMode ScreenLayout { get; set; } = ScreenLayoutMode.LauncherDefault;

    /// <summary>中断メニュー「設定」内の「画質」。ソフトウェアレンダリングのコアでのみ意味を持つ。</summary>
    public PictureQualityMode PictureQuality { get; set; } = PictureQualityMode.None;

    /// <summary>
    /// 操作説明バーの表示を、このコアだけ上書きする値。"Follow"(既定)=アプリ設定(AppPreferences.ShowControlGuide)に従う、
    /// "On"=常に表示、"Off"=常に非表示。設定画面「コア詳細設定」の標準項目「操作説明表示」で選ぶ。
    /// 上書きしている間は、ゲーム中の「操作説明表示」トグルもこの値を書き換える(GamePlayPage.ToggleShowControlGuide)。
    /// </summary>
    public string ControlGuide { get; set; } = ControlGuideFollow;

    public const string ControlGuideFollow = "Follow";
    public const string ControlGuideOn = "On";
    public const string ControlGuideOff = "Off";

    /// <summary>
    /// コアが申告するビデオフィルター系コアオプションの選択値(コアオプションキー -> 選択値)。
    /// 起動時、コアがそのキーを申告したタイミングで再適用される。
    /// </summary>
    public Dictionary<string, string> VideoFilterValues { get; set; } = new();

    /// <summary>
    /// 【廃止】ボタン割り当てはコア毎ではなくコンソール毎になった(ControllerMappingService.ResolveMapping)。
    /// 既存の設定ファイルを読めるよう項目だけ残してあるが、ゲーム起動時には使われない。
    ///
    /// 以下は旧仕様の説明:
    /// このコア固有のRetroPadボタン割り当て(RetroPadのボタン名("A"等。
    /// ControllerMappingService.RetroPadButtonKeys参照) → 物理ボタン名
    /// (GamepadButtonの名前の文字列))。ここに存在しないキーは
    /// ControllerMappingService.Load().DefaultMapping(コントローラー設定画面の
    /// 「デフォルト設定」で編集する既定値)を使う。つまりここは「既定値からの差分」
    /// だけを持つ(コントローラー設定画面の「コア設定」で編集する)。
    /// </summary>
    public Dictionary<string, string> ButtonMapping { get; set; } = new();
}

/// <summary>
/// CoreSettings の読み書きを行う。実行ファイルのディレクトリ配下の "config" フォルダに、
/// コアのDLLファイル名ごとに1つのJSONファイルとして保存する。
/// 例: &lt;実行ファイルのディレクトリ&gt;/config/snes9x_libretro.dll.json
/// </summary>
public static class CoreSettingsStore
{
    private static string GetConfigFolder() => Path.Combine(AppContext.BaseDirectory, "config");

    private static string SanitizeForPath(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    private static string GetConfigPath(string corePath)
    {
        string coreFileName = SanitizeForPath(Path.GetFileName(corePath));
        return Path.Combine(GetConfigFolder(), coreFileName + ".json");
    }

    /// <summary>指定コアの設定を読み込む。ファイルが無い/壊れている場合は既定値を返す。</summary>
    public static CoreSettings Load(string corePath)
    {
        try
        {
            string path = GetConfigPath(corePath);
            if (!File.Exists(path)) return new CoreSettings();

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<CoreSettings>(json) ?? new CoreSettings();
        }
        catch
        {
            // 壊れたjson等は「既定値」として扱う(起動自体は継続できるようにする)
            return new CoreSettings();
        }
    }

    /// <summary>指定コアの設定を保存する。</summary>
    public static void Save(string corePath, CoreSettings settings)
    {
        try
        {
            string folder = GetConfigFolder();
            Directory.CreateDirectory(folder);

            string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GetConfigPath(corePath), json);
        }
        catch
        {
            // 設定の保存に失敗しても動作自体は継続できるので握りつぶす
        }
    }
}