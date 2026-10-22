using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using EmulatorLauncher.Controls;
using EmulatorLauncher.Emulation;
using EmulatorLauncher.Models;
using EmulatorLauncher.RomLibrary;
using EmulatorLauncher.Input;
using EmulatorLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace EmulatorLauncher.Views;

// SettingsPage の一部(partial class)。アプリ設定(地域・操作ボタンの入れ替え・バックアップ/復元・フォントなど)。
// 動作は変えず、巨大だったSettingsPage.xaml.csを役割ごとに分けただけ。
public sealed partial class SettingsPage
{
    // ------------------------------------------------------------------
    // アプリ設定: 地域 / 操作ボタンの入れ替え(決定/戻る・キャンセルの物理ボタン)
    // (操作ボタンの入れ替えは以前は「アプリ情報」にあった。アプリ情報の表示内容も
    // 同じパネルの下部へ移した)
    // ------------------------------------------------------------------

    private const string ThemeRowKey = "theme";
    private const string RegionRowKey = "region";
    private const string StartFullScreenRowKey = "startFullScreen";
    private const string SwapButtonsRowKey = "swapButtons";
    private const string AutoPauseRowKey = "autoPause";
    private const string LaunchModeRowKey = "launchMode";
    private const string ArcadeClonesRowKey = "arcadeClones";
    private const string ResumeEnabledRowKey = "resumeEnabled";
    private const string ResumeLaunchRowKey = "resumeLaunch";
    private const string FontRowKey = "font";
    private const string VirtualResolutionRowKey = "virtualResolution";
    private const string MenuBgmRowKey = "menuBgm";
    private const string SoundEffectsRowKey = "soundEffects";
    private const string GameVolumeRowKey = "gameVolume";
    private const string BackupRowKey = "backup";
    private const string RestoreRowKey = "restore";
    private const string BackupFolderRowKey = "backupFolder";

    // 音量の段階(大まかに4段階)。メニューBGM/効果音は「オフ→小→中→大」、ゲームは「25→50→75→100%」を順に切り替える。
    private static readonly int[] BgmLevels = { 0, 25, 50, 100 };
    private static readonly int[] EffectLevels = { 0, 30, 60, 100 };
    private static readonly int[] GameLevels = { 25, 50, 75, 100 };

    /// <summary>アプリ設定パネルの設定行(「地域」「操作ボタンの入れ替え」)。</summary>
    public ObservableCollection<AppSettingRowItem> AppSettingRows { get; } = new()
    {
        new AppSettingRowItem
        {
            Key = ThemeRowKey,
            Label = "テーマ",
            Description = "画面の見た目を選びます。「RetroNexus標準」(標準)は今までの画面、"
                + "「ダーク」は暗い画面で、コンソールとコレクションを統合した画面になります。設定を閉じると反映されます。",
        },
        new AppSettingRowItem
        {
            Key = LaunchModeRowKey,
            Label = "ゲーム起動方法",
            Description = "ゲーム選択画面で決定ボタンを押したときの動作を選びます。"
                + "「詳細画面から起動」(標準)は、決定ボタンで詳細画面を開き、そこの「起動」(再開データがあれば「再開」も)を選んで起動します(この場合Xボタンの詳細は使いません)。"
                + "「ゲーム起動」は、すぐにゲームを起動します(Xボタンで詳細画面)。",
        },
        new AppSettingRowItem
        {
            Key = ResumeEnabledRowKey,
            Label = "ゲーム再開",
            Description = "「有効」にすると、「タイトル選択にもどる」で終了するときにその場面を保存し、次回続きから始められます(詳細画面の「再開」)。"
                + "「無効」(標準)は何も保存せず、「再開」も表示しません。無効にしても、すでに保存済みのデータは使われなくなるだけで、消えません。",
        },
        new AppSettingRowItem
        {
            Key = ResumeLaunchRowKey,
            Label = "再開でのゲーム起動",
            Description = "「ゲーム起動方法」が「ゲーム起動」のとき、決定ボタンで起動する際の動作を選びます。"
                + "「再開データ使用する」(標準)は、「タイトル選択にもどる」で保存した再開データがあれば続きから始めます。"
                + "「再開データ使用しない」は、常に最初から始めます。「詳細画面から起動」のときは、詳細画面の「再開」「起動」で選べます。"
                + "「ゲーム再開」が有効のときだけ設定できます。",
        },
        new AppSettingRowItem
        {
            Key = MenuBgmRowKey,
            Label = "メニューBGM",
            Description = "メニュー画面で流すBGMの音量を選びます。「オフ」(標準)はBGMを流しません。"
                + "「小」「中」「大」は音量で、BGMはゲーム中は止まります。",
        },
        new AppSettingRowItem
        {
            Key = SoundEffectsRowKey,
            Label = "効果音",
            Description = "カーソル移動や決定などの効果音の音量を選びます。「大」(標準)が一番大きく、"
                + "「オフ」は効果音を鳴らしません。",
        },
        new AppSettingRowItem
        {
            Key = GameVolumeRowKey,
            Label = "ゲームの音量",
            Description = "ゲーム中の音量を選びます。「100%」(標準)はそのままの音量です。"
                + "変更は、次にゲームを起動したときから反映されます。",
        },
        new AppSettingRowItem
        {
            Key = ArcadeClonesRowKey,
            Label = "アーケードのクローン",
            Description = "FBNeo・MAME 2003 Plusで、同じゲームの別バージョン(クローン)を一覧にどう並べるかを選びます。"
                + "「親のみ表示」(標準)は代表の1本だけを並べ、クローンは詳細画面の「バージョン」から選んで起動します(名前で検索すると、親が見つかります)。"
                + "「すべて表示」は、クローンも1本ずつ並べます。親が見つからないクローンは、どちらでも表示します。",
        },
        new AppSettingRowItem
        {
            Key = SwapButtonsRowKey,
            Label = "操作ボタンの入れ替え",
            Description = "ランチャーのメニューとゲーム中断メニューで、AボタンとBボタンの役割を入れ替えます。"
                + "標準は右のボタン(PSコントローラなら○)が決定、下のボタン(×)がもどる。入れ替えると×が決定、○がもどるになります。ゲーム本体の操作には影響しません。",
        },
        new AppSettingRowItem
        {
            Key = AutoPauseRowKey,
            Label = "自動中断",
            Description = "ゲーム中に別のウィンドウやアプリへ切り替えたとき、自動でゲームを中断(一時停止)して中断メニューを表示します。"
                + "無効(標準)の場合は、切り替えてもゲームは止まらず動き続けます。",
        },
        new AppSettingRowItem
        {
            Key = BackupRowKey,
            Label = "バックアップ",
            Description = "ゲームの登録や設定などを、日付つきのファイルに保存します。「基本データ」は必ず含まれ、「ステート」と「システム画像」は選べます(容量も表示します)。"
                + "ROM本体、BIOS、コアのファイルは含まれません。",
        },
        new AppSettingRowItem
        {
            Key = RestoreRowKey,
            Label = "バックアップから復元",
            Description = "保存先フォルダにあるバックアップを選んで、データを戻します。復元の前に、今の状態を自動でバックアップします。"
                + "復元はアプリを終了して、次に起動したときに行います。",
        },
        new AppSettingRowItem
        {
            Key = BackupFolderRowKey,
            Label = "バックアップの保存先",
            Description = "バックアップを保存する場所と、復元で探す場所です。外付けドライブなど、アプリのフォルダとは別の場所にしておくと安全です。",
        },
    };

    private AppSettingRowItem GetAppSettingRow(string key) => AppSettingRows.Concat(SystemMenuEntries).First(r => r.Key == key);

    /// <summary>保存済みの値を各行の右側(現在値)へ反映する。</summary>
    private void LoadAppSettingsUi()
    {
        GetAppSettingRow(RegionRowKey).ValueText =
            AppRegionInfo.GetDisplayName(AppPreferencesStore.Load().Region);
        GetAppSettingRow(StartFullScreenRowKey).ValueText =
            EnabledText(AppPreferencesStore.Load().StartFullScreen);
        GetAppSettingRow(MenuBgmRowKey).ValueText =
            LevelText(NearestLevel(BgmLevels, AppPreferencesStore.Load().MenuBgmEnabled ? AppPreferencesStore.Load().MenuBgmVolume : 0));
        GetAppSettingRow(SoundEffectsRowKey).ValueText =
            LevelText(NearestLevel(EffectLevels, AppPreferencesStore.Load().SoundEffectsEnabled ? AppPreferencesStore.Load().SoundEffectsVolume : 0));
        GetAppSettingRow(GameVolumeRowKey).ValueText = $"{NearestLevel(GameLevels, AppPreferencesStore.Load().GameVolume)}%";
        GetAppSettingRow(AutoPauseRowKey).ValueText =
            EnabledText(AppPreferencesStore.Load().AutoPauseOnDeactivate);
        GetAppSettingRow(LaunchModeRowKey).ValueText =
            LaunchModeText(AppPreferencesStore.Load().LaunchViaDetail);
        GetAppSettingRow(ThemeRowKey).ValueText = ThemeText(AppPreferencesStore.Load().Theme);
        GetAppSettingRow(ArcadeClonesRowKey).ValueText =
            ArcadeClonesText(AppPreferencesStore.Load().ShowArcadeClones);
        GetAppSettingRow(ResumeEnabledRowKey).ValueText =
            EnabledText(AppPreferencesStore.Load().ResumeEnabled);
        GetAppSettingRow(ResumeLaunchRowKey).ValueText =
            ResumeLaunchText(AppPreferencesStore.Load().ResumeOnDirectLaunch);
        GetAppSettingRow(ResumeLaunchRowKey).IsEnabled = AppPreferencesStore.Load().ResumeEnabled;
        GetAppSettingRow(VirtualResolutionRowKey).ValueText =
            VirtualResolutions.Get(AppPreferencesStore.Load().VirtualResolution).Label;
        GetAppSettingRow(FontRowKey).ValueText =
            FontText(AppPreferencesStore.Load().UseSystemFont);
        GetAppSettingRow(SwapButtonsRowKey).ValueText =
            SwapButtonsText(ControllerMappingService.Load().SwapLauncherConfirmCancel);
        GetAppSettingRow(BackupFolderRowKey).ValueText = BackupFolderText(BackupService.GetFolder());

        // 起動時に、バックアップからの復元を行っていたら、その結果をここで知らせる(1回だけ)。
        if (BackupService.TakeRestoreResult() is string restoreResult)
            _ = InfoAsync("バックアップからの復元", restoreResult);
    }

    /// <summary>「標準」(A=決定 / B=戻る・キャンセル) / 「入れ替え」(B=決定 / A=戻る・キャンセル)。</summary>
    private static string EnabledText(bool enabled) => enabled ? "有効" : "無効";

    /// <summary>保存値に一番近い段階の値。</summary>
    private static int NearestLevel(int[] levels, int value)
        => levels.OrderBy(l => Math.Abs(l - value)).First();

    /// <summary>0=オフ、それ以外は段階の大きさ(小/中/大)。</summary>
    private static string LevelText(int level) => level switch { <= 0 => "オフ", <= 30 => "小", < 100 => "中", _ => "大" };

    private static int NextLevel(int[] levels, int value)
        => levels[(Array.IndexOf(levels, NearestLevel(levels, value)) + 1) % levels.Length];

    private static string SoundEffectsText(bool enabled) => enabled ? "鳴らす" : "鳴らさない";

    private static string FontText(bool useSystemFont) => useSystemFont ? "システム標準(再起動で反映)" : "アプリ標準";

    private static string ResumeLaunchText(bool use) => use ? "再開データ使用する" : "再開データ使用しない";

    private static string ThemeText(string theme) => theme == AppThemes.Dark ? "ダーク" : "RetroNexus標準";

    private static string ArcadeClonesText(bool showAll) => showAll ? "すべて表示" : "親のみ表示";

    private static string LaunchModeText(bool viaDetail) => viaDetail ? "詳細画面から起動" : "ゲーム起動";

    private static string SwapButtonsText(bool swapped) => swapped ? "入れ替え" : "標準";

    private void AppSettingsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 選択行が変わったら(マウスクリック等)、枠の表示を合わせ直す。
        RefreshRowFocusFlags();
    }

    private void AppSettingRow_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AppSettingRowItem row })
        {
            AppSettingsListView.SelectedItem = row;
            SetFocusRegion(FocusRegion.AppSettingsList);
            _ = ActivateAppSettingRowAsync(row);
        }
    }

    /// <summary>設定行の決定(Aボタン/クリック)。地域は選択画面を開き、操作ボタンの入れ替えは標準⇔入れ替えを切り替える。</summary>
    private async Task ActivateAppSettingRowAsync(AppSettingRowItem row)
    {
        switch (row.Key)
        {
            case SystemMenuItemsKey:
                await EditSystemItemsAsync(row.Label);
                break;

            case SystemMenuReorderKey:
                await ReorderSystemItemsAsync(row.Label);
                break;

            case RegionRowKey:
                await ChooseRegionAsync(row);
                break;

            case StartFullScreenRowKey:
                ToggleStartFullScreen(row);
                break;

            case MenuBgmRowKey:
                ToggleMenuBgm(row);
                break;

            case SoundEffectsRowKey:
                ToggleSoundEffects(row);
                break;

            case GameVolumeRowKey:
                CycleGameVolume(row);
                break;

            case AutoPauseRowKey:
                ToggleAutoPause(row);
                break;

            case LaunchModeRowKey:
                ToggleLaunchMode(row);
                break;

            case ThemeRowKey:
                ToggleTheme(row);
                break;

            case ArcadeClonesRowKey:
                ToggleArcadeClones(row);
                break;

            case ResumeEnabledRowKey:
                ToggleResumeEnabled(row);
                break;

            case ResumeLaunchRowKey:
                if (row.IsEnabled) ToggleResumeLaunch(row);
                break;

            case FontRowKey:
                ToggleFont(row);
                break;

            case VirtualResolutionRowKey:
                await ChooseVirtualResolutionAsync(row);
                break;

            case SwapButtonsRowKey:
                ToggleSwapButtons(row);
                break;

            case BackupRowKey:
                await BackupAsync(row);
                break;

            case RestoreRowKey:
                await RestoreAsync(row);
                break;

            case BackupFolderRowKey:
                await ChooseBackupFolderAsync(row);
                break;
        }
    }

    // ---- バックアップ / 復元 ----

    private bool _backupBusy;

    /// <summary>長い保存先は、末尾を残して短く表示する(右側の現在値の欄に収めるため)。</summary>
    private static string BackupFolderText(string path)
        => path.Length <= 36 ? path : "…" + path[^35..];

    private bool AnyOverlayOpen()
        => _backupBusy || SystemItemsPicker.IsOpen || ReorderScreen.IsOpen || SystemOrderScreen.IsOpen
           || ChoiceOverlay.IsOpen || FolderChooser.IsOpen || ConfirmOverlay.IsOpen || MessageOverlay.IsOpen;

    private async Task BackupAsync(AppSettingRowItem row)
    {
        if (AnyOverlayOpen()) return;
        _backupBusy = true;
        string previous = row.ValueText;
        try
        {
            row.ValueText = "容量を計算中…";
            IReadOnlyList<BackupPart> sizes = await BackupService.MeasureAsync();
            row.ValueText = previous;

            var rows = sizes.Select(p => (
                Id: p.Id,
                Label: (p.Id == BackupService.BasePartId ? p.Label + "(必須)" : p.Label) + "　" + BackupService.FormatSize(p.Bytes),
                Selected: p.Id != BackupService.StatesPartId));   // ステートは大きくなりやすいので、最初は選ばない

            _backupBusy = false;   // 選択画面を開いている間は、画面の開閉状態で重複を防ぐ
            string[]? picked = await SystemItemsPicker.ShowAsync(
                row.Label,
                rows,
                "バックアップに含める項目を選びます。「基本データ」(ゲームの登録・設定・コントローラ設定・ランチャー/コアの設定・プラグイン・ゲーム内セーブ)は外せません。"
                + "「ステート」はステートセーブと再開データ、「システム画像」はコレクションのアイコン・Imagesフォルダの画像・ゲーム情報の編集で選んだ表紙です(自動で取得したパッケージ画像は含まれません)。"
                + "容量は圧縮前の大きさで、実際のファイルは小さくなることがあります。ROM本体、BIOS、コアのファイルは含まれません。",
                "バックアップする項目",
                "なし",
                new[] { BackupService.BasePartId });
            if (picked is null) return;

            _backupBusy = true;
            row.ValueText = "作成中…";
            await BackupService.CreateAsync(picked);
            row.ValueText = previous;
            _backupBusy = false;
        }
        catch (Exception ex)
        {
            row.ValueText = previous;
            AppLog.Error("Backup", "バックアップに失敗しました", ex);
            _backupBusy = false;
            await InfoAsync("バックアップできませんでした。", ex.Message);
        }
        finally
        {
            _backupBusy = false;
            row.ValueText = previous;
        }
    }

    private async Task RestoreAsync(AppSettingRowItem row)
    {
        if (AnyOverlayOpen()) return;
        try
        {
            IReadOnlyList<BackupInfo> list = await BackupService.ListAsync();
            if (list.Count == 0)
            {
                await InfoAsync("バックアップが見つかりません。",
                    "保存先: " + BackupService.GetFolder() + "\nバックアップを作るか、保存先を変えてください。");
                return;
            }

            GamepadPickerItem? choice = await ChoiceOverlay.ShowAsync(
                row.Label,
                list.Select(b => new GamepadPickerItem
                {
                    Label = $"{b.Created:yyyy/MM/dd HH:mm}　{BackupService.FormatSize(b.SizeBytes)}　"
                            + string.Join("・", b.Parts.Select(BackupService.PartLabel))
                            + (System.IO.Path.GetFileName(b.Path).Contains("復元前") ? "　(復元前の自動保存)" : ""),
                    Tag = b,
                }));
            if (choice?.Tag is not BackupInfo backup) return;

            string[] parts = backup.Parts.ToArray();
            if (backup.Parts.Count > 1)
            {
                string[]? picked = await SystemItemsPicker.ShowAsync(
                    "復元する項目",
                    backup.Parts.Select(id => (
                        Id: id,
                        Label: id == BackupService.BasePartId ? BackupService.PartLabel(id) + "(必須)" : BackupService.PartLabel(id),
                        Selected: true)),
                    "このバックアップに入っている項目のうち、戻すものを選びます。「基本データ」は必ず戻します。",
                    "復元する項目",
                    "なし",
                    new[] { BackupService.BasePartId });
                if (picked is null) return;
                parts = picked;
            }

            bool ok = await ConfirmOverlay.ShowAsync(
                $"{backup.Created:yyyy/MM/dd HH:mm} のバックアップから復元します。現在のデータは上書きされます"
                + "(復元の前に、今の状態を自動でバックアップします)。復元のため、アプリを終了します。続けますか?",
                "復元する");
            if (!ok) return;

            _backupBusy = true;
            row.ValueText = "準備中…";
            string safety = await BackupService.StageRestoreAsync(backup, parts);
            row.ValueText = string.Empty;
            _backupBusy = false;

            await InfoAsync("復元の準備ができました",
                "アプリを終了します。もう一度起動すると、復元が行われます。\n復元前の状態: " + System.IO.Path.GetFileName(safety));
            AppServices.MainWindow?.Close();
        }
        catch (Exception ex)
        {
            AppLog.Error("Backup", "復元の準備に失敗しました", ex);
            _backupBusy = false;
            row.ValueText = string.Empty;
            await InfoAsync("復元の準備ができませんでした。", ex.Message);
        }
    }

    private async Task ChooseBackupFolderAsync(AppSettingRowItem row)
    {
        if (AnyOverlayOpen()) return;

        string? picked = await FolderChooser.ShowAsync("バックアップの保存先を選択", BackupService.GetFolder());
        if (string.IsNullOrEmpty(picked)) return;

        AppPreferences preferences = AppPreferencesStore.Load();
        preferences.BackupFolder = picked;
        AppPreferencesStore.Save(preferences);
        row.ValueText = BackupFolderText(picked);
    }

    /// <summary>「コンソール並び替え」: 全画面の入れ替え画面で並び替え、変わっていればConsoles.jsonの並びへ保存する。</summary>
    private async Task ReorderConsolesAsync(string label)
    {
        if (ReorderScreen.IsOpen || SystemOrderScreen.IsOpen || ChoiceOverlay.IsOpen) return;

        IReadOnlyList<string>? order = await ReorderScreen.ShowAsync(
            label,
            SampleData.GetConsoles().Select(c => new ReorderItem { Id = c.Id, Label = c.Name }));
        if (order is null) return;

        try
        {
            LauncherConfigService.ReorderConsoles(order);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsPage] コンソールの並び順の保存に失敗しました: {ex.Message}");
            await InfoAsync("並び順を保存できませんでした。", ex.Message);
        }
    }

    /// <summary>「コレクション並び替え」: 「お気に入り」は常に先頭なので一覧に含めない。</summary>
    private async Task ReorderCollectionsAsync(string label)
    {
        if (ReorderScreen.IsOpen || SystemOrderScreen.IsOpen || ChoiceOverlay.IsOpen) return;

        List<RomCollectionInfo> collections;
        try
        {
            collections = await CollectionLibraryFacade.GetAllAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsPage] コレクションの読み込みに失敗しました: {ex.Message}");
            return;
        }

        IReadOnlyList<string>? order = await ReorderScreen.ShowAsync(
            label,
            collections
                .Where(c => c.Id != CollectionLibraryFacade.FavoriteCollectionId)
                .Select(c => new ReorderItem { Id = c.Id, Label = c.Name }));
        if (order is null) return;

        try
        {
            await CollectionLibraryFacade.ReorderAsync(order);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsPage] コレクションの並び順の保存に失敗しました: {ex.Message}");
            await InfoAsync("並び順を保存できませんでした。", ex.Message);
        }
    }

    private async Task ChooseRegionAsync(AppSettingRowItem row)
    {
        if (ChoiceOverlay.IsOpen) return;

        AppPreferences preferences = AppPreferencesStore.Load();
        GamepadPickerItem? picked = await ChoiceOverlay.ShowAsync(
            row.Label,
            Enum.GetValues<AppRegion>().Select(region => new GamepadPickerItem
            {
                Label = AppRegionInfo.GetDisplayName(region),
                Tag = region,
                IsChecked = region == preferences.Region,
            }));

        if (picked?.Tag is not AppRegion selected || selected == preferences.Region) return;

        preferences.Region = selected;
        AppPreferencesStore.Save(preferences);
        row.ValueText = AppRegionInfo.GetDisplayName(selected);
    }

    private async Task ChooseVirtualResolutionAsync(AppSettingRowItem row)
    {
        if (ChoiceOverlay.IsOpen) return;

        AppPreferences preferences = AppPreferencesStore.Load();
        string current = VirtualResolutions.Get(preferences.VirtualResolution).Id;
        GamepadPickerItem? picked = await ChoiceOverlay.ShowAsync(
            row.Label,
            VirtualResolutions.All.Select(r => new GamepadPickerItem
            {
                Label = r.Label,
                Tag = r.Id,
                IsChecked = r.Id == current,
            }));

        if (picked?.Tag is not string selected || selected == current) return;

        preferences.VirtualResolution = selected;
        AppPreferencesStore.Save(preferences);
        row.ValueText = VirtualResolutions.Get(selected).Label;
    }

    private void ToggleMenuBgm(AppSettingRowItem row)
    {
        // オフ→小→中→大→オフ…の順に切り替える。
        AppPreferences preferences = AppPreferencesStore.Load();
        int current = preferences.MenuBgmEnabled ? preferences.MenuBgmVolume : 0;
        int next = NextLevel(BgmLevels, current);
        preferences.MenuBgmEnabled = next > 0;
        if (next > 0) preferences.MenuBgmVolume = next;
        AppPreferencesStore.Save(preferences);
        row.ValueText = LevelText(next);
        if (next > 0) BgmPlayer.SetVolume(next);
        BgmPlayer.SetEnabled(preferences.MenuBgmEnabled); // すぐに鳴らす/止める
    }

    private void ToggleSoundEffects(AppSettingRowItem row)
    {
        // オフ→小→中→大→オフ…の順に切り替える。切り替えた直後に、その音量で1回鳴らして確認できるようにする。
        AppPreferences preferences = AppPreferencesStore.Load();
        int current = preferences.SoundEffectsEnabled ? preferences.SoundEffectsVolume : 0;
        int next = NextLevel(EffectLevels, current);
        preferences.SoundEffectsEnabled = next > 0;
        if (next > 0) preferences.SoundEffectsVolume = next;
        AppPreferencesStore.Save(preferences);
        row.ValueText = LevelText(next);
        SoundEffects.Enabled = preferences.SoundEffectsEnabled;
        if (next > 0) SoundEffects.Volume = next / 100.0;
    }

    private void CycleGameVolume(AppSettingRowItem row)
    {
        AppPreferences preferences = AppPreferencesStore.Load();
        int next = NextLevel(GameLevels, preferences.GameVolume);
        preferences.GameVolume = next;
        AppPreferencesStore.Save(preferences);
        row.ValueText = $"{next}%";
    }

    private void ToggleFont(AppSettingRowItem row)
    {
        AppPreferences preferences = AppPreferencesStore.Load();
        preferences.UseSystemFont = !preferences.UseSystemFont;
        AppPreferencesStore.Save(preferences);
        row.ValueText = FontText(preferences.UseSystemFont);
    }

    private void ToggleLaunchMode(AppSettingRowItem row)
    {
        AppPreferences preferences = AppPreferencesStore.Load();
        preferences.LaunchViaDetail = !preferences.LaunchViaDetail;
        AppPreferencesStore.Save(preferences);
        row.ValueText = LaunchModeText(preferences.LaunchViaDetail);
    }

    private void ToggleTheme(AppSettingRowItem row)
    {
        AppPreferences preferences = AppPreferencesStore.Load();
        preferences.Theme = preferences.Theme == AppThemes.Dark ? AppThemes.Standard : AppThemes.Dark;
        AppPreferencesStore.Save(preferences);
        row.ValueText = ThemeText(preferences.Theme);
        AppThemeRouter.ApplyForPage(typeof(SettingsPage));   // 設定画面自身もその場で切り替える
    }

    private void BackToPreviousButton_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        e.Handled = true;
        LeaveSettings();
    }

    /// <summary>設定を閉じてコンソール選択画面へ戻る。テーマが変わっていたら、履歴を消して新しいテーマの画面を開く。</summary>
    private void LeaveSettings()
    {
        if (Frame == null) return;
        if (AppThemeRouter.ThemeChanged)
        {
            Frame.BackStack.Clear();
            Frame.Navigate(AppThemeRouter.ConsoleSelectPageType);
            return;
        }
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private void ToggleArcadeClones(AppSettingRowItem row)
    {
        AppPreferences preferences = AppPreferencesStore.Load();
        preferences.ShowArcadeClones = !preferences.ShowArcadeClones;
        AppPreferencesStore.Save(preferences);
        row.ValueText = ArcadeClonesText(preferences.ShowArcadeClones);
    }

    private void ToggleResumeEnabled(AppSettingRowItem row)
    {
        AppPreferences preferences = AppPreferencesStore.Load();
        preferences.ResumeEnabled = !preferences.ResumeEnabled;
        AppPreferencesStore.Save(preferences);
        row.ValueText = EnabledText(preferences.ResumeEnabled);
        // 関連する「再開でのゲーム起動」は、有効のときだけ設定できる(無効ならグレーアウト)。
        GetAppSettingRow(ResumeLaunchRowKey).IsEnabled = preferences.ResumeEnabled;
    }

    private void ToggleResumeLaunch(AppSettingRowItem row)
    {
        AppPreferences preferences = AppPreferencesStore.Load();
        preferences.ResumeOnDirectLaunch = !preferences.ResumeOnDirectLaunch;
        AppPreferencesStore.Save(preferences);
        row.ValueText = ResumeLaunchText(preferences.ResumeOnDirectLaunch);
    }

    private void ToggleStartFullScreen(AppSettingRowItem row)
    {
        AppPreferences preferences = AppPreferencesStore.Load();
        preferences.StartFullScreen = !preferences.StartFullScreen;
        AppPreferencesStore.Save(preferences);
        row.ValueText = EnabledText(preferences.StartFullScreen);
    }

    private void ToggleAutoPause(AppSettingRowItem row)
    {
        AppPreferences preferences = AppPreferencesStore.Load();
        preferences.AutoPauseOnDeactivate = !preferences.AutoPauseOnDeactivate;
        AppPreferencesStore.Save(preferences);
        row.ValueText = EnabledText(preferences.AutoPauseOnDeactivate);
    }

    private void ToggleSwapButtons(AppSettingRowItem row)
    {
        ControllerMappingService.ControllerMappingData data = ControllerMappingService.Load();
        data.SwapLauncherConfirmCancel = !data.SwapLauncherConfirmCancel;
        ControllerMappingService.Save(data);
        row.ValueText = SwapButtonsText(data.SwapLauncherConfirmCancel);
        HintBar.NotifySwapChanged(); // 操作ガイドのA/B表示を、新しい設定に合わせる
    }

    /// <summary>
    /// cores/libretro.jsonの一覧を、実際のcoresフォルダの状態と同期したうえで読み込み、
    /// コンソール名(Consoles.json)を付与してCoresへ反映する。
    /// </summary>
    private async Task LoadCoresAsync()
    {
        CoreManagementHeader.IsLoading = true;
        try
        {
            List<LibretroCoreInfo> catalog;
            try
            {
                // 手動でのdll追加/削除や、前回起動時からの変化を拾えるよう、表示のたびに
                // 実際のcoresフォルダの状態へInstalledを合わせ直してから読み込む。
                catalog = await LibretroCoreCatalogService.RefreshInstalledStatusAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SettingsPage] コア一覧の読み込みに失敗しました: {ex.Message}");
                catalog = new List<LibretroCoreInfo>();
            }

            Dictionary<string, string> consoleNames = SampleData.GetConsoles()
                .ToDictionary(c => c.Id, c => c.Name, StringComparer.OrdinalIgnoreCase);

            List<LibretroCoreListItem> items = catalog
                .Select(core => new LibretroCoreListItem
                {
                    Core = core,
                    ConsoleDisplayName = consoleNames.TryGetValue(core.ConsoleId, out string? name)
                        ? name
                        : core.ConsoleId,
                    Installed = core.Installed,
                })
                // 表示名(cores/libretro.jsonのdisplay_name)順に並べる。同名の場合のみ
                // コンソール名で順序を安定させる(「コア管理」「コア毎の設定」共通)。
                .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.ConsoleDisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            Cores.Clear();
            foreach (LibretroCoreListItem item in items)
            {
                Cores.Add(item);
            }

            CoreManagementHeader.CountText = $"({Cores.Count}件)";
            CoreListView.SelectedIndex = Cores.Count > 0 ? 0 : -1;

            // Coresを作り直したため、詳細設定画面が古いインスタンスを参照したままに
            // ならないよう、インストール済み一覧と選択状態を一旦リセットする
            // (詳細設定画面へ入るたびにRefreshInstalledCoresで作り直される)。
            InstalledCores.Clear();
            ShowNoCoreSelectedForOptions();

            // Coresの中身自体を作り直したため、以前のIsFocusedは新しいインスタンスには
            // 引き継がれない。現在アクティブな領域に合わせて改めて設定し直す。
            RefreshRowFocusFlags();
        }
        finally
        {
            CoreManagementHeader.IsLoading = false;
        }
    }

    private readonly PaneFader _paneFader = new();

    private void CategoryListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        string? selected = (CategoryListView.SelectedItem as SettingsCategoryItem)?.Label;
        RefreshCategoryFlags();
        // 選択の印(白枠/青バー)が、前の行から新しい行へ滑るように動く。
        if (e.RemovedItems.Count > 0 && e.RemovedItems[0] is SettingsCategoryItem removed)
        {
            int oldIndex = Categories.IndexOf(removed);
            if (oldIndex >= 0 && CategoryListView.SelectedIndex >= 0)
                HighlightSlide.SlideRow(CategoryListView.ContainerFromIndex(oldIndex) as UIElement,
                    CategoryListView.ContainerFromIndex(CategoryListView.SelectedIndex) as UIElement);
        }
        // 右ペインはフェードアウト→切り替え→フェードインで表示する。
        _ = _paneFader.SwitchAsync(() =>
        {
            IsCoreManagementSelected = selected == CoreManagementCategoryLabel;
            IsGameFolderManagementSelected = selected == GameFolderManagementCategoryLabel;
            IsLauncherManagementSelected = selected == LauncherManagementCategoryLabel;
            IsConsoleManagementSelected = selected == ConsoleManagementCategoryLabel;
            IsSystemSettingsSelected = selected == SystemSettingsCategoryLabel;
            IsCollectionManagementSelected = selected == CollectionManagementCategoryLabel;
            IsAppSettingsSelected = selected == AppSettingsCategoryLabel;
            IsLicenseSelected = selected == LicenseCategoryLabel;
        }, CoreManagementPanel, GameFolderManagementPanel, LauncherManagementPanel, ConsoleManagementPanel, SystemSettingsPanel, CollectionManagementPanel, AppSettingsPanel, LicensePanel);

        // カテゴリが切り替わって、今フォーカスしている領域がもう選べなくなった場合は
        // (例: 「ゲームフォルダ管理」のFolderListにいたまま「コア管理」へ切り替えた場合)、
        // 常にどのカテゴリでも選べるCategory領域へ戻す。
        if (!CurrentRegions.Contains(_focusRegion))
        {
            SetFocusRegion(FocusRegion.Category);
        }
    }

    /// <summary>
    /// 「コアモジュール追加/削除」で行を決定したとき。確認ダイアログ(ConfirmOverlay)で
    /// 「〇〇コアをインストール/アンインストールしてもよろしいでしょうか?」を出し、
    /// 実行が選ばれた場合だけインストール/アンインストールする。
    /// </summary>
    private async Task ConfirmAndExecuteCoreActionAsync(LibretroCoreListItem item)
    {
        if (item.IsBusy || ConfirmOverlay.IsOpen)
        {
            return;
        }

        string action = item.Installed ? "アンインストール" : "インストール";
        bool confirmed = await ConfirmOverlay.ShowAsync(
            $"{item.DisplayName}コアを{action}してもよろしいでしょうか？",
            $"{action}する");

        if (confirmed)
        {
            await ExecuteCoreActionAsync(item);
        }
    }

    private void CoreRow_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LibretroCoreListItem item })
        {
            CoreListView.SelectedItem = item;
            SetFocusRegion(FocusRegion.CoreList);
            _ = ConfirmAndExecuteCoreActionAsync(item);
        }
    }
}
