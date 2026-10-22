using System;
using System.Collections.Generic;
using EmulatorLauncher.Emulation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace EmulatorLauncher.Views;

/// <summary>アプリ設定の「テーマ」に応じて、最初に出すコンソール選択画面のページ型を返す。</summary>
public static class AppThemeRouter
{
    /// <summary>いま表示している(最後にページ型を返した)テーマ。設定を閉じるとき、変わったかの判定に使う。</summary>
    public static string Applied { get; private set; } = AppThemes.Standard;

    // ---- ダークテーマの配色(コレクション作成/ゲームコントローラー/設定) ----
    //
    // (コンソール選択[ダーク]・設定・ゲームコントローラー・ゲーム選択・コレクションのゲーム選択)
    // これらの画面はアプリ共通のBrush(App.xaml→Styles/Brushes.xaml)を使っている。Brushはアプリ全体で
    // 共有された1つのインスタンスなので、色(Color)を書き換えれば表示中の画面にもそのまま反映される。
    // ダークテーマで「ダーク用の画面」を表示している間だけ暗い色にして、それ以外(ゲーム選択など)へ
    // 移る時に元の色へ戻す(ゲーム選択は標準の見た目のまま、という方針)。

    private static readonly (string Key, string Dark)[] DarkBrushes =
    {
        ("AppBackgroundBrush", "#0D0E11"),
        ("SettingsPageBackgroundBrush", "#101216"),
        ("SettingsContentBackgroundBrush", "#14161B"),
        ("AppSurfaceBrush", "#1C1F26"),
        ("PanelBackgroundBrush", "#181B21"),
        ("AppTextPrimaryBrush", "#E8ECF4"),
        ("AppTextSecondaryBrush", "#8E97A8"),
        ("SwitchSelectedBlueBrush", "#7C9BFF"),
    };

    private static readonly string[] DarkHintBarStops = { "#1E2128", "#181B21", "#12141A" };

    private static readonly Dictionary<string, Windows.UI.Color> LightColors = new();
    private static Windows.UI.Color[]? _lightHintStops;

    /// <summary>いま、暗い配色にしているか。画面の色を決めるコンバーターなども参照する。</summary>
    public static bool IsDarkActive { get; private set; }

    private static bool UsesDarkLook(Type pageType)
        => pageType == typeof(ConsoleSelectDarkPage) || pageType == typeof(SettingsPage) || pageType == typeof(ControllerSettingsPage)
        || pageType == typeof(GameSelectPage) || pageType == typeof(CollectionGameSelectPage);

    private static Windows.UI.Color ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        return Windows.UI.Color.FromArgb(0xFF,
            System.Convert.ToByte(hex.Substring(0, 2), 16), System.Convert.ToByte(hex.Substring(2, 2), 16), System.Convert.ToByte(hex.Substring(4, 2), 16));
    }

    /// <summary>これから表示するページに合わせて、共通Brushを暗い色/元の色へ切り替える。</summary>
    public static void ApplyForPage(Type pageType)
    {
        _pageDark = AppPreferencesStore.Load().Theme == AppThemes.Dark && UsesDarkLook(pageType);
        Refresh();
    }

    private static bool _pageDark;
    private static bool _forcedDark;

    /// <summary>ページの設定に関わらず、暗い配色を強制する(ゲーム中のシステム設定パネル用)。falseで元へ戻す。</summary>
    public static void SetForcedDark(bool on)
    {
        _forcedDark = on;
        Refresh();
    }

    private static void Refresh()
    {
        bool dark = _pageDark || _forcedDark;
        if (dark == IsDarkActive) return;
        IsDarkActive = dark;

        try
        {
            ResourceDictionary resources = Application.Current.Resources;
            foreach ((string key, string darkHex) in DarkBrushes)
            {
                if (resources[key] is not SolidColorBrush brush) continue;
                if (!LightColors.ContainsKey(key)) LightColors[key] = brush.Color;
                brush.Color = dark ? ParseHex(darkHex) : LightColors[key];
            }

            if (resources["HintBarBackgroundBrush"] is LinearGradientBrush bar && bar.GradientStops.Count >= 3)
            {
                _lightHintStops ??= new[] { bar.GradientStops[0].Color, bar.GradientStops[1].Color, bar.GradientStops[2].Color };
                for (int i = 0; i < 3; i++)
                    bar.GradientStops[i].Color = dark ? ParseHex(DarkHintBarStops[i]) : _lightHintStops[i];
            }
        }
        catch { /* 配色が変えられなくても、標準の見た目のまま動作する */ }
    }

    public static Type ConsoleSelectPageType
    {
        get
        {
            string theme = AppPreferencesStore.Load().Theme == AppThemes.Dark ? AppThemes.Dark : AppThemes.Standard;
            Applied = theme;
            return theme == AppThemes.Dark ? typeof(ConsoleSelectDarkPage) : typeof(ConsoleSelectPage);
        }
    }

    /// <summary>設定のテーマが、いま表示しているものと違うか。</summary>
    public static bool ThemeChanged
        => (AppPreferencesStore.Load().Theme == AppThemes.Dark ? AppThemes.Dark : AppThemes.Standard) != Applied;
}
