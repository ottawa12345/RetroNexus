using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EmulatorLauncher.Controls;
using EmulatorLauncher.Converters;
using EmulatorLauncher.Input;
using EmulatorLauncher.Models;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;

namespace EmulatorLauncher.Views;

/// <summary>
/// コントローラー設定画面(トップ画面のコントローラーアイコンから開く)。使い方はXAMLのコメント参照。
///
/// 左のメニューは「基本設定」と「コントローラ設定」の2つ。
///   ・基本設定: 1Pと2Pの入れ替え、接続中のコントローラー情報。
///   ・コントローラ設定: コンソールを選び、そのコンソールのコントローラー図で各ボタンの割り当てを変える。
///     ゲーム中の呼び出し操作(中断メニュー/巻き戻し/早送り)の組み合わせも、同じ画面で変える。
///     設定はコア毎ではなくコンソール毎(ControllerMappingService)。
///
/// コントローラ設定の中身はコードで組み立てる。図はConsoleControllerLayouts(コンソールごとの座標データ)から
/// 作り、操作できる部品(コンソール行、図のボタン、呼び出し操作の行、「初期状態に戻す」行)を
/// 座標つきの一覧(_items)に入れる。十字キーでは、その座標から「その方向でいちばん近い部品」へ動く。
/// </summary>
public sealed partial class ControllerSettingsPage : Page, IControllerPage
{
    private enum Region
    {
        Category,
        Basic,
        Console,
    }

    private enum FocusKind
    {
        Console,
        Pad,
        Direction,
        Hotkey,
        Keyboard,
        Port1,
        Check,
        Reset,
        Save,
    }

    /// <summary>コントローラ設定の操作できる部品1つ(座標はConsoleCanvas上)。</summary>
    private sealed class FocusItem
    {
        public FocusItem(FocusKind kind, string? key, double x, double y, double w, double h, Border visual)
        {
            Kind = kind; Key = key; X = x; Y = y; W = w; H = h; Visual = visual;
        }

        public FocusKind Kind { get; }

        /// <summary>ボタンの行なら物理ボタン名(GamepadButton)、呼び出し操作ならそのキー。</summary>
        public string? Key { get; }

        public double X { get; }
        public double Y { get; }
        public double W { get; }
        public double H { get; }

        /// <summary>選択枠(水色の枠)を付ける要素。</summary>
        public Border Visual { get; }
    }

    // ---- 画面の配置(ConsoleCanvas上の座標。縦に長いので、ConsoleScrollでスクロールする) ----
    private const double PaneWidth = 1312;
    private const double RowHeight = 84;         // コンソール行
    private const double ListRowHeight = 56;     // ボタンの行・呼び出し操作の行
    private const double ImageTop = 110;         // コントローラー画像の上端
    private const double ImageWidth = 600;
    private const double ImageHeight = 330;
    private const double RightColumnX = 640;     // 右の列(ボタンの一覧と呼び出し操作)
    private const double RightColumnWidth = 672;
    private const double ListTop = 100;         // 右の一覧(スクロールする部分)の上端
    private const double ButtonsHeaderY = 104;
    private const double ButtonsTop = 144;
    private const double ConsolePadTop = 425;   // コンソールのコントローラー(参照)の上端
    private const double HelpTop = 620;      // 説明文(コントローラーの図の下)

    public ObservableCollection<SettingsCategoryItem> Categories { get; } = new();

    private readonly SettingsCategoryItem _basicCategory = new("基本設定");
    private readonly SettingsCategoryItem _consoleCategory = new("コントローラ設定");

    private Region _region = Region.Category;
    private int _categoryIndex;

    /// <summary>基本設定の中で選んでいる行(0=コントローラーの入れ替え、1=振動の強さ、2=コントローラー情報。2では上下キーで情報をスクロールする)。</summary>
    private int _basicIndex;

    // 基本設定の行の番号(上から)。
    private const int BasicSwap = 0, BasicRumble = 1, BasicInfo = 2;

    private const int RumbleStep = 25;
    private const double InfoScrollStep = 240;

    // 画面を離れて戻ってきても、最後に選んだコンソールを覚えておく(アプリ起動中のみ)。
    private static string _lastConsoleId = string.Empty;

    private List<ConsoleInfo> _consoles = new();
    private string _consoleId = string.Empty;

    // ---- ゲーム別モード(ゲーム詳細画面から開く。BeginGameModeで切り替える) ----
    // 画面はそのまま使い、コンソールの行を「ゲーム名(選べない)」にして、左のメニューを隠す。
    // 変更はコンソールの設定ではなく、そのゲームのCustom(差分だけ)へ保存する。
    private bool _gameMode;
    private string _gameTitle = string.Empty;
    private string? _gameCustom;
    private Action<Action<JsonObject>>? _gamePersist;
    private Action? _gameClosed;

    // ---- 保存していない変更(「保存」を押すまでは下書き) ----
    private bool _dirty;
    private string? _gameCustomSaved;                                   // ゲーム別モード: 最後に保存した状態
    private readonly List<Action<JsonObject>> _pendingMutations = new(); // ゲーム別モード: 未保存の変更(保存時にまとめて渡す)
    private TextBlock? _saveValueText;
    private string? _saveError;
    private ControllerLayout _layout = ConsoleControllerLayouts.Get(string.Empty);

    private readonly List<FocusItem> _items = new();
    private PadDiagram? _padDiagram;
    private ConsolePadView? _consolePad;
    private int _focusIndex;
    private readonly Dictionary<string, TextBlock> _padValueTexts = new();
    private readonly Dictionary<string, TextBlock> _dirValueTexts = new();
    private readonly Dictionary<string, TextBlock> _hotkeyValueTexts = new();
    private readonly Dictionary<string, TextBlock> _hotkeyLabelTexts = new();
    private TextBlock? _consoleValueText;
    private TextBlock? _keyboardValueText;
    private TextBlock? _port1ValueText;
    private TextBlock? _helpText;

    /// <summary>1Pのコントローラーの表記(PS系なら○✕□△など)。画面を作るときに決める。</summary>
    private ControllerGlyphs.PadStyle _padStyle = ControllerGlyphs.PadStyle.Standard;

    /// <summary>選択画面/確認ダイアログの処理中は、このページ自身の入力を受け付けない。</summary>
    private bool _busy;

    private readonly BoolToBrushConverter _focusBrushConverter = new();
    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);

    // 「ボタン確認」の行にフォーカスがある間だけ動かす。コントローラーの押下状態を読んで、図の該当ボタンを光らせる。
    private readonly DispatcherTimer _checkTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private HashSet<GamepadButton> _checkHeld = new();

    private readonly DispatcherTimer _infoTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private string _infoSignature = string.Empty;

    public ControllerSettingsPage()
    {
        this.InitializeComponent();
        EmulatorLauncher.Controls.LauncherWheel.AttachAll(this);
        this.NavigationCacheMode = NavigationCacheMode.Required;

        Categories.Add(_basicCategory);
        Categories.Add(_consoleCategory);

        _checkTimer.Tick += (_, _) => PollCheckButtons();
        _infoTimer.Tick += (_, _) =>
        {
            RefreshInfo(force: false);

            // コントローラーの種類(PS系か)が変わったら、ボタンの表記と画像を作り直す。
            if (!_busy && ControllerGlyphs.GetPrimaryPadStyle() != _padStyle) BuildConsolePane();
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _consoles = ExpandFormScopes(SampleData.GetConsoles());
        if (_consoles.Count > 0 && !_consoles.Any(c => c.Id == _lastConsoleId))
        {
            _lastConsoleId = _consoles[0].Id;
        }

        _consoleId = _lastConsoleId;
        _region = Region.Category;
        _categoryIndex = 0;
        _busy = false;
        SwapStatusText.Visibility = Visibility.Collapsed;
        _basicIndex = 0;
        RumbleValueText.Text = RumbleText(ControllerMappingService.Load().RumbleLevel);

        BuildConsolePane();
        UpdateCategoryVisuals();
        RefreshInfo(force: true);
        _infoTimer.Start();
    }

    /// <summary>ゲーム別モードか(ゲーム詳細画面の中に表示している間はtrue)。</summary>
    public bool IsGameMode => _gameMode;

    /// <summary>ゲーム別モードで保持している、最新のCustom(JSON文字列)。</summary>
    public string? GameCustom => _gameCustom;

    /// <summary>
    /// ゲーム詳細画面の中で、このゲームだけのコントローラ設定として使い始める(Frameの移動なしで埋め込む)。
    /// </summary>
    /// <param name="persist">変更のたびに呼ばれる。同じ変更(RomFile.Customへの差分の書き込み)を保存してください。</param>
    /// <param name="closed">Bで画面を閉じるときに呼ばれる。</param>
    public void BeginGameMode(string consoleId, string gameTitle, string? custom,
        Action<Action<JsonObject>> persist, Action closed)
    {
        _gameMode = true;
        _gameTitle = gameTitle;
        _gameCustom = custom;
        _gameCustomSaved = custom;
        _pendingMutations.Clear();
        _dirty = false;
        _saveError = null;
        _gamePersist = persist;
        _gameClosed = closed;

        _consoles = ExpandFormScopes(SampleData.GetConsoles());
        _consoleId = consoleId;
        _categoryIndex = 1;
        _region = Region.Console;
        _busy = false;

        // 左のメニューを隠し、ゲーム別の内容だけを広く出す。
        MenuColumn.Width = new GridLength(0);
        GapColumn.Width = new GridLength(0);
        CategoryMenuBorder.Visibility = Visibility.Collapsed;
        HeaderTitle.Text = "コントローラ設定(このゲームだけ)";
        ConsoleHost.HorizontalAlignment = HorizontalAlignment.Center;   // 左メニューが無い分、内容を左右中央に置く

        BuildConsolePane();
        UpdateCategoryVisuals();
        _infoTimer.Start();
    }

    private void CloseGameMode()
    {
        _infoTimer.Stop();
        Action? closed = _gameClosed;
        _gameClosed = null;
        closed?.Invoke();
    }

    // ---- 設定の書き込み(コンソール別 / ゲーム別を切り替える) ----

    private void Mutate(Action<JsonObject> mutation)
    {
        MarkDirty();
        JsonObject obj = RomCustom.Parse(_gameCustom);
        mutation(obj);
        _gameCustom = RomCustom.Serialize(obj);
        _pendingMutations.Add(mutation);   // 保存を押したときに、まとめて保存する
    }

    /// <summary>設定を変えた。保存するまでは下書き(コンソール別はサービスの編集セッション、ゲーム別は未保存の変更の一覧)。</summary>
    private void MarkDirty()
    {
        if (!_dirty)
        {
            _dirty = true;
            if (!_gameMode) ControllerMappingService.BeginEdit();
        }

        _saveError = null;
        UpdateSaveRow();
    }

    private void UpdateSaveRow()
    {
        if (_saveValueText is null) return;
        _saveValueText.Text = _dirty ? "変更あり" : "変更なし";
    }

    /// <summary>未保存の変更を捨てる(保存済みの状態に戻す)。</summary>
    private void DiscardEdits()
    {
        if (!_dirty) return;
        if (_gameMode)
        {
            _gameCustom = _gameCustomSaved;
            _pendingMutations.Clear();
        }
        else ControllerMappingService.CancelEdit();

        _dirty = false;
        _saveError = null;
        UpdateSaveRow();
    }

    /// <summary>未保存の変更があれば、捨ててよいか確認する。続行してよければtrue(捨てた場合は表示も戻す)。</summary>
    private async Task<bool> ResolveDirtyAsync()
    {
        if (!_dirty) return true;

        string message = "保存していない変更があります。変更を捨てて移動しますか?";
        List<string> missing = FindUnassigned();
        if (missing.Count > 0)
        {
            message += "\n\n※ 割り当てのないボタンがあるため、このままでは保存できません: " + string.Join("、", missing);
        }

        bool ok = await ConfirmOverlay.ShowAsync(message, "変更を捨てる", "編集を続ける");
        if (!ok) return false;

        DiscardEdits();
        RefreshValues();
        return true;
    }

    /// <summary>このスコープで使うボタンのうち、割り当てが「なし」のものの表示名。</summary>
    private List<string> FindUnassigned()
    {
        Dictionary<string, GamepadButton?> assignments = ControllerMappingService.GetAssignments(_consoleId, _gameCustom);
        var missing = new List<string>();
        foreach ((string key, string label) in Targets())
        {
            if (!assignments.TryGetValue(key, out GamepadButton? physical) || physical is null) missing.Add(label);
        }

        return missing;
    }

    /// <summary>保存前の注意(保存は妨げない): 同じボタンが複数のゲームのボタンに割り当たっている/呼び出し操作がゲームのボタンと重なっている。</summary>
    private List<string> FindOverlaps()
    {
        var warnings = new List<string>();
        Dictionary<string, GamepadButton?> assignments = ControllerMappingService.GetAssignments(_consoleId, _gameCustom);
        List<(string Key, string Label)> targets = Targets();

        foreach (IGrouping<GamepadButton, string> g in targets
                     .Where(t => assignments.TryGetValue(t.Key, out GamepadButton? p) && p is not null)
                     .GroupBy(t => assignments[t.Key]!.Value, t => t.Label)
                     .Where(g => g.Count() > 1))
        {
            warnings.Add($"{PadLabel(g.Key)} が複数のボタンに割り当たっています({string.Join("、", g)})");
        }

        foreach (string key in ControllerMappingService.HotkeyKeys.Where(k => ControllerProfiles.IsHotkeyAvailable(_consoleId, k)))
        {
            GamepadButton[] combo = ControllerMappingService.GetHotkey(_consoleId, key, _gameCustom);
            if (HotkeyOverlapsGameButtons(combo))
            {
                warnings.Add($"{HotkeyLabel(key)}({DescribeCombo(combo)})が、ゲームのボタンと重なっています");
            }
        }

        return warnings;
    }

    /// <summary>「保存」: 使うボタンがすべて割り当て済みなら確定する。「なし」が残っていれば保存しない。</summary>
    private async Task SaveEditsAsync()
    {
        if (!_dirty) return;

        List<string> missing = FindUnassigned();
        if (missing.Count > 0)
        {
            _saveError = "割り当てのないボタンがあるため保存できません: " + string.Join("、", missing);
            if (_saveValueText is not null) _saveValueText.Text = "未設定あり";
            UpdateHelp();
            await ConfirmOverlay.AlertAsync(
                "割り当てのないボタンがあるため、保存できません。\n\n未設定: " + string.Join("、", missing)
                + "\n\nコントローラのボタンを選んで、割り当ててください。");
            return;
        }

        // 重複は保存を妨げないが、意図したものか確認する。
        List<string> overlaps = FindOverlaps();
        if (overlaps.Count > 0)
        {
            bool saveAnyway = await ConfirmOverlay.ShowAsync(
                "次の重複があります。\n\n・" + string.Join("\n・", overlaps) + "\n\nこのまま保存しますか?",
                "このまま保存する", "戻って直す");
            if (!saveAnyway) return;
        }

        if (_gameMode)
        {
            foreach (Action<JsonObject> m in _pendingMutations) _gamePersist?.Invoke(m);
            _pendingMutations.Clear();
            _gameCustomSaved = _gameCustom;
        }
        else ControllerMappingService.CommitEdit();

        _dirty = false;
        _saveError = null;
        UpdateSaveRow();
        if (_saveValueText is not null) _saveValueText.Text = "保存しました";
    }

    /// <summary>Bボタン等で画面/ペインを離れる。未保存の変更があれば確認する。</summary>
    private async Task LeaveConsolePaneAsync(bool closeGame)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (!await ResolveDirtyAsync()) return;
            if (closeGame) CloseGameMode();
            else
            {
                _region = Region.Category;
                UpdateCategoryVisuals();
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void WriteButton(string key, GamepadButton physical)
    {
        string consoleId = _consoleId;
        MarkDirty();
        if (_gameMode) Mutate(c => ControllerMappingService.SetGameAssignment(c, consoleId, key, physical));
        else ControllerMappingService.SetButton(consoleId, key, physical);
    }

    private void WriteButtonNone(string key)
    {
        string consoleId = _consoleId;
        MarkDirty();
        if (_gameMode) Mutate(c => ControllerMappingService.SetGameAssignment(c, consoleId, key, null));
        else ControllerMappingService.SetButtonNone(consoleId, key);
    }

    private void WriteTurbo(string key, GamepadButton? physical)
    {
        string consoleId = _consoleId;
        MarkDirty();
        if (_gameMode) Mutate(c => ControllerMappingService.SetGameTurbo(c, consoleId, key, physical));
        else ControllerMappingService.SetTurbo(consoleId, key, physical);
    }

    private string KeyboardValueText()
        => ControllerMappingService.IsKeyboardEnabled(_consoleId, _gameCustom) ? "オン" : "オフ";

    /// <summary>キーボード入力をコアへ渡す設定を切り替える(Aボタン/クリック)。</summary>
    private void ToggleKeyboard()
    {
        string consoleId = _consoleId;
        bool enabled = !ControllerMappingService.IsKeyboardEnabled(consoleId, _gameCustom);
        MarkDirty();
        if (_gameMode) Mutate(c => ControllerMappingService.SetGameKeyboard(c, consoleId, enabled));
        else ControllerMappingService.SetKeyboard(consoleId, enabled);
        RefreshValues();
    }

    private string Port1ValueText()
    {
        Port1Option? option = ControllerProfiles.FindPort1Option(_consoleId);
        string? id = ControllerMappingService.GetPort1DeviceId(_consoleId, _gameCustom);
        return option?.FindDevice(id)?.Label ?? string.Empty;
    }

    /// <summary>ポート1の装置を、一覧の次のものへ切り替える(Aボタン/クリック)。</summary>
    private void CyclePort1Device()
    {
        string consoleId = _consoleId;
        Port1Option? option = ControllerProfiles.FindPort1Option(consoleId);
        if (option is null) return;

        string? current = ControllerMappingService.GetPort1DeviceId(consoleId, _gameCustom);
        int index = option.Devices.FindIndex(d => string.Equals(d.Id, current, StringComparison.OrdinalIgnoreCase));
        string next = option.Devices[(index + 1) % option.Devices.Count].Id;
        MarkDirty();
        if (_gameMode) Mutate(c => ControllerMappingService.SetGamePort1Device(c, consoleId, next));
        else ControllerMappingService.SetPort1Device(consoleId, next);
        RefreshValues();
    }

    private void WriteHotkey(string key, GamepadButton[] combo)
    {
        string consoleId = _consoleId;
        MarkDirty();
        if (_gameMode) Mutate(c => ControllerMappingService.SetGameHotkey(c, consoleId, key, combo));
        else ControllerMappingService.SetHotkey(consoleId, key, combo);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _infoTimer.Stop();
        _checkTimer.Stop();
        _checkHeld.Clear();
        if (!_gameMode) DiscardEdits();   // 保存していない変更は残さない
    }

    // ==================================================================
    // 左メニュー / 操作領域
    // ==================================================================

    private readonly PaneFader _paneFader = new();
    private int _shownPane = -1;

    /// <summary>右ペインの切り替え。カテゴリが変わったときだけフェードアウト→フェードインする。</summary>
    private void ShowCategoryPane(int index)
    {
        if (index == _shownPane) return;
        bool first = _shownPane < 0;
        _shownPane = index;
        if (first)
        {
            // 画面を開いた直後はアニメーションせず即時に表示する。
            BasicPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            ConsolePanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        _ = _paneFader.SwitchAsync(() =>
        {
            BasicPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            ConsolePanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        }, BasicPanel, ConsolePanel);
    }

    /// <summary>選択中の行の背景。標準は白、ダークテーマの画面では文字が読める明るめのグレー。</summary>
    private static SolidColorBrush FocusFillBrush()
        => new(AppThemeRouter.IsDarkActive ? Windows.UI.Color.FromArgb(255, 0x36, 0x40, 0x5A) : Colors.White);

    /// <summary>左メニューの下の「前の画面に戻る」ボタンにフォーカスがあるか(Category領域の中の状態)。</summary>
    private bool _backFocused;

    /// <summary>
    /// 「前の画面に戻る」ボタンのフォーカス表示を切り替える(他の行と同じ、塗り+水色の枠)。
    /// 枠は、<paramref name="slideFrom"/>(直前に選んでいた行)の位置から、ボタンへ滑って来る。
    /// </summary>
    private void SetBackFocus(bool focused, UIElement? slideFrom = null)
    {
        _backFocused = focused;
        BackToPreviousFocusFrame.BorderBrush = GetFocusBrush();
        BackToPreviousFocusFrame.Background = FocusFillBrush();
        BackToPreviousFocusFrame.Visibility = focused ? Visibility.Visible : Visibility.Collapsed;
        FocusSlide.SetIsFocused(BackToPreviousFocusFrame, focused);
        UpdateCategoryVisuals();
        if (focused && slideFrom != null)
        {
            BackToPreviousFocusFrame.UpdateLayout();
            HighlightSlide.SlideElement(BackToPreviousFocusFrame, slideFrom);
        }
    }

    private void GoBackFromButton()
    {
        if (Frame != null && Frame.CanGoBack) Frame.GoBack();
    }

    private void UpdateCategoryVisuals()
    {
        // 左メニュー以外の領域へ移った(またはタップで選び直した)ら、ボタンのフォーカスは外す。
        if (_backFocused && _region != Region.Category)
        {
            _backFocused = false;
            BackToPreviousFocusFrame.Visibility = Visibility.Collapsed;
            FocusSlide.SetIsFocused(BackToPreviousFocusFrame, false);
        }
        _basicCategory.IsActive = _categoryIndex == 0;
        _consoleCategory.IsActive = _categoryIndex == 1;
        _basicCategory.IsFocused = _region == Region.Category && !_backFocused && _categoryIndex == 0;
        _consoleCategory.IsFocused = _region == Region.Category && !_backFocused && _categoryIndex == 1;

        ShowCategoryPane(_categoryIndex);

        bool swapFocused = _region == Region.Basic && _basicIndex == 0;
        SwapEntryBorder.Background = swapFocused ? FocusFillBrush() : TransparentBrush;
        SwapEntryBorder.BorderBrush = swapFocused ? GetFocusBrush() : TransparentBrush;
        FocusSlide.SetIsFocused(SwapEntryBorder, swapFocused);

        bool rumbleFocused = _region == Region.Basic && _basicIndex == 1;
        RumbleEntryBorder.Background = rumbleFocused ? FocusFillBrush() : TransparentBrush;
        RumbleEntryBorder.BorderBrush = rumbleFocused ? GetFocusBrush() : TransparentBrush;
        FocusSlide.SetIsFocused(RumbleEntryBorder, rumbleFocused);

        bool infoFocused = _region == Region.Basic && _basicIndex == BasicInfo;
        InfoHeaderBorder.Background = infoFocused ? FocusFillBrush() : TransparentBrush;
        InfoHeaderBorder.BorderBrush = infoFocused ? GetFocusBrush() : TransparentBrush;
        FocusSlide.SetIsFocused(InfoHeaderBorder, infoFocused);

        // 「コントローラー情報」を選んだら、見出しが上に来るまでスクロールして、1P/2Pの情報を見せる。
        // ほかの行を選んだときは、いちばん上へ戻す。
        if (_region == Region.Basic)
        {
            if (_basicIndex == BasicInfo) ScrollBasicToInfo();
            else BasicScroll.ChangeView(null, 0, null);
        }

        UpdateItemFocus();
    }

    private void CategoryItem_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (_busy) return;
        if (sender is not FrameworkElement { DataContext: SettingsCategoryItem item }) return;

        int index = Categories.IndexOf(item);
        if (index < 0) return;

        _ = SelectCategoryByTapAsync(index);
    }

    private async Task SelectCategoryByTapAsync(int index)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            // コントローラ設定に未保存の変更があるまま別のカテゴリへ移るときは、捨ててよいか確認する。
            if (index != _categoryIndex && !await ResolveDirtyAsync()) return;
            _categoryIndex = index;
            _region = Region.Category;
            _backFocused = false;
            UpdateCategoryVisuals();
        }
        finally
        {
            _busy = false;
        }
    }

    private void SwapEntry_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (_busy) return;

        _categoryIndex = 0;
        _basicIndex = 0;
        _region = Region.Basic;
        UpdateCategoryVisuals();
        DoSwap();
    }

    private void RumbleEntry_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (_busy) return;

        _categoryIndex = 0;
        _basicIndex = 1;
        _region = Region.Basic;
        UpdateCategoryVisuals();
        ChangeRumbleLevel(RumbleStep, wrap: true);
    }

    private void BackToPreviousButton_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (_busy) return;
        if (Frame != null && Frame.CanGoBack) Frame.GoBack();
    }

    private void EnterPane()
    {
        if (_categoryIndex == 0)
        {
            _basicIndex = 0;
            _region = Region.Basic;
        }
        else
        {
            _region = Region.Console;
            _focusIndex = Math.Clamp(_focusIndex, 0, Math.Max(_items.Count - 1, 0));
        }

        UpdateCategoryVisuals();
    }

    // ==================================================================
    // ゲームパッド入力
    // ==================================================================

    public void OnGamepadButton(GamepadButton button)
    {
        // 選択画面/確認ダイアログが開いている間は、そちらへ入力を渡す。
        if (ConfirmOverlay.IsOpen)
        {
            ConfirmOverlay.HandleGamepadInput(button);
            return;
        }

        if (ChoiceOverlay.IsOpen)
        {
            ChoiceOverlay.HandleGamepadInput(button);
            return;
        }

        if (HotkeyEditor.IsOpen)
        {
            HotkeyEditor.HandleGamepadInput(button);
            return;
        }

        if (_busy) return;

        switch (_region)
        {
            case Region.Category:
                HandleCategoryInput(button);
                break;
            case Region.Basic:
                HandleBasicInput(button);
                break;
            case Region.Console:
                HandleConsoleInput(button);
                break;
        }
    }

    public void OnGamepadButtonLongPress(GamepadButton button)
    {
        // この画面に長押し操作は無い。
    }

    private void HandleCategoryInput(GamepadButton button)
    {
        // 「前の画面に戻る」ボタンにフォーカスがあるとき。上=最後の項目、下=先頭の項目へ戻り、A/Bで前の画面へ。左右は何もしない。
        if (_backFocused)
        {
            switch (button)
            {
                case GamepadButton.DPadUp:
                    SelectCategoryFromBackButton(Categories.Count - 1);
                    break;
                case GamepadButton.DPadDown:
                    SelectCategoryFromBackButton(0);
                    break;
                case GamepadButton.A:
                case GamepadButton.B:
                    GoBackFromButton();
                    break;
            }

            return;
        }

        switch (button)
        {
            case GamepadButton.B:
                GoBackFromButton();
                break;
            case GamepadButton.DPadUp:
                if (_categoryIndex <= 0)
                {
                    // 先頭からさらに上へ: 「前の画面に戻る」へ。
                    SetBackFocus(true, CategoryItemsControl.ContainerFromIndex(_categoryIndex) as UIElement);
                    break;
                }

                _categoryIndex--;
                UpdateCategoryVisuals();
                SlideCategoryHighlight(_categoryIndex + 1);
                break;
            case GamepadButton.DPadDown:
                if (_categoryIndex >= Categories.Count - 1)
                {
                    // 末尾からさらに下へ: 「前の画面に戻る」へ。
                    SetBackFocus(true, CategoryItemsControl.ContainerFromIndex(_categoryIndex) as UIElement);
                    break;
                }

                _categoryIndex++;
                UpdateCategoryVisuals();
                SlideCategoryHighlight(_categoryIndex - 1);
                break;
            case GamepadButton.DPadRight:
            case GamepadButton.A:
                EnterPane();
                break;
        }
    }

    /// <summary>「前の画面に戻る」ボタンから、左メニューの<paramref name="index"/>番目へ戻る。枠はボタンの位置から滑って来る。</summary>
    private void SelectCategoryFromBackButton(int index)
    {
        _backFocused = false;
        BackToPreviousFocusFrame.Visibility = Visibility.Collapsed;
        FocusSlide.SetIsFocused(BackToPreviousFocusFrame, false);
        _categoryIndex = index;
        UpdateCategoryVisuals();
        CategoryItemsControl.UpdateLayout();
        HighlightSlide.SlideRow(BackToPreviousHost, CategoryItemsControl.ContainerFromIndex(index) as UIElement);
    }

    private void HandleBasicInput(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.B:
                _region = Region.Category;
                UpdateCategoryVisuals();
                break;
            case GamepadButton.DPadLeft:
                if (_basicIndex == BasicRumble)
                {
                    ChangeRumbleLevel(-RumbleStep, wrap: false);
                    break;
                }

                _region = Region.Category;
                UpdateCategoryVisuals();
                break;
            case GamepadButton.DPadRight:
                if (_basicIndex == BasicRumble) ChangeRumbleLevel(RumbleStep, wrap: false);
                else ShakeAnimation.Shake(_basicIndex == BasicInfo ? InfoHeaderBorder : SwapEntryBorder, vertical: false);
                break;
            case GamepadButton.A:
                if (_basicIndex == BasicSwap) DoSwap();
                else if (_basicIndex == BasicRumble) ChangeRumbleLevel(RumbleStep, wrap: true);
                break;
            case GamepadButton.DPadUp:
                if (_basicIndex > 0)
                {
                    _basicIndex--;
                    UpdateCategoryVisuals();
                }
                else
                {
                    ShakeAnimation.Shake(SwapEntryBorder, vertical: true);
                }

                break;
            case GamepadButton.DPadDown:
                if (_basicIndex < BasicInfo)
                {
                    _basicIndex++;
                    UpdateCategoryVisuals();
                }
                else if (BasicScroll.VerticalOffset < BasicScroll.ScrollableHeight - 1)
                {
                    // 下に隠れている情報(2Pなど)を、順に見せる。
                    BasicScroll.ChangeView(null, Math.Min(BasicScroll.ScrollableHeight, BasicScroll.VerticalOffset + InfoScrollStep), null);
                }
                else
                {
                    ShakeAnimation.Shake(InfoHeaderBorder, vertical: true);
                }

                break;
        }
    }

    /// <summary>左メニューの選択中の項目の枠を揺らす(先頭/末尾からさらに先へ動かそうとしたとき)。</summary>
    /// <summary>左メニューの選択の印を、前の行(<paramref name="oldIndex"/>)から現在の行へ滑らせる。</summary>
    private void SlideCategoryHighlight(int oldIndex)
        => HighlightSlide.SlideRow(CategoryItemsControl.ContainerFromIndex(oldIndex) as UIElement,
            CategoryItemsControl.ContainerFromIndex(_categoryIndex) as UIElement);

    private void ShakeCategoryRow(bool vertical)
    {
        ShakeAnimation.Shake(CategoryItemsControl.ContainerFromIndex(_categoryIndex) as UIElement, vertical);
    }

    /// <summary>コントローラ設定で選択中の部品を揺らす(これ以上その方向へ動かせないとき)。</summary>
    private void ShakeFocusedItem(bool vertical)
    {
        if (_focusIndex < 0 || _focusIndex >= _items.Count) return;
        ShakeAnimation.Shake(_items[_focusIndex].Visual, vertical);
    }

    private void HandleConsoleInput(GamepadButton button)
    {
        // 「ボタン確認」の行では、ボタンを押すと図が光るだけ。決定・もどる・左右は効かせず、上下で他の項目へ移る。
        if (_focusIndex >= 0 && _focusIndex < _items.Count && _items[_focusIndex].Kind == FocusKind.Check)
        {
            if (button == GamepadButton.DPadUp) { if (!MoveFocus(0, -1)) ShakeFocusedItem(vertical: true); }
            else if (button == GamepadButton.DPadDown) { if (!MoveFocus(0, 1)) ShakeFocusedItem(vertical: true); }
            return;
        }

        switch (button)
        {
            case GamepadButton.B:
                _ = LeaveConsolePaneAsync(closeGame: _gameMode);
                break;
            case GamepadButton.A:
                if (_focusIndex >= 0 && _focusIndex < _items.Count)
                {
                    _ = ActivateItemAsync(_items[_focusIndex]);
                }
                break;
            case GamepadButton.DPadUp:
                if (!MoveFocus(0, -1)) ShakeFocusedItem(vertical: true);
                break;
            case GamepadButton.DPadDown:
                if (!MoveFocus(0, 1)) ShakeFocusedItem(vertical: true);
                break;
            case GamepadButton.DPadRight:
                if (!MoveFocus(1, 0)) ShakeFocusedItem(vertical: false);
                break;
            case GamepadButton.DPadLeft:
                if (!MoveFocus(-1, 0))
                {
                    if (_gameMode)
                    {
                        ShakeFocusedItem(vertical: false);
                        break;
                    }

                    // 左にもう部品が無いときは、左のメニューへ戻る(未保存の変更があれば確認する)。
                    _ = LeaveConsolePaneAsync(closeGame: false);
                }

                break;
        }
    }

    // ==================================================================
    // 基本設定
    // ==================================================================

    private void DoSwap()
    {
        string message;
        try
        {
            message = GamepadSlots.SwapSlots()
                ? "1Pと2Pを入れ替えました。"
                : "コントローラが接続されていません。";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ControllerSettingsPage] 入れ替えに失敗しました: {ex.Message}");
            message = "入れ替えに失敗しました。";
        }

        SwapStatusText.Text = message;
        SwapStatusText.Visibility = Visibility.Visible;
        RefreshInfo(force: true);
    }

    /// <summary>「コントローラー情報」の見出しが、表示領域の上端に来るまでスクロールする(下が足りなければ末尾まで)。</summary>
    private void ScrollBasicToInfo()
    {
        try
        {
            Windows.Foundation.Point p = InfoHeaderBorder.TransformToVisual(BasicScroll)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            double target = Math.Clamp(BasicScroll.VerticalOffset + p.Y - 8, 0, BasicScroll.ScrollableHeight);
            BasicScroll.ChangeView(null, target, null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ControllerSettingsPage] 情報へのスクロールに失敗しました: {ex.Message}");
        }
    }

    private static string RumbleText(int level) => level <= 0 ? "オフ" : $"{level}%";

    /// <summary>
    /// 振動の強さを変えて保存し、1Pのコントローラーを短く振動させて強さを確かめられるようにする。
    /// wrap=true(Aボタン/タップ)は、100%の次は0%へ戻る。wrap=false(左右)は端で揺らして止まる。
    /// </summary>
    private void ChangeRumbleLevel(int delta, bool wrap)
    {
        ControllerMappingService.ControllerMappingData data = ControllerMappingService.Load();
        int next = Math.Clamp(data.RumbleLevel, 0, 100) + delta;
        if (next > 100)
        {
            if (!wrap)
            {
                ShakeAnimation.Shake(RumbleEntryBorder, vertical: false);
                return;
            }

            next = 0;
        }
        else if (next < 0)
        {
            ShakeAnimation.Shake(RumbleEntryBorder, vertical: false);
            return;
        }

        data.RumbleLevel = next;
        ControllerMappingService.Save(data);
        RumbleValueText.Text = RumbleText(next);
        TestRumble(next);
    }

    /// <summary>設定した強さで、1Pのコントローラーを約0.25秒振動させる(0%のときは何もしない)。</summary>
    private static void TestRumble(int level)
    {
        if (level <= 0) return;

        try
        {
            nint pad = GamepadSlots.GetHandle(0);
            if (pad == nint.Zero) return;

            ushort strength = (ushort)(0xC000 * level / 100);
            Sdl.SDL_RumbleGamepad(pad, strength, strength, 250);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ControllerSettingsPage] 振動の確認に失敗しました: {ex.Message}");
        }
    }

    /// <summary>接続中のコントローラー情報を作り直す(内容が変わったときだけ)。</summary>
    private void RefreshInfo(bool force)
    {
        var infos = new GamepadSlots.GamepadInfo?[GamepadSlots.MaxSlots];
        for (int slot = 0; slot < infos.Length; slot++)
        {
            try
            {
                infos[slot] = GamepadSlots.GetInfo(slot);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ControllerSettingsPage] コントローラ情報の取得に失敗しました: {ex.Message}");
            }
        }

        string signature = string.Join("|", infos.Select(i => i is null
            ? "-"
            : $"{i.Name}/{i.TypeName}/{i.VendorId}/{i.ProductId}/{i.Serial}"));
        if (!force && signature == _infoSignature) return;
        _infoSignature = signature;

        ControllerInfoPanel.Children.Clear();
        for (int slot = 0; slot < infos.Length; slot++)
        {
            ControllerInfoPanel.Children.Add(new TextBlock
            {
                Text = $"{slot + 1}P",
                FontSize = 28,
                Foreground = ResolveBrush("AppTextPrimaryBrush", 0x2D, 0x2D, 0x2D),
                Margin = new Thickness(0, slot == 0 ? 0 : 24, 0, 4),
            });

            GamepadSlots.GamepadInfo? info = infos[slot];
            if (info is null)
            {
                ControllerInfoPanel.Children.Add(new TextBlock
                {
                    Text = "未接続",
                    Style = (Style)Resources["InfoLabelTextStyle"],
                    Margin = new Thickness(48, 4, 0, 4),
                });
                continue;
            }

            AddInfoLine("名前", info.Name);
            AddInfoLine("種類", info.TypeName);
            AddInfoLine("ベンダーID / 製品ID", $"0x{info.VendorId:X4} / 0x{info.ProductId:X4}");
            AddInfoLine("シリアル", info.Serial ?? "取得できません");
        }
    }

    private void AddInfoLine(string label, string value)
    {
        var grid = new Grid { Margin = new Thickness(48, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 300 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelText = new TextBlock { Text = label, Style = (Style)Resources["InfoLabelTextStyle"] };
        var valueText = new TextBlock { Text = value, Style = (Style)Resources["InfoValueTextStyle"] };
        Grid.SetColumn(valueText, 1);

        grid.Children.Add(labelText);
        grid.Children.Add(valueText);
        ControllerInfoPanel.Children.Add(grid);
    }

}
