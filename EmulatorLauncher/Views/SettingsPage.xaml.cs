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

/// <summary>
/// トップ画面(ConsoleSelectPage)の設定アイコンから開く設定画面(ゲームパッド専用操作対応版)。
///
/// 左側に設定カテゴリのメニュー(<see cref="Categories"/>)、右側に選択中カテゴリの
/// 内容を表示する2ペイン構成。現時点のカテゴリは以下の3つ:
/// ・「コア管理」: cores/libretro.jsonの一覧を表示してインストール/アンインストールを行う。
/// ・「コア毎の設定」: 左でコアを選び、右にそのコアのオプション一覧(cores/libretro.jsonの
///   "options")を表示して値を変更する。現時点ではoptionsを持つコアがまだ無いため、
///   選んだコアに応じて「設定可能な項目がありません」と案内する。
/// ・「ゲームフォルダ管理」: 登録済みのROMフォルダ一覧を表示し、新規追加(フォルダ選択→
///   インストール済みコアに対応するコンソールを選択→スキャン)と削除(データベースからの
///   登録解除。RomFileとコレクション/お気に入りへの所属も合わせて削除するが、実ファイルや
///   ダウンロード済みの画像ファイルは削除しない)を行う。
///
/// ゲームパッドのみでの操作に対応するため、画面を<see cref="FocusRegion"/>で表す複数の
/// 操作領域に分け、左メニュー(Category)を「浅い」階層、各パネルの中身を「深い」階層とみなした
/// 階層移動(十字キー右/Aで深く、十字キー左/Bで浅く)で切り替える(詳しくは
/// SettingsPage.xamlの先頭コメント、およびOnGamepadButtonのコメント参照)。どの領域が
/// 選べるか、どういう順で深くなるかは選択中カテゴリによって変わる
/// (<see cref="RegionsByCategory"/>)。
///
/// 今後さらに設定項目(言語/テーマ等)を追加する場合は、Categoriesにラベルを増やし、
/// 対応するパネル(コア管理パネルのBorderと同じ要領)をもう1枚XAML側に追加したうえで、
/// <see cref="CategoryListView_SelectionChanged"/>での表示切り替えと、
/// <see cref="RegionsByCategory"/>への追加が必要になる。
/// </summary>
public sealed partial class SettingsPage : Page, IControllerPage, INotifyPropertyChanged
{
    private const string CoreManagementCategoryLabel = "コア管理";
    private const string GameFolderManagementCategoryLabel = "ゲームフォルダ管理";
    private const string LauncherManagementCategoryLabel = "ランチャー管理";
    private const string ConsoleManagementCategoryLabel = "コンソール管理";
    private const string CollectionManagementCategoryLabel = "コレクション管理";
    private const string SystemSettingsCategoryLabel = "システム設定";
    private const string AppSettingsCategoryLabel = "アプリ設定";
    private const string LicenseCategoryLabel = "ライセンス";

    /// <summary>
    /// ゲームパッドのL/Rショルダーで切り替える操作領域。どの領域が実際に選べるかは
    /// <see cref="RegionsByCategory"/>で選択中カテゴリごとに絞り込む。
    /// </summary>
    private enum FocusRegion
    {
        /// <summary>左側の設定カテゴリメニュー。常にどのカテゴリでも選べる。</summary>
        Category,

        /// <summary>「コア管理」パネルの2項目メニュー(コアモジュール追加/削除、コアモジュール詳細設定)。</summary>
        CoreMenu,

        /// <summary>全画面「コアモジュール追加/削除」のコア一覧。</summary>
        CoreList,

        /// <summary>全画面「コアモジュール詳細設定」の、対象コア(インストール済みのみ)を選ぶ左側の一覧。</summary>
        CoreOptionsCoreList,

        /// <summary>全画面「コアモジュール詳細設定」の、選択中コアのオプション一覧(右側)。</summary>
        CoreOptionsList,

        /// <summary>「ゲームフォルダ管理」パネルの2項目メニュー(ゲームフォルダの追加、ゲームフォルダの確認/削除)。</summary>
        FolderMenu,

        /// <summary>全画面「ゲームフォルダの確認/削除」のフォルダ一覧。</summary>
        FolderList,

        /// <summary>「ランチャー管理」パネルの2項目メニュー(ランチャーの追加、ランチャーの編集/削除)。</summary>
        LauncherMenu,

        /// <summary>全画面「ランチャーの編集/削除」のランチャー一覧。</summary>
        LauncherList,

        /// <summary>「コンソール管理」パネルのメニュー(コンソールの並び替え)。</summary>
        ConsoleMenu,

        /// <summary>「システム設定」パネルのメニュー(システム項目の編集)。</summary>
        SystemMenu,

        /// <summary>「コレクション管理」パネルの3項目メニュー(コレクションの追加/編集/削除)。</summary>
        CollectionMenu,

        /// <summary>「ライセンス」パネルのライセンス文(十字キー上下でスクロールする)。</summary>
        LicenseText,

        /// <summary>「アプリ設定」パネルの設定行(「地域」「操作ボタンの入れ替え」。十字キー上下で選び、Aで決定)。</summary>
        AppSettingsList,
    }

    /// <summary>カテゴリごとに、十字キー左右(またはA/B)で行き来できる操作領域の並び
    /// (浅い→深いの順。先頭は常にCategory自身)。</summary>
    private static readonly Dictionary<string, FocusRegion[]> RegionsByCategory = new()
    {
        // コア管理は2つの枝(CoreMenu→CoreList、CoreMenu→CoreOptionsCoreList→CoreOptionsList)に
        // 分かれるため、並びは「どの領域がこのカテゴリで使えるか」の一覧として使い、
        // 実際の行き来はEnterNextRegion/ExitToPreviousRegionで個別に決める。
        [CoreManagementCategoryLabel] = new[]
        {
            FocusRegion.Category, FocusRegion.CoreMenu, FocusRegion.CoreList,
            FocusRegion.CoreOptionsCoreList, FocusRegion.CoreOptionsList,
        },
        [GameFolderManagementCategoryLabel] = new[] { FocusRegion.Category, FocusRegion.FolderMenu, FocusRegion.FolderList },
        [LauncherManagementCategoryLabel] = new[] { FocusRegion.Category, FocusRegion.LauncherMenu, FocusRegion.LauncherList },
        [ConsoleManagementCategoryLabel] = new[] { FocusRegion.Category, FocusRegion.ConsoleMenu },
        [CollectionManagementCategoryLabel] = new[] { FocusRegion.Category, FocusRegion.CollectionMenu },
        [SystemSettingsCategoryLabel] = new[] { FocusRegion.Category, FocusRegion.SystemMenu },
        [AppSettingsCategoryLabel] = new[] { FocusRegion.Category, FocusRegion.AppSettingsList },
        [LicenseCategoryLabel] = new[] { FocusRegion.Category, FocusRegion.LicenseText },
    };

    /// <summary>左側メニューに表示する設定カテゴリの一覧。</summary>
    public ObservableCollection<SettingsCategoryItem> Categories { get; } = new()
    {
        // 区切り線(separatorBelow)でグループ分け: ゲームの管理 / 起動の仕組み(コンソール→ランチャー→コア) / アプリと環境 / ライセンス
        new(GameFolderManagementCategoryLabel),
        new(CollectionManagementCategoryLabel, separatorBelow: true),
        new(ConsoleManagementCategoryLabel),
        new(LauncherManagementCategoryLabel),
        new(CoreManagementCategoryLabel, separatorBelow: true),
        new(AppSettingsCategoryLabel),
        new(SystemSettingsCategoryLabel, separatorBelow: true),
        new(LicenseCategoryLabel),
    };

    // ---- 「アプリ情報」「ライセンス」パネルの表示内容(x:Bindで参照。読み取り専用) ----

    /// <summary>アプリ名(csprojのProduct)。</summary>
    public string AppNameText { get; } = "RetroNexus";

    /// <summary>バージョン(csprojのVersion=AssemblyInformationalVersion。「+ハッシュ」部分は除く)。</summary>
    public string AppVersionText { get; } = GetAppVersion();

    /// <summary>著作権表記(Services/LicenseTexts.cs)。</summary>
    public string AppCopyrightText { get; } = LicenseTexts.AppCopyright;

    /// <summary>実行中の.NETランタイム。</summary>
    public string RuntimeText { get; } = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

    /// <summary>OS。</summary>
    public string OsText { get; } = System.Runtime.InteropServices.RuntimeInformation.OSDescription;

    /// <summary>ライセンス画面に表示する全文(アプリ本体のMIT+CHDSharp等の表記)。</summary>
    public string LicenseFullText { get; } = LicenseTexts.Full;

    private static string GetAppVersion()
    {
        var assembly = typeof(SettingsPage).Assembly;
        string? info = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(info))
        {
            int plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }

        return assembly.GetName().Version?.ToString() ?? "-";
    }

    /// <summary>
    /// libretroコアの一覧(コンソール名→コア名の順に並べる)。「コア管理」パネルの一覧と
    /// 「コア毎の設定」パネルの対象コア選択用一覧の両方から、同じインスタンスを共有して使う。
    /// </summary>
    public ObservableCollection<LibretroCoreListItem> Cores { get; } = new();

    /// <summary>
    /// インストール済みのコアだけの一覧(全画面「コアモジュール詳細設定」の左の一覧)。
    /// Coresと同じインスタンスを指す。画面へ入るたびにRefreshInstalledCoresで作り直す。
    /// </summary>
    public ObservableCollection<LibretroCoreListItem> InstalledCores { get; } = new();

    /// <summary>全画面「コアモジュール詳細設定」で、選択中コアのオプション一覧として表示する項目。</summary>
    public ObservableCollection<LibretroCoreOptionListItem> SelectedCoreOptions { get; } = new();

    private const string CoreMenuInstallKey = "install";
    private const string CoreMenuDetailKey = "detail";

    /// <summary>「コア管理」パネルの2項目メニュー。</summary>
    public ObservableCollection<SettingsMenuEntryItem> CoreMenuEntries { get; } = new()
    {
        new SettingsMenuEntryItem
        {
            Key = CoreMenuInstallKey,
            Title = "コアモジュール追加/削除",
            Description = "libretroコアの追加や削除を行います。コアモジュールはインターネット経由で自動的にインストールされます。",
        },
        new SettingsMenuEntryItem
        {
            Key = CoreMenuDetailKey,
            Title = "コアモジュール詳細設定",
            Description = "libretroコアの詳細設定を行います。ここで表示されるオプション項目はゲーム中の中断メニューの設定にも表示されます。",
        },
    };

    /// <summary>全画面「ゲームフォルダの確認/削除」に表示する、登録済みROMフォルダの一覧(0件のこともある)。</summary>
    public ObservableCollection<RomFolderListItem> Folders { get; } = new();

    /// <summary>全画面「ランチャーの編集/削除」に表示する、Launchers.jsonのランチャー一覧。</summary>
    public ObservableCollection<LauncherListItem> Launchers { get; } = new();

    private const string FolderMenuAddKey = "add";
    private const string FolderMenuRefreshKey = "refresh";
    private const string FolderMenuListKey = "list";

    /// <summary>「ゲームフォルダ管理」パネルの3項目メニュー。</summary>
    public ObservableCollection<SettingsMenuEntryItem> FolderMenuEntries { get; } = new()
    {
        new SettingsMenuEntryItem
        {
            Key = FolderMenuAddKey,
            Title = "ゲームフォルダの追加",
            Description = "ROMが入っているフォルダを選んで登録します。登録したフォルダのゲームがゲーム一覧に表示されます。",
        },
        new SettingsMenuEntryItem
        {
            Key = FolderMenuRefreshKey,
            Title = "ゲームフォルダの更新",
            Description = "登録済みのフォルダを調べ直し、新しく追加されたROMをゲーム一覧に反映します。",
        },
        new SettingsMenuEntryItem
        {
            Key = FolderMenuListKey,
            Title = "ゲームフォルダの確認/削除",
            Description = "登録済みのフォルダを確認したり、登録から外したりします。フォルダ内のファイルは削除されません。",
        },
    };

    private const string LauncherMenuAddKey = "add";
    private const string LauncherMenuListKey = "list";

    /// <summary>「ランチャー管理」パネルの2項目メニュー。</summary>
    public ObservableCollection<SettingsMenuEntryItem> LauncherMenuEntries { get; } = new()
    {
        new SettingsMenuEntryItem
        {
            Key = LauncherMenuAddKey,
            Title = "ランチャーの追加",
            Description = "libretroコアや外部のエミュレータを使うランチャーを、新しく登録します。",
        },
        new SettingsMenuEntryItem
        {
            Key = LauncherMenuListKey,
            Title = "ランチャーの編集/削除",
            Description = "登録済みのランチャーの確認、編集、削除を行います。コンソールで使用中のランチャーには「使用中」と表示されます。",
        },
    };

    private const string ConsoleMenuOrderKey = "consoleOrder";
    private const string ConsoleMenuEditKey = "consoleEdit";
    private const string ConsoleMenuCreateKey = "consoleCreate";

    /// <summary>「コンソール管理」パネルのメニュー(コンソールの編集・並び替え)。</summary>
    public ObservableCollection<SettingsMenuEntryItem> ConsoleMenuEntries { get; } = new()
    {
        new SettingsMenuEntryItem
        {
            Key = ConsoleMenuEditKey,
            Title = "コンソール編集",
            Description = "コンソールのアイコンと、使う起動方法(ランチャー)を変更します。",
        },
        new SettingsMenuEntryItem
        {
            Key = ConsoleMenuCreateKey,
            Title = "コンソール作成",
            Description = "新しいコンソールを作ります。使う起動方法(ランチャー)も一緒に設定します。",
        },
        new SettingsMenuEntryItem
        {
            Key = ConsoleMenuOrderKey,
            Title = "コンソールの並び替え",
            Description = "コンソールの並び順を変えます。トップ画面に反映されます。",
        },
    };

    private bool _isConsoleManagementSelected;

    public bool IsConsoleManagementSelected
    {
        get => _isConsoleManagementSelected;
        private set
        {
            if (_isConsoleManagementSelected == value) return;
            _isConsoleManagementSelected = value;
            OnPropertyChanged();
        }
    }

    private const string SystemMenuItemsKey = "systemItems";
    private const string SystemMenuReorderKey = "systemItemsOrder";

    /// <summary>「システム設定」パネルの設定行(画面・PCの環境にかかわる項目と、システム画面に出す項目の編集)。</summary>
    public ObservableCollection<AppSettingRowItem> SystemMenuEntries { get; } = new()
    {
        new AppSettingRowItem
        {
            Key = SystemMenuItemsKey,
            Label = "システム項目の編集",
            Description = "システム画面に表示する項目を選びます。選ばなかった項目は表示されません。"
                + "Pluginsフォルダに置いたプラグインは、ここで選ぶと使えるようになります(最初は無効です)。",
        },
        new AppSettingRowItem
        {
            Key = SystemMenuReorderKey,
            Label = "システム項目の並び替え",
            Description = "システム画面に表示する項目の並び順を変えます。項目は上から順に、タイルは横に詰めて並びます。",
        },
        new AppSettingRowItem
        {
            Key = StartFullScreenRowKey,
            Label = "起動時にフルスクリーン",
            Description = "「有効」にすると、アプリをフルスクリーンで起動します。「無効」(標準)は通常のウィンドウで起動します。",
        },
        new AppSettingRowItem
        {
            Key = VirtualResolutionRowKey,
            Label = "仮想解像度",
            Description = "画面の描画の基準にする解像度を選びます。画面の縦横比に合わせて選ぶと、余白が出にくくなります。"
                + "「1920 x 1080」(標準)は16:9の画面向け、「1920 x 1200」は16:10の画面(GPD Win Max 2など)向けです。"
                + "変更はアプリを再起動すると反映されます。",
        },
        new AppSettingRowItem
        {
            Key = FontRowKey,
            Label = "フォント",
            Description = "画面の文字に使うフォントを選びます。「アプリ標準」(標準)は同梱のM PLUS Rounded 1c、"
                + "「システム標準」はWindowsの標準フォント(Segoe UI / 游ゴシック UI)です。変更はアプリを再起動すると反映されます。",
        },
        new AppSettingRowItem { Key = RegionRowKey, Label = "地域" },
    };

    private bool _isSystemSettingsSelected;

    public bool IsSystemSettingsSelected
    {
        get => _isSystemSettingsSelected;
        private set
        {
            if (_isSystemSettingsSelected == value) return;
            _isSystemSettingsSelected = value;
            OnPropertyChanged();
        }
    }

    private const string CollectionMenuOrderKey = "order";
    private const string CollectionMenuAddKey = "add";
    private const string CollectionMenuEditKey = "edit";
    private const string CollectionMenuDeleteKey = "delete";

    /// <summary>「コレクション管理」パネルの3項目メニュー。ダークテーマ以外でも使う共通の設定。</summary>
    public ObservableCollection<SettingsMenuEntryItem> CollectionMenuEntries { get; } = new()
    {
        new SettingsMenuEntryItem
        {
            Key = CollectionMenuOrderKey,
            Title = "コレクションの並び替え",
            Description = "コレクションの並び順を変えます。トップ画面に反映されます。",
        },
        new SettingsMenuEntryItem
        {
            Key = CollectionMenuAddKey,
            Title = "コレクションの追加",
            Description = "ゲームをまとめる、新しいコレクションを作ります。",
        },
        new SettingsMenuEntryItem
        {
            Key = CollectionMenuEditKey,
            Title = "コレクションの編集",
            Description = "コレクションの名前とアイコンを変えます。",
        },
        new SettingsMenuEntryItem
        {
            Key = CollectionMenuDeleteKey,
            Title = "コレクションの削除",
            Description = "コレクションを削除します。コレクション内のゲーム自体は削除されません。",
        },
    };

    private bool _isCollectionManagementSelected;

    public bool IsCollectionManagementSelected
    {
        get => _isCollectionManagementSelected;
        private set
        {
            if (_isCollectionManagementSelected == value) return;
            _isCollectionManagementSelected = value;
            OnPropertyChanged();
        }
    }

    private bool _isCoreManagementSelected = true;

    /// <summary>コア管理パネルを表示するかどうか(左メニューで「コア管理」が選ばれているか)。</summary>
    public bool IsCoreManagementSelected
    {
        get => _isCoreManagementSelected;
        private set
        {
            if (_isCoreManagementSelected == value) return;
            _isCoreManagementSelected = value;
            OnPropertyChanged();
        }
    }

    private bool _isCoreInstallScreenVisible;

    /// <summary>全画面「コアモジュール追加/削除」を表示するか(操作領域がCoreListのとき)。</summary>
    public bool IsCoreInstallScreenVisible
    {
        get => _isCoreInstallScreenVisible;
        private set
        {
            if (_isCoreInstallScreenVisible == value) return;
            _isCoreInstallScreenVisible = value;
            OnPropertyChanged();
        }
    }

    private bool _isCoreDetailScreenVisible;

    /// <summary>全画面「コアモジュール詳細設定」を表示するか(操作領域がCoreOptionsCoreList/CoreOptionsListのとき)。</summary>
    public bool IsCoreDetailScreenVisible
    {
        get => _isCoreDetailScreenVisible;
        private set
        {
            if (_isCoreDetailScreenVisible == value) return;
            _isCoreDetailScreenVisible = value;
            OnPropertyChanged();
        }
    }

    private bool _isFolderScreenVisible;

    /// <summary>全画面「ゲームフォルダの確認/削除」を表示するか(操作領域がFolderListのとき)。</summary>
    public bool IsFolderScreenVisible
    {
        get => _isFolderScreenVisible;
        private set
        {
            if (_isFolderScreenVisible == value) return;
            _isFolderScreenVisible = value;
            OnPropertyChanged();
        }
    }

    private bool _isLauncherScreenVisible;

    /// <summary>全画面「ランチャーの編集/削除」を表示するか(操作領域がLauncherListのとき)。</summary>
    public bool IsLauncherScreenVisible
    {
        get => _isLauncherScreenVisible;
        private set
        {
            if (_isLauncherScreenVisible == value) return;
            _isLauncherScreenVisible = value;
            OnPropertyChanged();
        }
    }

    private bool _isGameFolderManagementSelected;

    /// <summary>ゲームフォルダ管理パネルを表示するかどうか(左メニューで「ゲームフォルダ管理」が選ばれているか)。</summary>
    public bool IsGameFolderManagementSelected
    {
        get => _isGameFolderManagementSelected;
        private set
        {
            if (_isGameFolderManagementSelected == value) return;
            _isGameFolderManagementSelected = value;
            OnPropertyChanged();
        }
    }

    private bool _isLauncherManagementSelected;

    /// <summary>ランチャー管理パネルを表示するかどうか(左メニューで「ランチャー管理」が選ばれているか)。</summary>
    public bool IsLauncherManagementSelected
    {
        get => _isLauncherManagementSelected;
        private set
        {
            if (_isLauncherManagementSelected == value) return;
            _isLauncherManagementSelected = value;
            OnPropertyChanged();
        }
    }

    private bool _isAppSettingsSelected;

    /// <summary>アプリ設定パネルを表示するかどうか。</summary>
    public bool IsAppSettingsSelected
    {
        get => _isAppSettingsSelected;
        private set
        {
            if (_isAppSettingsSelected == value) return;
            _isAppSettingsSelected = value;
            OnPropertyChanged();
        }
    }

    private bool _isLicenseSelected;

    /// <summary>ライセンスパネルを表示するかどうか。</summary>
    public bool IsLicenseSelected
    {
        get => _isLicenseSelected;
        private set
        {
            if (_isLicenseSelected == value) return;
            _isLicenseSelected = value;
            OnPropertyChanged();
        }
    }

    private FocusRegion _focusRegion = FocusRegion.Category;

    /// <summary>要素が左メニュー(CategoryListView)の中にあるか。</summary>
    private bool IsInsideCategoryList(DependencyObject? element)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, CategoryListView)) return true;
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private bool _isCategoryRegionFocused = true;
    /// <summary>領域「Category」(左メニュー)が現在のゲームパッド操作対象かどうか。枠線の表示に使う。</summary>
    public bool IsCategoryRegionFocused
    {
        get => _isCategoryRegionFocused;
        private set { if (_isCategoryRegionFocused == value) return; _isCategoryRegionFocused = value; OnPropertyChanged(); }
    }

    private bool _isCoreListRegionFocused;
    /// <summary>領域「CoreList」(コア管理パネルの一覧)が現在のゲームパッド操作対象かどうか。</summary>
    public bool IsCoreListRegionFocused
    {
        get => _isCoreListRegionFocused;
        private set { if (_isCoreListRegionFocused == value) return; _isCoreListRegionFocused = value; OnPropertyChanged(); }
    }

    private bool _isCoreOptionsCoreListRegionFocused;
    /// <summary>領域「CoreOptionsCoreList」(コア毎の設定パネル左側の対象コア一覧)が
    /// 現在のゲームパッド操作対象かどうか。</summary>
    public bool IsCoreOptionsCoreListRegionFocused
    {
        get => _isCoreOptionsCoreListRegionFocused;
        private set { if (_isCoreOptionsCoreListRegionFocused == value) return; _isCoreOptionsCoreListRegionFocused = value; OnPropertyChanged(); }
    }

    private bool _isCoreOptionsListRegionFocused;
    /// <summary>領域「CoreOptionsList」(コア毎の設定パネル右側のオプション一覧)が
    /// 現在のゲームパッド操作対象かどうか。</summary>
    public bool IsCoreOptionsListRegionFocused
    {
        get => _isCoreOptionsListRegionFocused;
        private set { if (_isCoreOptionsListRegionFocused == value) return; _isCoreOptionsListRegionFocused = value; OnPropertyChanged(); }
    }

    private bool _isFolderListRegionFocused;
    /// <summary>領域「FolderList」(ゲームフォルダ管理パネルの一覧)が現在のゲームパッド操作対象かどうか。</summary>
    public bool IsFolderListRegionFocused
    {
        get => _isFolderListRegionFocused;
        private set { if (_isFolderListRegionFocused == value) return; _isFolderListRegionFocused = value; OnPropertyChanged(); }
    }

    /// <summary>ランチャー編集オーバーレイの多重起動防止。</summary>
    private bool _isEditingLauncher;

    /// <summary>新規フォルダ追加フローの多重起動防止(ヘッダーボタンと一覧カードの両方から呼べるため)。</summary>
    private bool _isAddingFolder;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public SettingsPage()
    {
        this.InitializeComponent();
        // 左メニュー(カテゴリ)にフォーカスがある間は、右のパネルの上でのホイールは、パネルを普通にスクロールする
        // (項目が多いパネルを、フォーカスを右へ移さなくても下まで見られるように)。右にフォーカスがあるときは十字キー上下と同じ。
        EmulatorLauncher.Controls.LauncherWheel.AttachAll(this, element =>
            _focusRegion == FocusRegion.Category && !IsInsideCategoryList(element));

        // ConsoleSelectPage同様、Frameのバックスタックでインスタンスを使い回す
        // (設定画面を開き直すたびに一覧を作り直さなくてよいようにする)。
        this.NavigationCacheMode = NavigationCacheMode.Required;

        // 「コア毎の設定」パネルは、コアを選ぶまでは案内メッセージだけを表示する。
        ShowNoCoreSelectedForOptions();

        SetFocusRegion(FocusRegion.Category);

        // キー操作はMainWindowが読んで処理するため、ListViewの標準の矢印/Enter処理(二重移動・フォーカス離脱)は止める。
        this.PreviewKeyDown += Page_PreviewKeyDown;
    }

    private static void Page_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox or PasswordBox) return;
        if (e.Key is Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down
            or Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right
            or Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
        {
            e.Handled = true;
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // OnNavigatedTo(戻り値void)から呼ぶため「発火して待たない」形にする
        // (WinUI3のUIスレッドにはDispatcherQueue経由のSynchronizationContextがあるため、
        // await後もUIスレッドへ自動的に戻ってくる。GameSelectPage.LoadGamesForConsoleAsyncと同じ考え方)。
        _ = LoadCoresAsync();
        _ = LoadFoldersAsync();
        LoadLaunchers();
        LoadAppSettingsUi();
    }

}