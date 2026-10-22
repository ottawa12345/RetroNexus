using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using EmulatorLauncher.Input;
using EmulatorLauncher.Plugins;
using EmulatorLauncher.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;

namespace EmulatorLauncher.Controls;

/// <summary>
/// システム画面の項目の一覧(Id, 表示名)。「システム項目の編集」で表示/非表示(プラグインは有効/無効)を選び、
/// 「システム項目の並び替え」で並びを変える。プラグインの項目Idは <c>plugin:&lt;id&gt;</c>。
/// </summary>
public static class SystemItemCatalog
{
    public static readonly (string Id, string Label)[] All =
    {
        ("cpu", "CPU使用率"),
        ("memory", "メモリ使用率"),
        ("wifi", "Wi-Fi"),
        ("bt", "Bluetooth"),
        ("power", "電源モード"),
        ("volume", "本体の音量"),
        ("brightness", "画面の明るさ"),
        ("sleep", "スリープ"),
        ("restart", "再起動"),
        ("shutdown", "シャットダウン"),
        ("pads", "コントローラー(1P/2P)"),
    };

    public const string PluginPrefix = "plugin:";
    public const string SpacerPrefix = "spacer:";

    public static bool IsPlugin(string id) => id.StartsWith(PluginPrefix, StringComparison.Ordinal);

    /// <summary>空白(何も表示しない1マス)。「システム項目の並び替え」で好きなだけ足せる。項目Idは <c>spacer:&lt;英数字&gt;</c>。</summary>
    public static bool IsSpacer(string id) => id.StartsWith(SpacerPrefix, StringComparison.Ordinal);

    public static string NewSpacerId() => SpacerPrefix + Guid.NewGuid().ToString("N")[..8];

    /// <summary>パネル上の項目の種類。</summary>
    public enum Kind { Tile, Stat, Slider, Spacer }

    /// <summary>項目の見た目の種類・横の幅(6マス中のマス数)・タイルの最小の高さ。パネルの実際の並びと、並び替え画面のプレビューで共通に使う。</summary>
    public static (Kind Kind, int Span, double Height) Describe(string id, SystemPluginDefinition? plugin = null)
    {
        if (IsSpacer(id)) return (Kind.Spacer, 2, 0);
        if (plugin is not null)
        {
            int span = Math.Clamp(plugin.EffectiveWidth, 1, 3) * 2;
            if (plugin.Display == "slider") return (Kind.Slider, 6, SliderHeight);
            if (plugin.Display == "stat") return (Kind.Stat, span, 152);
            return (Kind.Tile, span, plugin.Options is null ? 108 : 152);
        }
        return id switch
        {
            "cpu" or "memory" => (Kind.Stat, 2, 152),
            "volume" or "brightness" => (Kind.Slider, 6, SliderHeight),
            "sleep" or "restart" or "shutdown" => (Kind.Tile, 2, 108),
            _ => (Kind.Tile, 2, 152),
        };
    }

    /// <summary>スライダーの行の高さ(目安。プレビューの縦の比率にだけ使う)。</summary>
    public const double SliderHeight = 112;

    /// <summary>
    /// 並び替え画面の結果(newVisible: 表示中の項目の新しい並び。空白の追加・削除を含む)を、全体の並び(full)へ反映する。
    /// 表示していない項目(非表示・無効)は、元の「直前の表示項目」の後ろにくっつけて動かす。
    /// </summary>
    public static List<string> MergeOrder(IReadOnlyList<string> full, ISet<string> visibleOld, IReadOnlyList<string> newVisible)
    {
        // 表示していない項目の「直前の表示項目」(先頭ならnull)。
        var anchorOf = new Dictionary<string, string?>();
        var prevVisibleOf = new Dictionary<string, string?>();   // 表示項目→その直前の表示項目
        string? last = null;
        foreach (string id in full)
        {
            if (visibleOld.Contains(id)) { prevVisibleOf[id] = last; last = id; }
            else anchorOf[id] = last;
        }

        var newSet = new HashSet<string>(newVisible);
        // 空白を削除してアンカーが無くなったら、さらに前の表示項目へたどる。
        string? Resolve(string? anchor)
        {
            while (anchor is not null && !newSet.Contains(anchor))
                anchor = prevVisibleOf.TryGetValue(anchor, out string? p) ? p : null;
            return anchor;
        }

        var hiddenAfter = new Dictionary<string, List<string>>();
        var hiddenAtStart = new List<string>();
        foreach (var kv in anchorOf)
        {
            string? anchor = Resolve(kv.Value);
            if (anchor is null) hiddenAtStart.Add(kv.Key);
            else
            {
                if (!hiddenAfter.TryGetValue(anchor, out List<string>? list)) hiddenAfter[anchor] = list = new List<string>();
                list.Add(kv.Key);
            }
        }
        // 元の並びを保つ(辞書の順は不定なため)。
        int Index(string id) => full.ToList().IndexOf(id);
        hiddenAtStart.Sort((a, b) => Index(a).CompareTo(Index(b)));
        foreach (List<string> list in hiddenAfter.Values) list.Sort((a, b) => Index(a).CompareTo(Index(b)));

        var result = new List<string>(hiddenAtStart);
        foreach (string id in newVisible)
        {
            result.Add(id);
            if (hiddenAfter.TryGetValue(id, out List<string>? list)) result.AddRange(list);
        }
        return result;
    }

    /// <summary>標準の並び: 内蔵の項目(下部固定のコントローラー表示を除く)、続けてプラグイン(order順)。</summary>
    public static List<string> DefaultOrder(IEnumerable<SystemPluginDefinition> plugins)
    {
        var list = All.Where(i => i.Id != "pads").Select(i => i.Id).ToList();
        list.AddRange(plugins.Select(p => p.ItemId));
        return list;
    }

    /// <summary>保存された並び(saved)を優先し、そこに無い項目は標準の並びで末尾に付ける。存在しない項目Idは捨てる。</summary>
    public static List<string> Ordered(IEnumerable<string>? saved, IEnumerable<SystemPluginDefinition> plugins)
    {
        List<string> def = DefaultOrder(plugins);
        var remaining = new HashSet<string>(def);
        var result = new List<string>();
        var spacers = new HashSet<string>();
        if (saved is not null)
            foreach (string id in saved)
            {
                if (IsSpacer(id)) { if (spacers.Add(id)) result.Add(id); }   // 空白は、保存された並びにあるものだけ
                else if (remaining.Remove(id)) result.Add(id);
            }
        result.AddRange(def.Where(remaining.Contains));
        return result;
    }
}

/// <summary>タイル1つ(Wi-Fi、電源モードの選択肢、スリープ等)。表示だけの項目(CPU等。IsStat)も同じ型で持つ。</summary>
public sealed class SystemTile : INotifyPropertyChanged
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    /// <summary>Segoe Fluent Iconsのグリフ(空ならアイコン無し)。</summary>
    public string Glyph { get; set; } = string.Empty;
    public Visibility GlyphVisibility => string.IsNullOrEmpty(Glyph) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// フォントのグリフではなく、図形(Path)で描くミニアイコンの種類("cpu"/"ram"。空ならなし)。
    /// CPU/RAMのグリフ(EEA1/EEA0)は、Segoe Fluent Iconsの新しい版にしか無く、古いWindowsでは四角(未収録の文字)になるため。
    /// </summary>
    public string VectorIcon { get; set; } = string.Empty;
    public Visibility CpuIconVisibility => VectorIcon == "cpu" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RamIconVisibility => VectorIcon == "ram" ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>パネルの横6マスのうちの幅(CPU/メモリ=3、タイル=2、プラグインはwidth×2)。</summary>
    public int Span { get; set; } = 2;
    public double TileWidth { get; set; } = 202;
    /// <summary>フォーカス時の拡大率(幅の広いタイルは、パネルからはみ出さないよう小さめ)。</summary>
    public string FocusScale => Span >= 6 ? "1.03" : Span >= 4 ? "1.05" : "1.08";

    /// <summary>表示だけの項目(フォーカスは飛ばす)。大きな数値+詳細+バーで描く。</summary>
    public bool IsStat { get; set; }
    /// <summary>空白(何も表示せず、フォーカスも乗らない)。</summary>
    public bool IsSpacer { get; set; }
    /// <summary>環境が対応していない項目(グレーアウトして出すが、フォーカスは乗せない)。</summary>
    public bool NoFocus { get; set; }
    public Visibility NormalVisibility => IsStat || IsSpacer ? Visibility.Collapsed : Visibility.Visible;
    public Visibility StatVisibility => IsStat && !IsSpacer ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BarVisibility { get; set; } = Visibility.Visible;
    /// <summary>大きな数値の文字サイズ(1/3幅のタイルに「12:34:56」のような文字を入れても切れない30が既定。短い数値は大きくできる)。</summary>
    public double StatFontSize { get; set; } = 30;

    private string _statText = "--";
    public string StatText
    {
        get => _statText;
        set { if (_statText == value) return; _statText = value; OnPropertyChanged(); }
    }

    private string _detail = " ";
    public string Detail
    {
        get => _detail;
        set { if (_detail == value) return; _detail = value; OnPropertyChanged(); }
    }

    private double _progress;
    /// <summary>バーの量(0〜100)。</summary>
    public double Progress
    {
        get => _progress;
        set { if (_progress == value) return; _progress = value; OnPropertyChanged(); }
    }

    private bool _isOn;
    /// <summary>オン(Wi-Fiが入っている/今の電源モード)。濃い色で塗る。</summary>
    public bool IsOn
    {
        get => _isOn;
        set { if (_isOn == value) return; _isOn = value; RefreshColors(); }
    }

    private bool _disabled;
    /// <summary>選べない(グレーアウト)。ゲーム中のスリープ/再起動/シャットダウン等。</summary>
    public bool IsDisabled
    {
        get => _disabled;
        set { if (_disabled == value) return; _disabled = value; RefreshColors(); }
    }

    private bool _danger;
    /// <summary>実行の確認待ち(赤で塗る)。</summary>
    public bool Danger
    {
        get => _danger;
        set { if (_danger == value) return; _danger = value; RefreshColors(); }
    }

    private string _subText = string.Empty;
    public string SubText
    {
        get => _subText;
        set { if (_subText == value) return; _subText = value; OnPropertyChanged(); OnPropertyChanged(nameof(SubTextVisibility)); }
    }
    /// <summary>補足の文字が無いタイル(スリープ等)では、その行ぶんの空きを作らない(上下の余白を揃えるため)。</summary>
    public Visibility SubTextVisibility => string.IsNullOrEmpty(_subText) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>この種類のタイルが本来必要とする最小の高さ。補足文字のあるタイルは高め、スリープ等は低め。</summary>
    public double BaseMinHeight { get; set; } = 152;

    /// <summary>実際の最小の高さ(同じ行のタイルの最大値にそろえる)。</summary>
    public double TileMinHeight { get; set; } = 152;

    private bool _isFocused;
    public bool IsFocused
    {
        get => _isFocused;
        set { if (_isFocused == value) return; _isFocused = value; OnPropertyChanged(); }
    }

    private Microsoft.UI.Xaml.Media.Brush _background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
    public Microsoft.UI.Xaml.Media.Brush Background => _background;

    private Microsoft.UI.Xaml.Media.Brush _foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black);
    public Microsoft.UI.Xaml.Media.Brush Foreground => _foreground;

    private bool _dark;

    /// <summary>テーマ(ダークか)に合わせて色を付け直す。</summary>
    public void ApplyTheme(bool dark)
    {
        _dark = dark;
        RefreshColors();
    }

    private void RefreshColors()
    {
        Windows.UI.Color bg, fg;
        if (IsSpacer) { bg = Microsoft.UI.Colors.Transparent; fg = Microsoft.UI.Colors.Transparent; }
        else if (_disabled)
        {
            bg = _dark ? Windows.UI.Color.FromArgb(255, 0x1F, 0x25, 0x34) : Windows.UI.Color.FromArgb(255, 0xEE, 0xEF, 0xF2);
            fg = _dark ? Windows.UI.Color.FromArgb(255, 0x6A, 0x6F, 0x7A) : Windows.UI.Color.FromArgb(255, 0xA8, 0xAC, 0xB4);
        }
        else if (_danger) { bg = Windows.UI.Color.FromArgb(255, 0xC0, 0x39, 0x2B); fg = Microsoft.UI.Colors.White; }
        else if (_isOn) { bg = Windows.UI.Color.FromArgb(255, 0x3B, 0x49, 0xB5); fg = Microsoft.UI.Colors.White; }
        else if (_dark) { bg = Windows.UI.Color.FromArgb(255, 0x26, 0x2E, 0x44); fg = Windows.UI.Color.FromArgb(255, 0xE8, 0xEA, 0xF0); }
        else { bg = Windows.UI.Color.FromArgb(255, 0xE6, 0xE8, 0xEE); fg = Windows.UI.Color.FromArgb(255, 0x22, 0x22, 0x22); }
        _background = new Microsoft.UI.Xaml.Media.SolidColorBrush(bg);
        _foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(fg);
        OnPropertyChanged(nameof(Background));
        OnPropertyChanged(nameof(Foreground));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>パネルの1行。スライダー(音量・明るさ・プラグインの範囲指定)か、タイルの並び(Tiles)のどちらか。</summary>
public sealed class SystemSettingRow : INotifyPropertyChanged
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    /// <summary>Segoe Fluent Iconsのグリフ(スライダー用)。</summary>
    public string Glyph { get; set; } = string.Empty;
    /// <summary>左右キー1回で変わる量(スライダー用)。</summary>
    public int Step { get; set; } = 5;
    /// <summary>スライダーの最小・最大(内蔵は0〜100)。</summary>
    public int Min { get; set; }
    public int Max { get; set; } = 100;
    /// <summary>値の後ろに付ける単位(内蔵は%)。</summary>
    public string Unit { get; set; } = "%";

    /// <summary>タイルの行ならtrue(Tilesを使う)。falseならスライダー。</summary>
    public bool IsTiles { get; set; }
    public ObservableCollection<SystemTile> Tiles { get; } = new();
    public Visibility SliderVisibility => IsTiles ? Visibility.Collapsed : Visibility.Visible;
    public Visibility TilesVisibility => IsTiles ? Visibility.Visible : Visibility.Collapsed;

    private bool _disabled;
    /// <summary>操作できない(グレーアウト)。ゲーム中に使えないプラグイン。</summary>
    public bool IsDisabled
    {
        get => _disabled;
        set { if (_disabled == value) return; _disabled = value; OnPropertyChanged(); OnPropertyChanged(nameof(RowOpacity)); }
    }
    public double RowOpacity => _disabled ? 0.45 : 1.0;
    /// <summary>環境が対応していない項目(フォーカスは乗せない)。</summary>
    public bool NoFocus { get; set; }

    private int _value;
    public int Value
    {
        get => _value;
        set
        {
            value = Math.Clamp(value, Min, Math.Max(Min, Max));
            if (_value == value) return;
            _value = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ValueText));
        }
    }

    // バーの塗りは、値(_value)へ向かってなめらかに伸び縮みする(表示用の値_display)。
    private double _display;

    public string ValueText => $"{_value}{Unit}";

    private double Percent(double v) => Max > Min ? Math.Clamp((v - Min) * 100.0 / (Max - Min), 0, 100) : 0;
    public GridLength FillStar => new(Percent(_display), GridUnitType.Star);
    public GridLength RestStar => new(100 - Percent(_display), GridUnitType.Star);

    /// <summary>バーを、アニメーションなしで今の値に合わせる(表示直後など)。</summary>
    public void SnapDisplay()
    {
        _display = _value;
        OnPropertyChanged(nameof(FillStar));
        OnPropertyChanged(nameof(RestStar));
    }

    /// <summary>バーを、値へ向けて1コマ分近づける。まだ動いている(次のコマが要る)ならtrue。</summary>
    public bool AdvanceDisplay()
    {
        double diff = _value - _display;
        if (Math.Abs(diff) < Math.Max(0.001, (Max - Min) * 0.004))
        {
            if (_display == _value) return false;
            _display = _value;
        }
        else
        {
            _display += diff * 0.3;
        }
        OnPropertyChanged(nameof(FillStar));
        OnPropertyChanged(nameof(RestStar));
        return _display != _value;
    }

    private bool _isFocused;
    public bool IsFocused
    {
        get => _isFocused;
        set { if (_isFocused == value) return; _isFocused = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 画面全体を覆う「システム設定」パネル。使い方はSystemSettingsOverlay.xamlのコメント参照。
/// スライドイン/アウトのアニメーションは、SortOverlayと同じ方式(Compositionで暗幕はフェード、パネルは右からスライド)。
/// 項目(内蔵+有効にしたプラグイン)は、設定した並び順で横6マスに詰めて並べ、入りきらなければスクロールする。
/// </summary>
public sealed partial class SystemSettingsOverlay : UserControl
{
    private const string VolumeKey = "volume";
    private const string BrightnessKey = "brightness";

    private const double SlideInMilliseconds = 280;
    private const double SlideOutMilliseconds = 220;
    private const float MinSlideDistance = 560f;
    private const double SlideOvershoot = 16.0;
    private float _slideDistance = MinSlideDistance;
    private int _animationVersion;

    // 横6マスの並べ方。パネルの内側の幅(636)から、マスの幅を求める。
    private const int GridColumns = 6;
    private const double GridGap = 14;
    private const double PanelInnerWidth = 636;
    private static readonly double GridUnit = (PanelInnerWidth - GridGap * (GridColumns - 1)) / GridColumns;
    private static double SpanWidth(int span) => span * GridUnit + (span - 1) * GridGap;

    private HashSet<string> _hidden = new();
    private HashSet<string> _enabledPlugins = new();
    private List<string> _order = new();
    private readonly Dictionary<string, SystemTile> _tiles = new();
    private readonly Dictionary<string, SystemSettingRow> _sliders = new();
    private readonly HashSet<string> _detected = new();     // 環境が対応していると分かった項目(wifi, bt, power, brightness)
    private readonly Dictionary<string, PluginRuntime> _plugins = new();
    private string _signature = string.Empty;
    private int _showSeq;
    private bool _userMoved;

    private bool Hidden(string id) => _hidden.Contains(id);

    private bool IsShown(string id)
        => SystemItemCatalog.IsSpacer(id) || (SystemItemCatalog.IsPlugin(id) ? _enabledPlugins.Contains(id) : !_hidden.Contains(id));

    private bool IsAvailable(string id)
    {
        if (SystemItemCatalog.IsSpacer(id)) return true;
        if (SystemItemCatalog.IsPlugin(id)) return _plugins.TryGetValue(id, out PluginRuntime? rt) && rt.Available;
        return true;   // 本体の項目は、環境が対応していなくても出す(グレーアウトする)
    }

    /// <summary>環境が対応していない本体の項目(Wi-Fi/Bluetoothの無い機器など)。表示はするが選べない。</summary>
    private bool IsUnsupported(string id)
        => id is "wifi" or "bt" or "power" or "brightness" && !_detected.Contains(id);

    private bool IsVisibleItem(string id) => IsShown(id) && IsAvailable(id);

    private DispatcherTimer? _statusTimer;
    private DispatcherTimer? _statsTimer;
    private DispatcherTimer? _padTimer;
    private DispatcherTimer? _pluginTimer;
    private readonly double[] _padLevels = new double[EmulatorLauncher.Input.GamepadSlots.MaxSlots];
    private DispatcherTimer? _barTimer;

    /// <summary>バーの塗りをなめらかに動かす(約60コマ/秒。全部止まったら自動で止める)。</summary>
    private void StartBarAnimation()
    {
        if (_barTimer is null)
        {
            _barTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _barTimer.Tick += (_, _) =>
            {
                bool any = false;
                foreach (SystemSettingRow row in Rows) any |= row.AdvanceDisplay();
                if (!any) _barTimer?.Stop();
            };
        }
        _barTimer.Start();
    }

    public ObservableCollection<SystemSettingRow> Rows { get; } = new();

    public bool IsOpen { get; private set; }

    /// <summary>ゲーム中に開くか。trueのとき、スリープ/再起動/シャットダウンと、allowInGame=falseのプラグインはグレーアウトして選べない。</summary>
    public bool InGame { get; set; }

    public event EventHandler? Closed;

    public SystemSettingsOverlay()
    {
        this.InitializeComponent();
        EmulatorLauncher.Controls.LauncherWheel.AttachAll(this);
        Visibility = Visibility.Collapsed;
        ElementCompositionPreview.SetIsTranslationEnabled(SystemPanel, true);
    }

    private int _rowIndex;
    private int _tileIndex;
    private SystemSettingRow? _focusedRow;

    private static bool IsDark => EmulatorLauncher.Views.AppThemeRouter.IsDarkActive;

    private static SystemTile MakeTile(string key, string label, string glyph)
    {
        var tile = new SystemTile { Key = key, Label = label, Glyph = glyph };
        tile.ApplyTheme(IsDark);
        return tile;
    }

    // ---- 項目の読み込み(設定・プラグイン・並び順) ----

    /// <summary>表示/有効の設定、プラグインの一覧、並び順を読み直す。</summary>
    private void LoadSettings()
    {
        var prefs = EmulatorLauncher.Emulation.AppPreferencesStore.Load();
        _hidden = new HashSet<string>(prefs.HiddenSystemItems ?? new List<string>());
        _enabledPlugins = new HashSet<string>(prefs.EnabledSystemPlugins ?? new List<string>());
        List<SystemPluginDefinition> defs = LoadPlugins();
        _order = SystemItemCatalog.Ordered(prefs.SystemItemOrder, defs);
    }

    private static DateTime WriteTime(string path)
    {
        try { return System.IO.File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    /// <summary>Pluginsフォルダのjsonを読み直す。ファイルが変わっていないプラグインは、前回の状態をそのまま使う。</summary>
    private List<SystemPluginDefinition> LoadPlugins()
    {
        List<SystemPluginDefinition> defs;
        try
        {
            defs = SystemPluginLoader.LoadAll(msg => AppLog.WarnOnce("plugin:" + msg, "Plugin", msg));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Plugin", "プラグインの読み込みに失敗しました", ex);
            defs = new List<SystemPluginDefinition>();
        }

        var keep = new HashSet<string>();
        foreach (SystemPluginDefinition def in defs)
        {
            keep.Add(def.ItemId);
            DateTime stamp = WriteTime(def.FilePath);
            if (_plugins.TryGetValue(def.ItemId, out PluginRuntime? old) && old.Stamp == stamp && old.Def.FilePath == def.FilePath) continue;
            _plugins[def.ItemId] = new PluginRuntime(def, stamp);
            _tiles.Remove(def.ItemId);
            _sliders.Remove(def.ItemId);
        }
        foreach (string gone in _plugins.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _plugins.Remove(gone);
            _tiles.Remove(gone);
            _sliders.Remove(gone);
        }
        return defs;
    }

    private static string IconGlyph(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return string.Empty;
        icon = icon.Trim();
        // "E767" のような16進のコードでも指定できる(Segoe Fluent Iconsのコード)。
        if (icon.Length is 4 or 5 && int.TryParse(icon, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp)
            && cp is > 0 and <= 0x10FFFF && cp is < 0xD800 or > 0xDFFF)
            return char.ConvertFromUtf32(cp);
        return icon;
    }

    private SystemTile MakeStat(string id, string label, string glyph)
    {
        SystemTile tile = MakeTile(id, label, glyph);
        tile.IsStat = true;
        tile.Span = 2;            // 他のタイルと同じ1/3幅
        tile.StatFontSize = 40;   // 「100%」程度の短い数値なので大きく
        tile.BaseMinHeight = 152;
        tile.StatText = "--%";
        return tile;
    }

    /// <summary>項目(タイル/スライダー)の実体を、まだ無ければ作る。</summary>
    private void EnsureItem(string id)
    {
        if (_tiles.ContainsKey(id) || _sliders.ContainsKey(id)) return;

        if (SystemItemCatalog.IsSpacer(id))
        {
            _tiles[id] = new SystemTile { Key = id, IsSpacer = true, Span = 2, BaseMinHeight = 0 };
            return;
        }

        if (SystemItemCatalog.IsPlugin(id))
        {
            if (_plugins.TryGetValue(id, out PluginRuntime? rt)) CreatePluginItem(rt);
            return;
        }

        switch (id)
        {
            case VolumeKey:
                _sliders[id] = new SystemSettingRow { Key = id, Label = "本体の音量", Glyph = "", Step = 5 };
                break;
            case BrightnessKey:
                _sliders[id] = new SystemSettingRow { Key = id, Label = "画面の明るさ", Glyph = "", Step = 10 };
                break;
            case "cpu":
                _tiles[id] = MakeStat(id, "CPU", string.Empty);
                _tiles[id].VectorIcon = "cpu";   // グリフ(EEA1)は古いWindowsに無いため、図形で描く
                break;
            case "memory":
                _tiles[id] = MakeStat(id, "メモリ", string.Empty);
                _tiles[id].VectorIcon = "ram";   // グリフ(EEA0)は古いWindowsに無いため、図形で描く
                break;
            case "wifi":
                _tiles[id] = MakeTile(id, "Wi-Fi", "");
                break;
            case "bt":
                _tiles[id] = MakeTile(id, "Bluetooth", "");
                break;
            case "power":
                _tiles[id] = MakeTile(id, "電源モード", "");
                break;
            case "sleep":
                _tiles[id] = MakeTile(id, "スリープ", "");
                _tiles[id].BaseMinHeight = 108;
                break;
            case "restart":
                _tiles[id] = MakeTile(id, "再起動", "");
                _tiles[id].BaseMinHeight = 108;
                break;
            case "shutdown":
                _tiles[id] = MakeTile(id, "シャットダウン", "");
                _tiles[id].BaseMinHeight = 108;
                break;
        }
    }

    private void CreatePluginItem(PluginRuntime rt)
    {
        SystemPluginDefinition d = rt.Def;
        string glyph = IconGlyph(d.Icon);

        if (d.Display == "slider" && d.Options is { } o)
        {
            var row = new SystemSettingRow
            {
                Key = d.ItemId, Label = d.Name, Glyph = glyph,
                Min = o.Min, Max = o.Max, Step = Math.Max(1, o.Step), Unit = o.Unit ?? string.Empty,
            };
            row.Value = o.Min;
            row.SnapDisplay();
            _sliders[d.ItemId] = row;
            return;
        }

        SystemTile tile = MakeTile(d.ItemId, d.Name, glyph);
        tile.Span = Math.Clamp(d.EffectiveWidth, 1, 3) * 2;
        if (d.Display == "stat")
        {
            tile.IsStat = true;
            tile.StatFontSize = tile.Span >= 4 ? 40 : 30;   // 広いタイルなら大きく
            tile.BaseMinHeight = 152;
            tile.StatText = "--";
            tile.BarVisibility = d.Bar is null ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            // 選択肢/オンオフのタイルは現在の値を補足に出すので高め、ボタンだけのタイルは低め。
            tile.BaseMinHeight = d.Options is null ? 108 : 152;
        }
        _tiles[d.ItemId] = tile;
    }

    /// <summary>ゲーム中に使えない項目をグレーアウトする。</summary>
    private void ApplyItemState(string id)
    {
        bool disabled;
        if (SystemItemCatalog.IsPlugin(id)) disabled = InGame && _plugins.TryGetValue(id, out PluginRuntime? rt) && !rt.Def.AllowInGame;
        else disabled = (InGame && (id is "sleep" or "restart" or "shutdown")) || IsUnsupported(id);

        bool unsupported = IsUnsupported(id);
        if (_tiles.TryGetValue(id, out SystemTile? tile)) { tile.IsDisabled = disabled; tile.NoFocus = unsupported; }
        if (_sliders.TryGetValue(id, out SystemSettingRow? row)) { row.IsDisabled = disabled; row.NoFocus = unsupported; }
    }

    // ---- 行の組み立て(並び順どおりに、タイルは横6マスに詰める。スライダーは1行を使う) ----

    private static bool IsFocusableTile(SystemTile t) => !t.IsStat && !t.IsSpacer && !t.NoFocus;
    private static bool RowFocusable(SystemSettingRow r) => r.IsTiles ? r.Tiles.Any(IsFocusableTile) : !r.NoFocus;

    private string? FocusedId
    {
        get
        {
            if (_focusedRow is null) return null;
            if (!_focusedRow.IsTiles) return _focusedRow.Key;
            return _tileIndex >= 0 && _tileIndex < _focusedRow.Tiles.Count ? _focusedRow.Tiles[_tileIndex].Key : null;
        }
    }

    /// <summary>項目の構成が変わったときだけ行を作り直す(作り直したらtrue)。</summary>
    private bool RebuildRows(bool force)
    {
        List<string> ids = _order.Where(IsVisibleItem).ToList();
        string signature = string.Join("|", ids);
        if (!force && signature == _signature) return false;
        _signature = signature;

        string? focusId = _userMoved ? FocusedId : null;

        Rows.Clear();
        var current = new List<SystemTile>();
        int used = 0;
        int seq = 0;

        void Flush()
        {
            if (current.Count == 0) return;
            double height = current.Max(t => t.BaseMinHeight);
            if (height <= 0) height = 108;   // 空白だけの行
            var row = new SystemSettingRow { Key = "tiles:" + seq++, IsTiles = true };
            foreach (SystemTile t in current)
            {
                t.TileMinHeight = height;
                t.TileWidth = SpanWidth(t.Span);
                t.ApplyTheme(IsDark);
                row.Tiles.Add(t);
            }
            Rows.Add(row);
            current.Clear();
            used = 0;
        }

        foreach (string id in ids)
        {
            EnsureItem(id);
            ApplyItemState(id);
            if (_sliders.TryGetValue(id, out SystemSettingRow? slider))
            {
                Flush();
                Rows.Add(slider);
            }
            else if (_tiles.TryGetValue(id, out SystemTile? tile))
            {
                if (used + tile.Span > GridColumns) Flush();
                current.Add(tile);
                used += tile.Span;
            }
        }
        Flush();

        FocusById(focusId);
        return true;
    }

    /// <summary>行を作り直さない開き直しのとき、テーマ・ゲーム中の状態・フォーカスだけ更新する。</summary>
    private void RefreshItemsInPlace()
    {
        foreach (SystemTile tile in _tiles.Values) tile.ApplyTheme(IsDark);
        foreach (string id in _order.Where(IsVisibleItem)) ApplyItemState(id);
        FocusById(null);
    }

    /// <summary>項目Idのところへフォーカスを置く。無ければ(または未指定なら)いちばん上の操作できる項目。</summary>
    private void FocusById(string? id)
    {
        _focusedRow = null;
        _rowIndex = 0;
        _tileIndex = 0;

        if (id is not null)
        {
            for (int i = 0; i < Rows.Count; i++)
            {
                SystemSettingRow row = Rows[i];
                if (!row.IsTiles)
                {
                    if (row.Key == id) { _rowIndex = i; RefreshFocus(); return; }
                    continue;
                }
                for (int j = 0; j < row.Tiles.Count; j++)
                {
                    if (row.Tiles[j].Key == id && IsFocusableTile(row.Tiles[j])) { _rowIndex = i; _tileIndex = j; RefreshFocus(); return; }
                }
            }
        }

        for (int i = 0; i < Rows.Count; i++)
        {
            if (!RowFocusable(Rows[i])) continue;
            _rowIndex = i;
            _tileIndex = Rows[i].IsTiles ? Rows[i].Tiles.ToList().FindIndex(IsFocusableTile) : 0;
            break;
        }
        RefreshFocus();
    }

    // ---- 値の読み込み・環境の判定 ----

    private void LoadVolume()
    {
        if (!IsVisibleItem(VolumeKey)) return;
        EnsureItem(VolumeKey);
        SystemSettingRow row = _sliders[VolumeKey];
        row.Value = SystemControls.GetVolume() ?? row.Value;
        row.SnapDisplay();
    }

    private static string PowerModeName(Guid mode)
    {
        if (mode == SystemControls.PowerModeSaver) return "省電力";
        if (mode == SystemControls.PowerModeBalanced) return "バランス";
        if (mode == SystemControls.PowerModePerformance) return "高性能";
        return "その他";
    }

    /// <summary>電源モードのタイル(今のモード名)を更新する。取得できない環境では使えない項目にする。</summary>
    private void LoadPowerTile()
    {
        Guid? mode = SystemControls.GetPowerMode();
        if (mode is Guid m && !Hidden("power"))
        {
            EnsureItem("power");
            _tiles["power"].SubText = PowerModeName(m);
            _detected.Add("power");
        }
        else
        {
            _detected.Remove("power");
            if (!Hidden("power")) MarkUnsupported("power");
        }
    }

    /// <summary>対応していない項目のタイルに「使えません」と出す(グレーアウトは ApplyItemState が行う)。</summary>
    private void MarkUnsupported(string id)
    {
        EnsureItem(id);
        if (_tiles.TryGetValue(id, out SystemTile? tile))
        {
            tile.IsOn = false;
            tile.SubText = "使えません";
        }
    }

    /// <summary>Wi-Fi/Bluetooth/明るさの現在の状態を調べ、対応している項目だけ出す。</summary>
    private async Task DetectAsync(int seq)
    {
        bool? wifi = Hidden("wifi") ? null : await SystemControls.GetRadioOnAsync(SystemControls.RadioType.WiFi);
        bool? bt = Hidden("bt") ? null : await SystemControls.GetRadioOnAsync(SystemControls.RadioType.Bluetooth);
        int? brightness = Hidden("brightness") ? null : await SystemControls.GetBrightnessAsync();
        if (seq != _showSeq || !IsOpen) return;

        SetRadio("wifi", wifi);
        SetRadio("bt", bt);

        if (brightness is int b)
        {
            EnsureItem(BrightnessKey);
            SystemSettingRow row = _sliders[BrightnessKey];
            row.Value = b;
            row.SnapDisplay();
            _detected.Add(BrightnessKey);
        }
        else
        {
            _detected.Remove(BrightnessKey);
            if (!Hidden(BrightnessKey)) EnsureItem(BrightnessKey);
        }

        try
        {
            RebuildRows(false);
            // 項目の構成が同じでも、対応の有無でグレーアウトが変わるので状態だけ更新する。
            foreach (string id in _order.Where(IsVisibleItem)) ApplyItemState(id);
            // フォーカスが対応していない項目に乗っていたら(または、まだ触っていなければ)、操作できる項目へ置き直す。
            FocusById(_userMoved ? FocusedId : null);
        }
        catch (Exception ex)
        {
            AppLog.Error("SystemPanel", $"DetectAsync後の組み立て直しで例外: {ex}", ex);
            throw;
        }
    }

    private void SetRadio(string id, bool? on)
    {
        if (on is null)
        {
            _detected.Remove(id);
            if (!Hidden(id)) MarkUnsupported(id);
            return;
        }
        EnsureItem(id);
        SystemTile tile = _tiles[id];
        tile.IsOn = on.Value;
        tile.SubText = on.Value ? "オン" : "オフ";
        _detected.Add(id);
    }

    private static Guid PowerModeOf(string key) => key switch
    {
        "saver" => SystemControls.PowerModeSaver,
        "performance" => SystemControls.PowerModePerformance,
        _ => SystemControls.PowerModeBalanced,
    };

    private void UpdateStatus()
    {
        string text = DateTime.Now.ToString("HH:mm");
        var battery = SystemControls.GetBattery();
        if (battery is { } b)
        {
            text = (b.Charging ? "充電中 " : "") + $"{b.Percent}%　" + text;
        }
        StatusText.Text = text;
    }

    private void ApplyStatsTheme()
    {
        bool dark = IsDark;
        var bg = new Microsoft.UI.Xaml.Media.SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(255, 0x26, 0x2E, 0x44) : Windows.UI.Color.FromArgb(255, 0xE6, 0xE8, 0xEE));
        var fg = new Microsoft.UI.Xaml.Media.SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(255, 0xE8, 0xEA, 0xF0) : Windows.UI.Color.FromArgb(255, 0x22, 0x22, 0x22));
        Pad1Card.Background = bg;
        Pad2Card.Background = bg;
        foreach (TextBlock tb in new[] { Pad1Label, Pad1State, Pad2Label, Pad2State }) tb.Foreground = fg;
        Pad1Icon.Foreground = fg;
        Pad2Icon.Foreground = fg;
    }

    /// <summary>1P/2Pの接続状態と入力ランプ(約30Hz)。ControllerStatusIndicatorと同じ点灯のしかた。</summary>
    private void UpdatePads()
    {
        UpdatePad(0, Pad1Card, Pad1Light, Pad1State);
        UpdatePad(1, Pad2Card, Pad2Light, Pad2State);
    }

    private void UpdatePad(int slot, Border card, Border light, TextBlock state)
    {
        bool connected = EmulatorLauncher.Input.GamepadSlots.IsConnected(slot);
        string text = connected
            ? (string.IsNullOrWhiteSpace(EmulatorLauncher.Input.GamepadSlots.GetName(slot)) ? "接続中" : EmulatorLauncher.Input.GamepadSlots.GetName(slot)!)
            : "未接続";
        if (state.Text != text) state.Text = text;
        double dim = connected ? 1.0 : 0.45;
        if (card.Opacity != dim) card.Opacity = dim;

        double target = connected && EmulatorLauncher.Input.GamepadSlots.IsActive(slot) ? 1.0 : 0.0;
        double level = _padLevels[slot];
        level = target > level ? Math.Min(target, level + 0.10) : Math.Max(target, level - 0.035);
        _padLevels[slot] = level;
        light.Opacity = connected ? 0.18 + 0.82 * level : 0.0;
    }


    private void UpdateStats()
    {
        if (IsVisibleItem("cpu") && _tiles.TryGetValue("cpu", out SystemTile? cpuTile) && SystemControls.GetCpuUsage() is { } cpu)
        {
            cpuTile.StatText = $"{cpu}%";
            cpuTile.Detail = $"{Environment.ProcessorCount} スレッド";
            cpuTile.Progress = cpu;
        }
        if (IsVisibleItem("memory") && _tiles.TryGetValue("memory", out SystemTile? memTile) && SystemControls.GetMemoryUsage() is { } mem)
        {
            memTile.StatText = $"{mem.Percent}%";
            memTile.Detail = $"{mem.UsedGb:0.0} / {mem.TotalGb:0.0} GB";
            memTile.Progress = mem.Percent;
        }
    }

    // ---- プラグイン(外部コマンドでstatus取得/action実行。パネルを開いている間だけ動く) ----

    private sealed class PluginRuntime
    {
        public PluginRuntime(SystemPluginDefinition def, DateTime stamp)
        {
            Def = def;
            Stamp = stamp;
            Available = def.Status is null;   // statusが無いもの(ボタン等)は、そのまま使える
        }

        public SystemPluginDefinition Def { get; }
        public DateTime Stamp { get; }
        /// <summary>この環境で使えるか(statusが成功したか)。使えない間は非表示。</summary>
        public bool Available { get; set; }
        public int Failures { get; set; }
        public bool Busy { get; set; }
        public DateTime NextStatusUtc { get; set; }
        /// <summary>常駐型status(stream)の実行中のもの。止まっている間はnull。</summary>
        public SystemPluginStream? Stream { get; set; }
        /// <summary>常駐型statusが終了したとき、この時刻以降に起動し直す。</summary>
        public DateTime StreamRestartUtc { get; set; }
        public bool IsStream => Def.Status?.Stream == true;
        /// <summary>スライダーで調整した、まだ実行していない値(止まってから実行する)。</summary>
        public string? Pending { get; set; }
        public DateTime PendingDueUtc { get; set; }
        /// <summary>statusが最後に返した値。</summary>
        public string? CurrentValue { get; set; }
    }

    private const int SliderDebounceMilliseconds = 400;

    private void StartPlugins()
    {
        bool any = false;
        foreach (PluginRuntime rt in _plugins.Values)
        {
            if (!_enabledPlugins.Contains(rt.Def.ItemId)) continue;
            rt.NextStatusUtc = rt.IsStream ? DateTime.MaxValue : DateTime.UtcNow;
            if (rt.IsStream) { rt.Failures = 0; rt.StreamRestartUtc = DateTime.MinValue; }
            any = true;
        }
        if (!any) { _pluginTimer?.Stop(); return; }

        if (_pluginTimer is null)
        {
            _pluginTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _pluginTimer.Tick += (_, _) => Guard("PluginTick", PluginTick);
        }
        _pluginTimer.Start();
        PluginTick();
    }

    private void PluginTick()
    {
        DateTime now = DateTime.UtcNow;
        foreach (PluginRuntime rt in _plugins.Values.ToList())
        {
            if (!_enabledPlugins.Contains(rt.Def.ItemId)) continue;

            // 常駐型status: 動いていなければ(開いた直後、または終了後の待ちが済んだら)起動する。3回続けて失敗したら、閉じるまであきらめる。
            if (rt.IsStream && rt.Stream is null && rt.Failures < 3 && now >= rt.StreamRestartUtc)
                StartPluginStream(rt);

            if (rt.Busy) continue;
            if (rt.Pending is not null)
            {
                if (now >= rt.PendingDueUtc) _ = RunPluginActionAsync(rt, rt.Pending);
            }
            else if (rt.Def.Status is not null && !rt.IsStream && now >= rt.NextStatusUtc)
            {
                _ = RunPluginStatusAsync(rt);
            }
        }
    }

    private async Task RunPluginStatusAsync(PluginRuntime rt)
    {
        SystemPluginCommand command = rt.Def.Status!;
        rt.Busy = true;
        SystemPluginResult result;
        try { result = await SystemPluginRunner.RunAsync(rt.Def, command, isAction: false, value: null); }
        catch (Exception ex) { result = SystemPluginResult.Fail(ex.Message); }
        rt.Busy = false;

        // 間隔(秒)の指定が無ければ、開いたとき(と操作のあと)だけ取得する。
        rt.NextStatusUtc = command.IntervalSec > 0 ? DateTime.UtcNow.AddSeconds(command.IntervalSec) : DateTime.MaxValue;

        HandleStatusResult(rt, result);
    }

    /// <summary>常駐型statusのコマンドを起動する。1行届くたびにUIスレッドで表示へ反映し、終了したら5秒後に起動し直す。</summary>
    private void StartPluginStream(PluginRuntime rt)
    {
        SystemPluginStream? self = null;
        bool Current() => IsOpen && ReferenceEquals(rt.Stream, self);

        self = SystemPluginStream.Start(rt.Def, rt.Def.Status!,
            result => DispatcherQueue.TryEnqueue(() => Guard("PluginStream", () => { if (Current()) HandleStatusResult(rt, result); })),
            reason => DispatcherQueue.TryEnqueue(() => Guard("PluginStreamEnd", () =>
            {
                if (!Current()) return;
                rt.Stream = null;
                rt.StreamRestartUtc = DateTime.UtcNow.AddSeconds(5);
                rt.Failures++;
                AppLog.WarnOnce($"plugin-stream:{rt.Def.Id}:{reason}", "Plugin", $"{rt.Def.Id}: 常駐のstatusが止まりました: {reason}");
                if (!rt.Available || rt.Failures >= 3)
                {
                    bool was = rt.Available;
                    rt.Available = false;
                    if (was) RebuildRows(false);
                }
            })));
        rt.Stream = self;
    }

    /// <summary>statusの結果(1回ぶん)を反映する。成功なら表示を更新し、失敗が続いたら項目を隠す。</summary>
    private void HandleStatusResult(PluginRuntime rt, SystemPluginResult result)
    {
        bool wasAvailable = rt.Available;
        if (result.Success)
        {
            rt.Failures = 0;
            rt.Available = true;
            EnsureItem(rt.Def.ItemId);
            ApplyPluginStatus(rt, result, first: !wasAvailable);
        }
        else
        {
            rt.Failures++;
            AppLog.WarnOnce($"plugin-status:{rt.Def.Id}:{result.Message}", "Plugin", $"{rt.Def.Id}: statusを取得できません: {result.Message}");
            // 一度でも使えていたものは、3回続けて失敗したら使えないとみなす(一時的な失敗でちらつかないように)。
            if (!rt.Available || rt.Failures >= 3) rt.Available = false;
        }

        if (wasAvailable != rt.Available) RebuildRows(false);
    }

    private static string FormatValue(SystemPluginDefinition d, string value, string? detail)
        => (d.Format ?? "{value}").Replace("{value}", value, StringComparison.Ordinal).Replace("{detail}", detail ?? string.Empty, StringComparison.Ordinal);

    private static bool IsOnValue(string value)
        => value.Equals("on", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";

    private void ApplyPluginStatus(PluginRuntime rt, SystemPluginResult result, bool first)
    {
        SystemPluginDefinition d = rt.Def;
        string value = result.Value ?? string.Empty;
        rt.CurrentValue = result.Value;

        if (_sliders.TryGetValue(d.ItemId, out SystemSettingRow? row))
        {
            // 調整中(まだ実行していない値がある)は、取得した値で上書きしない。
            if (rt.Pending is null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            {
                row.Value = (int)Math.Round(v);
                if (first) row.SnapDisplay(); else StartBarAnimation();
            }
            return;
        }

        if (!_tiles.TryGetValue(d.ItemId, out SystemTile? tile)) return;

        if (d.Display == "stat")
        {
            tile.StatText = FormatValue(d, value, result.Detail);
            tile.Detail = string.IsNullOrEmpty(result.Detail) ? " " : result.Detail;
            if (d.Bar is { } bar && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
                tile.Progress = Math.Clamp((n - bar.Min) * 100.0 / (bar.Max - bar.Min), 0, 100);
            return;
        }

        switch (d.Options?.Type)
        {
            case "toggle":
            {
                bool on = IsOnValue(value);
                tile.IsOn = on;
                tile.SubText = on ? "オン" : "オフ";
                break;
            }
            case "choice":
            {
                string label = d.Options.Items.FirstOrDefault(i => i.Value == value)?.Label ?? value;
                tile.SubText = d.Format is null ? label : FormatValue(d, label, result.Detail);
                break;
            }
            default:
                tile.SubText = result.Value is null ? (result.Detail ?? string.Empty) : FormatValue(d, value, result.Detail);
                break;
        }
    }

    /// <summary>actionを実行する。失敗したら揺れで知らせ、表示を実際の状態へ戻すため、すぐstatusを取り直す。</summary>
    private async Task RunPluginActionAsync(PluginRuntime rt, string? value, Action? revert = null)
    {
        if (rt.Def.Action is null) return;
        rt.Busy = true;
        rt.Pending = null;
        SystemPluginResult result;
        try { result = await SystemPluginRunner.RunAsync(rt.Def, rt.Def.Action, isAction: true, value: value); }
        catch (Exception ex) { result = SystemPluginResult.Fail(ex.Message); }
        rt.Busy = false;

        if (!result.Success)
        {
            AppLog.Warn("Plugin", $"{rt.Def.Id}: actionに失敗しました: {result.Message}");
            revert?.Invoke();
            if (IsOpen) ShakeItem(rt.Def.ItemId);
        }
        rt.NextStatusUtc = DateTime.UtcNow;

        // 実行中にさらに調整された値が残っていて、パネルがもう閉じているなら、ここで実行する(タイマーは止まっているため)。
        if (rt.Pending is not null && !IsOpen) await RunPluginActionAsync(rt, rt.Pending);
    }

    private void ShakeItem(string id)
    {
        for (int i = 0; i < Rows.Count; i++)
        {
            if (Rows[i].Key == id || Rows[i].Tiles.Any(t => t.Key == id))
            {
                ShakeAnimation.Shake(RowsControl.ContainerFromIndex(i) as UIElement, vertical: false);
                return;
            }
        }
    }

    /// <summary>パネルを閉じるとき、止まるのを待っていた調整値をすぐ実行する。</summary>
    private void FlushPluginActions()
    {
        _pluginTimer?.Stop();
        foreach (PluginRuntime rt in _plugins.Values.ToList())
        {
            // 常駐型statusは、標準入力を閉じて終了を促す(終わらなければ強制終了される)。
            SystemPluginStream? stream = rt.Stream;
            rt.Stream = null;
            stream?.Stop();
        }
        foreach (PluginRuntime rt in _plugins.Values.ToList())
            if (rt.Pending is not null && !rt.Busy) _ = RunPluginActionAsync(rt, rt.Pending);
    }


    // ---- フォーカス中の部品を少し大きくする(アニメーション) ----

    private readonly Dictionary<object, FrameworkElement> _focusables = new();
    private readonly Dictionary<FrameworkElement, bool> _scaledState = new();
    private const int FocusScaleMilliseconds = 160;

    private void Focusable_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        object? item;
        try { item = el.DataContext; }
        catch (System.Runtime.InteropServices.COMException) { return; }   // 破棄の途中の要素は読めないことがある
        if (item is null) return;
        RegisterFocusable(item, el);
    }

    // ItemsRepeater(タイル)は DataContext が当てにならないので、要素の用意/破棄のイベントで登録する。
    private void Tiles_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not FrameworkElement el) return;
        object? item = sender.ItemsSourceView?.GetAt(args.Index);
        if (item is null) return;
        RegisterFocusable(item, el);
    }

    private void Tiles_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is not FrameworkElement el) return;
        el.SizeChanged -= Focusable_SizeChanged;
        foreach (var kv in _focusables.Where(k => ReferenceEquals(k.Value, el)).ToList()) _focusables.Remove(kv.Key);
        _scaledState.Remove(el);
        ElementCompositionPreview.GetElementVisual(el).Scale = Vector3.One;
    }

    private void RegisterFocusable(object item, FrameworkElement el)
    {
        _focusables[item] = el;
        el.SizeChanged -= Focusable_SizeChanged;
        el.SizeChanged += Focusable_SizeChanged;
        UpdateCenter(el);
        ApplyFocusScale(item, el, animate: false);
    }

    private void Focusable_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        // 破棄の途中の要素からDataContextは読めない(COMExceptionになる)ので、登録済みの一覧を要素の参照で逆引きする。
        foreach (var kv in _focusables.Where(k => ReferenceEquals(k.Value, el)).ToList()) _focusables.Remove(kv.Key);
        try { el.SizeChanged -= Focusable_SizeChanged; }
        catch (System.Runtime.InteropServices.COMException) { }
        _scaledState.Remove(el);
    }

    private void Focusable_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement el) UpdateCenter(el);
    }

    private static void UpdateCenter(FrameworkElement el)
    {
        Visual v = ElementCompositionPreview.GetElementVisual(el);
        v.CenterPoint = new Vector3((float)el.ActualWidth / 2f, (float)el.ActualHeight / 2f, 0f);
    }

    private static bool IsItemFocused(object item) => item switch
    {
        SystemTile t => t.IsFocused,
        SystemSettingRow r => r.IsFocused,
        _ => false,
    };

    private void ApplyFocusScale(object item, FrameworkElement el, bool animate)
    {
        bool focused = IsItemFocused(item);
        if (_scaledState.TryGetValue(el, out bool prev) && prev == focused) return;
        _scaledState[el] = focused;

        float big = float.TryParse(el.Tag as string, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float s) ? s : 1.05f;
        var target = focused ? new Vector3(big, big, 1f) : Vector3.One;

        try
        {
            Visual v = ElementCompositionPreview.GetElementVisual(el);
            if (!animate)
            {
                v.StopAnimation("Scale");
                v.Scale = target;
                return;
            }
            Compositor compositor = v.Compositor;
            CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1.0f));
            Vector3KeyFrameAnimation anim = compositor.CreateVector3KeyFrameAnimation();
            anim.InsertKeyFrame(1f, target, ease);
            anim.Duration = TimeSpan.FromMilliseconds(FocusScaleMilliseconds);
            v.StartAnimation("Scale", anim);
        }
        catch (Exception ex)
        {
            // 破棄の途中の要素に当たっても、拡大の見た目が崩れるだけ。動作は止めない。
            _focusables.Remove(item);
            _scaledState.Remove(el);
            AppLog.WarnOnce("system-panel-focus-scale", "SystemPanel", "フォーカスの拡大を適用できませんでした", ex);
        }
    }

    private void ApplyAllFocusScales()
    {
        foreach (var kv in _focusables.ToList()) ApplyFocusScale(kv.Key, kv.Value, animate: true);
    }


    private void RefreshFocus()
    {
        if (Rows.Count > 0)
        {
            _rowIndex = Math.Clamp(_rowIndex, 0, Rows.Count - 1);
            _focusedRow = Rows[_rowIndex];
            if (_focusedRow.IsTiles)
            {
                _tileIndex = Math.Clamp(_tileIndex, 0, Math.Max(0, _focusedRow.Tiles.Count - 1));
                // 表示だけのタイルにはフォーカスを置かない。
                if (_tileIndex < _focusedRow.Tiles.Count && !IsFocusableTile(_focusedRow.Tiles[_tileIndex]))
                {
                    int first = _focusedRow.Tiles.ToList().FindIndex(IsFocusableTile);
                    if (first >= 0) _tileIndex = first;
                }
            }
        }
        else
        {
            _focusedRow = null;
        }

        bool focusable = _focusedRow is not null && RowFocusable(_focusedRow);
        for (int i = 0; i < Rows.Count; i++)
        {
            SystemSettingRow row = Rows[i];
            row.IsFocused = focusable && i == _rowIndex && !row.IsTiles;
            for (int j = 0; j < row.Tiles.Count; j++)
                row.Tiles[j].IsFocused = focusable && i == _rowIndex && row.IsTiles && j == _tileIndex && IsFocusableTile(row.Tiles[j]);
        }
        ApplyAllFocusScales();
        ScrollToFocus();
    }

    /// <summary>フォーカス中の行が見えるようにスクロールする(項目が多くてパネルに入りきらないとき)。</summary>
    private void ScrollToFocus()
    {
        int index = _rowIndex;
        bool atTop = Rows.Take(index).All(r => !RowFocusable(r));
        DispatcherQueue.TryEnqueue(() =>
        {
            // 位置合わせは補助。組み立て直しの途中でまだ画面に載っていない要素などで失敗しても、動作には影響させない。
            try
            {
                if (!IsOpen) return;
                if (atTop)
                {
                    RowsScroll.ChangeView(null, 0, null, false);
                    return;
                }
                if (index >= Rows.Count) return;
                if (RowsControl.ContainerFromIndex(index) is FrameworkElement { IsLoaded: true } container)
                    container.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
            }
            catch (Exception ex)
            {
                AppLog.WarnOnce("system-panel-scroll", "SystemPanel", "スクロール位置の調整に失敗しました", ex);
            }
        });
    }

    // ---- 表示/非表示(SortOverlayと同じ方式) ----

    /// <summary>例外が出たとき、スタックトレースごとログ(data\log)へ残して、そのまま投げ直す(原因の調査用)。</summary>
    private static void Guard(string name, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            AppLog.Error("SystemPanel", $"{name}で例外: {ex}", ex);
            throw;
        }
    }

    public void Show() => Guard("Show", ShowCore);

    private void ShowCore()
    {
        _animationVersion++;
        int seq = ++_showSeq;

        Visual rootVisual = ElementCompositionPreview.GetElementVisual(this);
        Visual panelVisual = ElementCompositionPreview.GetElementVisual(SystemPanel);
        Compositor compositor = rootVisual.Compositor;

        bool wasCollapsed = Visibility == Visibility.Collapsed;

        // ゲーム中は、ページのテーマに関わらず常にダーク配色で出す(閉じるときに戻す)。
        if (InGame) EmulatorLauncher.Views.AppThemeRouter.SetForcedDark(true);
        // ダーク用のパネル色(標準テーマは共通の面の色のまま)。
        SystemPanel.Background = IsDark
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x18, 0x1D, 0x2B))
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppSurfaceBrush"];

        _userMoved = false;
        LoadSettings();
        LoadVolume();
        LoadPowerTile();
        if (!RebuildRows(force: false)) RefreshItemsInPlace();
        PadsGrid.Visibility = Hidden("pads") ? Visibility.Collapsed : Visibility.Visible;
        UpdateStatus();
        if (_statusTimer is null)
        {
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _statusTimer.Tick += (_, _) => Guard("UpdateStatus", UpdateStatus);
        }
        _statusTimer.Start();

        ApplyStatsTheme();
        if (IsVisibleItem("cpu") || IsVisibleItem("memory"))
        {
            SystemControls.GetCpuUsage(); // 基準値の取得(次回から差分で使用率になる)
            if (_statsTimer is null)
            {
                _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _statsTimer.Tick += (_, _) => Guard("UpdateStats", UpdateStats);
            }
            _statsTimer.Start();
        }
        else
        {
            _statsTimer?.Stop();
        }

        if (_padTimer is null)
        {
            _padTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _padTimer.Tick += (_, _) => Guard("UpdatePads", UpdatePads);
        }
        if (!Hidden("pads"))
        {
            UpdatePads();
            _padTimer.Start();
        }

        Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        IsOpen = true;

        _ = DetectAsync(seq);
        StartPlugins();

        if (wasCollapsed)
        {
            UpdateLayout();
            _slideDistance = ComputeSlideDistance();
            rootVisual.Opacity = 0.0f;
            panelVisual.Properties.InsertVector3("Translation", new Vector3(_slideDistance, 0, 0));
        }

        CubicBezierEasingFunction easeOut = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f));
        rootVisual.StartAnimation("Opacity", CreateOpacityAnimation(compositor, 1.0f, SlideInMilliseconds, easeOut));
        panelVisual.StartAnimation("Translation", CreateTranslationAnimation(compositor, Vector3.Zero, SlideInMilliseconds, easeOut));
    }

    public void Close() => Guard("Close", CloseCore);

    private void CloseCore()
    {
        if (!IsOpen) return;

        IsOpen = false;
        IsHitTestVisible = false;
        _statusTimer?.Stop();
        _statsTimer?.Stop();
        _padTimer?.Stop();
        _barTimer?.Stop();
        FlushPluginActions();
        ClosePicker();
        foreach (SystemSettingRow r in Rows) r.SnapDisplay();
        int version = ++_animationVersion;
        _slideDistance = ComputeSlideDistance();

        Visual rootVisual = ElementCompositionPreview.GetElementVisual(this);
        Visual panelVisual = ElementCompositionPreview.GetElementVisual(SystemPanel);
        Compositor compositor = rootVisual.Compositor;

        CubicBezierEasingFunction easeIn = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.4f, 0.0f), new Vector2(1.0f, 1.0f));

        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        rootVisual.StartAnimation("Opacity", CreateOpacityAnimation(compositor, 0.0f, SlideOutMilliseconds, easeIn));
        panelVisual.StartAnimation("Translation", CreateTranslationAnimation(compositor, new Vector3(_slideDistance, 0, 0), SlideOutMilliseconds, easeIn));
        batch.End();
        batch.Completed += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (version != _animationVersion) return;
                Visibility = Visibility.Collapsed;
                if (InGame) EmulatorLauncher.Views.AppThemeRouter.SetForcedDark(false); // 閉じ終えてから元の配色へ
            });
        };

        Closed?.Invoke(this, EventArgs.Empty);
    }


    private float ComputeSlideDistance()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || SystemPanel.ActualWidth <= 0) return MinSlideDistance;

        double scale = Math.Min(ActualWidth / CanvasRoot.Width, ActualHeight / CanvasRoot.Height);
        if (scale <= 0) return MinSlideDistance;

        double letterboxX = (ActualWidth / scale - CanvasRoot.Width) / 2;
        double visibleRight = CanvasRoot.Width + letterboxX;
        Point panelLeft = SystemPanel.TransformToVisual(CanvasRoot).TransformPoint(new Point(0, 0));
        return (float)Math.Max(visibleRight - panelLeft.X + SlideOvershoot, MinSlideDistance);
    }

    private static ScalarKeyFrameAnimation CreateOpacityAnimation(
        Compositor compositor, float to, double milliseconds, CompositionEasingFunction easing)
    {
        ScalarKeyFrameAnimation animation = compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
        animation.InsertKeyFrame(1.0f, to, easing);
        return animation;
    }

    private static Vector3KeyFrameAnimation CreateTranslationAnimation(
        Compositor compositor, Vector3 to, double milliseconds, CompositionEasingFunction easing)
    {
        Vector3KeyFrameAnimation animation = compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
        animation.InsertKeyFrame(1.0f, to, easing);
        return animation;
    }


    // ---- 入力 ----

    public void HandleGamepadInput(GamepadButton button)
    {
        // 確認ダイアログ・選択肢の画面が出ている間は、そちらだけが入力を受ける。
        if (ConfirmDialog.IsOpen) { ConfirmDialog.HandleGamepadInput(button); return; }
        if (_pickerOpen) { HandlePickerInput(button); return; }

        switch (button)
        {
            case GamepadButton.DPadUp:
                MoveRow(-1);
                break;
            case GamepadButton.DPadDown:
                MoveRow(1);
                break;
            case GamepadButton.DPadLeft:
                HorizontalInput(-1);
                break;
            case GamepadButton.DPadRight:
                HorizontalInput(1);
                break;
            case GamepadButton.A:
                if (_focusedRow is { IsTiles: true } row && _tileIndex < row.Tiles.Count)
                    ActivateTile(row, row.Tiles[_tileIndex]);
                break;
            case GamepadButton.B:
                Close();
                break;
        }
    }

    private void ShakeFocused(bool vertical)
        => ShakeAnimation.Shake(RowsControl.ContainerFromIndex(_rowIndex) as UIElement, vertical);

    private void MoveRow(int delta)
    {
        if (Rows.Count == 0) return;
        // 表示だけの行(CPU等)は飛ばす。
        int next = _rowIndex + delta;
        while (next >= 0 && next < Rows.Count && !RowFocusable(Rows[next])) next += delta;
        if (next < 0 || next >= Rows.Count)
        {
            ShakeFocused(vertical: true);
            return;
        }
        _userMoved = true;
        _rowIndex = next;
        RefreshFocus();
    }

    private void HorizontalInput(int direction)
    {
        if (_focusedRow is null) return;
        if (_focusedRow.IsTiles)
        {
            int next = _tileIndex + direction;
            while (next >= 0 && next < _focusedRow.Tiles.Count && !IsFocusableTile(_focusedRow.Tiles[next])) next += direction;
            if (next < 0 || next >= _focusedRow.Tiles.Count)
            {
                ShakeFocused(vertical: false);
                return;
            }
            _userMoved = true;
            _tileIndex = next;
            RefreshFocus();
            return;
        }
        AdjustSelected(direction);
    }

    private void AdjustSelected(int direction)
    {
        if (_focusedRow is null || _focusedRow.IsTiles) return;
        SystemSettingRow row = _focusedRow;
        if (row.IsDisabled)
        {
            ShakeFocused(vertical: false);
            return;
        }

        ApplySliderValue(row, row.Value + direction * row.Step);
    }

    /// <summary>スライダーの値を設定して反映する(左右キーでもバーのクリック/ドラッグでも共通)。端で動かなかったときは揺れで知らせる。</summary>
    private void ApplySliderValue(SystemSettingRow row, int newValue, bool shakeAtLimit = true)
    {
        int before = row.Value;
        row.Value = newValue;
        if (row.Value == before)
        {
            // 最小/最大の端では、これ以上動かせないことを揺れで知らせる。
            if (shakeAtLimit) ShakeFocused(vertical: false);
            return;
        }

        StartBarAnimation();

        switch (row.Key)
        {
            case VolumeKey:
                SystemControls.SetVolume(row.Value);
                break;
            case BrightnessKey:
                SystemControls.SetBrightness(row.Value);
                break;
            default:
                // プラグイン: 調整が止まってから(400ms後)、最後の値でactionを1回だけ実行する。
                if (_plugins.TryGetValue(row.Key, out PluginRuntime? rt))
                {
                    rt.Pending = row.Value.ToString(CultureInfo.InvariantCulture);
                    rt.PendingDueUtc = DateTime.UtcNow.AddMilliseconds(SliderDebounceMilliseconds);
                }
                break;
        }
    }

    // ---- タイルの実行 ----

    private async void ActivateTile(SystemSettingRow row, SystemTile tile)
    {
        if (!IsFocusableTile(tile)) return;
        if (tile.IsDisabled)
        {
            ShakeFocused(vertical: false);
            return;
        }

        if (SystemItemCatalog.IsPlugin(tile.Key))
        {
            ActivatePluginTile(tile);
            return;
        }

        switch (tile.Key)
        {
            case "wifi":
            case "bt":
            {
                var type = tile.Key == "wifi" ? SystemControls.RadioType.WiFi : SystemControls.RadioType.Bluetooth;
                bool target = !tile.IsOn;
                tile.IsOn = target;
                tile.SubText = target ? "オン" : "オフ";
                bool ok = await SystemControls.SetRadioAsync(type, target);
                if (!ok)
                {
                    // 切り替えられなかった(権限や機器の都合)。表示を元へ戻して、揺れで知らせる。
                    tile.IsOn = !target;
                    tile.SubText = tile.IsOn ? "オン" : "オフ";
                    ShakeFocused(vertical: false);
                }
                break;
            }

            case "power":
                OpenPowerPicker();
                break;

            case "sleep":
            case "restart":
            case "shutdown":
            {
                if (ConfirmDialog.IsOpen) return;
                (string message, string label) = tile.Key switch
                {
                    "sleep" => ("スリープしますか？", "スリープする"),
                    "restart" => ("再起動しますか？", "再起動する"),
                    _ => ("シャットダウンしますか？", "シャットダウンする"),
                };
                if (!await ConfirmDialog.ShowAsync(message, label)) break;

                // パネルを閉じてから実行する。
                Close();
                if (tile.Key == "sleep") SystemControls.Sleep();
                else if (tile.Key == "restart") SystemControls.Restart();
                else SystemControls.Shutdown();
                break;
            }
        }
    }

    /// <summary>プラグインのタイル: オンオフは切り替え、選択肢は選ぶ画面を開く、options無しはボタン(押すと実行)。</summary>
    private void ActivatePluginTile(SystemTile tile)
    {
        if (!_plugins.TryGetValue(tile.Key, out PluginRuntime? rt) || rt.Def.Action is null || rt.Busy)
        {
            ShakeFocused(vertical: false);
            return;
        }

        switch (rt.Def.Options?.Type)
        {
            case "toggle":
            {
                bool target = !tile.IsOn;
                tile.IsOn = target;
                tile.SubText = target ? "オン" : "オフ";
                _ = RunPluginActionAsync(rt, target ? "on" : "off", () =>
                {
                    tile.IsOn = !target;
                    tile.SubText = tile.IsOn ? "オン" : "オフ";
                });
                break;
            }

            case "choice":
            {
                List<SystemPluginChoice> items = rt.Def.Options.Items;
                int selected = items.FindIndex(i => i.Value == rt.CurrentValue);
                OpenPicker(rt.Def.Name,
                    items.Select(i => (i.Value, i.Label, string.Empty)).ToList(),
                    selected,
                    index =>
                    {
                        if (index < 0 || index >= items.Count) return false;
                        if (rt.Busy) return false;
                        rt.CurrentValue = items[index].Value;
                        tile.SubText = rt.Def.Format is null ? items[index].Label : FormatValue(rt.Def, items[index].Label, null);
                        _ = RunPluginActionAsync(rt, items[index].Value);
                        return true;
                    });
                break;
            }

            default:
                _ = RunPluginActionAsync(rt, null);
                break;
        }
    }

    /// <summary>パネルの外(暗幕の部分)をクリック/タップしたら閉じる(B=もどると同じ)。パネルの中、操作ガイド、確認ダイアログでの操作は対象外。</summary>
    private void ScrimRoot_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (!IsOpen || ConfirmDialog.IsOpen || _pickerOpen) return;

        // クリックされた要素から親をたどる。パネル/操作ガイド/確認ダイアログの中なら閉じない。
        // 一覧の作り直しなどで要素がすでにツリーから外れているとき(このコントロールまでたどれないとき)も、閉じない。
        DependencyObject? node = e.OriginalSource as DependencyObject;
        bool reachedSelf = false;
        while (node is not null)
        {
            if (ReferenceEquals(node, SystemPanel) || node is HintBar || ReferenceEquals(node, ConfirmDialog)) return;
            if (ReferenceEquals(node, this)) { reachedSelf = true; break; }
            node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        }
        if (!reachedSelf) return;

        e.Handled = true;
        Close();
    }

    // ---- スライダー(音量・明るさ等)のマウス/タッチ操作 ----

    private SystemSettingRow? _draggingRow;

    /// <summary>スライダーの行(バー以外の部分)を押したとき: その行にフォーカスを移す。</summary>
    private void SliderRow_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (ConfirmDialog.IsOpen || _pickerOpen) return;
        if (sender is not FrameworkElement { DataContext: SystemSettingRow row } || row.IsTiles || row.NoFocus) return;
        FocusRow(row);
    }

    private void FocusRow(SystemSettingRow row)
    {
        int index = Rows.IndexOf(row);
        if (index < 0) return;
        _userMoved = true;
        _rowIndex = index;
        RefreshFocus();
    }

    private void SliderBar_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (ConfirmDialog.IsOpen || _pickerOpen) return;
        if (sender is not FrameworkElement { DataContext: SystemSettingRow row } bar || row.IsTiles || row.NoFocus) return;
        if (row.IsDisabled) { ShakeFocused(vertical: false); return; }

        e.Handled = true;
        FocusRow(row);
        _draggingRow = row;
        bar.CapturePointer(e.Pointer);
        SetSliderFromPointer(bar, row, e);
    }

    private void SliderBar_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SystemSettingRow row } bar || !ReferenceEquals(_draggingRow, row)) return;
        e.Handled = true;
        SetSliderFromPointer(bar, row, e);
    }

    private void SliderBar_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement bar) return;
        _draggingRow = null;
        bar.ReleasePointerCaptures();
    }

    /// <summary>ポインタの位置(バーの左端=最小、右端=最大)から値を決める。刻み(Step)に丸める。</summary>
    private void SetSliderFromPointer(FrameworkElement bar, SystemSettingRow row, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        double width = bar.ActualWidth;
        if (width <= 0 || row.Max <= row.Min) return;
        double ratio = Math.Clamp(e.GetCurrentPoint(bar).Position.X / width, 0, 1);
        int step = Math.Max(1, row.Step);
        int steps = (int)Math.Round(ratio * (row.Max - row.Min) / step);
        int value = Math.Min(row.Max, row.Min + steps * step);
        ApplySliderValue(row, value, shakeAtLimit: false);
    }

    private void Tile_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (ConfirmDialog.IsOpen || _pickerOpen) return;
        if (sender is not FrameworkElement { DataContext: SystemTile tile } || !IsFocusableTile(tile)) return;
        for (int i = 0; i < Rows.Count; i++)
        {
            int j = Rows[i].Tiles.IndexOf(tile);
            if (j < 0) continue;
            _userMoved = true;
            _rowIndex = i;
            _tileIndex = j;
            RefreshFocus();
            ActivateTile(Rows[i], tile);
            return;
        }
    }

    // ---- 選択肢の画面(電源モード、プラグインのchoice。パネルの上に重ねて出すカードの並び) ----

    public ObservableCollection<SystemTile> PickerChoices { get; } = new();
    private bool _pickerOpen;
    private int _pickerIndex;
    private Func<int, bool>? _pickerApply;

    /// <summary>選択肢の画面を開く。applyは選ばれた番号を受け取り、成功したらtrue(画面を閉じる)、失敗ならfalse(揺れて開いたまま)。</summary>
    private void OpenPicker(string title, IReadOnlyList<(string Key, string Label, string Sub)> items, int selected, Func<int, bool> apply)
    {
        PickerTitle.Text = title;
        PickerChoices.Clear();
        for (int i = 0; i < items.Count; i++)
        {
            SystemTile choice = MakeTile(items[i].Key, items[i].Label, string.Empty);
            choice.SubText = items[i].Sub;
            choice.IsOn = i == selected;
            PickerChoices.Add(choice);
        }
        _pickerApply = apply;
        _pickerIndex = selected >= 0 && selected < items.Count ? selected : (items.Count > 1 ? 1 : 0);
        if (items.Count > 2 && selected < 0) _pickerIndex = 0;
        RefreshPickerFocus();

        _pickerOpen = true;
        PickerOverlay.Visibility = Visibility.Visible;
        PickerScroll.ChangeView(null, 0, null, true);
        Visual visual = ElementCompositionPreview.GetElementVisual(PickerOverlay);
        visual.Opacity = 0f;
        visual.StartAnimation("Opacity", CreateOpacityAnimation(visual.Compositor, 1f, 160,
            visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f))));
    }

    private void OpenPowerPicker()
    {
        Guid? mode = SystemControls.GetPowerMode();
        var items = new List<(string Key, string Label, string Sub)>
        {
            ("saver", "省電力", "バッテリーを長持ちさせます"),
            ("balanced", "バランス", "性能と電力のちょうどよい設定です"),
            ("performance", "高性能", "性能を優先します(電力を多く使います)"),
        };
        int selected = items.FindIndex(i => mode is Guid m && PowerModeOf(i.Key) == m);
        OpenPicker("電源モード", items, selected, index =>
        {
            if (index < 0 || index >= items.Count) return false;
            if (!SystemControls.SetPowerMode(PowerModeOf(items[index].Key))) return false;
            LoadPowerTile(); // 電源モードのタイルの表示(今のモード名)を更新する
            return true;
        });
    }

    private void ClosePicker()
    {
        _pickerOpen = false;
        PickerOverlay.Visibility = Visibility.Collapsed;
    }

    private void RefreshPickerFocus()
    {
        for (int i = 0; i < PickerChoices.Count; i++) PickerChoices[i].IsFocused = i == _pickerIndex;
        ApplyAllFocusScales();
        int index = _pickerIndex;
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (!_pickerOpen) return;
                if (PickerItems.ContainerFromIndex(index) is FrameworkElement { IsLoaded: true } container)
                    container.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
            }
            catch (Exception ex)
            {
                AppLog.WarnOnce("system-panel-picker-scroll", "SystemPanel", "選択肢のスクロール位置の調整に失敗しました", ex);
            }
        });
    }

    private void ApplyPickerChoice(int index)
    {
        if (index < 0 || index >= PickerChoices.Count) return;
        if (_pickerApply?.Invoke(index) != true)
        {
            ShakePicker();
            return;
        }
        ClosePicker();
    }

    private void ShakePicker()
        => ShakeAnimation.Shake(PickerItems.ContainerFromIndex(_pickerIndex) as UIElement, vertical: true);

    private void HandlePickerInput(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.DPadUp:
            case GamepadButton.DPadDown:
            {
                int next = Math.Clamp(_pickerIndex + (button == GamepadButton.DPadUp ? -1 : 1), 0, PickerChoices.Count - 1);
                if (next == _pickerIndex) { ShakePicker(); break; }
                _pickerIndex = next;
                RefreshPickerFocus();
                break;
            }
            case GamepadButton.A:
                ApplyPickerChoice(_pickerIndex);
                break;
            case GamepadButton.B:
                ClosePicker();
                break;
        }
    }

    private void PickerChoice_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SystemTile tile }) return;
        int index = PickerChoices.IndexOf(tile);
        if (index < 0) return;
        _pickerIndex = index;
        RefreshPickerFocus();
        ApplyPickerChoice(index);
    }
}
