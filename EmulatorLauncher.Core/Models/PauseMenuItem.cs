using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EmulatorLauncher.Models;

/// <summary>ポーズメニュー左側の項目(つづける/ロード/どこでもセーブ/リセット/タイトル選択にもどる)。</summary>
public class PauseMenuItem : INotifyPropertyChanged
{
    private string _text = string.Empty;
    public string Text
    {
        get => _text;
        set { _text = value; OnPropertyChanged(); }
    }

    public PauseMenuAction Action { get; set; }

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
public enum PauseMenuAction
{
    Continue,
    Save,
    Load,
    Reset,
    BackToTitleSelect,

    /// <summary>次のディスク(FDSならA面⇔B面、1→2…)へ入れ替える。ディスクが2つ以上あるコアのときだけ項目が出る。</summary>
    SwitchDisk,

    /// <summary>右側に「設定」枠(アスペクト比/画質など)を開く。メニュー上のラベルは常に"設定"のまま固定。</summary>
    OpenSettings,

    /// <summary>「システム設定」パネル(本体の音量・画面の明るさなど)を、中断メニューの上に重ねて開く。</summary>
    OpenSystemSettings,

    /// <summary>いま使っているコントローラーのボタンの割り当てを、設定を開かずに確認する(全画面の表示)。</summary>
    ShowControls,
}

/// <summary>「設定」枠の中の1項目(アスペクト比/画質/フルスクリーン/操作説明表示など)が何を操作するか。</summary>
public enum SettingsMenuAction
{
    AspectRatio,
    PictureQuality,

    /// <summary>
    /// トップ画面のシステムアイコンバー(操作/設定/フルスクリーン/終了。ConsoleSelectPage.xaml.cs の
    /// ActivateSystemIcon参照)の「フルスクリーン」アイコンと同じ MainWindow.ToggleFullScreen() を呼ぶ。
    /// ウィンドウの表示状態そのもの(AppWindow.Presenter.Kind)であり、コアごとの設定ではないため
    /// CoreSettingsには保存しない。GamePlayPage.xaml.cs の ToggleFullscreenSetting/FullscreenText参照。
    /// </summary>
    Fullscreen,

    /// <summary>下部ガイドバー(PlayHintBar)の操作説明表示ON/OFF。ゲームごとではなく全体設定(AppPreferences.ShowControlGuide)。</summary>
    ShowControlGuide,

    /// <summary>
    /// cores/libretro.jsonの起動中コアのOptions(LibretroCoreOptionInfo)のうち、
    /// Hide=falseな項目1件分を汎用的に表す(以前はmupen64plus-nextの内部解像度/FXAAを
    /// それぞれ専用のenum値として個別にハードコードしていたが、SettingsPageの
    /// 「コア毎の設定」タブと同じく「cores/libretro.jsonにOptionsを足すだけで、コアの種類に
    /// 関係なく自動的にこの中断メニューの設定パネルにも出る」ようにするため、この汎用的な
    /// 1種類へ置き換えた)。どのオプションを指すかはSettingsMenuItem.CoreOptionKeyへ
    /// LibretroCoreOptionInfo.Keyをそのまま入れて識別する。GamePlayPage.CycleCoreOption参照。
    /// </summary>
    CoreOption,

    /// <summary>
    /// 「このゲームで変えた設定(画質・コアオプション)を、このコアの標準(コア詳細設定)にする」。
    /// そのゲームの設定が標準と違うときだけ並ぶ(GamePlayPage.HasDefaultableChanges)。
    /// アスペクト比は対象外(ゲームごとに決める運用のため)。
    /// </summary>
    ApplyDefaults,
}

/// <summary>
/// 中断メニュー「設定」を選んだときに右側へ表示する設定項目1件分。
/// アスペクト比・画質と同じ要領で、以後追加する設定もこのモデルへ項目を足すだけでよい。
/// ラベル(Label)と現在値(ValueText)を分けて持ち、表示用に"ラベル: 現在値"へ結合する
/// (画面比率メニュー項目が以前 "画面比率: 4:3" のようにテキストを直接書き換えていたのと
/// 同じ見た目を、右側の設定枠の各行でも再現するため)。
/// </summary>
public class SettingsMenuItem : INotifyPropertyChanged
{
    public SettingsMenuAction Action { get; set; }

    /// <summary>
    /// Action == SettingsMenuAction.CoreOptionのときだけ使う。どのコアオプションを指すかを
    /// LibretroCoreOptionInfo.Keyの値そのままで持つ(GamePlayPage.CycleCoreOptionが、
    /// 起動中コアのOptionsからこのKeyと一致するものを探して値を切り替える)。
    /// それ以外のActionでは常にnull。
    /// </summary>
    public string? CoreOptionKey { get; set; }

    private string _label = string.Empty;
    public string Label
    {
        get => _label;
        set { _label = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayText)); }
    }

    private string _valueText = string.Empty;
    public string ValueText
    {
        get => _valueText;
        set { _valueText = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayText)); }
    }

    /// <summary>この項目の上に区切り線を引くか(「標準にする」は、通常の設定項目と分けて見せる)。</summary>
    public bool HasSeparatorAbove => Action == SettingsMenuAction.ApplyDefaults;

    public string DisplayText => string.IsNullOrEmpty(ValueText) ? Label : $"{Label}: {ValueText}";

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

/// <summary>4枚目画像のソートダイアログの項目。</summary>
public class SortOption : INotifyPropertyChanged
{
    public string Text { get; set; } = string.Empty;
    public SortMode Mode { get; set; }

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

public enum SortMode
{
    Favorite,
    AlphabeticalKana,
    ReleaseDate,
    RecentlyPlayed,
    PlayTimeDescending,
}