using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;

namespace EmulatorLauncher.Models;

/// <summary>1枚目画像の「コンソール選択」カルーセルに表示する1機種分の情報。</summary>
public class ConsoleInfo : INotifyPropertyChanged, ISelectableCard
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Maker { get; set; } = string.Empty;
    public int ReleaseYear { get; set; }
    public int GameCount { get; set; }

    /// <summary>パッケージ画像の縦横比(幅÷高さ)。Consoles.jsonの"aspectRatio"から読み込む。
    /// 例: 0.62なら縦長(SFC箱)、1.38なら横長(N64箱)。未指定時は1.0(正方形)扱い。
    /// GameSelectPageのカード表示で、コンソールごとの実際のパッケージ形状に近い比率で
    /// カードを表示するために使う(Converters.AspectRatioToCardSizeConverter参照)。</summary>
    public double AspectRatio { get; set; } = 1.0;

    public string IconPath { get; set; } = string.Empty;
    public string LogoPath { get; set; } = string.Empty;

    /// <summary>
    /// ヒーロー画像(ダークテーマのコンソール選択画面の上部に出す横長の画像)。Consoles.jsonの"heroPath"
    /// ("./Images/Heroes/fc.png"のような実行フォルダ基準の相対パス)から読み込む。
    /// 画像ファイルが無い/未指定のときは空文字になり、画面側はアイコン画像で代用する。
    /// </summary>
    public string HeroPath { get; set; } = string.Empty;

    /// <summary>
    /// ダークテーマ用のコンソールアイコン(Consoles.jsonの"darkIconPath"。"./Images/Console/Dark/fc.png"のような相対パス)。
    /// 画像ファイルが無い/未指定のときは空文字になり、<see cref="DarkTileIconPath"/>が標準の<see cref="IconPath"/>で代用する。
    /// </summary>
    public string DarkIconPath { get; set; } = string.Empty;

    /// <summary>ダークテーマのコンソールタイルに表示する画像(ダーク用があればそれ、無ければ標準のアイコン)。</summary>
    public string DarkTileIconPath => string.IsNullOrEmpty(DarkIconPath) ? IconPath : DarkIconPath;

    /// <summary>
    /// 現在選択中のlibretroコアを表す、LauncherInfo.Name の値("Snes9x"等)。
    /// 実際のコア情報(コアDLLのパス等)は SampleData.GetLaunchers() が返す一覧から、
    /// Id(このConsoleInfo.Idと同じ) と Name(このLauncherName) が一致するものを
    /// 引く(<see cref="SampleData.ResolveLauncher"/> 参照)。
    /// 同じコンソールIdに対して複数のLauncherInfoが存在できるため、この値を切り替える
    /// だけで、画面上から同じコンソールの別コアへユーザーが変更できるようにしている。
    /// </summary>
    public string LauncherName { get; set; } = string.Empty;

    /// <summary>
    /// このコンソールのROMとして扱うファイル拡張子(".rpx"、".wua"等。Consoles.jsonの"romExtensions")。
    /// Data/rdb/rdblist.json側にこのコンソールの拡張子定義が無い場合だけ、ROMフォルダのスキャン時に
    /// 代わりに使われる(RomFolderScanService.ScanFolderContentsAsync参照)。設定画面の
    /// 「ランチャー管理」で新しいコンソールIDを指定して作ったコンソール用。
    /// </summary>
    public List<string> RomExtensions { get; set; } = new();

    /// <summary>ボックスアート代わりのプレースホルダー色(実装後に実画像へ差し替え想定)。</summary>
    public string AccentColorHex { get; set; } = "#607D8B";

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}