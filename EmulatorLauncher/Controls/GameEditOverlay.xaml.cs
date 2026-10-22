using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EmulatorLauncher.Input;
using EmulatorLauncher.Models;
using EmulatorLauncher.RomLibrary;
using EmulatorLauncher.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EmulatorLauncher.Controls;

/// <summary>
/// 登録ゲームの情報(タイトル・日本語タイトル・よみ・発売元・発売年・プレイ人数・ジャンル・概要・パッケージ画像)を
/// 書き換える全画面。画面構成・操作はGameEditOverlay.xamlのコメント参照(コレクションの編集に準拠)。
///
/// 使う側(GameDetailOverlay):
/// - <c>Show(RomFile)</c> で開く。表示中のゲームパッド入力は <see cref="HandleGamepadInput"/> へ渡す。
/// - 保存すると、渡したRomFileの値をその場で書き換え(<see cref="RomFile.NotifyGameInfoChanged"/>で一覧の表示も更新)、
///   Closedを「保存したか」つきで発火する。
///
/// 保存先はRomFileテーブルの該当列だけ(RomFileRepository.UpdateGameInfoAsync)。パッケージ画像は選んだ画像を
/// &lt;親フォルダ&gt;\RetroNexus\Named_Boxarts へ「custom_&lt;No&gt;_日時.拡張子」としてコピーし、そのファイル名を保存する
/// (元の画像ファイルは残す)。フォルダを再スキャンしても、すでに登録済みのゲームは上書きされない。
/// </summary>
public sealed partial class GameEditOverlay : UserControl
{
    private const string KeyTitle = "title";
    private const string KeyTitleJa = "titleJa";
    private const string KeyKana = "kana";
    private const string KeyPublisher = "publisher";
    private const string KeyYear = "year";
    private const string KeyPlayers = "players";
    private const string KeyGenre = "genre";
    private const string KeyDescription = "description";
    private const string KeyCover = "cover";
    private const string KeyDiscs = "discs";

    private const double RowPitch = 92;

    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp" };

    public ObservableCollection<EditFormRow> Rows { get; } = new();

    /// <summary>「保存する」ボタンの状態(x:Bindで表示)。</summary>
    public LauncherSaveButtonState SaveState { get; } = new();

    public bool IsOpen { get; private set; }

    /// <summary>閉じたときに発火する。引数は「保存したか」。</summary>
    public event EventHandler<bool>? Closed;

    /// <summary>
    /// 「複数枚(ディスク)」の行が決定されたとき、編集画面を閉じたあとに発火する
    /// (使う側が、まとめる/解除の操作を続ける。ゲームの.m3uに対しては解除、それ以外はまとめる)。
    /// </summary>
    public event EventHandler? DiscSetRequested;

    private RomFile? _game;

    // 編集中の値(文字の行は、保存まで画面の中だけで持つ)。
    private string _title = string.Empty;
    private string _titleJa = string.Empty;
    private string _kana = string.Empty;
    private string _publisher = string.Empty;
    private int _year;
    private string _players = string.Empty;
    private string _genre = string.Empty;
    private string _description = string.Empty;

    /// <summary>「パッケージ画像」で選んだ画像ファイル(まだコピーしていない。保存時に取り込む)。選んでいなければnull。</summary>
    private string? _pickedCover;

    /// <summary>開いた時点の値(戻るときの変更判定用)。</summary>
    private string _baseSnapshot = string.Empty;

    private int _focusIndex;
    private bool _onSave;
    private bool _textEditing;
    private bool _busy;
    private string? _editingKey;

    public GameEditOverlay()
    {
        this.InitializeComponent();
        EmulatorLauncher.Controls.LauncherWheel.AttachAll(this);
        Visibility = Visibility.Collapsed;
        OverlayFade.Attach(this);
    }

    // ==================================================================
    // 開く/閉じる
    // ==================================================================

    public void Show(RomFile game)
    {
        _game = game;
        _busy = false;
        _onSave = false;
        _textEditing = false;
        _editingKey = null;
        _pickedCover = null;
        TextEditLayer.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        PathChooser.Visibility = Visibility.Collapsed;

        TitleText.Text = "ゲーム情報の編集";

        _title = game.Title ?? string.Empty;
        _titleJa = game.TitleJa ?? string.Empty;
        _kana = game.TitleKana ?? string.Empty;
        _publisher = game.Publisher ?? string.Empty;
        _year = game.ReleaseYear;
        _players = game.Players ?? string.Empty;
        _genre = game.Genre ?? string.Empty;
        _description = game.Description ?? string.Empty;
        _baseSnapshot = Snapshot();

        Rows.Clear();
        var keys = new List<string>
        {
            KeyTitle, KeyTitleJa, KeyKana, KeyPublisher, KeyYear, KeyPlayers, KeyGenre, KeyDescription, KeyCover,
        };
        // 複数枚組のまとめに対応しているコンソール(いまはPS1)だけ、最後に「複数枚(ディスク)」の行を出す。
        if (GameLibraryFacade.CanGroupDiscs(game)) keys.Add(KeyDiscs);
        foreach (string key in keys)
        {
            Rows.Add(new EditFormRow { Key = key });
        }
        _focusIndex = 0;

        Refresh();

        Visibility = Visibility.Visible;
        IsOpen = true;
        RowsScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
    }

    private string Snapshot() => string.Join("\u001f", _title, _titleJa, _kana, _publisher, _year, _players, _genre, _description, _pickedCover ?? string.Empty);

    private bool IsDirty() => Snapshot() != _baseSnapshot;

    private async Task ConfirmDiscardAsync()
    {
        bool discard = await ConfirmOverlay.ShowAsync("保存していない変更があります。変更を捨てて戻りますか?", "変更を捨てる", "編集を続ける");
        if (discard && IsOpen) Close(saved: false);
    }

    private void Close(bool saved)
    {
        if (!IsOpen) return;

        Visibility = Visibility.Collapsed;
        IsOpen = false;
        _busy = false;
        _textEditing = false;

        Closed?.Invoke(this, saved);
    }

    // ==================================================================
    // 表示の更新
    // ==================================================================

    private string? FocusedKey => _focusIndex >= 0 && _focusIndex < Rows.Count ? Rows[_focusIndex].Key : null;

    /// <summary>タイトルは、英語・日本語のどちらかが入っていればよい(両方空だと一覧に出す名前が無くなる)。</summary>
    private bool HasTitle => !string.IsNullOrWhiteSpace(_title) || !string.IsNullOrWhiteSpace(_titleJa);

    private void Refresh()
    {
        foreach (EditFormRow row in Rows)
        {
            switch (row.Key)
            {
                case KeyTitle: Set(row, "タイトル(英語)", _title, "未入力"); break;
                case KeyTitleJa: Set(row, "日本語タイトル", _titleJa, "なし"); break;
                case KeyKana: Set(row, "よみ(ひらがな)", _kana, "なし"); break;
                case KeyPublisher: Set(row, "発売元", _publisher, "未入力"); break;
                case KeyYear: Set(row, "発売年", _year > 0 ? _year + "年" : string.Empty, "未設定"); break;
                case KeyPlayers: Set(row, "プレイ人数", _players, "未入力"); break;
                case KeyGenre: Set(row, "ジャンル", _genre, "未入力"); break;
                case KeyDescription: Set(row, "概要", FirstLine(_description), "未入力"); break;
                case KeyCover:
                    row.Label = "パッケージ画像";
                    bool changed = _pickedCover is not null;
                    bool has = changed || !string.IsNullOrEmpty(_game?.IconPath);
                    row.IsPlaceholder = !has;
                    row.DisplayValue = changed ? Path.GetFileName(_pickedCover!) : has ? "設定済み" : "なし";
                    break;
                case KeyDiscs:
                    row.Label = "複数枚(ディスク)";
                    bool isSet = _game?.IsDiscSet == true;
                    row.IsPlaceholder = !isSet;
                    row.DisplayValue = isSet ? "まとめ済み(Aで解除)" : "まとめる(Aで選ぶ)";
                    break;
            }
        }

        UpdatePreview();
        SaveState.IsEnabled = HasTitle && IsDirty();
        UpdateFocusFlags();
        RefreshHelpText();
    }

    private static void Set(EditFormRow row, string label, string value, string empty)
    {
        row.Label = label;
        bool has = !string.IsNullOrWhiteSpace(value);
        row.IsPlaceholder = !has;
        row.DisplayValue = has ? value : empty;
    }

    private static string FirstLine(string text)
    {
        string line = (text ?? string.Empty).Replace("\r", string.Empty).Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? string.Empty;
        return line.Trim();
    }

    private void RefreshHelpText()
    {
        if (_onSave)
        {
            HelpText.Text = "ゲーム情報を保存します。ゲームのファイルそのものは変更されません。";
            return;
        }

        HelpText.Text = FocusedKey switch
        {
            KeyTitle => "英語のタイトルです。日本語タイトルが無いときは、一覧にこの名前が出ます。",
            KeyTitleJa => "一覧と詳細画面に出す日本語のタイトルです。入れると、こちらが優先して表示されます。",
            KeyKana => "「あいうえお順」の並べ替えと検索に使う、ひらがなの読みです。",
            KeyPublisher => "発売元(メーカー)です。",
            KeyYear => "発売された西暦の年です(数字だけ。空にすると表示しません)。",
            KeyPlayers => "プレイ人数の表示です(例: 1人用、2人まで)。",
            KeyGenre => "ジャンルの表示です(例: アクション)。",
            KeyDescription => "詳細画面に出す概要です。複数行を入力できます。",
            KeyDiscs => _game?.IsDiscSet == true
                ? "複数枚組をまとめたゲームです。Aボタンで、まとめを解除します(.m3uを削除し、ディスクは一覧に戻ります。ディスクのファイルは消えません)。"
                : "「ディスク1」「ディスク2」…の複数枚組を、1本のゲームにまとめます。Aボタンで、このゲームを1枚目にして、2枚目以降を選びます(同じフォルダに.m3uを作ります)。",
            KeyCover => "一覧と詳細画面に出すパッケージ画像です。png / jpg / bmp のファイルを選びます(元のファイルは残り、コピーを使います)。",
            _ => string.Empty,
        };
    }

    private void UpdatePreview()
    {
        string? source = _pickedCover ?? (string.IsNullOrEmpty(_game?.IconPath) ? null : _game!.IconPath);
        if (!string.IsNullOrEmpty(source) && File.Exists(source))
        {
            try
            {
                var bitmap = new BitmapImage { DecodePixelWidth = 480 };
                bitmap.UriSource = new Uri(source);
                PreviewImage.Source = bitmap;
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
        if (PathChooser.IsOpen)
        {
            PathChooser.HandleGamepadInput(button);
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
                else Close(saved: false);
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
        string? key = FocusedKey;
        if (key == KeyDiscs) RequestDiscSet();
        else if (key == KeyCover) _ = PickCoverAsync();
        else if (key is not null) BeginTextEdit(key);
    }

    /// <summary>マウス/タッチ: 行をクリックしたらフォーカスを移してAと同じ動作にする。</summary>
    private void RowsItemsControl_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!IsOpen || _busy || _textEditing || PathChooser.IsOpen || ConfirmOverlay.IsOpen) return;
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
        if (!IsOpen || _busy || _textEditing || PathChooser.IsOpen || ConfirmOverlay.IsOpen) return;

        ErrorText.Visibility = Visibility.Collapsed;
        SetSaveFocus(true);
        _ = TrySaveAsync();
    }

    // ==================================================================
    // 複数枚(ディスク)のまとめ/解除。選択の操作は使う側(GameDetailOverlay)が続ける
    // ==================================================================

    private void RequestDiscSet()
    {
        if (IsDirty())
        {
            ShowError("編集中の内容があります。先に保存するか、変更を戻してから操作してください。");
            return;
        }

        Close(saved: false);
        DiscSetRequested?.Invoke(this, EventArgs.Empty);
    }

    // ==================================================================
    // パッケージ画像の選択
    // ==================================================================

    private async Task PickCoverAsync()
    {
        _busy = true;
        try
        {
            string? initial = _pickedCover ?? (string.IsNullOrEmpty(_game?.IconPath) ? null : _game!.IconPath);
            string? picked = await PathChooser.ShowFileAsync("パッケージ画像を選択", ImageExtensions, initial);
            if (!string.IsNullOrEmpty(picked))
            {
                _pickedCover = picked;
            }
        }
        catch (Exception ex)
        {
            ShowError($"ファイル選択に失敗しました: {ex.Message}");
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    // ==================================================================
    // 文字入力(文字の行ぜんぶ)
    // ==================================================================

    private (string Label, string Help, int MaxLength, bool Multiline, string Value) FieldOf(string key) => key switch
    {
        KeyTitle => ("タイトル(英語)", "ゲームの英語のタイトルです(100文字まで)。", 100, false, _title),
        KeyTitleJa => ("日本語タイトル", "一覧と詳細画面に出す日本語のタイトルです(100文字まで。空にすると英語のタイトルを使います)。", 100, false, _titleJa),
        KeyKana => ("よみ(ひらがな)", "「あいうえお順」の並べ替えと検索に使う、ひらがなの読みです(100文字まで)。", 100, false, _kana),
        KeyPublisher => ("発売元", "発売元(メーカー)の名前です(60文字まで)。", 60, false, _publisher),
        KeyYear => ("発売年", "発売された西暦の年を、数字だけで入力します(例: 1990)。空にすると表示しません。", 4, false, _year > 0 ? _year.ToString() : string.Empty),
        KeyPlayers => ("プレイ人数", "プレイ人数の表示です(例: 1人用、2人まで。30文字まで)。", 30, false, _players),
        KeyGenre => ("ジャンル", "ジャンルの表示です(例: アクション。40文字まで)。", 40, false, _genre),
        KeyDescription => ("概要", "詳細画面に出す概要です(1000文字まで。改行できます)。", 1000, true, _description),
        _ => (string.Empty, string.Empty, 100, false, string.Empty),
    };

    private void BeginTextEdit(string key)
    {
        var f = FieldOf(key);
        _editingKey = key;
        _textEditing = true;
        TextEditLabel.Text = f.Label;
        TextEditHelp.Text = f.Help + (f.Multiline ? "(確定はAボタン、またはCtrl+Enter)" : string.Empty);
        TextEditError.Visibility = Visibility.Collapsed;
        TextEditBox.MaxLength = f.MaxLength;
        TextEditBox.AcceptsReturn = f.Multiline;
        TextEditBox.TextWrapping = f.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap;
        TextEditBox.MinHeight = f.Multiline ? 280 : 0;
        TextEditBox.Text = f.Value;
        TextEditLayer.Visibility = Visibility.Visible;
        TextEditBox.Focus(FocusState.Programmatic);
        TextEditBox.SelectAll();
    }

    private void CommitTextEdit()
    {
        if (_editingKey is null) { EndTextEdit(); return; }

        string text = TextEditBox.Text ?? string.Empty;
        // 複数行以外は、前後の空白を取る。概要は、行末の余計な空白と前後の空行だけ取る。
        text = _editingKey == KeyDescription ? text.Replace("\r\n", "\n").Replace('\r', '\n').Trim() : text.Trim();

        switch (_editingKey)
        {
            case KeyTitle: _title = text; break;
            case KeyTitleJa: _titleJa = text; break;
            case KeyKana: _kana = text; break;
            case KeyPublisher: _publisher = text; break;
            case KeyYear:
                if (text.Length == 0) { _year = 0; break; }
                if (!int.TryParse(text, out int year) || year < 1900 || year > 2100)
                {
                    TextEditError.Text = "発売年は、1900〜2100の数字で入力してください。";
                    TextEditError.Visibility = Visibility.Visible;
                    return;   // 入力画面は閉じない
                }
                _year = year;
                break;
            case KeyPlayers: _players = text; break;
            case KeyGenre: _genre = text; break;
            case KeyDescription: _description = text; break;
        }

        EndTextEdit();
        Refresh();
    }

    private void EndTextEdit()
    {
        _textEditing = false;
        _editingKey = null;
        TextEditLayer.Visibility = Visibility.Collapsed;
        // キーボードフォーカスがTextBoxに残らないよう、このオーバーレイ自身へ戻す。
        this.Focus(FocusState.Programmatic);
    }

    private void TextEditBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (e.Key == Windows.System.VirtualKey.Enter && (!TextEditBox.AcceptsReturn || ctrl))
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
    // 保存
    // ==================================================================

    /// <summary>「保存する」。グレー(タイトルが空、または変更なし)の間は、理由を表示するだけで保存しない。</summary>
    private async Task TrySaveAsync()
    {
        if (_game is null) return;

        if (!SaveState.IsEnabled)
        {
            ShowError(HasTitle ? "変更はありません。" : "タイトル(英語または日本語)を入力してください。");
            return;
        }

        RomFile game = _game;
        _busy = true;
        try
        {
            // 英語タイトルが空で日本語タイトルだけあるときは、英語欄にも日本語タイトルを入れる(DB上、空にできないため)。
            string title = string.IsNullOrWhiteSpace(_title) ? _titleJa.Trim() : _title.Trim();

            string? iconFile = null;
            string? iconFullPath = null;
            if (_pickedCover is not null)
            {
                (iconFile, iconFullPath) = await ImportCoverAsync(game, _pickedCover);
            }

            bool ok = await RomFileRepository.UpdateGameInfoAsync(
                game.No, title, _titleJa.Trim(), _kana.Trim(), _publisher.Trim(), _year,
                _players.Trim(), _genre.Trim(), _description, iconFile);
            if (!ok)
            {
                ShowError("保存できませんでした(このゲームがデータベースに見つかりません)。");
                return;
            }

            // 画面が持っているゲームの値も更新し、一覧の表示へ知らせる。
            game.Title = title;
            game.TitleJa = _titleJa.Trim();
            game.TitleKana = _kana.Trim();
            game.Publisher = _publisher.Trim();
            game.ReleaseYear = _year;
            game.Players = _players.Trim();
            game.Genre = _genre.Trim();
            game.Description = _description;
            if (iconFullPath is not null) game.IconPath = iconFullPath;
            game.NotifyGameInfoChanged();

            _busy = false;
            Close(saved: true);
        }
        catch (Exception ex)
        {
            ShowError($"保存に失敗しました: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// 選んだ画像を、親フォルダの RetroNexus\Named_Boxarts へ「custom_&lt;No&gt;_日時.拡張子」でコピーする。
    /// 以前に取り込んだ同じゲームの画像(custom_&lt;No&gt;_*)は消す。戻り値は(DBへ保存するファイル名, 表示に使うフルパス)。
    /// </summary>
    private static async Task<(string FileName, string FullPath)> ImportCoverAsync(RomFile game, string sourcePath)
    {
        RomFolder? folder = await RomFolderRepository.GetAsync(game.ParentId);
        if (folder is null) throw new InvalidOperationException("ゲームのフォルダが見つかりません。");
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("選んだ画像が見つかりません。", sourcePath);

        string directory = Path.Combine(folder.Path, "RetroNexus", "Named_Boxarts");
        Directory.CreateDirectory(directory);

        string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        string fileName = $"custom_{game.No}_{DateTime.Now:yyyyMMddHHmmssfff}{extension}";
        string destination = Path.Combine(directory, fileName);
        await Task.Run(() => File.Copy(sourcePath, destination, overwrite: false));

        // 古い取り込み画像(同じゲームの custom_<No>_*)を片付ける。失敗しても保存は続ける。
        try
        {
            foreach (string old in Directory.EnumerateFiles(directory, $"custom_{game.No}_*"))
            {
                if (!string.Equals(old, destination, StringComparison.OrdinalIgnoreCase)) File.Delete(old);
            }
        }
        catch (Exception)
        {
        }

        return (fileName, destination);
    }
}
