using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EmulatorLauncher.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace EmulatorLauncher.Controls;

/// <summary>
/// アイコンを選ぶダイアログ風の画面。使い方はIconPickerOverlay.xamlのコメント参照。
/// 画像の列挙・ハッシュ・取り込み先などは、これまでコレクション編集にあった処理
/// (CollectionEditOverlayの静的ヘルパー)をそのまま使う。
/// </summary>
public sealed partial class IconPickerOverlay : UserControl
{
    /// <summary>1行のタイル数。XAMLのItemsWrapGrid.MaximumRowsOrColumnsと合わせること。</summary>
    private const int Columns = 8;

    public ObservableCollection<CollectionIconItem> Icons { get; } = new();

    public bool IsOpen { get; private set; }

    private TaskCompletionSource<string?>? _tcs;
    private string _currentPath = string.Empty;
    private string? _currentHash;
    private int _index;
    private bool _busy;
    private int _session;

    public IconPickerOverlay()
    {
        this.InitializeComponent();
        EmulatorLauncher.Controls.LauncherWheel.AttachAll(this);
        Visibility = Visibility.Collapsed;
        OverlayFade.Attach(this);
    }

    /// <summary>
    /// アイコン選択画面を開き、選ばれる(またはキャンセルされる)まで待つ。
    /// 戻り値: null=キャンセル、""=「なし」、それ以外=選んだ画像の絶対パス。
    /// </summary>
    /// <param name="folders">一覧に並べる画像のフォルダ(順に並べ、中身が同じ画像は先に見つかった方だけ残す)。
    /// ユーザーが「ファイルから追加」した画像のフォルダも、呼び出し側で含めて渡す。</param>
    public Task<string?> ShowAsync(string title, IEnumerable<string> folders, string? currentIconPath)
    {
        _session++;
        _busy = false;
        TitleText.Text = title;
        _currentPath = currentIconPath ?? string.Empty;

        ReloadIcons(folders.ToList());
        IconScrollViewer.ChangeView(null, 0, null, disableAnimation: true);

        Visibility = Visibility.Visible;
        IsOpen = true;

        int selected = Icons.ToList().FindIndex(i => i.IsSelected);
        SetFocus(selected >= 0 ? selected : 0);

        _tcs = new TaskCompletionSource<string?>();
        return _tcs.Task;
    }

    private void Close(string? result)
    {
        if (!IsOpen) return;

        _session++;
        Visibility = Visibility.Collapsed;
        IsOpen = false;
        _busy = false;

        TaskCompletionSource<string?>? tcs = _tcs;
        _tcs = null;
        tcs?.TrySetResult(result);
    }

    /// <summary>並びは「なし」→ 各フォルダの画像 →「ファイルから追加」。現在のアイコンが一覧のどれとも
    /// 一致しない(同梱画像や別の場所の画像)場合は、選択を失わないよう「なし」の直後に並べる。</summary>
    private void ReloadIcons(List<string> folders)
    {
        var seenHashes = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<(string Path, string? Hash)>();

        foreach (string path in folders.SelectMany(CollectionEditOverlay.EnumerateIconFiles))
        {
            string? hash = CollectionEditOverlay.ComputeFileHash(path);
            if (hash is not null && !seenHashes.Add(hash)) continue;
            entries.Add((path, hash));
        }

        _currentHash = string.IsNullOrEmpty(_currentPath) ? null : CollectionEditOverlay.ComputeFileHash(_currentPath);

        bool currentListed = string.IsNullOrEmpty(_currentPath)
            || entries.Any(e => CollectionEditOverlay.IsSamePath(e.Path, _currentPath)
                                || (_currentHash is not null && e.Hash == _currentHash));
        if (!currentListed && (File.Exists(_currentPath) || _currentPath.Contains("://", StringComparison.Ordinal)))
        {
            entries.Insert(0, (_currentPath, _currentHash));
        }

        Icons.Clear();
        Icons.Add(new CollectionIconItem { Kind = CollectionIconKind.None });
        foreach ((string path, string? hash) in entries)
        {
            Icons.Add(new CollectionIconItem
            {
                Kind = CollectionIconKind.Image,
                Path = path,
                ContentHash = hash,
                Thumbnail = CollectionEditOverlay.CreateThumbnail(path),
            });
        }
        Icons.Add(new CollectionIconItem { Kind = CollectionIconKind.PickFile });

        foreach (CollectionIconItem item in Icons)
        {
            item.IsSelected = item.Kind switch
            {
                CollectionIconKind.None => string.IsNullOrEmpty(_currentPath),
                CollectionIconKind.Image => !string.IsNullOrEmpty(_currentPath)
                    && (CollectionEditOverlay.IsSamePath(item.Path, _currentPath)
                        || (_currentHash is not null && item.ContentHash == _currentHash)),
                _ => false,
            };
        }
    }

    private void IconTile_Click(object sender, RoutedEventArgs e)
    {
        if (!IsOpen || _busy || sender is not Button { Tag: CollectionIconItem item }) return;

        int index = Icons.IndexOf(item);
        SetFocus(index);
        Activate(index);
    }

    public void HandleGamepadInput(GamepadButton button)
    {
        if (!IsOpen) return;
        if (FileChooser.IsOpen)
        {
            FileChooser.HandleGamepadInput(button);
            return;
        }
        if (_busy) return;

        int count = Icons.Count;
        if (count == 0) return;

        int column = _index % Columns;
        int lastRow = (count - 1) / Columns;

        switch (button)
        {
            case GamepadButton.B:
                Close(null);
                break;
            case GamepadButton.DPadLeft:
                if (column > 0) SetFocus(_index - 1);
                break;
            case GamepadButton.DPadRight:
                if (column < Columns - 1 && _index + 1 < count) SetFocus(_index + 1);
                break;
            case GamepadButton.DPadUp:
                if (_index - Columns >= 0) SetFocus(_index - Columns);
                break;
            case GamepadButton.DPadDown:
                if (_index + Columns < count) SetFocus(_index + Columns);
                else if (_index / Columns < lastRow) SetFocus(count - 1);
                break;
            case GamepadButton.A:
                Activate(_index);
                break;
        }
    }

    private void SetFocus(int index)
    {
        if (Icons.Count == 0) return;

        _index = Math.Clamp(index, 0, Icons.Count - 1);
        for (int i = 0; i < Icons.Count; i++)
        {
            Icons[i].IsFocused = i == _index;
        }

        if (IconItemsControl.ContainerFromIndex(_index) is FrameworkElement container)
        {
            container.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
        }
    }

    private void Activate(int index)
    {
        if (index < 0 || index >= Icons.Count) return;

        CollectionIconItem item = Icons[index];
        switch (item.Kind)
        {
            case CollectionIconKind.None:
                Close(string.Empty);
                break;
            case CollectionIconKind.Image:
                Close(item.Path);
                break;
            case CollectionIconKind.PickFile:
                _ = PickAndAddIconAsync();
                break;
        }
    }

    /// <summary>ファイル選択(FileChooser)で画像を選び、data/collection_iconsへコピーして、そのアイコンを選んだ扱いで閉じる。
    /// 中身が同じ画像が一覧に既にあれば、コピーを増やさずそれを選ぶ。</summary>
    private async Task PickAndAddIconAsync()
    {
        int session = _session;
        _busy = true;
        try
        {
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            string? pickedFile = await FileChooser.ShowFileAsync(
                "アイコン画像を選択", CollectionEditOverlay.IconExtensions, string.IsNullOrEmpty(pictures) ? null : pictures);
            if (string.IsNullOrEmpty(pickedFile) || session != _session || !IsOpen) return;
            var file = new { Path = pickedFile };

            string? pickedHash = CollectionEditOverlay.ComputeFileHash(file.Path);
            CollectionIconItem? existing = pickedHash is null
                ? null
                : Icons.FirstOrDefault(i => i.Kind == CollectionIconKind.Image && i.ContentHash == pickedHash);

            string selectedPath;
            if (existing is not null)
            {
                selectedPath = existing.Path;
            }
            else
            {
                Directory.CreateDirectory(CollectionEditOverlay.UserIconsFolder);
                selectedPath = System.IO.Path.Combine(
                    CollectionEditOverlay.UserIconsFolder, $"{Guid.NewGuid():N}{System.IO.Path.GetExtension(file.Path)}");
                File.Copy(file.Path, selectedPath, overwrite: true);
            }

            _busy = false;
            Close(selectedPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[IconPickerOverlay] アイコンの取り込みに失敗しました: {ex.Message}");
        }
        finally
        {
            if (session == _session) _busy = false;
        }
    }
}
