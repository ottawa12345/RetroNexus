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

// SettingsPage の一部(partial class)。コア管理・コア毎の設定・ゲームフォルダ管理。
// 動作は変えず、巨大だったSettingsPage.xaml.csを役割ごとに分けただけ。
public sealed partial class SettingsPage
{
    // ------------------------------------------------------------------
    // コア管理の2項目メニュー(コアモジュール追加/削除、コアモジュール詳細設定)
    // ------------------------------------------------------------------

    private void CoreMenuListView_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshRowFocusFlags();

    private void CoreMenuEntry_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SettingsMenuEntryItem entry })
        {
            CoreMenuListView.SelectedItem = entry;
            SetFocusRegion(FocusRegion.CoreMenu);
            OpenCoreMenuEntry();
        }
    }

    /// <summary>選択中のメニュー項目の全画面へ進む(追加/削除 → CoreList、詳細設定 → CoreOptionsCoreList)。</summary>
    private void OpenCoreMenuEntry()
    {
        if (CoreMenuListView.SelectedItem is not SettingsMenuEntryItem entry) return;

        switch (entry.Key)
        {
            case CoreMenuInstallKey:
                SetFocusRegion(FocusRegion.CoreList);
                break;

            case CoreMenuDetailKey:
                RefreshInstalledCores();
                if (InstalledCores.Count == 0)
                {
                    _ = InfoAsync(
                        "コアモジュール詳細設定",
                        "設定できる項目を持つインストール済みのコアがありません。先に「コアモジュール追加/削除」でコアをインストールしてください。");
                    return;
                }
                SetFocusRegion(FocusRegion.CoreOptionsCoreList);
                break;
        }
    }

    /// <summary>設定画面に出せる項目(Hideでないオプション)を1つ以上持つコアか。</summary>
    private static bool HasVisibleOptions(LibretroCoreListItem core) => core.Core.Options.Any(o => !o.Hide);

    /// <summary>
    /// インストール済みで、設定できる項目を持つコアだけをInstalledCoresへ作り直す(詳細設定画面へ入るたびに呼ぶ)。
    /// 前回選んでいたコアがまだインストール済みならそれを選び直し、無ければ先頭を選ぶ。
    /// </summary>
    private void RefreshInstalledCores()
    {
        LibretroCoreListItem? previous = CoreOptionsCoreListView.SelectedItem as LibretroCoreListItem;

        InstalledCores.Clear();
        foreach (LibretroCoreListItem core in Cores.Where(c => c.Installed && HasVisibleOptions(c)))
        {
            InstalledCores.Add(core);
        }

        int index = previous != null ? InstalledCores.IndexOf(previous) : -1;
        CoreOptionsCoreListView.SelectedIndex = InstalledCores.Count == 0 ? -1 : Math.Max(index, 0);
        RefreshRowFocusFlags();
    }

    /// <summary>
    /// 指定コアの現在の状態(Installed)に応じてインストール/アンインストールのどちらかを実行し、
    /// 成功したらLibretroCoreListItem.Installedへ結果を反映する。処理中に多重クリックされても
    /// 二重実行しないよう、IsBusy中は何もしない。
    /// </summary>
    private async Task ExecuteCoreActionAsync(LibretroCoreListItem item)
    {
        if (item.IsBusy)
        {
            return;
        }

        item.IsBusy = true;
        try
        {
            bool success = item.Installed
                ? await LibretroCoreCatalogService.UninstallAsync(item.Core)
                : await LibretroCoreCatalogService.DownloadAndInstallAsync(item.Core);

            if (success)
            {
                item.Installed = item.Core.Installed;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsPage] コア操作に失敗しました({item.Core.CoreName}): {ex.Message}");
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    // ------------------------------------------------------------------
    // コア毎の設定パネル
    // ------------------------------------------------------------------

    /// <summary>
    /// 「コア毎の設定」パネルの対象コア一覧で選択が変わったときに呼ばれる。選ばれたコアの
    /// Options(cores/libretro.jsonの"options")を読み、SelectedCoreOptionsへ反映する。
    /// </summary>
    /// <summary>
    /// 「コア管理」の一覧で選択が変わったとき(ゲームパッドの十字キー、マウスクリックの
    /// どちらでも)にIsFocusedを追従させる。ゲームパッド経由の場合はMoveWithinRegionからも
    /// RefreshRowFocusFlagsが呼ばれるため二重に更新されることがあるが、実害は無い。
    /// </summary>
    private void CoreListView_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshRowFocusFlags();

    /// <summary>
    /// 「コア毎の設定」のオプション一覧で選択が変わったときにIsFocusedを追従させる
    /// (CoreListView_SelectionChangedと同じ考え方)。
    /// </summary>
    private void CoreOptionsListView_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshRowFocusFlags();

    private void CoreOptionsCoreListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CoreOptionsCoreListView.SelectedItem is LibretroCoreListItem item)
        {
            ShowCoreOptions(item);
        }
        else
        {
            ShowNoCoreSelectedForOptions();
        }

        // マウスクリックで選択が変わった場合もIsFocusedを追従させる(ゲームパッドの
        // 十字キー経由はMoveWithinRegionが呼ぶが、ここは入力方法によらず呼ばれるため
        // 二重に更新されることがあっても実害は無い)。
        RefreshRowFocusFlags();
    }

    /// <summary>
    /// 指定コアのオプション一覧を組み立ててSelectedCoreOptionsへ反映し、パネルの表示状態を
    /// 「一覧表示」または「設定可能な項目がありません」のどちらかへ切り替える。
    /// 各項目の初期選択値は、保存済みの値(LibretroCoreOptionInfo.Value)があればそれ、
    /// 無ければDefaultを使う。
    /// </summary>
    private void ShowCoreOptions(LibretroCoreListItem item)
    {
        SelectedCoreOptions.Clear();

        // 標準項目(どのコアにもある: 画質・操作説明表示)を先頭に並べる。
        foreach (LibretroCoreOptionListItem builtin in BuildBuiltinCoreRows(item.Core))
        {
            SelectedCoreOptions.Add(builtin);
        }

        foreach (LibretroCoreOptionInfo option in item.Core.Options)
        {
            // Hide=trueの項目は、画面上で選んで切り替える対象ではない(常に固定値として
            // コアへ渡すだけの)パラメータなので、この一覧には出さない
            // (LibretroCoreOptionInfo.Hideのドキュメントコメント参照)。
            if (option.Hide)
            {
                continue;
            }

            SelectedCoreOptions.Add(new LibretroCoreOptionListItem
            {
                Core = item.Core,
                Option = option,
                SelectedValue = string.IsNullOrEmpty(option.Value) ? option.Default : option.Value!,
            });
        }

        // 末尾に「初期値に戻す」(このコアのコア詳細設定を、すべて初期値へ戻す)。
        SelectedCoreOptions.Add(new LibretroCoreOptionListItem
        {
            Core = item.Core,
            Option = new LibretroCoreOptionInfo { Key = BuiltinResetKey, Label = "初期値に戻す" },
            SelectedValue = string.Empty,
        });

        bool hasOptions = SelectedCoreOptions.Count > 0;
        NoOptionsPanel.Visibility = hasOptions ? Visibility.Collapsed : Visibility.Visible;
        CoreOptionsListView.Visibility = hasOptions ? Visibility.Visible : Visibility.Collapsed;
        CoreOptionsListView.SelectedIndex = hasOptions ? 0 : -1;
    }

    // ------------------------------------------------------------------
    // 標準項目(どのコアにも入っている設定): 画質(2Dコアのみ)・操作説明表示
    // ------------------------------------------------------------------
    // 値は CoreSettings(config\<コア>.json)に保存する。ゲーム中の中断メニューの変更は、
    // ここで決めた値(標準値)との差分だけがそのゲームに保存される(GamePlayPage.CyclePictureQuality等)。
    // アスペクト比はゲームごとに中断メニューで決めるため、ここには入れない。

    private const string BuiltinKeyPrefix = "@";
    private const string BuiltinQualityKey = "@quality";
    private const string BuiltinResetKey = "@reset";
    private const string BuiltinGuideKey = "@guide";

    private static List<LibretroCoreOptionListItem> BuildBuiltinCoreRows(LibretroCoreInfo core)
    {
        var rows = new List<LibretroCoreOptionListItem>();
        CoreSettings settings = CoreSettingsStore.Load(core.CoreName);

        // 画質: 2D(ソフトウェアレンダリング)のコアだけ。
        if (!LibretroCoreTraits.IsHardwareRendered(core.CoreName))
        {
            var quality = new LibretroCoreOptionInfo { Key = BuiltinQualityKey, Label = "画質", Default = nameof(PictureQualityMode.None) };
            quality.Choices.Add(new LibretroCoreOptionChoice { Value = nameof(PictureQualityMode.None), Label = "なし" });
            quality.Choices.Add(new LibretroCoreOptionChoice { Value = nameof(PictureQualityMode.HighQuality3x), Label = "High Quality 3× scaler" });
            quality.Choices.Add(new LibretroCoreOptionChoice { Value = nameof(PictureQualityMode.Hq2x), Label = "HQ2x" });
            rows.Add(new LibretroCoreOptionListItem { Core = core, Option = quality, SelectedValue = settings.PictureQuality.ToString() });
        }

        // 操作説明表示: アプリ設定に従う / 表示 / 非表示
        var guide = new LibretroCoreOptionInfo { Key = BuiltinGuideKey, Label = "操作説明表示", Default = CoreSettings.ControlGuideFollow };
        guide.Choices.Add(new LibretroCoreOptionChoice { Value = CoreSettings.ControlGuideFollow, Label = "アプリ設定に従う" });
        guide.Choices.Add(new LibretroCoreOptionChoice { Value = CoreSettings.ControlGuideOn, Label = "表示" });
        guide.Choices.Add(new LibretroCoreOptionChoice { Value = CoreSettings.ControlGuideOff, Label = "非表示" });
        string guideValue = settings.ControlGuide is CoreSettings.ControlGuideOn or CoreSettings.ControlGuideOff
            ? settings.ControlGuide
            : CoreSettings.ControlGuideFollow;
        rows.Add(new LibretroCoreOptionListItem { Core = core, Option = guide, SelectedValue = guideValue });

        return rows;
    }

    /// <summary>標準項目の変更を、そのコアの設定ファイル(CoreSettings)へ保存する。</summary>
    private static void PersistBuiltinCoreSetting(LibretroCoreOptionListItem item)
    {
        CoreSettings settings = CoreSettingsStore.Load(item.Core.CoreName);
        switch (item.Option.Key)
        {
            case BuiltinQualityKey:
                if (Enum.TryParse(item.SelectedValue, out PictureQualityMode quality)) settings.PictureQuality = quality;
                break;
            case BuiltinGuideKey:
                settings.ControlGuide = item.SelectedValue;
                break;
            default:
                return;
        }
        CoreSettingsStore.Save(item.Core.CoreName, settings);
    }

    /// <summary>
    /// 「初期値に戻す」: このコアの標準項目(画質・操作説明表示)と、コアオプション(非表示のものを除く)を初期値へ戻す。
    /// ゲームごとの設定(中断メニューで変えたもの)は変えない。アスペクト比も、ゲームごとの運用なのでそのまま。
    /// 非表示(hide)のコアオプションは、開発側が固定している値のため触らない。
    /// </summary>
    private async Task ConfirmAndResetCoreAsync(LibretroCoreInfo core)
    {
        if (ChoiceOverlay.IsOpen) return;

        GamepadPickerItem? picked = await ChoiceOverlay.ShowAsync(
            "このコアの設定を初期値に戻す",
            new[]
            {
                new GamepadPickerItem { Label = "初期値に戻す", Tag = "reset" },
                new GamepadPickerItem { Label = "キャンセル", Tag = "cancel" },
            });
        if (picked?.Tag as string != "reset") return;

        CoreSettings settings = CoreSettingsStore.Load(core.CoreName);
        settings.PictureQuality = PictureQualityMode.None;
        settings.ControlGuide = CoreSettings.ControlGuideFollow;
        CoreSettingsStore.Save(core.CoreName, settings);

        foreach (LibretroCoreOptionInfo option in core.Options.ToList())
        {
            if (option.Hide) continue;
            if (string.IsNullOrEmpty(option.Value) || string.Equals(option.Value, option.Default, StringComparison.Ordinal)) continue;
            await LibretroCoreCatalogService.SetOptionValueAsync(core, option.Key, option.Default);
        }

        // 一覧を作り直して、画面の値を初期値に揃える(カーソルは「初期値に戻す」のまま)。
        if (CoreOptionsCoreListView.SelectedItem is LibretroCoreListItem current && ReferenceEquals(current.Core, core))
        {
            ShowCoreOptions(current);
            CoreOptionsListView.SelectedIndex = SelectedCoreOptions.Count - 1;
            RefreshRowFocusFlags();
        }
    }

    /// <summary>「コア毎の設定」パネルを、まだ対象コアが選ばれていない初期状態の表示に戻す。</summary>
    private void ShowNoCoreSelectedForOptions()
    {
        SelectedCoreOptions.Clear();
        NoOptionsPanel.Visibility = Visibility.Collapsed;
        CoreOptionsListView.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 変更後の値(LibretroCoreOptionListItem.SelectedValue。x:Bind TwoWayで既に反映済み)を
    /// cores/libretro.jsonへ保存する。
    /// </summary>
    private async Task PersistOptionValueAsync(LibretroCoreOptionListItem item)
    {
        if (string.IsNullOrEmpty(item.SelectedValue))
        {
            return;
        }

        // 標準項目は cores/libretro.json ではなく、コアごとの設定ファイル(config\<コア>.json)へ保存する。
        if (item.Option.Key.StartsWith(BuiltinKeyPrefix, StringComparison.Ordinal))
        {
            PersistBuiltinCoreSetting(item);
            return;
        }

        try
        {
            bool success = await LibretroCoreCatalogService.SetOptionValueAsync(
                item.Core, item.Option.Key, item.SelectedValue);

            if (!success)
            {
                Debug.WriteLine(
                    $"[SettingsPage] コアオプションの保存に失敗しました({item.Core.CoreName}.{item.Option.Key})。");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[SettingsPage] コアオプションの保存中に例外が発生しました({item.Core.CoreName}.{item.Option.Key}): {ex.Message}");
        }
    }

    /// <summary>
    /// 選択中のオプション行の値を、ゲームパッドの十字キー左右で送る
    /// (ControllerSettingsPage.CycleMappingValueと同じ考え方)。
    /// </summary>
    private void CycleOptionValue(LibretroCoreOptionListItem row, int delta)
    {
        if (row.Choices.Count == 0)
        {
            return;
        }

        int currentIndex = row.Choices.FindIndex(c => c.Value == row.SelectedValue);
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        int newIndex = GamepadListNavigator.Move(currentIndex, delta, row.Choices.Count, wrap: true);
        row.SelectedValue = row.Choices[newIndex].Value;
        _ = PersistOptionValueAsync(row);
    }

    private void CoreOptionsCoreRow_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LibretroCoreListItem item })
        {
            CoreOptionsCoreListView.SelectedItem = item;
            SetFocusRegion(FocusRegion.CoreOptionsCoreList);
            EnterNextRegion();
        }
    }

    private void CoreOptionRow_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LibretroCoreOptionListItem row })
        {
            CoreOptionsListView.SelectedItem = row;
            SetFocusRegion(FocusRegion.CoreOptionsList);
            _ = ActivateCoreOptionAsync(row);
        }
    }

    /// <summary>
    /// オプション行の決定(Aボタン/クリック)。選択肢が3つ以上なら全画面の選択画面
    /// (ChoiceOverlay。現在値にチェック)で選ばせ、2つ(有効/無効など)なら押すたびに切り替える。
    /// 十字キー左右での値送り(CycleOptionValue)は従来どおり使える。
    /// </summary>
    private async Task ActivateCoreOptionAsync(LibretroCoreOptionListItem row)
    {
        if (row.Option.Key == BuiltinResetKey)
        {
            await ConfirmAndResetCoreAsync(row.Core);
            return;
        }

        List<CycleChoice> choices = row.Choices;
        if (choices.Count == 0) return;

        if (choices.Count <= 2)
        {
            CycleOptionValue(row, 1);
            return;
        }

        if (ChoiceOverlay.IsOpen) return;

        GamepadPickerItem? picked = await ChoiceOverlay.ShowAsync(
            row.Label,
            choices.Select(c => new GamepadPickerItem
            {
                Label = c.Label,
                Tag = c.Value,
                IsChecked = c.Value == row.SelectedValue,
            }));

        if (picked?.Tag is string value && value != row.SelectedValue)
        {
            row.SelectedValue = value;
            await PersistOptionValueAsync(row);
        }
    }

    // ------------------------------------------------------------------
    // ゲームフォルダ管理パネル
    // ------------------------------------------------------------------

    /// <summary>
    /// 登録済みのROMフォルダ一覧(RomFolderRepository)を読み込み、コンソール名
    /// (Consoles.json)を付与してFoldersへ反映する。
    /// </summary>
    private async Task LoadFoldersAsync()
    {
        GameFolderManagementHeader.IsLoading = true;
        try
        {
            IReadOnlyList<RomFolder> folders;
            try
            {
                folders = await GameLibraryFacade.GetAllFoldersAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SettingsPage] フォルダ一覧の読み込みに失敗しました: {ex.Message}");
                folders = Array.Empty<RomFolder>();
            }

            Dictionary<string, string> consoleNames = SampleData.GetConsoles()
                .ToDictionary(c => c.Id, c => c.Name, StringComparer.OrdinalIgnoreCase);

            List<RomFolderListItem> items = folders
                .Select(folder => new RomFolderListItem
                {
                    Folder = folder,
                    ConsoleDisplayName = consoleNames.TryGetValue(folder.ConsoleId, out string? name)
                        ? name
                        : folder.ConsoleId,
                })
                .OrderBy(item => item.ConsoleDisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.DisplayPath, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            Folders.Clear();
            foreach (RomFolderListItem item in items)
            {
                Folders.Add(item);
            }

            GameFolderManagementHeader.CountText = $"({items.Count}件)";
            FolderListView.SelectedIndex = Folders.Count > 0 ? 0 : -1;
            // 削除で0件になった場合は、空の全画面に取り残されないようメニューへ戻す。
            if (Folders.Count == 0 && _focusRegion == FocusRegion.FolderList)
            {
                SetFocusRegion(FocusRegion.FolderMenu);
            }

            // Foldersの中身自体を作り直したため、以前のIsFocusedは新しいインスタンスには
            // 引き継がれない。現在アクティブな領域に合わせて改めて設定し直す。
            RefreshRowFocusFlags();
        }
        finally
        {
            GameFolderManagementHeader.IsLoading = false;
        }
    }

    private void FolderListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 選択が変わったら(マウスクリック等)、枠の表示を合わせ直す。
        RefreshRowFocusFlags();
    }

    private void FolderMenuListView_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshRowFocusFlags();

    private void FolderMenuEntry_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SettingsMenuEntryItem entry })
        {
            FolderMenuListView.SelectedItem = entry;
            SetFocusRegion(FocusRegion.FolderMenu);
            OpenFolderMenuEntry();
        }
    }

    /// <summary>選択中のメニュー項目を開く(追加 → フォルダ選択から登録、確認/削除 → 全画面の一覧)。</summary>
    private void OpenFolderMenuEntry()
    {
        if (FolderMenuListView.SelectedItem is not SettingsMenuEntryItem entry) return;

        switch (entry.Key)
        {
            case FolderMenuAddKey:
                _ = AddFolderAsync();
                break;

            case FolderMenuRefreshKey:
                _ = RefreshFoldersAsync();
                break;

            case FolderMenuListKey:
                if (Folders.Count == 0)
                {
                    _ = InfoAsync(
                        "ゲームフォルダの確認/削除",
                        "登録されているゲームフォルダがありません。先に「ゲームフォルダの追加」で登録してください。");
                    return;
                }
                SetFocusRegion(FocusRegion.FolderList);
                break;
        }
    }

    /// <summary>
    /// 「閉じる」だけのお知らせ。他の確認ダイアログと同じ見た目(ConfirmOverlayのAlertAsync)で出す。
    /// タイトルと本文は、空行をはさんで1つのメッセージにする。
    /// </summary>
    private Task InfoAsync(string title, string message)
        => ConfirmOverlay.AlertAsync(string.IsNullOrWhiteSpace(message) ? title : title + "\n\n" + message);

    private bool _isRefreshingFolders;

    /// <summary>
    /// 「ゲームフォルダの更新」。登録済みの全フォルダを順に再スキャンする
    /// (RomFolderScanService.RefreshFolderAsync: 新規ROMの追加・名前変更の引き継ぎ・消えたROMの登録削除)。
    /// スキャン前に全フォルダの存在を確認し、見つからないフォルダがあれば先に確認ダイアログを出す。
    /// 「しない」を選ぶと、見つからないフォルダは(取り外し中かもしれないので)更新対象から外す。
    /// 「する」を選ぶと、そのフォルダの登録をゲームごとデータベースから削除する(ROM本体は消えない)。
    /// </summary>
    private async Task RefreshFoldersAsync()
    {
        if (_isRefreshingFolders) return;

        await LoadFoldersAsync();
        if (Folders.Count == 0) return;

        _isRefreshingFolders = true;
        SettingsMenuEntryItem? entry = FolderMenuEntries.FirstOrDefault(e => e.Key == FolderMenuRefreshKey);
        string? originalTitle = entry?.Title;
        int added = 0, renamed = 0, removed = 0;
        var failed = new List<string>();

        try
        {
            List<RomFolderListItem> items = Folders.ToList();
            List<RomFolderListItem> missing = items.Where(i => !Directory.Exists(i.Folder.Path)).ToList();
            bool force = false;
            if (missing.Count > 0)
            {
                string names = string.Join("\n", missing.Take(5).Select(i => i.Folder.Path));
                if (missing.Count > 5) names += $"\n…ほか{missing.Count - 5}件";
                force = await ConfirmOverlay.ShowAsync(
                    $"ドライブまたはフォルダが見つからないゲームフォルダが{missing.Count}件あります。\n{names}\n\n" +
                    "取り外し中の場合は「対象外にして更新」を選んでください。\n" +
                    "すでに無いものなら「強制的に更新」で、登録したゲームを一覧から削除します(ROM本体は消えません)。",
                    "強制的に更新", "対象外にして更新");
            }

            if (entry != null) entry.Title = "フォルダを更新中…";
            GameFolderManagementHeader.IsLoading = true;

            foreach (RomFolderListItem item in items)
            {
                bool isMissing = missing.Contains(item);
                if (isMissing && !force) continue;

                RomFolderRefreshResult result = await RomFolderScanService.RefreshFolderAsync(item.Folder.No, force);
                Debug.WriteLine($"[SettingsPage] フォルダ更新: {result.Status} 追加={result.AddedFileCount} 名前変更={result.RenamedFileCount} 削除={result.RemovedFileCount} {result.Message}");
                added += result.AddedFileCount;
                renamed += result.RenamedFileCount;
                removed += result.RemovedFileCount;
                if (result.Status != RomFolderRefreshStatus.Refreshed) failed.Add($"{item.Folder.Path}: {result.Message}");
            }
        }
        finally
        {
            if (entry != null && originalTitle != null) entry.Title = originalTitle;
            GameFolderManagementHeader.IsLoading = false;
            _isRefreshingFolders = false;
        }

        await LoadFoldersAsync();

        if (failed.Count > 0)
        {
            await InfoAsync("更新できなかったフォルダがあります", string.Join("\n", failed.Take(5)));
        }
        else if (removed > 0 || renamed > 0)
        {
            await InfoAsync("フォルダを更新しました",
                $"追加 {added}件 / 名前変更 {renamed}件 / 削除 {removed}件");
        }
    }

    private void FolderRow_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RomFolderListItem item })
        {
            FolderListView.SelectedItem = item;
            SetFocusRegion(FocusRegion.FolderList);
            _ = ActivateFolderRowAsync(item);
        }
    }

    /// <summary>
    /// 「＋ 新規追加」。①フォルダ選択 → ②インストール済みコアに対応するコンソールを
    /// 選択(GamepadListPickerOverlay) → ③RomFolderScanService.RegisterFolderAsyncで
    /// 新規登録(スキャン・画像取得含む)、という流れをこの1つのメソッドで行う。
    /// SectionHeaderの「＋ 新規追加」ボタン、および一覧先頭の「＋ 新規追加」カードの
    /// どちらからも呼ばれるため、多重起動しないよう_isAddingFolderで簡単に排他する。
    ///
    /// ①のフォルダ選択はFolderPickerOverlay、②③はGamepadListPickerOverlay/
    /// GamepadMessageOverlayを使い、ゲームパッドだけで完結できる。
    /// </summary>
    /// <summary>直近に選んだフォルダ(次回のフォルダ選択をそこから開始する)。</summary>
    private string? _lastPickedFolderPath;

    private async Task AddFolderAsync()
    {
        if (_isAddingFolder)
        {
            return;
        }

        _isAddingFolder = true;
        try
        {
            // ゲームパッドだけで操作できるフォルダ選択(OSのFolderPickerの代替)。
            string? pickedPath = await FolderChooser.ShowAsync("ゲームフォルダを選択", _lastPickedFolderPath);
            if (string.IsNullOrEmpty(pickedPath))
            {
                // キャンセルされた。
                return;
            }
            _lastPickedFolderPath = pickedPath;
            var folder = new { Path = pickedPath };

            List<ConsoleInfo> installableConsoles = await GetInstallableConsolesAsync();
            if (installableConsoles.Count == 0)
            {
                await InfoAsync(
                    "コアがインストールされていません",
                    "対応するコンソールがありません。「コア管理」でコアを確認してください。");
                return;
            }

            GamepadPickerItem? picked = await ListPickerOverlay.ShowAsync(
                "対応するコンソールを選択",
                installableConsoles.Select(c => new GamepadPickerItem { Label = c.Name, Tag = c }));

            if (picked?.Tag is not ConsoleInfo selectedConsole)
            {
                // キャンセルされた。
                return;
            }

            // 検索/登録中は、メニュー項目の文言を変えて処理中であることを示す(結果のダイアログは出さない)。
            SettingsMenuEntryItem? addEntry = FolderMenuEntries.FirstOrDefault(e => e.Key == FolderMenuAddKey);
            string? addEntryTitle = addEntry?.Title;
            if (addEntry != null) addEntry.Title = "フォルダを検索中…";
            GameFolderManagementHeader.IsLoading = true;
            try
            {
                // 既定のランチャーのコアが未導入なら、ここで自動的に入れる(失敗しても登録は続ける)。
                await EnsureDefaultCoreInstalledAsync(selectedConsole, addEntry);

                if (addEntry != null) addEntry.Title = "フォルダを検索中…";
                RomFolderRegistrationResult result =
                    await RomFolderScanService.RegisterFolderAsync(folder.Path, selectedConsole.Id);
                if (result.Status != RomFolderRegistrationStatus.Registered)
                {
                    Debug.WriteLine($"[SettingsPage] フォルダ登録: {result.Status} {result.Message}");
                }
            }
            finally
            {
                if (addEntry != null && addEntryTitle != null) addEntry.Title = addEntryTitle;
                GameFolderManagementHeader.IsLoading = false;
            }

            await LoadFoldersAsync();
        }
        finally
        {
            _isAddingFolder = false;
        }
    }

    /// <summary>
    /// 現在インストール済みのlibretroコアに対応するコンソールだけを返す(新規追加時、
    /// コアが無いコンソール向けにフォルダを登録しても起動できないため、選択肢から除外する)。
    /// </summary>
    private static async Task<List<ConsoleInfo>> GetInstallableConsolesAsync()
    {
        List<LibretroCoreInfo> cores;
        try
        {
            cores = await LibretroCoreCatalogService.RefreshInstalledStatusAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsPage] コア一覧の読み込みに失敗しました: {ex.Message}");
            cores = new List<LibretroCoreInfo>();
        }

        HashSet<string> installedConsoleIds = cores
            .Where(core => core.Installed)
            .Select(core => core.ConsoleId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 「ランチャー管理」で追加したランチャー(外部コマンド、または他コンソール用コアを流用したもの)
        // が存在するコンソールも、起動できるので選択肢に含める。
        HashSet<string> installedDlls = cores
            .Where(core => core.Installed)
            .Select(core => core.CoreName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (LauncherInfo launcher in SampleData.GetLaunchers())
        {
            if (launcher.IsExternal || installedDlls.Contains(System.IO.Path.GetFileName(launcher.CorePath ?? string.Empty)))
            {
                installedConsoleIds.Add(launcher.Id);
            }
        }

        // 既定のランチャーのコアがカタログにあるコンソールは、未導入でも選べる(登録時に自動で入れる)。
        return SampleData.GetConsoles()
            .Where(console => installedConsoleIds.Contains(console.Id) || FindDefaultCore(console, cores) is not null)
            .ToList();
    }

    /// <summary>コンソールの既定ランチャー(Consoles.jsonのlauncherName)が使うコアのカタログ項目。外部ランチャーや、カタログに無いコアならnull。</summary>
    private static LibretroCoreInfo? FindDefaultCore(ConsoleInfo console, List<LibretroCoreInfo> cores)
    {
        LauncherInfo? launcher = SampleData.ResolveLauncher(console.Id, console.LauncherName);
        if (launcher is null || launcher.IsExternal) return null;

        string dll = System.IO.Path.GetFileName(launcher.CorePath ?? string.Empty);
        if (string.IsNullOrEmpty(dll)) return null;

        return cores.FirstOrDefault(c => string.Equals(c.CoreName, dll, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 新規登録するコンソールの既定コアが未導入なら、ダウンロードして入れる。
    /// 外部ランチャー(Cemu・RPCS3など)は対象外。失敗してもフォルダの登録は続けられるよう、例外は投げずログに残す。
    /// </summary>
    private static async Task EnsureDefaultCoreInstalledAsync(ConsoleInfo console, SettingsMenuEntryItem? statusEntry)
    {
        try
        {
            List<LibretroCoreInfo> cores = await LibretroCoreCatalogService.RefreshInstalledStatusAsync();
            LibretroCoreInfo? core = FindDefaultCore(console, cores);
            if (core is null || core.Installed) return;

            if (statusEntry != null) statusEntry.Title = $"コアを導入中… ({core.DisplayName})";
            AppLog.Info("Core", $"{console.Id} の既定コアを自動で導入します: {core.CoreName}");

            bool ok = await LibretroCoreCatalogService.DownloadAndInstallAsync(core);
            if (ok)
            {
                AppLog.Info("Core", $"自動導入に成功しました: {core.CoreName}");
            }
            else
            {
                AppLog.Warn("Core", $"自動導入に失敗しました(フォルダの登録は続けます。コア管理から入れ直せます): {core.CoreName}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Core", $"コアの自動導入中にエラーが起きました: {console.Id}", ex);
        }
    }

    /// <summary>
    /// フォルダ一覧の選択中行の登録を解除する。確認ダイアログ(ConfirmOverlay)で
    /// 「〇〇を登録から削除してもよろしいでしょうか?」を出し、「削除する」が選ばれた場合だけ
    /// 解除する(実際のファイルは削除されない)。
    /// </summary>
    private async Task ActivateFolderRowAsync(RomFolderListItem row)
    {
        if (row.IsBusy || ConfirmOverlay.IsOpen)
        {
            return;
        }

        bool confirmed = await ConfirmOverlay.ShowAsync(
            $"{row.DisplayPath}({row.ConsoleDisplayName})を登録から削除してもよろしいでしょうか？\n※フォルダ内のファイルは削除されません。",
            "削除する");
        if (!confirmed)
        {
            return;
        }

        row.IsBusy = true;
        try
        {
            RomFolderRemovalResult result = await RomFolderScanService.RemoveFolderAsync(row.Folder.No);
            if (result.Status != RomFolderRemovalStatus.Removed)
            {
                await InfoAsync("削除に失敗しました", result.Message ?? "不明なエラーです。");
            }
        }
        finally
        {
            row.IsBusy = false;
        }

        await LoadFoldersAsync();
    }
}
