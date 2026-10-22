using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using EmulatorLauncher.Input;
using EmulatorLauncher.Models;
using EmulatorLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace EmulatorLauncher.Controls;

/// <summary>編集画面の各行の種類。</summary>
public enum LauncherEditRowKind
{
    Console,
    NewConsoleId,
    NewConsoleName,
    NewConsoleExtensions,
    Name,
    Type,
    Core,
    Exe,
    Args,
    WorkingPath,
    AspectRatio,
    UseForConsole,
    Delete,
}

/// <summary>編集画面の1行分の表示用モデル(LauncherEditOverlayが値を更新する)。</summary>
public sealed class LauncherEditRow : INotifyPropertyChanged
{
    public LauncherEditRowKind Kind { get; set; }

    private string _label = string.Empty;
    public string Label
    {
        get => _label;
        set { if (_label == value) return; _label = value; OnPropertyChanged(); }
    }

    private string _displayValue = string.Empty;
    public string DisplayValue
    {
        get => _displayValue;
        set { if (_displayValue == value) return; _displayValue = value; OnPropertyChanged(); }
    }

    private bool _isPlaceholder;

    /// <summary>値が未設定で、説明用の文言を出している(グレーで表示する)。</summary>
    public bool IsPlaceholder
    {
        get => _isPlaceholder;
        set
        {
            if (_isPlaceholder == value) return;
            _isPlaceholder = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ValueBrush));
        }
    }

    private bool _isFocused;
    public bool IsFocused
    {
        get => _isFocused;
        set { if (_isFocused == value) return; _isFocused = value; OnPropertyChanged(); }
    }

    internal static Brush ResolveBrush(string key, Windows.UI.Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush brush) return brush;
        return new SolidColorBrush(fallback);
    }

    private static readonly Windows.UI.Color SwitchBlue = Windows.UI.Color.FromArgb(255, 0x30, 0x50, 0xEF);
    private static readonly Windows.UI.Color Gray = Windows.UI.Color.FromArgb(255, 0xA6, 0xA6, 0xA6);

    /// <summary>ラベルの文字色(削除行だけ赤、それ以外は通常の文字色)。</summary>
    public Brush LabelBrush => Kind == LauncherEditRowKind.Delete
        ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE5, 0x39, 0x50))
        : ResolveBrush("AppTextPrimaryBrush", Windows.UI.Color.FromArgb(255, 0x2D, 0x2D, 0x2D));

    /// <summary>値の文字色(Switchの青。未設定の説明文言だけグレー)。</summary>
    public Brush ValueBrush => IsPlaceholder
        ? new SolidColorBrush(Gray)
        : ResolveBrush("SwitchSelectedBlueBrush", SwitchBlue);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>「保存する」ボタンの状態(保存できない間はグレー)。</summary>
public sealed class LauncherSaveButtonState : INotifyPropertyChanged
{
    private bool _isFocused;
    public bool IsFocused
    {
        get => _isFocused;
        set { if (_isFocused == value) return; _isFocused = value; OnPropertyChanged(); }
    }

    private bool _isEnabled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Background));
            OnPropertyChanged(nameof(Foreground));
        }
    }

    // ダークテーマの画面では、保存できる時は青地+白文字(押せると一目で分かる)、
    // 保存できない間は背景に溶け込む暗いグレー+薄い文字にする。
    private static bool Dark => EmulatorLauncher.Views.AppThemeRouter.IsDarkActive;

    public Brush Background => new SolidColorBrush(IsEnabled
        ? (Dark ? Windows.UI.Color.FromArgb(255, 0x30, 0x50, 0xEF) : Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF))
        : (Dark ? Windows.UI.Color.FromArgb(255, 0x1A, 0x1D, 0x24) : Windows.UI.Color.FromArgb(255, 0xDC, 0xDC, 0xDC)));

    public Brush Foreground => IsEnabled
        ? (Dark ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF))
                : LauncherEditRow.ResolveBrush("AppTextPrimaryBrush", Windows.UI.Color.FromArgb(255, 0x2D, 0x2D, 0x2D)))
        : new SolidColorBrush(Dark ? Windows.UI.Color.FromArgb(255, 0x5A, 0x62, 0x72) : Windows.UI.Color.FromArgb(255, 0xA6, 0xA6, 0xA6));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// ランチャーの新規追加/編集/削除オーバーレイ。使い方はLauncherEditOverlay.xamlのコメント参照。
///
/// 呼び出し側(SettingsPage)は
///     bool changed = await LauncherEditor.ShowAsync(editing);   // editing==nullなら新規追加
/// で開き、表示中のゲームパッド入力は先頭で HandleGamepadInput へ渡す。
/// 保存/削除は <see cref="LauncherConfigService"/> が Launchers.json / Consoles.json へ書き込む。
/// </summary>
public sealed partial class LauncherEditOverlay : UserControl
{
    private const string NewConsoleChoiceLabel = "＋ 新しいコンソールIDを指定";

    /// <summary>1行の高さ。選択行が見える位置へスクロールするための計算に使う。</summary>
    private const double RowPitch = 92;

    private static readonly string[] BaseAspectRatios = { "Auto", "4:3", "16:9", "3:2", "10:9", "12:7", "20:19", "14:9", "1:1" };

    public ObservableCollection<LauncherEditRow> Rows { get; } = new();

    /// <summary>「保存する」ボタンの状態(x:Bindで表示)。</summary>
    public LauncherSaveButtonState SaveState { get; } = new();

    public bool IsOpen { get; private set; }

    private TaskCompletionSource<bool>? _tcs;

    // ---- 編集中の状態 ----
    private LauncherInfo? _original;
    private List<ConsoleInfo> _consoles = new();
    private List<LibretroCoreInfo> _installedCores = new();
    private List<LibretroCoreInfo> _cores = new();   // 現在のコンソールに合わせて並べ替えた、インストール済みコア
    private List<string> _ratios = new();

    private int _consoleIndex;                       // _consoles.Count のとき「新しいコンソールIDを指定」
    private string _newId = string.Empty;
    private string _newConsoleName = string.Empty;
    private string _newExtensions = string.Empty;

    private string _name = string.Empty;
    private bool _external;
    private int _coreIndex = -1;
    private string _exePath = string.Empty;
    private string _args = string.Empty;
    private string _workingPath = string.Empty;
    private int _ratioIndex;
    private bool _useForConsole = true;
    private bool _newConsoleOnly;                    // コンソール作成から開いた時は、対応コンソールを既存に変えさせない

    private int _focusIndex;
    private bool _onSave;                            // 右側の「保存する」ボタンにフォーカスがある
    private string _initialSnapshot = string.Empty;  // 開いた時点の値(編集時、変更があるかの判定用)
    private bool _busy;                              // ファイル/フォルダ選択ダイアログ表示中は入力を無視する
    private LauncherEditRowKind? _textEditKind;

    public LauncherEditOverlay()
    {
        this.InitializeComponent();
        EmulatorLauncher.Controls.LauncherWheel.AttachAll(this);
        Visibility = Visibility.Collapsed;
        OverlayFade.Attach(this);
    }

    private bool IsNewConsole => _original is null && _consoleIndex >= _consoles.Count;

    /// <summary>編集対象のコンソールID(新規コンソールなら入力中のID)。</summary>
    private string CurrentConsoleId
        => _original is not null ? _original.Id
         : IsNewConsole ? _newId.Trim()
         : _consoles[_consoleIndex].Id;

    // ==================================================================
    // 開く/閉じる
    // ==================================================================

    /// <summary>編集画面を開く。editingがnullなら新規追加。保存/削除で変更があればtrueを返す。</summary>
    public async Task<bool> ShowAsync(LauncherInfo? editing, bool newConsole = false)
    {
        _original = editing;
        _newConsoleOnly = newConsole && editing is null;
        _consoles = SampleData.GetConsoles()
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        try
        {
            List<LibretroCoreInfo> catalog = await LibretroCoreCatalogService.RefreshInstalledStatusAsync();
            _installedCores = catalog
                .Where(c => c.Installed && !string.IsNullOrEmpty(c.CoreName))
                .GroupBy(c => c.CoreName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LauncherEditOverlay] コア一覧の読み込みに失敗: {ex.Message}");
            _installedCores = new List<LibretroCoreInfo>();
        }

        _ratios = BaseAspectRatios.ToList();
        _newId = _newConsoleName = _newExtensions = string.Empty;
        _onSave = false;
        _busy = false;
        _textEditKind = null;
        TextEditLayer.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;

        if (editing is null)
        {
            TitleText.Text = newConsole ? "コンソールの作成" : "ランチャーの追加";
            // コンソール作成は「新しいコンソールID」を選んだ状態で開く(コンソールは起動方法が必須のため)
            _consoleIndex = newConsole ? _consoles.Count : 0;
            _name = string.Empty;
            _external = _installedCores.Count == 0;
            _exePath = _args = _workingPath = string.Empty;
            _ratioIndex = 0;
            _useForConsole = true;
            _cores = OrderCores(CurrentConsoleId);
            _coreIndex = _cores.Count > 0 ? 0 : -1;
        }
        else
        {
            TitleText.Text = "ランチャーの編集";
            _consoleIndex = Math.Max(0, _consoles.FindIndex(c => string.Equals(c.Id, editing.Id, StringComparison.OrdinalIgnoreCase)));
            _name = editing.Name;
            _external = editing.IsExternal;
            _exePath = editing.ExePath ?? string.Empty;
            _args = editing.Args ?? string.Empty;
            _workingPath = editing.WorkingPath ?? string.Empty;

            string ratio = string.IsNullOrWhiteSpace(editing.DefaultAspectRatio) ? "Auto" : editing.DefaultAspectRatio.Trim();
            _ratioIndex = _ratios.FindIndex(r => string.Equals(r, ratio, StringComparison.OrdinalIgnoreCase));
            if (_ratioIndex < 0)
            {
                _ratios.Add(ratio);
                _ratioIndex = _ratios.Count - 1;
            }

            ConsoleInfo? console = _consoles.FirstOrDefault(c => string.Equals(c.Id, editing.Id, StringComparison.OrdinalIgnoreCase));
            _useForConsole = console is not null && console.LauncherName == editing.Name;

            _cores = OrderCores(editing.Id);
            string currentDll = Path.GetFileName(editing.CorePath ?? string.Empty);
            _coreIndex = _cores.FindIndex(c => string.Equals(c.CoreName, currentDll, StringComparison.OrdinalIgnoreCase));
        }

        Rows.Clear();
        _focusIndex = 0;
        ApplyStructure(forceRebuild: true);
        _initialSnapshot = BuildSnapshot();
        RefreshSaveState();

        ChoiceOverlay.Visibility = Visibility.Collapsed;
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        Visibility = Visibility.Visible;
        IsOpen = true;
        RowsScrollViewer.ChangeView(null, 0, null, disableAnimation: true);

        _tcs = new TaskCompletionSource<bool>();
        return await _tcs.Task;
    }

    /// <summary>変更があるまま戻ろうとしたとき、捨ててよいか確認する(不意に戻って変更が消えないようにする)。</summary>
    private bool IsDirty() => BuildSnapshot() != _initialSnapshot;

    private async Task ConfirmDiscardAsync()
    {
        bool discard = await ConfirmOverlay.ShowAsync("保存していない変更があります。変更を捨てて戻りますか?", "変更を捨てる", "編集を続ける");
        if (discard && IsOpen) Close(false);
    }

    private void Close(bool changed)
    {
        if (!IsOpen) return;

        Visibility = Visibility.Collapsed;
        IsOpen = false;

        TaskCompletionSource<bool>? tcs = _tcs;
        _tcs = null;
        tcs?.TrySetResult(changed);
    }

    // ==================================================================
    // 行の構成と表示テキスト
    // ==================================================================

    private List<LauncherEditRowKind> BuildKinds()
    {
        var kinds = new List<LauncherEditRowKind> { LauncherEditRowKind.Console };

        if (IsNewConsole)
        {
            kinds.Add(LauncherEditRowKind.NewConsoleId);
            kinds.Add(LauncherEditRowKind.NewConsoleName);
            kinds.Add(LauncherEditRowKind.NewConsoleExtensions);
        }

        kinds.Add(LauncherEditRowKind.Name);
        kinds.Add(LauncherEditRowKind.Type);

        if (_external)
        {
            kinds.Add(LauncherEditRowKind.Exe);
            kinds.Add(LauncherEditRowKind.Args);
            kinds.Add(LauncherEditRowKind.WorkingPath);
        }
        else
        {
            kinds.Add(LauncherEditRowKind.Core);
        }

        kinds.Add(LauncherEditRowKind.AspectRatio);

        if (!IsNewConsole)
        {
            kinds.Add(LauncherEditRowKind.UseForConsole);
        }

        if (_original is not null)
        {
            kinds.Add(LauncherEditRowKind.Delete);
        }

        return kinds;
    }

    /// <summary>行の構成が変わったときだけ作り直し(フォーカス位置は同じ種類の行へ引き継ぐ)、常に表示テキストを更新する。</summary>
    private void ApplyStructure(bool forceRebuild = false)
    {
        List<LauncherEditRowKind> kinds = BuildKinds();
        bool same = !forceRebuild && kinds.Count == Rows.Count && kinds.Select((k, i) => Rows[i].Kind == k).All(b => b);

        if (!same)
        {
            LauncherEditRowKind? focusedKind = _focusIndex >= 0 && _focusIndex < Rows.Count ? Rows[_focusIndex].Kind : null;

            Rows.Clear();
            foreach (LauncherEditRowKind kind in kinds)
            {
                Rows.Add(new LauncherEditRow { Kind = kind });
            }

            int restored = focusedKind is null ? -1 : kinds.IndexOf(focusedKind.Value);
            _focusIndex = restored >= 0 ? restored : Math.Min(Math.Max(_focusIndex, 0), Rows.Count - 1);
        }

        RefreshTexts();
        UpdateFocusFlags();

        if (!same)
        {
            DispatcherQueue.TryEnqueue(ScrollToFocus);
        }
    }

        private string AutoName()
    {
        if (_external)
        {
            string fromExe = Path.GetFileNameWithoutExtension(_exePath ?? string.Empty);
            return fromExe.Length > 0 ? fromExe : "外部コマンド";
        }

        return _coreIndex >= 0 && _coreIndex < _cores.Count ? _cores[_coreIndex].DisplayName : "コア";
    }

    private void RefreshTexts()
    {
        foreach (LauncherEditRow row in Rows)
        {
            bool placeholder = false;
            switch (row.Kind)
            {
                case LauncherEditRowKind.Console:
                    row.Label = "対応コンソール";
                    if (_original is not null)
                    {
                        ConsoleInfo? c = _consoles.FirstOrDefault(x => string.Equals(x.Id, _original.Id, StringComparison.OrdinalIgnoreCase));
                        row.DisplayValue = c is null ? _original.Id : $"{c.Name} ({c.Id})";
                    }
                    else if (IsNewConsole)
                    {
                        row.DisplayValue = NewConsoleChoiceLabel;
                    }
                    else
                    {
                        ConsoleInfo c = _consoles[_consoleIndex];
                        row.DisplayValue = $"{c.Name} ({c.Id})";
                    }
                    break;

                case LauncherEditRowKind.NewConsoleId:
                    row.Label = "コンソールID";
                    placeholder = _newId.Length == 0;
                    row.DisplayValue = placeholder ? "未入力" : _newId;
                    break;

                case LauncherEditRowKind.NewConsoleName:
                    row.Label = "コンソール表示名";
                    placeholder = _newConsoleName.Length == 0;
                    row.DisplayValue = placeholder ? "未入力(IDと同じ)" : _newConsoleName;
                    break;

                case LauncherEditRowKind.NewConsoleExtensions:
                    row.Label = "ROMの拡張子";
                    placeholder = _newExtensions.Length == 0;
                    row.DisplayValue = placeholder ? "未入力" : _newExtensions;
                    break;

                case LauncherEditRowKind.Name:
                    row.Label = "ランチャー名";
                    placeholder = _name.Length == 0;
                    row.DisplayValue = placeholder ? $"自動 ({AutoName()})" : _name;
                    break;

                case LauncherEditRowKind.Type:
                    row.Label = "起動方式";
                    row.DisplayValue = _external ? "外部コマンド" : "libretroコア";
                    break;

                case LauncherEditRowKind.Core:
                    row.Label = "コア";
                    if (_cores.Count == 0)
                    {
                        placeholder = true;
                        row.DisplayValue = "インストール済みのコアがありません";
                    }
                    else if (_coreIndex < 0)
                    {
                        placeholder = true;
                        row.DisplayValue = "未選択";
                    }
                    else
                    {
                        LibretroCoreInfo core = _cores[_coreIndex];
                        row.DisplayValue = $"{core.DisplayName} ({core.CoreName})";
                    }
                    break;

                case LauncherEditRowKind.Exe:
                    row.Label = "実行ファイル";
                    placeholder = _exePath.Length == 0;
                    row.DisplayValue = placeholder ? "未選択" : _exePath;
                    break;

                case LauncherEditRowKind.Args:
                    row.Label = "引数";
                    placeholder = _args.Length == 0;
                    row.DisplayValue = placeholder ? "未設定" : _args;
                    break;

                case LauncherEditRowKind.WorkingPath:
                    row.Label = "作業フォルダ";
                    placeholder = _workingPath.Length == 0;
                    row.DisplayValue = placeholder ? "未設定(実行ファイルのフォルダ)" : _workingPath;
                    break;

                case LauncherEditRowKind.AspectRatio:
                    row.Label = "画面比率";
                    row.DisplayValue = _ratios[_ratioIndex];
                    break;

                case LauncherEditRowKind.UseForConsole:
                    row.Label = "このコンソールで使う";
                    row.DisplayValue = _useForConsole ? "はい" : "いいえ";
                    break;

                case LauncherEditRowKind.Delete:
                    row.Label = "このランチャーを削除";
                    row.DisplayValue = string.Empty;
                    break;
            }

            row.IsPlaceholder = placeholder;
        }

        RefreshSaveState();
        RefreshHelpText();
    }

    /// <summary>選んでいる行の説明(右側に表示)。</summary>
    private static string HelpFor(LauncherEditRowKind kind) => kind switch
    {
        LauncherEditRowKind.Console => "このランチャーで起動するゲーム機を選びます。(編集中は変更できません)",
        LauncherEditRowKind.NewConsoleId => "英数字・ハイフン・アンダースコアのみ(例: wiiu)。",
        LauncherEditRowKind.NewConsoleName => "コンソール選択画面に表示される名前。空ならIDと同じになります。",
        LauncherEditRowKind.NewConsoleExtensions => "ROMフォルダのスキャン対象にする拡張子を空白区切りで(例: .rpx .wua)。",
        LauncherEditRowKind.Name => "同じコンソール内で重複しない名前。空なら自動で付けます。",
        LauncherEditRowKind.Type => "libretroコアで起動するか、外部のエミュレータを起動するかを切り替えます。",
        LauncherEditRowKind.Core => "インストール済みのlibretroコアから選びます。コアのインストールは「コア管理」で行います。",
        LauncherEditRowKind.Exe => "起動する実行ファイル(.exe/.bat/.cmd)を選びます。",
        LauncherEditRowKind.Args => "{rom}=ROMのフルパス / {romdir}=ROMのフォルダ / {romname}=拡張子なしのROM名。{rom}が無ければ末尾にROMのパスを自動で追加します。例: -f -g \"{rom}\"",
        LauncherEditRowKind.WorkingPath => "未設定なら、実行ファイルのフォルダで起動します。",
        LauncherEditRowKind.AspectRatio => "ゲーム起動時の既定の画面比率です。",
        LauncherEditRowKind.UseForConsole => "「はい」にすると、このコンソールのゲームをこのランチャーで起動します。",
        LauncherEditRowKind.Delete => "このランチャーを登録から外します。",
        _ => string.Empty,
    };

    private void RefreshHelpText()
    {
        HelpText.Text = !_onSave && FocusedKind is { } kind
            ? HelpFor(kind)
            : _onSave ? "設定内容を保存します。" : string.Empty;
    }

    // ---- 保存できるか ----

    /// <summary>保存できない理由(保存できるならnull)。</summary>
    private string? GetValidationError()
    {
        try
        {
            LauncherConfigService.ValidateId(CurrentConsoleId);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        if (_external)
        {
            if (string.IsNullOrWhiteSpace(_exePath)) return "実行ファイルを選択してください。";
        }
        else if (_coreIndex < 0 || _coreIndex >= _cores.Count)
        {
            return _cores.Count == 0
                ? "インストール済みのコアがありません。先に「コア管理」でコアをインストールしてください。"
                : "コアを選択してください。";
        }

        return null;
    }

    private string BuildSnapshot() => string.Join(
        "\u001f",
        CurrentConsoleId, _newConsoleName, _newExtensions, _name, _external,
        !_external && _coreIndex >= 0 && _coreIndex < _cores.Count ? _cores[_coreIndex].CoreName : string.Empty,
        _exePath, _args, _workingPath, _ratios[_ratioIndex], _useForConsole);

    /// <summary>入力が足りない/編集中で変更が無い間は、保存ボタンをグレー(押せない状態)にする。</summary>
    private void RefreshSaveState()
    {
        bool dirty = _original is null || BuildSnapshot() != _initialSnapshot;
        SaveState.IsEnabled = dirty && GetValidationError() is null;
    }

    private void UpdateFocusFlags()
    {
        for (int i = 0; i < Rows.Count; i++)
        {
            Rows[i].IsFocused = !_onSave && i == _focusIndex;
        }

        SaveState.IsFocused = _onSave;
    }

    private void ScrollToFocus()
    {
        double target = _focusIndex * RowPitch - 260;
        RowsScrollViewer.ChangeView(null, Math.Max(0, target), null, disableAnimation: true);
    }

    private List<LibretroCoreInfo> OrderCores(string consoleId)
        => _installedCores
            .OrderBy(c => string.Equals(c.ConsoleId, consoleId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>コンソールが変わった(選択/新規ID入力)ので、コアの並びを合わせ直す。</summary>
    private void OnConsoleChanged()
    {
        string? keep = _coreIndex >= 0 && _coreIndex < _cores.Count ? _cores[_coreIndex].CoreName : null;
        string consoleId = CurrentConsoleId;

        _cores = OrderCores(consoleId);

        bool hasMatching = _cores.Count > 0
                           && consoleId.Length > 0
                           && string.Equals(_cores[0].ConsoleId, consoleId, StringComparison.OrdinalIgnoreCase);

        if (_original is null && hasMatching)
        {
            _coreIndex = 0; // 新規追加では、そのコンソール用のコアを優先して先頭に選ぶ
        }
        else if (keep is not null)
        {
            _coreIndex = _cores.FindIndex(c => c.CoreName == keep);
        }
        else
        {
            _coreIndex = _cores.Count > 0 ? 0 : -1;
        }
    }

    // ==================================================================
    // 入力
    // ==================================================================

    public void HandleGamepadInput(GamepadButton button)
    {
        if (!IsOpen) return;

        // 入れ子の画面(確認ダイアログ/選択画面)が開いている間は、入力をそちらへ渡す。
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

        if (PathChooser.IsOpen)
        {
            PathChooser.HandleGamepadInput(button);
            return;
        }

        if (_busy) return;

        if (_textEditKind is not null)
        {
            if (button == GamepadButton.A) CommitTextEdit();
            else if (button == GamepadButton.B) CancelTextEdit();
            return;
        }

        ErrorText.Visibility = Visibility.Collapsed;

        switch (button)
        {
            case GamepadButton.B:
                if (IsDirty()) _ = ConfirmDiscardAsync();
                else Close(false);
                break;

            case GamepadButton.DPadUp:
                if (_onSave) SetSaveFocus(false);
                else MoveFocus(-1);
                break;
            case GamepadButton.DPadDown:
                if (!_onSave) MoveFocus(1);
                break;

            case GamepadButton.DPadRight:
                if (!_onSave) SetSaveFocus(true);
                break;
            case GamepadButton.DPadLeft:
                if (_onSave) SetSaveFocus(false);
                break;

            case GamepadButton.A:
                if (_onSave) TrySave();
                else ActivateFocused();
                break;

            case GamepadButton.X:
                ClearFocused();
                break;
        }
    }

    private void SetSaveFocus(bool onSave)
    {
        _onSave = onSave;
        UpdateFocusFlags();
        RefreshHelpText();
    }

    private void MoveFocus(int delta)
    {
        if (Rows.Count == 0) return;

        _focusIndex = GamepadListNavigator.Move(Math.Max(_focusIndex, 0), delta, Rows.Count, wrap: true);
        UpdateFocusFlags();
        RefreshHelpText();
        ScrollToFocus();
    }

    private LauncherEditRowKind? FocusedKind => _focusIndex >= 0 && _focusIndex < Rows.Count ? Rows[_focusIndex].Kind : null;

    private void ToggleValue(LauncherEditRowKind kind)
    {
        switch (kind)
        {
            case LauncherEditRowKind.Type:
                _external = !_external;
                if (!_external && _coreIndex < 0 && _cores.Count > 0) _coreIndex = 0;
                break;

            case LauncherEditRowKind.UseForConsole:
                _useForConsole = !_useForConsole;
                break;

            default:
                return;
        }

        ApplyStructure();
    }

    private void ActivateFocused()
    {
        if (FocusedKind is not { } kind) return;

        switch (kind)
        {
            case LauncherEditRowKind.Console:
                if (_original is null && !_newConsoleOnly) _ = PickConsoleAsync();
                break;

            case LauncherEditRowKind.Core:
                _ = PickCoreAsync();
                break;

            case LauncherEditRowKind.AspectRatio:
                _ = PickRatioAsync();
                break;

            case LauncherEditRowKind.Type:
            case LauncherEditRowKind.UseForConsole:
                ToggleValue(kind);
                break;

            case LauncherEditRowKind.NewConsoleId:
            case LauncherEditRowKind.NewConsoleName:
            case LauncherEditRowKind.NewConsoleExtensions:
            case LauncherEditRowKind.Name:
            case LauncherEditRowKind.Args:
                BeginTextEdit(kind);
                break;

            case LauncherEditRowKind.Exe:
                _ = PickExeAsync();
                break;

            case LauncherEditRowKind.WorkingPath:
                _ = PickWorkingFolderAsync();
                break;

            case LauncherEditRowKind.Delete:
                _ = ConfirmAndDeleteAsync();
                break;
        }
    }

    private void ClearFocused()
    {
        if (_onSave) return;

        switch (FocusedKind)
        {
            case LauncherEditRowKind.Args: _args = string.Empty; break;
            case LauncherEditRowKind.WorkingPath: _workingPath = string.Empty; break;
            case LauncherEditRowKind.Name: _name = string.Empty; break;
            default: return;
        }

        RefreshTexts();
    }

    /// <summary>マウス/タッチ: 行をクリックしたらフォーカスを移してAと同じ動作にする。</summary>
    private void RowsItemsControl_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!IsOpen || _busy || _textEditKind is not null || ChoiceOverlay.IsOpen || ConfirmOverlay.IsOpen) return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not LauncherEditRow row) return;

        int index = Rows.IndexOf(row);
        if (index < 0) return;

        ErrorText.Visibility = Visibility.Collapsed;
        _focusIndex = index;
        _onSave = false;
        UpdateFocusFlags();
        RefreshHelpText();
        ActivateFocused();
    }

    private void SaveButton_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!IsOpen || _busy || _textEditKind is not null || ChoiceOverlay.IsOpen || ConfirmOverlay.IsOpen) return;

        ErrorText.Visibility = Visibility.Collapsed;
        SetSaveFocus(true);
        TrySave();
    }

    // ==================================================================
    // 選択画面(全画面)
    // ==================================================================

    private async Task PickConsoleAsync()
    {
        var items = _consoles
            .Select((c, i) => new GamepadPickerItem { Label = $"{c.Name} ({c.Id})", Tag = i, IsChecked = i == _consoleIndex })
            .ToList();

        GamepadPickerItem? picked = await ChoiceOverlay.ShowAsync("対応コンソール", items);
        if (picked?.Tag is int index && index != _consoleIndex)
        {
            _consoleIndex = index;
            OnConsoleChanged();
            ApplyStructure();
        }
    }

    private async Task PickCoreAsync()
    {
        if (_cores.Count == 0)
        {
            ShowError("インストール済みのコアがありません。先に「コア管理」でコアをインストールしてください。");
            return;
        }

        var items = _cores
            .Select((c, i) => new GamepadPickerItem { Label = $"{c.DisplayName} ({c.CoreName})", Tag = i, IsChecked = i == _coreIndex })
            .ToList();

        GamepadPickerItem? picked = await ChoiceOverlay.ShowAsync("コア", items);
        if (picked?.Tag is int index && index != _coreIndex)
        {
            _coreIndex = index;
            ApplyStructure();
        }
    }

    private async Task PickRatioAsync()
    {
        var items = _ratios
            .Select((r, i) => new GamepadPickerItem { Label = r, Tag = i, IsChecked = i == _ratioIndex })
            .ToList();

        GamepadPickerItem? picked = await ChoiceOverlay.ShowAsync("画面比率", items);
        if (picked?.Tag is int index && index != _ratioIndex)
        {
            _ratioIndex = index;
            ApplyStructure();
        }
    }

    // ==================================================================
    // 文字入力
    // ==================================================================

    private void BeginTextEdit(LauncherEditRowKind kind)
    {
        _textEditKind = kind;

        (string label, string current) = kind switch
        {
            LauncherEditRowKind.NewConsoleId => ("コンソールID", _newId),
            LauncherEditRowKind.NewConsoleName => ("コンソール表示名", _newConsoleName),
            LauncherEditRowKind.NewConsoleExtensions => ("ROMの拡張子", _newExtensions),
            LauncherEditRowKind.Name => ("ランチャー名", _name),
            _ => ("引数", _args),
        };
        string note = HelpFor(kind);

        TextEditLabel.Text = label;
        TextEditNote.Text = note;
        TextEditBox.Text = current;
        TextEditLayer.Visibility = Visibility.Visible;
        TextEditBox.Focus(FocusState.Programmatic);
        TextEditBox.SelectAll();
    }

    private void CommitTextEdit()
    {
        if (_textEditKind is not { } kind) return;

        string text = TextEditBox.Text ?? string.Empty;
        switch (kind)
        {
            case LauncherEditRowKind.NewConsoleId:
                _newId = text.Trim();
                OnConsoleChanged();
                break;
            case LauncherEditRowKind.NewConsoleName: _newConsoleName = text.Trim(); break;
            case LauncherEditRowKind.NewConsoleExtensions: _newExtensions = text.Trim(); break;
            case LauncherEditRowKind.Name: _name = text.Trim(); break;
            case LauncherEditRowKind.Args: _args = text.Trim(); break;
        }

        EndTextEdit();
        ApplyStructure();
    }

    private void CancelTextEdit() => EndTextEdit();

    private void EndTextEdit()
    {
        _textEditKind = null;
        TextEditLayer.Visibility = Visibility.Collapsed;
        // キーボードフォーカスがTextBoxに残らないよう、このオーバーレイ自身へ戻す。
        this.Focus(FocusState.Programmatic);
    }

    private void TextEditBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            CommitTextEdit();
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            CancelTextEdit();
        }
    }

    // ==================================================================
    // ファイル/フォルダ選択(FolderPickerOverlay。ゲームパッドで操作できる)
    // ==================================================================

    private async Task PickExeAsync()
    {
        _busy = true;
        try
        {
            string? picked = await PathChooser.ShowFileAsync(
                "実行ファイルを選択", new[] { ".exe", ".bat", ".cmd" }, _exePath);
            if (!string.IsNullOrEmpty(picked))
            {
                _exePath = picked;
            }
        }
        catch (Exception ex)
        {
            ShowError($"ファイル選択に失敗しました: {ex.Message}");
        }
        finally
        {
            _busy = false;
            ApplyStructure();
        }
    }

    private async Task PickWorkingFolderAsync()
    {
        _busy = true;
        try
        {
            string? picked = await PathChooser.ShowAsync("作業フォルダを選択", _workingPath);
            if (!string.IsNullOrEmpty(picked))
            {
                _workingPath = picked;
            }
        }
        catch (Exception ex)
        {
            ShowError($"フォルダ選択に失敗しました: {ex.Message}");
        }
        finally
        {
            _busy = false;
            ApplyStructure();
        }
    }

    // ==================================================================
    // 保存/削除
    // ==================================================================

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    /// <summary>「保存する」。保存できない状態(グレー)なら、理由をエラー表示するだけで保存しない。</summary>
    private void TrySave()
    {
        if (!SaveState.IsEnabled)
        {
            string? reason = GetValidationError();
            if (reason is not null) ShowError(reason);
            return;
        }

        Save();
    }

    private void Save()
    {
        try
        {
            string consoleId = CurrentConsoleId;
            LauncherConfigService.ValidateId(consoleId);

            var edited = new LauncherInfo
            {
                Id = consoleId,
                Name = string.IsNullOrWhiteSpace(_name) ? AutoName() : _name.Trim(),
                DefaultAspectRatio = _ratios[_ratioIndex],
                CorePath = _external ? string.Empty : _cores[_coreIndex].CoreName,
                ExePath = _external ? _exePath.Trim() : string.Empty,
                Args = _external ? _args : string.Empty,
                WorkingPath = _external ? _workingPath : string.Empty,
            };

            bool useForConsole = IsNewConsole || _useForConsole;
            LauncherConfigService.Save(_original, edited, useForConsole, _newConsoleName, _newExtensions);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            return;
        }

        Close(true);
    }

    /// <summary>削除行: 確認ダイアログ(ConfirmDialogOverlay)で「削除する」が選ばれたら削除する。</summary>
    private async Task ConfirmAndDeleteAsync()
    {
        if (_original is null || ConfirmOverlay.IsOpen) return;

        bool confirmed = await ConfirmOverlay.ShowAsync(
            $"ランチャー「{_original.Name}」を削除しますか？", "削除する");
        if (confirmed) DeleteLauncher();
    }

    private void DeleteLauncher()
    {
        if (_original is null) return;

        try
        {
            LauncherConfigService.Delete(_original);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            return;
        }

        Close(true);
    }
}
