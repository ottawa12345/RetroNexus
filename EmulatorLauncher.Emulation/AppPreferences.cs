using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmulatorLauncher.Emulation;

/// <summary>
/// アプリ全体の「地域」設定。設定画面の「アプリ設定」→「地域」で選ぶ。
/// 将来、リージョンを受け付けるlibretroコアへこの値を渡すための保持用
/// (現時点では保存するだけで、コアへの送信は未実装・未検証)。
/// jsonには名前の文字列で保存する(並び替え・追加しても既存の保存値が壊れないように)。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AppRegion
{
    Japan,
    Americas,
    Europe,
    Oceania,
    HongKongTaiwanKorea,
}

/// <summary>AppRegionの表示名。</summary>
public static class AppRegionInfo
{
    public static string GetDisplayName(AppRegion region) => region switch
    {
        AppRegion.Japan => "日本",
        AppRegion.Americas => "アメリカ大陸",
        AppRegion.Europe => "ヨーロッパ",
        AppRegion.Oceania => "オーストラリア/ニュージーランド",
        AppRegion.HongKongTaiwanKorea => "香港/台湾/韓国",
        _ => region.ToString(),
    };
}

/// <summary>テーマの保存値(<see cref="AppPreferences.Theme"/>)。</summary>
public static class AppThemes
{
    public const string Standard = "standard";
    public const string Dark = "dark";
}

/// <summary>
/// 画面の描画の基準にする「仮想解像度」(各画面はこの大きさの固定キャンバスを、実際のウィンドウへ拡大縮小して表示する)。
/// 保存値はId("1920x1080"など)。変更はアプリの再起動で反映される。
/// </summary>
public static class VirtualResolutions
{
    public const string Default = "1920x1080";

    public static readonly (string Id, string Label, double Width, double Height)[] All =
    {
        ("1920x1080", "1920 x 1080(16:9・標準)", 1920, 1080),
        ("1920x1200", "1920 x 1200(16:10)", 1920, 1200),
    };

    /// <summary>Idに対応する解像度。不明なIdは標準(1920x1080)。</summary>
    public static (string Id, string Label, double Width, double Height) Get(string? id)
    {
        foreach (var r in All) if (r.Id == id) return r;
        return All[0];
    }
}

/// <summary>data/app_settings.jsonの中身(アプリ全体の設定)。</summary>
public sealed class AppPreferences
{
    /// <summary>地域。既定は日本。</summary>
    public AppRegion Region { get; set; } = AppRegion.Japan;

    /// <summary>バックアップの保存先フォルダ。空なら、実行フォルダの backup。</summary>
    public string BackupFolder { get; set; } = string.Empty;

    /// <summary>別のウィンドウへ切り替えたとき、ゲームを自動で中断(一時停止)するか。既定は無効。</summary>
    public bool AutoPauseOnDeactivate { get; set; }

    /// <summary>起動時にフルスクリーンで始めるか(既定は無効)。</summary>
    public bool StartFullScreen { get; set; }

    /// <summary>
    /// ゲーム選択画面で決定(A)を押した時の動作。true(既定)=詳細画面を開き、そこの「起動」で起動する、
    /// false=すぐにゲームを起動(この場合、Xボタンの詳細は使わない)。
    /// </summary>
    public bool LaunchViaDetail { get; set; } = true;

    /// <summary>
    /// アーケード(FBNeo / MAME 2003-Plus)で、クローン(同じゲームの別バージョン)も一覧に並べるか。
    /// false(既定)=親だけを並べ、クローンは詳細画面の「バージョン」から選ぶ(検索では見つかる)。
    /// </summary>
    public bool ShowArcadeClones { get; set; }

    /// <summary>
    /// 画面のテーマ。"standard"(既定)=RetroNexus標準、"dark"=ダーク(暗い画面で、コンソールとコレクションを統合した画面)。
    /// </summary>
    public string Theme { get; set; } = AppThemes.Standard;

    /// <summary>
    /// ゲームの再開機能を使うか。false(既定)=終了時に再開用のステートを保存せず、「再開」も出さない。
    /// true=「タイトル選択にもどる」で再開用ステート(suspend)を保存し、次回続きから始められる。
    /// </summary>
    public bool ResumeEnabled { get; set; }

    /// <summary>
    /// 「ゲーム起動」(詳細画面を挟まない)で起動する時、中断データ(再開用)があれば続きから始めるか。
    /// true(既定)=再開データを使う、false=使わず最初から。
    /// </summary>
    public bool ResumeOnDirectLaunch { get; set; } = true;

    /// <summary>
    /// 画面の文字にシステム標準のフォントを使うか。false(既定)=アプリ同梱のM PLUS Rounded 1c。
    /// 変更は次回起動時から反映される。
    /// </summary>
    public bool UseSystemFont { get; set; }

    /// <summary>メニュー画面のBGMを再生するか。既定は再生しない(false)。</summary>
    public bool MenuBgmEnabled { get; set; }

    /// <summary>メニュー操作の効果音を鳴らすか。既定は鳴らす(true)。</summary>
    public bool SoundEffectsEnabled { get; set; } = true;

    /// <summary>メニューBGMの音量(0〜100)。「再生する」のときだけ使う。既定は50(中)。</summary>
    public int MenuBgmVolume { get; set; } = 50;

    /// <summary>効果音の音量(0〜100)。「鳴らす」のときだけ使う。既定は100(大)。</summary>
    public int SoundEffectsVolume { get; set; } = 100;

    /// <summary>ゲームの音量(0〜100)。ゲーム起動時に反映される。既定は100。</summary>
    public int GameVolume { get; set; } = 100;

    /// <summary>
    /// ゲーム画面下の操作説明バーを表示するか。既定は表示(true)。
    /// ゲームごとではなく全体の設定(ゲーム中の中断メニュー「設定」から切り替える)。
    /// </summary>
    public bool ShowControlGuide { get; set; } = true;

    /// <summary>
    /// コンソール選択画面のアイコンの大きさ。false(既定)=大サイズ(横スクロール)、
    /// true=小サイズ(2段のページ表示)。コンソール選択画面の△ボタンで切り替える。
    /// </summary>
    public bool ConsoleViewCompact { get; set; }

    /// <summary>
    /// 「システム」パネルで表示しない項目のId(cpu, memory, wifi, bt, power, volume, brightness, sleep,
    /// restart, shutdown, pads)。既定は空(すべて表示)。あとから項目が増えても、既定では表示される。
    /// </summary>
    public System.Collections.Generic.List<string> HiddenSystemItems { get; set; } = new();

    /// <summary>「システム」パネルで使う(有効にした)プラグインのId(<c>plugin:&lt;id&gt;</c>)。既定は空(プラグインは無効)。</summary>
    public System.Collections.Generic.List<string> EnabledSystemPlugins { get; set; } = new();

    /// <summary>「システム」パネルの項目の並び順(項目Id)。空なら標準の並び。一覧に無い項目は、標準の並びで末尾に付く。</summary>
    public System.Collections.Generic.List<string> SystemItemOrder { get; set; } = new();

    /// <summary>仮想解像度のId(<see cref="VirtualResolutions"/>)。既定は"1920x1080"。変更は再起動で反映。</summary>
    public string VirtualResolution { get; set; } = VirtualResolutions.Default;
}

/// <summary>AppPreferencesの読み書き(実行ファイルのディレクトリ配下のdata/app_settings.json)。</summary>
public static class AppPreferencesStore
{
    private static string GetPath() => Path.Combine(AppContext.BaseDirectory, "data", "app_settings.json");

    /// <summary>読み込む。ファイルが無い/壊れている場合は既定値。</summary>
    public static AppPreferences Load()
    {
        try
        {
            string path = GetPath();
            if (!File.Exists(path)) return new AppPreferences();
            return JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(path)) ?? new AppPreferences();
        }
        catch
        {
            return new AppPreferences();
        }
    }

    /// <summary>保存する。失敗しても動作は継続できるので握りつぶす。</summary>
    public static void Save(AppPreferences preferences)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(GetPath())!);
            File.WriteAllText(GetPath(),
                JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }
}
