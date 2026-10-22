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

// SettingsPage の一部(partial class)。ランチャー管理・コレクション管理・システム設定(システム項目の編集)。
// 動作は変えず、巨大だったSettingsPage.xaml.csを役割ごとに分けただけ。
public sealed partial class SettingsPage
{
    // ------------------------------------------------------------------
    // ランチャー管理
    // ------------------------------------------------------------------

    /// <summary>
    /// Launchers.jsonを読み直して一覧(Launchers)へ反映する。
    /// 選択位置はなるべく保つ。ファイルは毎回読み直される(SampleData.GetLaunchers)ため、
    /// 編集オーバーレイで保存した直後にも呼ぶ。
    /// </summary>
    private void LoadLaunchers()
    {
        List<ConsoleInfo> consoles = SampleData.GetConsoles();
        Dictionary<string, string> consoleNames = consoles
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> inUse = consoles
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().LauncherName, StringComparer.OrdinalIgnoreCase);

        List<LauncherListItem> items = SampleData.GetLaunchers()
            .Select(launcher => new LauncherListItem
            {
                Launcher = launcher,
                ConsoleDisplayName = consoleNames.TryGetValue(launcher.Id, out string? name) ? name : launcher.Id,
                IsInUse = inUse.TryGetValue(launcher.Id, out string? used) && used == launcher.Name,
            })
            .OrderBy(item => item.ConsoleDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        int previousIndex = LauncherListView.SelectedIndex;

        Launchers.Clear();
        foreach (LauncherListItem item in items)
        {
            Launchers.Add(item);
        }

        LauncherManagementHeader.CountText = $"({items.Count}件)";
        LauncherListView.SelectedIndex = Launchers.Count == 0 ? -1 : Math.Clamp(previousIndex, 0, Launchers.Count - 1);

        // 削除で0件になった場合は、空の全画面に取り残されないようメニューへ戻す。
        if (Launchers.Count == 0 && _focusRegion == FocusRegion.LauncherList)
        {
            SetFocusRegion(FocusRegion.LauncherMenu);
        }

        RefreshRowFocusFlags();
    }

    private void LauncherListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 選択行が変わったら(マウスクリック等)、枠の表示を合わせ直す。
        RefreshRowFocusFlags();
    }

    private void LauncherMenuListView_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshRowFocusFlags();

    // ---- コレクション管理(追加/編集/削除) ----

    private void ConsoleMenuListView_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshRowFocusFlags();

    private void ConsoleMenuEntry_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SettingsMenuEntryItem entry })
        {
            ConsoleMenuListView.SelectedItem = entry;
            SetFocusRegion(FocusRegion.ConsoleMenu);
            OpenConsoleMenuEntry();
        }
    }

    private void OpenConsoleMenuEntry()
    {
        if (ConsoleMenuListView.SelectedIndex < 0 && ConsoleMenuEntries.Count > 0)
        {
            ConsoleMenuListView.SelectedIndex = 0;
        }
        int idx = ConsoleMenuListView.SelectedIndex;
        if (idx < 0 || idx >= ConsoleMenuEntries.Count) return;
        if (ConsoleMenuEntries[idx].Key == ConsoleMenuEditKey)
        {
            _ = EditConsoleAsync(ConsoleMenuEntries[idx].Title);
        }
        else if (ConsoleMenuEntries[idx].Key == ConsoleMenuCreateKey)
        {
            _ = EditLauncherAsync(null, newConsole: true);
        }
        else if (ConsoleMenuEntries[idx].Key == ConsoleMenuOrderKey)
        {
            _ = ReorderConsolesAsync(ConsoleMenuEntries[idx].Title);
        }
    }

    /// <summary>「コンソール編集」: 編集するコンソールを選び、既存の編集画面(ConsoleEditOverlay)を開く。</summary>
    private async Task EditConsoleAsync(string label)
    {
        if (ConsoleEditor.IsOpen || ReorderScreen.IsOpen || SystemOrderScreen.IsOpen || ChoiceOverlay.IsOpen) return;

        GamepadPickerItem? picked = await ChoiceOverlay.ShowAsync(
            label,
            SampleData.GetConsoles().Select(c => new GamepadPickerItem { Label = c.Name, Tag = c }));
        if (picked?.Tag is ConsoleInfo console) ConsoleEditor.Show(console);
    }

    // ---- システム設定(システム項目の編集) ----

    private void SystemMenuListView_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshRowFocusFlags();

    private void SystemMenuEntry_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AppSettingRowItem row })
        {
            SystemMenuListView.SelectedItem = row;
            SetFocusRegion(FocusRegion.SystemMenu);
            _ = ActivateAppSettingRowAsync(row);
        }
    }

    private void OpenSystemMenuEntry()
    {
        if (SystemMenuListView.SelectedIndex < 0 && SystemMenuEntries.Count > 0)
        {
            SystemMenuListView.SelectedIndex = 0;
        }
        int idx = SystemMenuListView.SelectedIndex;
        if (idx < 0 || idx >= SystemMenuEntries.Count) return;
        _ = ActivateAppSettingRowAsync(SystemMenuEntries[idx]);
    }

    /// <summary>Pluginsフォルダのプラグイン(読み込めないjsonは除く)。</summary>
    private static List<EmulatorLauncher.Plugins.SystemPluginDefinition> LoadSystemPlugins()
    {
        try
        {
            return EmulatorLauncher.Plugins.SystemPluginLoader.LoadAll(msg => AppLog.WarnOnce("plugin:" + msg, "Plugin", msg));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Plugin", "プラグインの読み込みに失敗しました", ex);
            return new List<EmulatorLauncher.Plugins.SystemPluginDefinition>();
        }
    }

    /// <summary>
    /// 「システム項目の編集」: 表示する項目を選んで適用する。内蔵の項目は、選ばなかったものを非表示として保存し、
    /// プラグインは、選んだものだけを有効として保存する(プラグインは最初は無効)。
    /// </summary>
    private async Task EditSystemItemsAsync(string title)
    {
        if (SystemItemsPicker.IsOpen || ReorderScreen.IsOpen || SystemOrderScreen.IsOpen || ChoiceOverlay.IsOpen) return;

        AppPreferences preferences = AppPreferencesStore.Load();
        var hidden = new HashSet<string>(preferences.HiddenSystemItems ?? new List<string>());
        var enabled = new HashSet<string>(preferences.EnabledSystemPlugins ?? new List<string>());
        var plugins = LoadSystemPlugins();

        var rows = SystemItemCatalog.All.Select(i => (Id: i.Id, Label: i.Label, Selected: !hidden.Contains(i.Id)))
            .Concat(plugins.Select(p => (Id: p.ItemId, Label: "プラグイン: " + p.Name, Selected: enabled.Contains(p.ItemId))));

        string[]? picked = await SystemItemsPicker.ShowAsync(
            title,
            rows,
            "システム画面に表示したい項目を、いくつでも選べます。選ばなかった項目は表示されません(その機器で使えない項目は、選んでいても表示されません)。"
            + "「プラグイン」は、選んだものだけが有効になります。",
            "表示する項目",
            "なし");
        if (picked is null) return;

        var shown = new HashSet<string>(picked);
        preferences = AppPreferencesStore.Load();
        preferences.HiddenSystemItems = SystemItemCatalog.All.Where(i => !shown.Contains(i.Id)).Select(i => i.Id).ToList();
        // 一覧に出ていないプラグイン(jsonを移動中など)の有効/無効は、そのまま残す。
        var known = new HashSet<string>(plugins.Select(p => p.ItemId));
        var keep = (preferences.EnabledSystemPlugins ?? new List<string>()).Where(id => !known.Contains(id));
        preferences.EnabledSystemPlugins = keep.Concat(plugins.Where(p => shown.Contains(p.ItemId)).Select(p => p.ItemId)).ToList();
        AppPreferencesStore.Save(preferences);
    }

    /// <summary>
    /// 「システム項目の並び替え」: 今システム画面に出る項目(非表示・無効のものは除く)を全画面の入れ替え画面で並び替え、
    /// 並び順を保存する。出ていない項目は、元の位置のまま。
    /// </summary>
    private async Task ReorderSystemItemsAsync(string label)
    {
        if (ReorderScreen.IsOpen || SystemOrderScreen.IsOpen || SystemItemsPicker.IsOpen || ChoiceOverlay.IsOpen) return;

        AppPreferences preferences = AppPreferencesStore.Load();
        var hidden = new HashSet<string>(preferences.HiddenSystemItems ?? new List<string>());
        var enabled = new HashSet<string>(preferences.EnabledSystemPlugins ?? new List<string>());
        var plugins = LoadSystemPlugins();

        var names = SystemItemCatalog.All.ToDictionary(i => i.Id, i => i.Label);
        foreach (var p in plugins) names[p.ItemId] = "プラグイン: " + p.Name;

        List<string> full = SystemItemCatalog.Ordered(preferences.SystemItemOrder, plugins);
        List<string> visible = full.Where(id => SystemItemCatalog.IsSpacer(id) || (SystemItemCatalog.IsPlugin(id) ? enabled.Contains(id) : !hidden.Contains(id))).ToList();
        if (visible.Count(id => !SystemItemCatalog.IsSpacer(id)) < 1)
        {
            await InfoAsync("並び替える項目がありません。", "システム画面に表示する項目があるときに並び替えられます。");
            return;
        }

        var defs = plugins.ToDictionary(p => p.ItemId);
        IEnumerable<SystemOrderEntry> entries = visible.Select(id =>
        {
            defs.TryGetValue(id, out var def);
            var d = SystemItemCatalog.Describe(id, def);
            return new SystemOrderEntry
            {
                Id = id,
                Label = SystemItemCatalog.IsSpacer(id) ? "空白" : (names.TryGetValue(id, out string? n) ? n : id),
                Kind = d.Kind,
                Span = d.Span,
                BaseHeight = d.Height,
            };
        });

        IReadOnlyList<string>? order = await SystemOrderScreen.ShowAsync(label, entries);
        if (order is null) return;

        // 表示している項目(空白を含む)の並びを反映する(出ていない項目は、直前の項目にくっついて動く)。
        List<string> merged = SystemItemCatalog.MergeOrder(full, new HashSet<string>(visible), order);

        preferences = AppPreferencesStore.Load();
        preferences.SystemItemOrder = merged;
        AppPreferencesStore.Save(preferences);
    }

    private void CollectionMenuListView_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshRowFocusFlags();

    private void CollectionMenuEntry_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SettingsMenuEntryItem entry })
        {
            CollectionMenuListView.SelectedItem = entry;
            SetFocusRegion(FocusRegion.CollectionMenu);
            OpenCollectionMenuEntry();
        }
    }

    private bool _isManagingCollection;

    private void OpenCollectionMenuEntry()
    {
        if (CollectionMenuListView.SelectedIndex < 0 && CollectionMenuEntries.Count > 0)
        {
            CollectionMenuListView.SelectedIndex = 0;
        }
        int idx = CollectionMenuListView.SelectedIndex;
        if (idx < 0 || idx >= CollectionMenuEntries.Count) return;
        _ = ManageCollectionAsync(CollectionMenuEntries[idx].Key);
    }

    /// <summary>追加=編集オーバーレイを新規で開く。編集/削除=コレクションを選んでから、編集オーバーレイ/確認ダイアログ。</summary>
    private async Task ManageCollectionAsync(string key)
    {
        if (_isManagingCollection || CollectionEditor.IsOpen) return;
        _isManagingCollection = true;
        try
        {
            if (key == CollectionMenuOrderKey)
            {
                await ReorderCollectionsAsync("コレクションの並び替え");
                return;
            }

            List<RomCollectionInfo> collections = await CollectionLibraryFacade.GetAllAsync();

            if (key == CollectionMenuAddKey)
            {
                CollectionEditor.Show(null, collections.Select(c => c.Name));
                return;
            }

            // 「お気に入り」は削除できない。編集(名前/アイコン)も、削除の行が出ないだけで開ける。
            List<RomCollectionInfo> targets = key == CollectionMenuDeleteKey
                ? collections.Where(c => c.Id != CollectionLibraryFacade.FavoriteCollectionId).ToList()
                : collections;
            string title = key == CollectionMenuEditKey ? "編集するコレクション" : "削除するコレクション";
            if (targets.Count == 0)
            {
                await InfoAsync(title, "対象のコレクションがありません。");
                return;
            }

            // 新しいコレクション選択画面(アイコン+名前+ゲーム数)。選んだあとは既存の編集画面/削除の確認へ。
            RomCollectionInfo? target = await CollectionSelector.ShowAsync(title, targets);
            if (target is null) return;

            if (key == CollectionMenuEditKey)
            {
                CollectionEditor.Show(target);
            }
            else if (await ConfirmOverlay.ShowAsync($"コレクション「{target.Name}」を削除しますか？", "削除する"))
            {
                await CollectionLibraryFacade.SoftDeleteAsync(target.Id);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsPage] コレクション管理に失敗しました: {ex.Message}");
            await InfoAsync("コレクションの操作に失敗しました", ex.Message);
        }
        finally
        {
            _isManagingCollection = false;
        }
    }

    private void LauncherMenuEntry_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SettingsMenuEntryItem entry })
        {
            LauncherMenuListView.SelectedItem = entry;
            SetFocusRegion(FocusRegion.LauncherMenu);
            OpenLauncherMenuEntry();
        }
    }

    /// <summary>選択中のメニュー項目を開く(追加 → 編集オーバーレイを新規追加で、編集/削除 → 全画面の一覧)。</summary>
    private void OpenLauncherMenuEntry()
    {
        if (LauncherMenuListView.SelectedItem is not SettingsMenuEntryItem entry) return;

        switch (entry.Key)
        {
            case LauncherMenuAddKey:
                _ = EditLauncherAsync(null);
                break;

            case LauncherMenuListKey:
                if (Launchers.Count == 0)
                {
                    _ = InfoAsync(
                        "ランチャーの編集/削除",
                        "登録されているランチャーがありません。先に「ランチャーの追加」で登録してください。");
                    return;
                }
                SetFocusRegion(FocusRegion.LauncherList);
                break;
        }
    }

    /// <summary>行そのものをクリック/タップしたら編集オーバーレイを開く(編集ボタンを廃止した代わり)。
    /// ゲームパッドは従来どおりAボタン(ActivateFocusRegion)で開く。</summary>
    private void LauncherRow_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LauncherListItem item })
        {
            LauncherListView.SelectedItem = item;
            SetFocusRegion(FocusRegion.LauncherList);
            _ = EditLauncherAsync(item);
        }
    }

    /// <summary>
    /// 編集オーバーレイを開く(rowがnullなら新規追加)。保存/削除で変更があれば
    /// 一覧を読み直す。
    /// </summary>
    private async Task EditLauncherAsync(LauncherListItem? row, bool newConsole = false)
    {
        if (_isEditingLauncher)
        {
            return;
        }

        _isEditingLauncher = true;
        try
        {
            bool changed = await LauncherEditor.ShowAsync(row?.Launcher, newConsole);
            if (changed)
            {
                LoadLaunchers();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsPage] ランチャー編集に失敗しました: {ex.Message}");
            await InfoAsync("ランチャーの編集に失敗しました", ex.Message);
        }
        finally
        {
            _isEditingLauncher = false;
        }
    }
}
