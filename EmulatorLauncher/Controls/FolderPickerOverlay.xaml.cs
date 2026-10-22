using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EmulatorLauncher.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmulatorLauncher.Controls;

/// <summary>
/// ゲームパッドだけで操作できるフォルダ選択/ファイル選択オーバーレイ。使い方はFolderPickerOverlay.xamlのコメント参照。
/// フォルダ選択はShowAsync、ファイル選択(拡張子で絞り込み)はShowFileAsyncで開く。
/// 最上位の一覧(ドライブ+デスクトップ/ドキュメント/ダウンロード)から、サブフォルダへ降りていく。
/// </summary>
public sealed partial class FolderPickerOverlay : UserControl
{
    public ObservableCollection<GamepadPickerItem> Items { get; } = new();

    public bool IsOpen { get; private set; }

    private enum EntryKind { SelectCurrent, GoUp, Folder, File }

    private sealed record Entry(EntryKind Kind, string? Path);

    /// <summary>現在開いているフォルダ。nullは最上位(ドライブ一覧)。</summary>
    private string? _currentPath;

    /// <summary>trueならファイル選択(フォルダを降りてファイルを選ぶ)。</summary>
    private bool _fileMode;

    /// <summary>ファイル選択時の拡張子フィルタ(小文字、ドット付き)。空なら全ファイル。</summary>
    private HashSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase);

    private int _selectedIndex;
    private int _loadToken;
    private bool _loading;
    private TaskCompletionSource<string?>? _tcs;

    public FolderPickerOverlay()
    {
        this.InitializeComponent();
        EmulatorLauncher.Controls.LauncherWheel.AttachAll(this);
        Visibility = Visibility.Collapsed;
        OverlayFade.Attach(this);
    }

    /// <summary>
    /// フォルダ選択を表示し、決定(フルパス)またはキャンセル(null)まで待つ。
    /// initialPathが存在するフォルダならそこから開始し、無ければ最上位から開始する。
    /// </summary>
    public Task<string?> ShowAsync(string title, string? initialPath = null)
    {
        _fileMode = false;
        _extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return OpenAsync(title, initialPath);
    }

    /// <summary>
    /// ファイル選択を表示し、決定(ファイルのフルパス)またはキャンセル(null)まで待つ。
    /// extensionsは".exe"のようなドット付き拡張子(空なら全ファイルを表示)。
    /// initialPathはフォルダでもファイルでもよく、ファイルならその親フォルダを開いて選択状態にする。
    /// </summary>
    public Task<string?> ShowFileAsync(string title, IEnumerable<string> extensions, string? initialPath = null)
    {
        _fileMode = true;
        _extensions = new HashSet<string>(
            extensions.Select(e => e.StartsWith('.') ? e : "." + e), StringComparer.OrdinalIgnoreCase);
        return OpenAsync(title, initialPath);
    }

    private Task<string?> OpenAsync(string title, string? initialPath)
    {
        TitleText.Text = title;
        FolderHints.Visibility = _fileMode ? Visibility.Collapsed : Visibility.Visible;
        FileHints.Visibility = _fileMode ? Visibility.Visible : Visibility.Collapsed;
        Visibility = Visibility.Visible;
        IsOpen = true;

        _tcs = new TaskCompletionSource<string?>();

        string? start = null;
        string? focus = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(initialPath))
            {
                if (Directory.Exists(initialPath))
                {
                    start = initialPath;
                }
                else if (_fileMode && File.Exists(initialPath))
                {
                    start = Path.GetDirectoryName(initialPath);
                    focus = initialPath;
                }
                else if (_fileMode)
                {
                    // 保存済みのファイルが無くなっていても、親フォルダが残っていればそこから開く。
                    string? dir = Path.GetDirectoryName(initialPath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) start = dir;
                }
            }
        }
        catch { }

        _ = LoadAsync(start, focus);
        return _tcs.Task;
    }

    private void Close(string? result)
    {
        if (!IsOpen) return;

        Visibility = Visibility.Collapsed;
        IsOpen = false;
        _loadToken++;

        TaskCompletionSource<string?>? tcs = _tcs;
        _tcs = null;
        tcs?.TrySetResult(result);
    }

    // ------------------------------------------------------------------
    // 一覧の読み込み
    // ------------------------------------------------------------------

    private async Task LoadAsync(string? path, string? focusChildPath)
    {
        int token = ++_loadToken;
        _loading = true;

        List<(Entry Entry, string Label)> rows = await Task.Run(() => BuildRows(path, _fileMode, _extensions));
        if (token != _loadToken) return;

        _currentPath = path;
        PathText.Text = path ?? "コンピューター";

        Items.Clear();
        int focusIndex = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            Items.Add(new GamepadPickerItem { Label = rows[i].Label, Tag = rows[i].Entry });
            if (focusChildPath != null && rows[i].Entry.Path != null &&
                string.Equals(rows[i].Entry.Path, focusChildPath, StringComparison.OrdinalIgnoreCase))
            {
                focusIndex = i;
            }
        }

        // サブフォルダが無い(「このフォルダを選択」「上の階層」だけ)ときは案内を出す。
        EmptyText.Text = _fileMode ? "該当するファイルやフォルダはありません" : "サブフォルダはありません";
        EmptyText.Visibility = path != null && rows.All(r => r.Entry.Kind != EntryKind.Folder && r.Entry.Kind != EntryKind.File)
            ? Visibility.Visible : Visibility.Collapsed;

        // 先頭は「このフォルダを選択」。フォルダへ入った直後は、誤って即決定しないよう
        // 最初のサブフォルダ(あれば)ではなく先頭に置く。戻ったときは元のフォルダ上に置く。
        _selectedIndex = Math.Clamp(focusIndex, 0, Math.Max(0, Items.Count - 1));
        UpdateFocusVisuals();
        ListScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        _loading = false;

        // レイアウト確定後にスクロール位置を合わせる。
        DispatcherQueue.TryEnqueue(() => BringSelectedIntoView(animated: false));
    }

    private static List<(Entry Entry, string Label)> BuildRows(string? path, bool fileMode, HashSet<string> extensions)
    {
        var rows = new List<(Entry, string)>();

        if (path == null)
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    string name = drive.Name;
                    string vol = string.Empty;
                    try { vol = drive.VolumeLabel; } catch { }
                    rows.Add((new Entry(EntryKind.Folder, name), string.IsNullOrEmpty(vol) ? $"💽  {name}" : $"💽  {name}  ({vol})"));
                }
                catch { }
            }

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var shortcuts = new (string Label, string? Path)[]
            {
                ("🖥  デスクトップ", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
                ("📄  ドキュメント", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
                ("⬇  ダウンロード", string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "Downloads")),
                ("🎬  ビデオ", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
            };
            foreach (var (label, p) in shortcuts)
            {
                try
                {
                    if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) rows.Add((new Entry(EntryKind.Folder, p), label));
                }
                catch { }
            }
            return rows;
        }

        if (!fileMode)
        {
            rows.Add((new Entry(EntryKind.SelectCurrent, path), "✔  このフォルダを選択"));
        }
        rows.Add((new Entry(EntryKind.GoUp, null), "↩  上の階層へ"));

        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            };
            IEnumerable<DirectoryInfo> dirs = new DirectoryInfo(path).EnumerateDirectories("*", options)
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase);
            foreach (DirectoryInfo d in dirs)
            {
                rows.Add((new Entry(EntryKind.Folder, d.FullName), $"📁  {d.Name}"));
            }
        }
        catch
        {
            // 読み取れないフォルダは、サブフォルダ無しとして扱う。
        }

        if (fileMode)
        {
            try
            {
                var fileOptions = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = false,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                };
                IEnumerable<FileInfo> files = new DirectoryInfo(path).EnumerateFiles("*", fileOptions)
                    .Where(f => extensions.Count == 0 || extensions.Contains(f.Extension))
                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
                foreach (FileInfo f in files)
                {
                    rows.Add((new Entry(EntryKind.File, f.FullName), $"📄  {f.Name}"));
                }
            }
            catch
            {
                // 読み取れないフォルダは、ファイル無しとして扱う。
            }
        }
        return rows;
    }

    // ------------------------------------------------------------------
    // 操作
    // ------------------------------------------------------------------

    private void ItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not Button { Tag: GamepadPickerItem item }) return;
        int index = Items.IndexOf(item);
        if (index < 0) return;
        _selectedIndex = index;
        UpdateFocusVisuals();
        ActivateSelected();
    }

    public void HandleGamepadInput(GamepadButton button)
    {
        if (_loading) return;

        switch (button)
        {
            case GamepadButton.B:
                GoUpOrCancel();
                break;
            case GamepadButton.DPadUp:
                MoveSelection(-1);
                break;
            case GamepadButton.DPadDown:
                MoveSelection(1);
                break;
            case GamepadButton.Y:
                // どの階層からでも即キャンセルする。
                Close(null);
                break;
            case GamepadButton.LeftShoulder:
                MoveSelection(-8);
                break;
            case GamepadButton.RightShoulder:
                MoveSelection(8);
                break;
            case GamepadButton.A:
                ActivateSelected();
                break;
            case GamepadButton.X:
                if (!_fileMode && _currentPath != null) Close(_currentPath);
                break;
        }
    }

    private void ActivateSelected()
    {
        if (_selectedIndex < 0 || _selectedIndex >= Items.Count) return;
        if (Items[_selectedIndex].Tag is not Entry entry) return;

        switch (entry.Kind)
        {
            case EntryKind.SelectCurrent:
                Close(entry.Path);
                break;
            case EntryKind.GoUp:
                GoUpOrCancel();
                break;
            case EntryKind.Folder:
                _ = LoadAsync(entry.Path, null);
                break;
            case EntryKind.File:
                Close(entry.Path);
                break;
        }
    }

    private void GoUpOrCancel()
    {
        if (_currentPath == null)
        {
            Close(null);
            return;
        }

        string? parent = null;
        try { parent = Directory.GetParent(_currentPath)?.FullName; } catch { }

        // ドライブ直下(親なし)からは最上位の一覧へ戻る。
        _ = LoadAsync(parent, _currentPath);
    }

    private void MoveSelection(int delta)
    {
        if (Items.Count == 0) return;
        int target = Math.Clamp(_selectedIndex + delta, 0, Items.Count - 1);
        // 1行ずつの移動は端で反対側へ回り込む。
        if (Math.Abs(delta) == 1)
        {
            target = GamepadListNavigator.Move(_selectedIndex, delta, Items.Count, wrap: true);
        }
        _selectedIndex = target;
        UpdateFocusVisuals();
        BringSelectedIntoView(animated: true);
    }

    private void BringSelectedIntoView(bool animated)
    {
        if (ItemsHost.ContainerFromIndex(_selectedIndex) is FrameworkElement container)
        {
            container.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = animated });
        }
    }

    private void UpdateFocusVisuals()
    {
        for (int i = 0; i < Items.Count; i++)
        {
            Items[i].IsFocused = i == _selectedIndex;
        }
    }
}
