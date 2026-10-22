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

// SettingsPage の一部(partial class)。ゲームパッド入力・フォーカス領域の移動。
// 動作は変えず、巨大だったSettingsPage.xaml.csを役割ごとに分けただけ。
public sealed partial class SettingsPage
{
    // ------------------------------------------------------------------
    // ゲームパッド入力
    // ------------------------------------------------------------------
    // 左メニュー(Category)を「浅い」階層、各パネルの中身(CoreList/CoreOptionsCoreList/
    // CoreOptionsList/FolderList)を「深い」階層とみなし、十字キー右(またはAボタン)で
    // 1段深く入り、十字キー左(またはBボタン)で1段浅く戻る、という単純な階層移動にしてある
    // (RegionsByCategoryが選択中カテゴリごとの階層の並びを表す)。
    // Categoryにいる間はBボタンでこの画面自体から抜ける(前の画面へ戻る)。
    // 十字キー上下は、現在フォーカスしている領域内のListViewの選択を動かす(Category自身の
    // カテゴリ選択も含む)。「コア毎の設定」のオプション一覧(CoreOptionsList)だけは
    // 最も深い階層のため、そこでの十字キー左右は階層移動ではなく選択中オプションの値の
    // 変更に使う(戻る操作はBボタンのみ)。マウス操作(各行のボタンクリック・
    // CycleSelectorの矢印クリック)も引き続き使える。

    public void OnGamepadButton(GamepadButton button)
    {
        // オーバーレイ表示中は、入力をすべてオーバーレイへ渡す。
        if (ConfirmOverlay.IsOpen)
        {
            ConfirmOverlay.HandleGamepadInput(button);
            return;
        }
        if (ConsoleEditor.IsOpen)
        {
            ConsoleEditor.HandleGamepadInput(button);
            return;
        }
        if (LauncherEditor.IsOpen)
        {
            LauncherEditor.HandleGamepadInput(button);
            return;
        }
        if (CollectionEditor.IsOpen)
        {
            CollectionEditor.HandleGamepadInput(button);
            return;
        }
        if (CollectionSelector.IsOpen)
        {
            CollectionSelector.HandleGamepadInput(button);
            return;
        }
        if (FolderChooser.IsOpen)
        {
            FolderChooser.HandleGamepadInput(button);
            return;
        }
        if (ListPickerOverlay.IsOpen)
        {
            ListPickerOverlay.HandleGamepadInput(button);
            return;
        }
        if (SystemItemsPicker.IsOpen)
        {
            SystemItemsPicker.HandleGamepadInput(button);
            return;
        }
        if (ReorderScreen.IsOpen)
        {
            ReorderScreen.HandleGamepadInput(button);
            return;
        }
        if (SystemOrderScreen.IsOpen)
        {
            SystemOrderScreen.HandleGamepadInput(button);
            return;
        }
        if (ChoiceOverlay.IsOpen)
        {
            ChoiceOverlay.HandleGamepadInput(button);
            return;
        }
        if (MessageOverlay.IsOpen)
        {
            MessageOverlay.HandleGamepadInput(button);
            return;
        }

        // 左メニュー(Category)の下にある「前の画面に戻る」ボタンへの出入り。
        // 左メニューの最後の項目で下、先頭の項目で上を押すと、このボタンへ移る(いままでは反対側の端へ循環していた)。
        if (_focusRegion == FocusRegion.Category && Categories.Count > 0)
        {
            if (_backButtonFocused)
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
                        LeaveSettings();
                        break;
                }
                return;   // 左右は何もしない(右のパネルへは入らない)
            }

            if ((button == GamepadButton.DPadDown && CategoryListView.SelectedIndex >= Categories.Count - 1)
                || (button == GamepadButton.DPadUp && CategoryListView.SelectedIndex <= 0))
            {
                SetBackButtonFocus(true, CategoryListView.ContainerFromIndex(Math.Max(CategoryListView.SelectedIndex, 0)) as UIElement);
                return;
            }
        }

        switch (button)
        {
            case GamepadButton.B:
                if (_focusRegion == FocusRegion.Category)
                {
                    LeaveSettings();
                }
                else
                {
                    ExitToPreviousRegion();
                }
                break;

            case GamepadButton.DPadUp:
                MoveWithinRegion(-1);
                break;
            case GamepadButton.DPadDown:
                MoveWithinRegion(1);
                break;

            case GamepadButton.DPadLeft:
                if (_focusRegion == FocusRegion.CoreOptionsList)
                {
                    if (CoreOptionsListView.SelectedItem is LibretroCoreOptionListItem leftRow)
                    {
                        CycleOptionValue(leftRow, -1);
                    }
                }
                else
                {
                    ExitToPreviousRegion();
                }
                break;

            case GamepadButton.DPadRight:
                if (_focusRegion == FocusRegion.CoreOptionsList)
                {
                    if (CoreOptionsListView.SelectedItem is LibretroCoreOptionListItem rightRow)
                    {
                        CycleOptionValue(rightRow, 1);
                    }
                }
                else
                {
                    EnterNextRegion();
                }
                break;

            case GamepadButton.A:
                // Category(左メニュー)、およびCoreOptionsCoreList(コア毎の設定の対象コア
                // 一覧)では、十字キー右と同じ「1段深く入る」動作にする(コア管理/
                // ゲームフォルダ管理の1段目、コア毎の設定のオプション一覧の2段目、
                // どちらも右/決定のどちらでも入れるようにするため)。それ以外の領域では、
                // 選択中行そのものを実行する(コア管理のインストール切替、ゲームフォルダ
                // 管理の削除確認/新規追加)。
                if (_focusRegion == FocusRegion.Category || _focusRegion == FocusRegion.CoreMenu
                    || _focusRegion == FocusRegion.FolderMenu || _focusRegion == FocusRegion.LauncherMenu
                    || _focusRegion == FocusRegion.CollectionMenu || _focusRegion == FocusRegion.ConsoleMenu
                    || _focusRegion == FocusRegion.SystemMenu
                    || _focusRegion == FocusRegion.CoreOptionsCoreList)
                {
                    EnterNextRegion();
                }
                else
                {
                    ActivateFocusRegion();
                }
                break;
        }
    }

    public void OnGamepadButtonLongPress(GamepadButton button)
    {
        // 現時点でこの画面に長押し操作は無い。
    }

    /// <summary>選択中カテゴリで、十字キー左右(またはA/B)により深く/浅く移動できる
    /// 領域の並び(浅い→深いの順)。</summary>
    private FocusRegion[] CurrentRegions =>
        RegionsByCategory.TryGetValue((CategoryListView.SelectedItem as SettingsCategoryItem)?.Label ?? CoreManagementCategoryLabel, out FocusRegion[]? regions)
            ? regions
            : new[] { FocusRegion.Category };

    /// <summary>左メニューの下の「前の画面に戻る」ボタンにフォーカスがあるか(Category領域の中の状態)。</summary>
    private bool _backButtonFocused;

    private static readonly EmulatorLauncher.Converters.BoolToMenuHighlightConverter BackButtonFillConverter = new();

    /// <summary>
    /// 「前の画面に戻る」ボタンのフォーカス表示を切り替える(他の一覧と同じ、塗り+水色の枠)。
    /// 枠は、<paramref name="slideFrom"/>(直前に選んでいた行)の位置から、ボタンへ滑って来る。
    /// </summary>
    private void SetBackButtonFocus(bool focused, UIElement? slideFrom = null)
    {
        _backButtonFocused = focused;
        BackToPreviousFocusFrame.BorderBrush = SelectionHighlightBrush;
        BackToPreviousFocusFrame.Background = (Brush)BackButtonFillConverter.Convert(true, typeof(Brush), null!, string.Empty);
        BackToPreviousFocusFrame.Visibility = focused ? Visibility.Visible : Visibility.Collapsed;
        FocusSlide.SetIsFocused(BackToPreviousFocusFrame, focused);
        RefreshCategoryFlags();   // ボタンにある間は、左メニューの項目の水色の枠を外す
        if (focused && slideFrom != null)
        {
            BackToPreviousFocusFrame.UpdateLayout();
            HighlightSlide.SlideElement(BackToPreviousFocusFrame, slideFrom);
        }
    }

    /// <summary>左メニューの<paramref name="index"/>番目を選ぶ。枠は「前の画面に戻る」ボタンの位置から滑って来る。</summary>
    private void SelectCategoryFromBackButton(int index)
    {
        SetBackButtonFocus(false);
        SelectCategoryByIndex(index);
        CategoryListView.UpdateLayout();
        HighlightSlide.SlideRow(BackToPreviousHost, CategoryListView.ContainerFromIndex(index) as UIElement);
    }

    private void SelectCategoryByIndex(int index)
    {
        CategoryListView.SelectedIndex = index;
        CategoryListView.ScrollIntoView(Categories[index]);
        RefreshCategoryFlags();
    }

    private void SetFocusRegion(FocusRegion region)
    {
        _focusRegion = region;
        if (_backButtonFocused) SetBackButtonFocus(false);
        // コレクション管理のメニューは、XAMLのSelectedIndexが効かず未選択(-1)のままのことがある。
        // 未選択だとハイライトも決定も効かないため、入るときに先頭を選んでおく。
        if (region == FocusRegion.ConsoleMenu && ConsoleMenuListView.SelectedIndex < 0 && ConsoleMenuEntries.Count > 0)
        {
            ConsoleMenuListView.SelectedIndex = 0;
        }
        if (region == FocusRegion.SystemMenu && SystemMenuListView.SelectedIndex < 0 && SystemMenuEntries.Count > 0)
        {
            SystemMenuListView.SelectedIndex = 0;
        }
        if (region == FocusRegion.CollectionMenu && CollectionMenuListView.SelectedIndex < 0 && CollectionMenuEntries.Count > 0)
        {
            CollectionMenuListView.SelectedIndex = 0;
        }
        IsCategoryRegionFocused = region == FocusRegion.Category;
        IsCoreListRegionFocused = region == FocusRegion.CoreList;
        IsCoreOptionsCoreListRegionFocused = region == FocusRegion.CoreOptionsCoreList;
        IsCoreOptionsListRegionFocused = region == FocusRegion.CoreOptionsList;
        IsFolderListRegionFocused = region == FocusRegion.FolderList;

        // コア管理の全画面は、今の操作領域で表示/非表示が決まる。
        IsCoreInstallScreenVisible = region == FocusRegion.CoreList;
        IsCoreDetailScreenVisible = region == FocusRegion.CoreOptionsCoreList || region == FocusRegion.CoreOptionsList;
        IsFolderScreenVisible = region == FocusRegion.FolderList;
        IsLauncherScreenVisible = region == FocusRegion.LauncherList;

        // ライセンス文の領域: 操作中は、他の一覧の選択行と同じ水色の枠で囲む。
        LicenseRegionBorder.BorderBrush = region == FocusRegion.LicenseText
            ? SelectionHighlightBrush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        // Categoryも他の一覧と同じくIsFocused系のフラグで描く(Switchの設定画面の左メニューと
        // 同じく、選択中のカテゴリは右へ移っても青い縦バー+青文字で残り、左メニューを
        // 操作中の間だけ白背景+水色の枠が付く)。RefreshCategoryFlags参照。
        RefreshCategoryFlags();

        // 他の4つの一覧(CoreList/CoreOptionsCoreList/CoreOptionsList/FolderList)は、
        // 各行が不透明な背景を持つ独自のカードを敷き詰めているため、ListViewItem自体の
        // 背景では選択中ハイライトが隠れて見えなくなってしまう(実機で確認済み)。
        // そのため、これらは各モデル(LibretroCoreListItem/LibretroCoreOptionListItem/
        // RomFolderListItem)のIsFocusedプロパティ経由で、ItemTemplate側の外枠Borderを
        // 直接光らせる方式にしてある(RefreshRowFocusFlags参照)。
        RefreshRowFocusFlags();

    }

    /// <summary>
    /// 左メニュー(Categories)の各項目のIsActive/IsFocusedを設定し直す。
    /// IsActive=選択中のカテゴリ(右側へフォーカスが移っても残る。青い縦バー+青文字)、
    /// IsFocused=選択中で、かつ今Category領域を操作している(白背景+水色の枠)。
    /// カテゴリの選択が変わったとき、および領域が切り替わったときに呼ぶ。
    /// </summary>
    private void RefreshCategoryFlags()
    {
        int selectedIndex = CategoryListView.SelectedIndex;
        bool categoryRegionActive = _focusRegion == FocusRegion.Category && !_backButtonFocused;
        for (int i = 0; i < Categories.Count; i++)
        {
            bool isActive = i == selectedIndex;
            Categories[i].IsActive = isActive;
            Categories[i].IsFocused = isActive && categoryRegionActive;
        }
    }

    /// <summary>
    /// CoreList/CoreOptionsCoreList/CoreOptionsList/FolderListの4領域について、
    /// 現在アクティブな領域とその一覧のSelectedIndexに基づいて、対応するコレクション
    /// (Cores/SelectedCoreOptions/Folders)の各項目のIsFocusedを設定し直す
    /// (アクティブな一覧の選択中の行だけがtrueになり、それ以外は全てfalseになる)。
    /// ItemTemplate側はIsFocusedをBoolToBrushConverter経由で外枠Borderの色に
    /// バインドしているため、これを呼ぶとその場で該当行だけが水色の枠で囲まれる。
    ///
    /// Coresは「コア管理」(CoreListView)と「コア毎の設定」の対象コア一覧
    /// (CoreOptionsCoreListView)の両方から同じインスタンスを共有しているが、
    /// この2領域が同時にアクティブになることは無いため、1つのIsFocusedで両方の
    /// 見た目を兼ねても問題ない。
    ///
    /// 呼び出しタイミング: 領域が切り替わったとき(SetFocusRegion)、領域内で
    /// 十字キー上下により選択行が動いたとき(MoveWithinRegion)、一覧の中身自体が
    /// 読み込み直されたとき(LoadCoresAsync/LoadFoldersAsync)。
    /// </summary>
    private void RefreshRowFocusFlags()
    {
        // Cores(追加/削除画面)とInstalledCores(詳細設定画面の左の一覧。同じインスタンス)の
        // IsFocused: 今アクティブな領域の一覧の、選択中の1行だけがtrue。
        LibretroCoreListItem? focusedCore = _focusRegion switch
        {
            FocusRegion.CoreList => Cores.ElementAtOrDefault(CoreListView.SelectedIndex),
            FocusRegion.CoreOptionsCoreList => InstalledCores.ElementAtOrDefault(CoreOptionsCoreListView.SelectedIndex),
            _ => null,
        };
        object? activeCore = CoreOptionsCoreListView.SelectedItem;
        for (int i = 0; i < Cores.Count; i++)
        {
            Cores[i].IsFocused = ReferenceEquals(Cores[i], focusedCore);
            // IsActive: 詳細設定画面で今オプションを表示しているコア(青い縦バー+青文字)。
            Cores[i].IsActive = ReferenceEquals(Cores[i], activeCore);
        }

        int coreMenuFocusIndex = _focusRegion == FocusRegion.CoreMenu ? CoreMenuListView.SelectedIndex : -1;
        for (int i = 0; i < CoreMenuEntries.Count; i++)
        {
            CoreMenuEntries[i].IsFocused = i == coreMenuFocusIndex;
        }

        int folderMenuFocusIndex = _focusRegion == FocusRegion.FolderMenu ? FolderMenuListView.SelectedIndex : -1;
        for (int i = 0; i < FolderMenuEntries.Count; i++)
        {
            FolderMenuEntries[i].IsFocused = i == folderMenuFocusIndex;
        }

        int launcherMenuFocusIndex = _focusRegion == FocusRegion.LauncherMenu ? LauncherMenuListView.SelectedIndex : -1;
        for (int i = 0; i < LauncherMenuEntries.Count; i++)
        {
            LauncherMenuEntries[i].IsFocused = i == launcherMenuFocusIndex;
        }

        int consoleMenuFocusIndex = _focusRegion == FocusRegion.ConsoleMenu ? ConsoleMenuListView.SelectedIndex : -1;
        for (int i = 0; i < ConsoleMenuEntries.Count; i++)
        {
            ConsoleMenuEntries[i].IsFocused = i == consoleMenuFocusIndex;
        }

        int systemMenuFocusIndex = _focusRegion == FocusRegion.SystemMenu ? SystemMenuListView.SelectedIndex : -1;
        for (int i = 0; i < SystemMenuEntries.Count; i++)
        {
            SystemMenuEntries[i].IsFocused = i == systemMenuFocusIndex;
        }

        int collectionMenuFocusIndex = _focusRegion == FocusRegion.CollectionMenu ? CollectionMenuListView.SelectedIndex : -1;
        for (int i = 0; i < CollectionMenuEntries.Count; i++)
        {
            CollectionMenuEntries[i].IsFocused = i == collectionMenuFocusIndex;
        }

        int optionFocusIndex = _focusRegion == FocusRegion.CoreOptionsList ? CoreOptionsListView.SelectedIndex : -1;
        for (int i = 0; i < SelectedCoreOptions.Count; i++)
        {
            SelectedCoreOptions[i].IsFocused = i == optionFocusIndex;
        }

        int folderFocusIndex = _focusRegion == FocusRegion.FolderList ? FolderListView.SelectedIndex : -1;
        for (int i = 0; i < Folders.Count; i++)
        {
            Folders[i].IsFocused = i == folderFocusIndex;
        }

        int appSettingFocusIndex = _focusRegion == FocusRegion.AppSettingsList ? AppSettingsListView.SelectedIndex : -1;
        for (int i = 0; i < AppSettingRows.Count; i++)
        {
            AppSettingRows[i].IsFocused = i == appSettingFocusIndex;
        }

        int launcherFocusIndex = _focusRegion == FocusRegion.LauncherList ? LauncherListView.SelectedIndex : -1;
        for (int i = 0; i < Launchers.Count; i++)
        {
            Launchers[i].IsFocused = i == launcherFocusIndex;
        }
    }

    /// <summary>
    /// ListViewItemの選択中ハイライト("Selected"/"SelectedPointerOver"/"SelectedPressed"/
    /// "SelectedUnfocused"の各VisualStateが参照するブラシ)を上書きするためのテーマ
    /// リソースキー。
    ///
    /// 【実機で判明した誤り】当初は「これらのキーへの上書きを外せば、WinUI既定の
    /// テーマカラーがそのまま水色で表示される」という前提で実装していたが、実機で
    /// 試したところ、既定のテーマカラーは水色ではなく薄いグレー(この配色の
    /// AppAccentColor/#00C3E3とは無関係な、システム既定の中立色)だと判明した。
    /// フォーカスの有無("Selected"か"SelectedUnfocused"か)を切り替えても、
    /// どちらも薄いグレーであることに変わりは無く、水色は一切出ない。
    /// そのため今は、上書きを外す(既定へ戻す)のではなく、**このアプリの配色
    /// (Styles/Colors.xamlのAppAccentColor、BoolToBrushConverterが返す色と同じ
    /// #00C3E3の水色)を表示側の全キーへ直接設定する**方式に変更した。
    /// フォーカスの有無に左右されないよう、"SelectedUnfocused"も含めた4キー全てへ
    /// 同じブラシを設定する。
    /// </summary>
    private static readonly string[] SelectionHighlightResourceKeys =
    {
        "ListViewItemBackgroundSelected",
        "ListViewItemBackgroundSelectedPointerOver",
        "ListViewItemBackgroundSelectedPressed",
        "ListViewItemBackgroundSelectedUnfocused",
    };

    /// <summary>
    /// このアプリの配色(Styles/Colors.xamlのAppAccentColor)と同じ水色。
    /// ListView.Resourcesへ実行時に追加するブラシはXAMLの{StaticResource}を経由できない
    /// (コードビハインドから直接色を指定する必要がある)ため、
    /// Converters/BoolToBrushConverter.csが返す色(Color.FromArgb(255, 0x00, 0xC3, 0xE3))と
    /// 完全に一致させて、ここだけ色がずれることが無いようにしてある。
    /// </summary>
    private static readonly SolidColorBrush SelectionHighlightBrush =
        new(Windows.UI.Color.FromArgb(255, 0x00, 0xC3, 0xE3));

    private static readonly SolidColorBrush SelectionHighlightHiddenBrush =
        new(Microsoft.UI.Colors.Transparent);

    /// <summary>
    /// 指定ListViewの選択中ハイライトを表示するかどうかを切り替える。
    ///
    /// 今どの領域をゲームパッドで操作しているかを示すのに、以前はパネル/一覧全体を
    /// 囲むBorderの枠線色を使っていたが、「パネル全体が囲まれると分かりにくい、
    /// 選択している項目そのものだけを水色で囲んでほしい」というフィードバックを受けて、
    /// 代わりにListViewItemの「選択中」ハイライト(項目1件だけを囲む、角丸の水色の
    /// 縁取り)を使う方式に変えた。ただし5つのListView(Category/CoreList/
    /// CoreOptionsCoreList/CoreOptionsList/FolderList)は常にどれかの行が
    /// SelectedIndexを持っているため、何もしないと5つ全部が同時にハイライトされてしまう。
    /// そこで、アクティブなListViewには水色のSolidColorBrush(<see cref="SelectionHighlightBrush"/>)を、
    /// 非アクティブなListViewには透明なブラシ(<see cref="SelectionHighlightHiddenBrush"/>)を、
    /// それぞれ自分自身のResourcesへ上書きとして設定する(ListViewItemの選択ハイライト用
    /// リソースはコントロール単位で解決されるため、ListView.Resourcesへ同名キーを足すと
    /// そのListViewだけに効き、他のListViewには影響しない)。
    /// </summary>
    private static void SetSelectionHighlightVisible(ListView listView, bool visible)
    {
        SolidColorBrush brush = visible ? SelectionHighlightBrush : SelectionHighlightHiddenBrush;
        foreach (string key in SelectionHighlightResourceKeys)
        {
            listView.Resources[key] = brush;
        }
    }

    /// <summary>
    /// 現在の領域より1段深い領域へ移動する(Category→CoreList/CoreOptionsCoreList/
    /// FolderList、CoreOptionsCoreList→CoreOptionsList)。既に最も深い領域にいる場合は
    /// 何もしない。「コア毎の設定」でまだ設定可能な項目が無いコアを選択中は、
    /// CoreOptionsListへは入れない(NoOptionsPanelの案内のまま)。
    /// </summary>
    private void EnterNextRegion()
    {
        // コア管理は枝分かれがあるため、CoreMenu以降は配列の並びではなく個別に決める。
        switch (_focusRegion)
        {
            case FocusRegion.CoreMenu:
                OpenCoreMenuEntry();
                return;

            case FocusRegion.FolderMenu:
                OpenFolderMenuEntry();
                return;

            case FocusRegion.LauncherMenu:
                OpenLauncherMenuEntry();
                return;

            case FocusRegion.ConsoleMenu:
                OpenConsoleMenuEntry();
                return;

            case FocusRegion.SystemMenu:
                OpenSystemMenuEntry();
                return;

            case FocusRegion.CollectionMenu:
                OpenCollectionMenuEntry();
                return;

            case FocusRegion.CoreList:
            case FocusRegion.CoreOptionsList:
                ShakeFocusedRow(vertical: false); // それぞれの枝の最も深い領域(これ以上右へ行けない)
                return;

            case FocusRegion.CoreOptionsCoreList:
                // 設定可能な項目が無いコアの間は、オプション一覧へは入れない(案内のまま)。
                if (SelectedCoreOptions.Count > 0)
                {
                    SetFocusRegion(FocusRegion.CoreOptionsList);
                }
                else
                {
                    ShakeFocusedRow(vertical: false);
                }
                return;
        }

        FocusRegion[] regions = CurrentRegions;
        int index = Array.IndexOf(regions, _focusRegion);
        if (index < 0) index = 0;
        if (index + 1 >= regions.Length)
        {
            ShakeFocusedRow(vertical: false); // これ以上右へ行けない
            return;
        }

        SetFocusRegion(regions[index + 1]);
    }

    /// <summary>
    /// 現在の領域より1段浅い領域へ移動する(CoreMenu/FolderList等→Category、
    /// CoreList/CoreOptionsCoreList→CoreMenu、CoreOptionsList→CoreOptionsCoreList)。
    /// 既にCategoryにいる場合は何もしない(この画面自体から抜けるのはBボタン、OnGamepadButton参照)。
    /// </summary>
    private void ExitToPreviousRegion()
    {
        switch (_focusRegion)
        {
            case FocusRegion.Category:
                ShakeFocusedRow(vertical: false); // これ以上左へ行けない
                return;

            case FocusRegion.CoreList:
            case FocusRegion.CoreOptionsCoreList:
                SetFocusRegion(FocusRegion.CoreMenu);
                return;

            case FocusRegion.CoreOptionsList:
                SetFocusRegion(FocusRegion.CoreOptionsCoreList);
                return;
        }

        FocusRegion[] regions = CurrentRegions;
        int index = Array.IndexOf(regions, _focusRegion);
        SetFocusRegion(index > 0 ? regions[index - 1] : FocusRegion.Category);
    }

    /// <summary>現在フォーカスしている領域の選択行を、揺らして「これ以上動かせない」ことを知らせる。
    /// vertical=trueなら縦揺れ(上下キー)、falseなら横揺れ(左右キー)。</summary>
    private void ShakeFocusedRow(bool vertical)
    {
        ListView? listView = _focusRegion switch
        {
            FocusRegion.Category => CategoryListView,
            FocusRegion.CoreMenu => CoreMenuListView,
            FocusRegion.FolderMenu => FolderMenuListView,
            FocusRegion.LauncherMenu => LauncherMenuListView,
            FocusRegion.ConsoleMenu => ConsoleMenuListView,
            FocusRegion.SystemMenu => SystemMenuListView,
            FocusRegion.CollectionMenu => CollectionMenuListView,
            FocusRegion.CoreList => CoreListView,
            FocusRegion.CoreOptionsCoreList => CoreOptionsCoreListView,
            FocusRegion.CoreOptionsList => CoreOptionsListView,
            FocusRegion.FolderList => FolderListView,
            FocusRegion.LauncherList => LauncherListView,
            FocusRegion.AppSettingsList => AppSettingsListView,
            _ => null,
        };
        if (listView is null || listView.SelectedIndex < 0) return;

        ShakeAnimation.Shake(listView.ContainerFromIndex(listView.SelectedIndex) as UIElement, vertical);
    }

    private void MoveWithinRegion(int delta)
    {
        switch (_focusRegion)
        {
            case FocusRegion.Category:
                if (Categories.Count == 0) return;
                int categoryIndex = GamepadListNavigator.Move(
                    Math.Max(CategoryListView.SelectedIndex, 0), delta, Categories.Count, wrap: true);
                CategoryListView.SelectedIndex = categoryIndex;
                CategoryListView.ScrollIntoView(Categories[categoryIndex]);
                RefreshCategoryFlags();
                break;

            case FocusRegion.CoreMenu:
                MoveListSelection(CoreMenuListView, CoreMenuEntries.Count, delta);
                break;

            case FocusRegion.FolderMenu:
                MoveListSelection(FolderMenuListView, FolderMenuEntries.Count, delta);
                break;

            case FocusRegion.LauncherMenu:
                MoveListSelection(LauncherMenuListView, LauncherMenuEntries.Count, delta);
                break;

            case FocusRegion.ConsoleMenu:
                MoveListSelection(ConsoleMenuListView, ConsoleMenuEntries.Count, delta);
                break;

            case FocusRegion.SystemMenu:
                MoveListSelection(SystemMenuListView, SystemMenuEntries.Count, delta);
                break;

            case FocusRegion.CollectionMenu:
                MoveListSelection(CollectionMenuListView, CollectionMenuEntries.Count, delta);
                break;

            case FocusRegion.CoreList:
                MoveListSelection(CoreListView, Cores.Count, delta);
                break;

            case FocusRegion.CoreOptionsCoreList:
                MoveListSelection(CoreOptionsCoreListView, InstalledCores.Count, delta);
                break;

            case FocusRegion.CoreOptionsList:
                MoveListSelection(CoreOptionsListView, SelectedCoreOptions.Count, delta);
                break;

            case FocusRegion.FolderList:
                MoveListSelection(FolderListView, Folders.Count, delta);
                break;

            case FocusRegion.LauncherList:
                MoveListSelection(LauncherListView, Launchers.Count, delta);
                break;

            case FocusRegion.AppSettingsList:
                MoveListSelection(AppSettingsListView, AppSettingRows.Count, delta);
                break;

            case FocusRegion.LicenseText:
                // 十字キー上下1回で約4行ぶんスクロールする。
                LicenseScrollViewer.ChangeView(null, LicenseScrollViewer.VerticalOffset + delta * 140, null, disableAnimation: false);
                break;
        }

        // Category以外は、選択行(SelectedIndex)が動くたびにIsFocusedを再計算しないと
        // 水色の枠が古い行に残ったままになってしまう。
        if (_focusRegion != FocusRegion.Category)
        {
            RefreshRowFocusFlags();
        }
    }

    private static ScrollViewer? FindParentScrollViewer(DependencyObject child)
    {
        for (DependencyObject? p = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(child); p is not null;
             p = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(p))
        {
            if (p is ScrollViewer sv) return sv;
        }
        return null;
    }

    /// <summary>ListView.SelectedIndexを十字キー上下1回分動かし、見える位置までスクロールする。</summary>
    private void MoveListSelection(ListView listView, int count, int delta)
    {
        if (count == 0) return;

        int current = Math.Max(listView.SelectedIndex, 0);
        int index = GamepadListNavigator.Move(current, delta, count);

        // 先頭/末尾でこれ以上動かせないときは、選択中の行をブルブルっと揺らして知らせる(Switchと同じ)。
        if (index == current && listView.SelectedIndex >= 0)
        {
            ShakeAnimation.Shake(listView.ContainerFromIndex(index) as UIElement, vertical: true);
            return;
        }

        listView.SelectedIndex = index;
        if (index >= 0 && index < listView.Items.Count)
        {
            listView.ScrollIntoView(listView.Items[index]);

            // アプリ設定はListView自身ではなく外側のScrollViewerがスクロールするため、
            // 選択行(先頭/末尾なら全体の先頭/末尾=アプリ情報まで)が見える位置へ動かす。
            if (ReferenceEquals(listView, AppSettingsListView))
            {
                if (index == 0)
                    AppSettingsScrollViewer.ChangeView(null, 0, null, disableAnimation: false);
                else if (index == listView.Items.Count - 1)
                    AppSettingsScrollViewer.ChangeView(null, AppSettingsScrollViewer.ScrollableHeight, null, disableAnimation: false);
                else if (listView.ContainerFromIndex(index) is FrameworkElement container)
                    container.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
            }
            else if (listView.ContainerFromIndex(index) is FrameworkElement menuContainer)
            {
                // 項目が増えて収まらないパネルでも、選択項目(説明文まで)が見えるよう外側のScrollViewerを動かす。
                // 先頭はイラスト付きの見出しまで戻す。
                if (index == 0 && FindParentScrollViewer(listView) is { } scroll)
                    scroll.ChangeView(null, 0, null, disableAnimation: false);
                else
                    menuContainer.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
            }
        }
    }

    private void ActivateFocusRegion()
    {
        switch (_focusRegion)
        {
            case FocusRegion.CoreList:
                if (CoreListView.SelectedItem is LibretroCoreListItem core)
                {
                    _ = ConfirmAndExecuteCoreActionAsync(core);
                }
                break;

            case FocusRegion.CoreOptionsList:
                if (CoreOptionsListView.SelectedItem is LibretroCoreOptionListItem optionRow)
                {
                    _ = ActivateCoreOptionAsync(optionRow);
                }
                break;

            case FocusRegion.FolderList:
                if (FolderListView.SelectedItem is RomFolderListItem row)
                {
                    _ = ActivateFolderRowAsync(row);
                }
                break;

            case FocusRegion.LauncherList:
                if (LauncherListView.SelectedItem is LauncherListItem launcherRow)
                {
                    _ = EditLauncherAsync(launcherRow);
                }
                break;

            case FocusRegion.AppSettingsList:
                if (AppSettingsListView.SelectedItem is AppSettingRowItem settingRow)
                {
                    _ = ActivateAppSettingRowAsync(settingRow);
                }
                break;
        }
    }
}
