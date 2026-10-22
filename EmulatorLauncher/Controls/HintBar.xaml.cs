using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EmulatorLauncher.Controls;

/// <summary>
/// 画面の操作ガイド(HintBar)共通UserControl。使い方・表示形式はHintBar.xamlのコメント参照。
/// </summary>
public sealed partial class HintBar : UserControl
{
    /// <summary>
    /// 表示するキー割り当ての一覧。使う側のXAMLで
    /// &lt;controls:HintBar.Hints&gt;...&lt;/controls:HintBar.Hints&gt; として宣言する
    /// (XAMLの暗黙のコレクション構文により、子要素ごとにAdd()が呼ばれる)。
    /// </summary>
    public ObservableCollection<HintDefinition> Hints { get; } = new();

    private bool _isOverlay;
    private bool _showFooterFrame;

    public HintBar()
    {
        this.InitializeComponent();
        ApplyAppearance();
        HintItemsControl.Tapped += HintItems_Tapped;

        // 「操作ボタンの入れ替え」の設定が変わったときに、A/Bの表示を更新する。
        // 画面に載っている間だけ購読し、載るたびにも最新の設定で表示し直す。
        // コントローラーの接続/種類の変化(PS系かどうか)も、1秒ごとに確認して反映する。
        EmulatorLauncher.Input.ControllerGlyphs.PadStyle lastStyle = EmulatorLauncher.Input.ControllerGlyphs.PadStyle.Standard;
        Microsoft.UI.Dispatching.DispatcherQueueTimer? psTimer = null;
        Loaded += (_, _) =>
        {
            ApplyAppearance();   // ダークテーマの画面では、帯の暗い色に合わせて丸/文字を白にする
            SwapChanged += RefreshKeys;
            EmulatorLauncher.Input.InputDeviceTracker.Changed += RefreshKeys;   // キーボード操作中はキー名の表示にする
            lastStyle = EmulatorLauncher.Input.ControllerGlyphs.GetPrimaryPadStyle();
            RefreshKeys();

            psTimer ??= DispatcherQueue.CreateTimer();
            psTimer.Interval = TimeSpan.FromSeconds(1);
            psTimer.Tick -= PsTimer_Tick;
            psTimer.Tick += PsTimer_Tick;
            psTimer.Start();
        };
        Unloaded += (_, _) =>
        {
            SwapChanged -= RefreshKeys;
            EmulatorLauncher.Input.InputDeviceTracker.Changed -= RefreshKeys;
            psTimer?.Stop();
        };

        void PsTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        {
            var style = EmulatorLauncher.Input.ControllerGlyphs.GetPrimaryPadStyle();
            if (style == lastStyle) return;
            lastStyle = style;
            RefreshKeys();
        }
    }

    private static event Action? SwapChanged;

    /// <summary>「操作ボタンの入れ替え」の設定を変えたときに呼ぶ。表示中の全操作ガイドのA/B表示を更新する。</summary>
    public static void NotifySwapChanged() => SwapChanged?.Invoke();

    private void RefreshKeys()
    {
        foreach (HintDefinition hint in Hints) hint.RefreshDisplayKey();
    }

    /// <summary>
    /// ガイドのボタン(A/B/X/Y/Menu)をマウスクリック・タップしたとき、そのボタンを押した操作として現在の画面へ渡す
    /// (タッチ操作でX/Y等を使えるようにするため)。十字キーなど対応しない表示は何もしない。
    /// </summary>
    private void HintItems_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        DependencyObject? node = e.OriginalSource as DependencyObject;
        while (node is not null && node != HintItemsControl)
        {
            if (node is FrameworkElement { DataContext: HintDefinition hint }
                && hint.TryGetLauncherButton(out EmulatorLauncher.Input.GamepadButton button))
            {
                e.Handled = true;
                EmulatorLauncher.AppServices.MainWindow?.InjectLauncherButton(
                    button, e.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch);
                return;
            }
            node = VisualTreeHelper.GetParent(node);
        }
    }

    /// <summary>
    /// true にすると、暗い半透明の帯(左上だけ丸角)+反転配色(白の丸/白ラベル)になる。
    /// 画面の右下隅へ寄せて、ゲーム一覧・中断メニュー等の上に重ねる用途。
    /// </summary>
    public bool IsOverlay
    {
        get => _isOverlay;
        set { _isOverlay = value; ApplyAppearance(); }
    }

    /// <summary>
    /// true にすると、上端に区切り線、左端にコントローラーアイコンを付ける
    /// (Switchのホーム/設定画面の最下段と同じ)。横幅いっぱいに置くこと。
    /// </summary>
    /// <summary>
    /// <see cref="IsOverlay"/>と一緒に使う。true にすると、帯と配色を白黒反転する(明るい半透明の帯+濃色の丸/濃色ラベル)。
    /// 暗い背景の画面(ダークテーマ)の右下に重ねる用途。
    /// </summary>
    public bool IsOverlayLight
    {
        get => _isOverlayLight;
        set { _isOverlayLight = value; ApplyAppearance(); }
    }

    private bool _isOverlayLight;

    public bool ShowFooterFrame
    {
        get => _showFooterFrame;
        set { _showFooterFrame = value; ApplyAppearance(); }
    }

    private void ApplyAppearance()
    {
        // 配色(丸/文字/ラベル)はテーマ辞書(Styles/HintTheme.xaml)で決まる。
        // フラット=Light(濃色の丸)、オーバーレイ=Dark(白の丸)。
        // OSのテーマに関係なく常にこの2つのどちらかへ固定する。
        RequestedTheme = _isOverlay && !_isOverlayLight || !_isOverlay && EmulatorLauncher.Views.AppThemeRouter.IsDarkActive
            ? ElementTheme.Dark : ElementTheme.Light;

        if (_isOverlay)
        {
            BarBorder.Background = new SolidColorBrush(_isOverlayLight
                ? Windows.UI.Color.FromArgb(0xE8, 0xF2, 0xF2, 0xF2)
                : Windows.UI.Color.FromArgb(0xD8, 0x1C, 0x1C, 0x1C));
            BarBorder.CornerRadius = new CornerRadius(14, 0, 0, 0);
            BarBorder.Padding = new Thickness(36, 12, 36, 12);
        }
        else
        {
            BarBorder.Background = null;
            BarBorder.CornerRadius = new CornerRadius(0);
            BarBorder.Padding = _showFooterFrame ? new Thickness(0, 18, 0, 0) : new Thickness(0, 8, 0, 8);
        }

        Visibility frameVisibility = _showFooterFrame && !_isOverlay ? Visibility.Visible : Visibility.Collapsed;
        FooterLine.Visibility = frameVisibility;
    }
}

/// <summary>
/// HintBar.Hintsの1件。表示するキー(例: "B")とラベル(例: "もどる")の組。
/// 画面の状態に応じてラベルを差し替えられるよう、INotifyPropertyChangedを実装している
/// (使う側でx:Nameを付け、コードビハインドからLabelを書き換える)。
/// </summary>
public class HintDefinition : INotifyPropertyChanged
{
    /// <summary>Keyにこの値を指定すると、丸バッジの代わりに十字キーのアイコンを表示する。</summary>
    public const string DPadKey = "DPad";

    private string _key = string.Empty;
    private string _label = string.Empty;

    public string Key
    {
        get => _key;
        set { _key = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayKey)); OnPropertyChanged(nameof(BadgeKey)); }
    }

    /// <summary>丸バッジに実際に出す文字。「操作ボタンの入れ替え」がONのときは、A↔Bを入れ替えて表示する
    /// (ランチャー操作では物理Bが「決定」になるため)。</summary>
    public string DisplayKey => EmulatorLauncher.Input.ControllerMappingService.TranslateKeyLabelForLauncher(_key);

    /// <summary>実際に丸バッジに出す文字。DisplayKey(位置を表すA/B/X/Y)を、1Pのコントローラー(Xbox/Switch)の印字にする。PSは記号を別に出す。</summary>
    public string BadgeKey => EmulatorLauncher.Input.InputDeviceTracker.UsesKeyboardLabels
        ? KeyboardLabel(_key)
        : EmulatorLauncher.Input.ControllerGlyphs.TranslateHintKeyForPad(DisplayKey);

    /// <summary>キーボード操作中に丸バッジへ出すキー名(MainWindowのキー割り当てと同じ)。対応しない表示はそのまま。</summary>
    private static string KeyboardLabel(string key) => key switch
    {
        "A" => "Enter",
        "B" => "Esc",
        "X" => "F2",
        "Y" => "F3",
        "Menu" => "F4",
        "L/R" => "F11/F12",
        "L2/R2" => "PgUp/PgDn",
        _ => key,
    };

    /// <summary>このガイドをクリック/タップしたときに押したことにするボタン。対応しない表示(十字キー等)はfalse。</summary>
    public bool TryGetLauncherButton(out EmulatorLauncher.Input.GamepadButton button)
    {
        switch (_key)
        {
            case "A": button = EmulatorLauncher.Input.GamepadButton.A; return true;
            case "B": button = EmulatorLauncher.Input.GamepadButton.B; return true;
            case "X": button = EmulatorLauncher.Input.GamepadButton.X; return true;
            case "Y": button = EmulatorLauncher.Input.GamepadButton.Y; return true;
            case "Menu": button = EmulatorLauncher.Input.GamepadButton.Menu; return true;
            default: button = default; return false;
        }
    }

    /// <summary>1PがPS系のとき、A/B/X/Yを置き換えるPSの記号(○✕△□)。対象外なら空文字。</summary>
    public string PsSymbol => TryPs(out string symbol, out _) ? symbol : string.Empty;

    /// <summary>PS記号の色。</summary>
    public Microsoft.UI.Xaml.Media.Brush PsBrush => TryPs(out _, out var c)
        ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, c.R, c.G, c.B))
        : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

    /// <summary>PS記号を出すか(通常の文字バッジとどちらか一方を表示する)。</summary>
    public Visibility PsGlyphVisibility => TryPs(out _, out _) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NormalGlyphVisibility => TryPs(out _, out _) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility PsCircleVisibility => PsSymbol == "○" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PsCrossVisibility => PsSymbol == "✕" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PsSquareVisibility => PsSymbol == "□" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PsTriangleVisibility => PsSymbol == "△" ? Visibility.Visible : Visibility.Collapsed;

    private bool TryPs(out string symbol, out (byte R, byte G, byte B) color)
    {
        symbol = string.Empty;
        color = default;
        if (EmulatorLauncher.Input.InputDeviceTracker.UsesKeyboardLabels) return false;
        return EmulatorLauncher.Input.ControllerGlyphs.IsPlayStationPrimary()
               && EmulatorLauncher.Input.ControllerGlyphs.TryGetPlayStationSymbol(DisplayKey, out symbol, out color);
    }

    /// <summary>設定(A/B入れ替え)やコントローラーの種類が変わったときに、バッジの表示を作り直す。</summary>
    public void RefreshDisplayKey()
    {
        OnPropertyChanged(nameof(DisplayKey));
        OnPropertyChanged(nameof(BadgeKey));
        OnPropertyChanged(nameof(PsSymbol));
        OnPropertyChanged(nameof(PsBrush));
        OnPropertyChanged(nameof(PsGlyphVisibility));
        OnPropertyChanged(nameof(PsCircleVisibility));
        OnPropertyChanged(nameof(PsCrossVisibility));
        OnPropertyChanged(nameof(PsSquareVisibility));
        OnPropertyChanged(nameof(PsTriangleVisibility));
        OnPropertyChanged(nameof(NormalGlyphVisibility));
    }

    public string Label
    {
        get => _label;
        set { _label = value; OnPropertyChanged(); }
    }

    /// <summary>丸バッジ(キー文字)の表示状態。十字キー指定時は隠す。</summary>
    public Visibility BadgeVisibility => Key == DPadKey ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>十字キーアイコンの表示状態。</summary>
    public Visibility DPadVisibility => Key == DPadKey ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
