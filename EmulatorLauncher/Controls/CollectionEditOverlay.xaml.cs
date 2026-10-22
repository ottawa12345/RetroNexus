using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using EmulatorLauncher.RomLibrary;
using EmulatorLauncher.Models;
using EmulatorLauncher.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EmulatorLauncher.Controls;

/// <summary>
/// コレクションの新規追加/編集の全画面。画面構成・操作はLauncherEditOverlayに準拠
/// (使い方はCollectionEditOverlay.xamlのコメント参照)。
///
/// 使う側(ConsoleSelectPage):
/// - 新規追加は <c>Show(null)</c>、編集は <c>Show(編集対象のRomCollectionInfo)</c>。
/// - 表示中のゲームパッド入力は <see cref="HandleGamepadInput"/> へそのまま渡す。
/// - 閉じると <see cref="Closed"/> が発火する。引数は「保存または削除でDBが変わったか」で、
///   trueならコレクション一覧を読み直す。
///
/// アイコンの選択はIconPickerOverlay(ダイアログ風の画面)、削除の確認はConfirmDialogOverlayを
/// 入れ子で出す。表示中はそちらへ入力を渡す。
/// アイコンの保存形式は従来どおり、画像ファイルの絶対パスをRomCollectionInfo.IconPathへそのまま入れる
/// (ConsoleSelectPageのコレクションカードは絶対パスで読み込んでいるため)。
/// 下の静的ヘルパー(アイコン画像の列挙・ハッシュ・サムネイル等)は、コンソール編集とIconPickerOverlayも使う。
/// </summary>
public sealed partial class CollectionEditOverlay : UserControl
{
    private const string KeyName = "name";
    private const string KeyIcon = "icon";
    private const string KeyDelete = "delete";

    private const double RowPitch = 92;

    internal static readonly string[] IconExtensions = { ".png", ".jpg", ".jpeg", ".bmp" };

    /// <summary>同梱のコレクション用アイコン(実行フォルダ配下のImages/System)。</summary>
    internal static string SystemIconsFolder => Path.Combine(AppContext.BaseDirectory, "Images", "System");
    internal static string ConsoleIconsFolder => Path.Combine(AppContext.BaseDirectory, "Images", "Console");

    /// <summary>「ファイルから追加」で取り込んだアイコンのコピー先(旧CollectionEditPageと同じ場所)。</summary>
    internal static string UserIconsFolder => Path.Combine(AppContext.BaseDirectory, "data", "collection_icons");

    public ObservableCollection<EditFormRow> Rows { get; } = new();

    /// <summary>「保存する」ボタンの状態(x:Bindで表示)。</summary>
    public LauncherSaveButtonState SaveState { get; } = new();

    public bool IsOpen { get; private set; }

    /// <summary>閉じたときに発火する。引数は「保存または削除でDBが変わったか」。</summary>
    public event EventHandler<bool>? Closed;

    /// <summary>nullなら新規追加、値があれば編集対象。</summary>
    private RomCollectionInfo? _editing;

    private string _name = string.Empty;
    private string _originalName = string.Empty;
    private string _dirtyBaseName = string.Empty;   // 開いた時点の値(新規の既定名も含む。戻るときの変更判定用)
    private string _dirtyBaseIcon = string.Empty;

    /// <summary>選択中アイコンの絶対パス(空文字なら「なし」)。保存時にIconPathへ入る。</summary>
    private string _iconPath = string.Empty;
    private string _originalIconPath = string.Empty;

    private int _focusIndex;
    private bool _onSave;
    private bool _textEditing;

    /// <summary>保存・削除の非同期処理中はゲームパッド入力を受け付けない(二重保存の防止)。</summary>
    private bool _busy;

    /// <summary>Show()のたびに進める世代番号。非同期処理の完了時に、その間に閉じられたり
    /// 開き直されたりしていないかの判定に使う。</summary>
    private int _session;

    public CollectionEditOverlay()
    {
        this.InitializeComponent();
        EmulatorLauncher.Controls.LauncherWheel.AttachAll(this);
        Visibility = Visibility.Collapsed;
        OverlayFade.Attach(this);
    }

    // ==================================================================
    // 表示/非表示
    // ==================================================================

    /// <param name="editing">編集対象。nullなら新規追加。</param>
    /// <param name="existingNames">新規追加時の既定名(「コレクション1」「コレクション2」…)を
    /// 決めるための既存コレクション名。呼び出し側(ConsoleSelectPage)が表示中の一覧から渡す。
    /// 渡せばその場で同期的に既定名が決まる。nullの場合だけDBから読み込んで決める。</param>
    public void Show(RomCollectionInfo? editing, IEnumerable<string>? existingNames = null)
    {
        _session++;
        _editing = editing;
        _busy = false;
        _onSave = false;
        _textEditing = false;
        TextEditLayer.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;

        bool canDelete = editing is not null && editing.Id != CollectionLibraryFacade.FavoriteCollectionId;

        if (editing is not null)
        {
            TitleText.Text = "コレクションの編集";
            _name = editing.Name;
            _iconPath = editing.IconPath ?? string.Empty;
        }
        else
        {
            TitleText.Text = "コレクションの追加";
            _name = existingNames is not null ? CreateDefaultName(existingNames) : string.Empty;
            _iconPath = string.Empty;
        }

        _originalName = editing is not null ? _name : string.Empty;
        _originalIconPath = editing is not null ? _iconPath : string.Empty;
        _dirtyBaseName = _name;
        _dirtyBaseIcon = _iconPath;

        Rows.Clear();
        Rows.Add(new EditFormRow { Key = KeyName });
        Rows.Add(new EditFormRow { Key = KeyIcon });
        if (canDelete) Rows.Add(new EditFormRow { Key = KeyDelete, IsDanger = true });
        _focusIndex = 0;

        IconPicker.Visibility = Visibility.Collapsed;
        ConfirmOverlay.Visibility = Visibility.Collapsed;

        Refresh();

        Visibility = Visibility.Visible;
        IsOpen = true;
        RowsScrollViewer.ChangeView(null, 0, null, disableAnimation: true);

        // 既存名が渡されなかった新規追加だけ、DBから読んで既定名を決める。
        // 必ずIsOpen=trueにした後で開始すること(完了時に「まだ開いているか」を確認するため)。
        if (editing is null && existingNames is null)
        {
            _ = InitializeDefaultNameAsync(_session);
        }
    }

    /// <summary>変更があるまま戻ろうとしたとき、捨ててよいか確認する(不意に戻って変更が消えないようにする)。</summary>
    private bool IsDirty() => _name != _dirtyBaseName || _iconPath != _dirtyBaseIcon;

    private async Task ConfirmDiscardAsync()
    {
        bool discard = await ConfirmOverlay.ShowAsync("保存していない変更があります。変更を捨てて戻りますか?", "変更を捨てる", "編集を続ける");
        if (discard && IsOpen) Close(changed: false);
    }

    private void Close(bool changed)
    {
        if (!IsOpen) return;

        _session++;
        Visibility = Visibility.Collapsed;
        IsOpen = false;
        _busy = false;
        _textEditing = false;

        Closed?.Invoke(this, changed);
    }

    // ==================================================================
    // 表示の更新
    // ==================================================================

    private string? FocusedKey => _focusIndex >= 0 && _focusIndex < Rows.Count ? Rows[_focusIndex].Key : null;

    private bool IsNameValid => !string.IsNullOrWhiteSpace(_name);

    private void Refresh()
    {
        foreach (EditFormRow row in Rows)
        {
            switch (row.Key)
            {
                case KeyName:
                    row.Label = "コレクション名";
                    row.IsPlaceholder = !IsNameValid;
                    row.DisplayValue = IsNameValid ? _name : "未入力";
                    break;

                case KeyIcon:
                    row.Label = "アイコン";
                    bool hasIcon = !string.IsNullOrEmpty(_iconPath);
                    row.IsPlaceholder = !hasIcon;
                    row.DisplayValue = hasIcon ? Path.GetFileName(_iconPath) : "なし";
                    row.IconSource = hasIcon ? CreateThumbnail(_iconPath) : null;
                    break;

                case KeyDelete:
                    row.Label = "このコレクションを削除";
                    row.DisplayValue = string.Empty;
                    break;
            }
        }

        UpdatePreview();

        bool dirty = _editing is null
            || !string.Equals(_name.Trim(), _originalName.Trim(), StringComparison.Ordinal)
            || !string.Equals(_iconPath, _originalIconPath, StringComparison.Ordinal);
        SaveState.IsEnabled = IsNameValid && dirty;

        UpdateFocusFlags();
        RefreshHelpText();
    }

    private void RefreshHelpText()
    {
        if (_onSave)
        {
            HelpText.Text = "設定内容を保存します。";
            return;
        }

        HelpText.Text = FocusedKey switch
        {
            KeyName => "コレクションの一覧に表示される名前です。",
            KeyIcon => "コレクションの一覧に表示されるアイコンです。",
            KeyDelete => "このコレクションを削除します。コレクション内のゲーム自体は削除されません。",
            _ => string.Empty,
        };
    }

    private void UpdatePreview()
    {
        if (!string.IsNullOrEmpty(_iconPath))
        {
            try
            {
                PreviewImage.Source = new BitmapImage(new Uri(_iconPath));
                PreviewImage.Visibility = Visibility.Visible;
                PreviewPlaceholderText.Visibility = Visibility.Collapsed;
                return;
            }
            catch
            {
                // 下のフォールバックへ
            }
        }

        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewPlaceholderText.Visibility = Visibility.Visible;
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

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    // ==================================================================
    // 入力
    // ==================================================================

    public void HandleGamepadInput(GamepadButton button)
    {
        if (!IsOpen) return;

        if (ConfirmOverlay.IsOpen)
        {
            ConfirmOverlay.HandleGamepadInput(button);
            return;
        }
        if (IconPicker.IsOpen)
        {
            IconPicker.HandleGamepadInput(button);
            return;
        }

        if (_busy) return;

        if (_textEditing)
        {
            if (button == GamepadButton.A) CommitTextEdit();
            else if (button == GamepadButton.B) EndTextEdit();
            return;
        }

        ErrorText.Visibility = Visibility.Collapsed;

        switch (button)
        {
            case GamepadButton.B:
                if (IsDirty()) _ = ConfirmDiscardAsync();
                else Close(changed: false);
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
                if (_onSave) _ = TrySaveAsync();
                else ActivateFocused();
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

    private void ActivateFocused()
    {
        switch (FocusedKey)
        {
            case KeyName:
                BeginTextEdit();
                break;
            case KeyIcon:
                _ = PickIconAsync();
                break;
            case KeyDelete:
                _ = ConfirmAndDeleteAsync();
                break;
        }
    }

    private async Task PickIconAsync()
    {
        string? picked = await IconPicker.ShowAsync(
            "アイコンを選択",
            new[] { SystemIconsFolder, UserIconsFolder },
            _iconPath);

        if (picked is not null && !string.Equals(picked, _iconPath, StringComparison.Ordinal))
        {
            _iconPath = picked;
            Refresh();
        }
    }

    /// <summary>マウス/タッチ: 行をクリックしたらフォーカスを移してAと同じ動作にする。</summary>
    private void RowsItemsControl_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!IsOpen || _busy || _textEditing || IconPicker.IsOpen || ConfirmOverlay.IsOpen) return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not EditFormRow row) return;

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
        if (!IsOpen || _busy || _textEditing || IconPicker.IsOpen || ConfirmOverlay.IsOpen) return;

        ErrorText.Visibility = Visibility.Collapsed;
        SetSaveFocus(true);
        _ = TrySaveAsync();
    }

    // ==================================================================
    // 文字入力(コレクション名)
    // ==================================================================

    private void BeginTextEdit()
    {
        _textEditing = true;
        TextEditBox.Text = _name;
        TextEditLayer.Visibility = Visibility.Visible;
        TextEditBox.Focus(FocusState.Programmatic);
        TextEditBox.SelectAll();
    }

    private void CommitTextEdit()
    {
        _name = (TextEditBox.Text ?? string.Empty).Trim();
        EndTextEdit();
        Refresh();
    }

    private void EndTextEdit()
    {
        _textEditing = false;
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
            EndTextEdit();
        }
    }

    // ==================================================================
    // 保存/削除
    // ==================================================================

    /// <summary>「保存する」。グレー(名前が空、または変更なし)の間は、理由を表示するだけで保存しない。</summary>
    private async Task TrySaveAsync()
    {
        if (!SaveState.IsEnabled)
        {
            ShowError(IsNameValid ? "変更はありません。" : "コレクション名を入力してください。");
            return;
        }

        string name = _name.Trim();

        int session = _session;
        _busy = true;
        try
        {
            if (_editing is null)
            {
                await CollectionLibraryFacade.CreateAsync(name, _iconPath);
            }
            else
            {
                await CollectionLibraryFacade.UpdateAsync(_editing.Id, name, _iconPath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CollectionEditOverlay] コレクションの保存に失敗しました: {ex.Message}");
            if (session == _session)
            {
                _busy = false;
                ShowError($"保存に失敗しました: {ex.Message}");
            }
            return;
        }

        if (session == _session) Close(changed: true);
    }

    /// <summary>削除行: 確認ダイアログ(ConfirmDialogOverlay)で「削除する」が選ばれたら削除する。</summary>
    private async Task ConfirmAndDeleteAsync()
    {
        if (_editing is null || ConfirmOverlay.IsOpen) return;

        bool confirmed = await ConfirmOverlay.ShowAsync(
            $"コレクション「{_editing.Name}」を削除しますか？", "削除する");
        if (!confirmed || !IsOpen) return;

        int session = _session;
        _busy = true;
        try
        {
            await CollectionLibraryFacade.SoftDeleteAsync(_editing.Id);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CollectionEditOverlay] コレクションの削除に失敗しました: {ex.Message}");
            if (session == _session)
            {
                _busy = false;
                ShowError($"削除に失敗しました: {ex.Message}");
            }
            return;
        }

        if (session == _session) Close(changed: true);
    }

    /// <summary>
    /// 既存名が渡されなかった場合の既定名の決定(DBから既存コレクション名を読む)。
    /// 画面への反映はDispatcherQueue経由で必ずUIスレッドで行う。
    /// </summary>
    private async Task InitializeDefaultNameAsync(int session)
    {
        List<string> existingNames = new();
        try
        {
            List<RomCollectionInfo> existing = await CollectionLibraryFacade.GetAllAsync();
            existingNames = existing.Select(c => c.Name).ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CollectionEditOverlay] 既存コレクション名の取得に失敗しました: {ex.Message}");
        }

        string candidate = CreateDefaultName(existingNames);

        DispatcherQueue.TryEnqueue(() =>
        {
            // 読み込み中に閉じられた/開き直された場合や、既にユーザーが入力し始めている場合は上書きしない。
            if (session != _session || !IsOpen || !string.IsNullOrEmpty(_name)) return;
            _name = candidate;
            Refresh();
        });
    }

    /// <summary>
    /// 「コレクション1」から始めて、既存のコレクション名と重複しない名前を返す
    /// (既に同名があれば「コレクション2」…と数字を上げていく。空いている一番小さい番号になる)。
    /// 重複の判定は前後の空白を無視し、大文字小文字(全角英字含む)も区別しない。
    /// </summary>
    private static string CreateDefaultName(IEnumerable<string> existingNames)
    {
        var used = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (string name in existingNames)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                used.Add(name.Trim());
            }
        }

        int n = 1;
        string candidate;
        do
        {
            candidate = $"コレクション{n}";
            n++;
        } while (used.Contains(candidate));

        return candidate;
    }

    // ==================================================================
    // アイコン画像まわりの静的ヘルパー(コンソール編集・IconPickerOverlayからも使う)
    // ==================================================================

    /// <summary>ファイル内容のSHA-256(16進文字列)。読めなければnull。
    /// アイコン画像は小さいので、一覧を開くたびに計算しても負担は小さい。</summary>
    internal static string? ComputeFileHash(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return null;
        }
    }

    internal static IEnumerable<string> EnumerateIconFiles(string folder)
    {
        if (!Directory.Exists(folder)) return Enumerable.Empty<string>();

        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(f => IconExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f), StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CollectionEditOverlay] アイコンフォルダの列挙に失敗しました({folder}): {ex.Message}");
            return Enumerable.Empty<string>();
        }
    }

    /// <summary>一覧用のサムネイル。表示サイズ(136px)に対して十分な解像度でデコードし、
    /// 元画像が大きくてもメモリを使いすぎないようにする。</summary>
    internal static ImageSource? CreateThumbnail(string path)
    {
        try
        {
            return new BitmapImage { DecodePixelWidth = 256, UriSource = new Uri(path) };
        }
        catch
        {
            return null;
        }
    }

    internal static bool IsSamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}

public enum CollectionIconKind
{
    /// <summary>「なし」(アイコンを使わない)。</summary>
    None,

    /// <summary>画像ファイルのアイコン。</summary>
    Image,

    /// <summary>「ファイルから追加」(OSのファイル選択ダイアログを開く)。</summary>
    PickFile,
}

/// <summary>コレクション編集オーバーレイのアイコン一覧の1タイル。</summary>
public sealed class CollectionIconItem : INotifyPropertyChanged
{
    // 注意: このクラスはXAMLのx:DataTypeに指定しているため、XAMLコンパイラが公開プロパティの
    // setterを呼ぶコード(XamlTypeInfo)を自動生成する。initアクセサーにするとその生成コードが
    // コンパイルエラーになるので、通常のsetにしている(値を設定するのは生成直後の初期化子だけ)。
    public CollectionIconKind Kind { get; set; }

    /// <summary>画像ファイルの絶対パス(Kind=Imageの時のみ)。</summary>
    public string Path { get; set; } = string.Empty;

    public ImageSource? Thumbnail { get; set; }

    /// <summary>画像ファイル内容のハッシュ(重複排除と、選択中マークの判定に使う)。</summary>
    public string? ContentHash { get; set; }

    public Visibility ImageVisibility => Kind == CollectionIconKind.Image ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoneVisibility => Kind == CollectionIconKind.None ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PickFileVisibility => Kind == CollectionIconKind.PickFile ? Visibility.Visible : Visibility.Collapsed;

    private bool _isFocused;

    /// <summary>ゲームパッドの選択枠が乗っているか。</summary>
    public bool IsFocused
    {
        get => _isFocused;
        set
        {
            if (_isFocused == value) return;
            _isFocused = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFocused)));
        }
    }

    private bool _isSelected;

    /// <summary>現在このアイコンが選ばれているか(右上のチェックマーク)。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedVisibility)));
        }
    }

    public Visibility SelectedVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
}