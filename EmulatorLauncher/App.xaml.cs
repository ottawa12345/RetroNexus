using System;
using EmulatorLauncher.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace EmulatorLauncher;

/// <summary>
/// アプリケーションのエントリポイント。
/// Unpackaged(MSIXなし)構成の WinUI3 デスクトップアプリでは、
/// XAML側の自動生成Mainを無効化し(csproj の DISABLE_XAML_GENERATED_MAIN)、
/// ここで手動で Application.Start を呼び出す。
///
/// SDL3のゲームパッドサブシステムは、コンソール選択画面からゲーム実行中の
/// 中断メニューまでアプリ全体で共有するため、ここ(プロセスの起点)で
/// 一度だけ初期化し、アプリ終了時に解放する。
///
/// ROMフォルダの登録はここでは行わない。設定画面の「ゲームフォルダ管理」
/// (Views.SettingsPage)から、ユーザーが自由にフォルダを追加/削除する
/// (以前はここで固定パスを1件強制登録していたが、その仕様は廃止した)。
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        // XAMLが書体を解決する前に同梱フォントをOSへ登録する(失敗しても既定フォントで続行)。
        FontLoader.Register();
        // DirectWriteのシステムフォント一覧を、更新確認つきで取り直す。診断ログが目的に見えるが、これが無いと、登録済みのフォントがXAMLに見えず、
        // 別のPCで同梱フォントが適用されなかった(2026-10-20に確認)。InitializeComponentより前に呼ぶこと。
        FontLoader.CheckDirectWrite();
        this.InitializeComponent();
        this.UnhandledException += OnUnhandledException;
    }

    /// <summary>アプリ全体の書体リソース(既定の書体と、TextBlockの既定スタイル)を差し替える。</summary>
    private void ApplyAppFont(Microsoft.UI.Xaml.Media.FontFamily family)
    {
        Resources["ContentControlThemeFontFamily"] = family;
        var textStyle = new Style(typeof(Microsoft.UI.Xaml.Controls.TextBlock));
        textStyle.Setters.Add(new Setter(Microsoft.UI.Xaml.Controls.TextBlock.FontFamilyProperty, family));
        Resources[typeof(Microsoft.UI.Xaml.Controls.TextBlock)] = textStyle;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 動作ログ(data\log\app_*.log)を開き、バージョンやOSの情報を先頭に書く。
        string appVersion = typeof(App).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "(不明)";
        int plus = appVersion.IndexOf('+');
        EmulatorLauncher.Services.AppLog.Start(plus >= 0 ? appVersion[..plus] : appVersion);

        // 同梱フォントの登録結果を記録する(別のPCで書体が効かないときの手がかり)。
        foreach (string line in FontLoader.Diagnostics)
            EmulatorLauncher.Services.AppLog.Info("Font", line);
        try
        {
            // アプリ設定「フォント」(システム標準を選んでいると、同梱フォントは使わない)と、登録済みのフォントの状態。
            bool useSystem = EmulatorLauncher.Emulation.AppPreferencesStore.Load().UseSystemFont;
            EmulatorLauncher.Services.AppLog.Info("Font", $"アプリ設定のフォント={(useSystem ? "システム標準(同梱フォントは使わない)" : "同梱フォント")}");
            string userFonts = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
            foreach (string name in new[] { "MPLUSRounded1c-Regular.ttf", "MPLUSRounded1c-Bold.ttf" })
            {
                string path = System.IO.Path.Combine(userFonts, name);
                string size = System.IO.File.Exists(path) ? new System.IO.FileInfo(path).Length.ToString() : "なし";
                string bundled = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", name);
                string bundledSize = System.IO.File.Exists(bundled) ? new System.IO.FileInfo(bundled).Length.ToString() : "なし";
                EmulatorLauncher.Services.AppLog.Info("Font", $"{name}: ユーザーフォント={size}バイト, 同梱={bundledSize}バイト");
            }
        }
        catch (Exception ex)
        {
            EmulatorLauncher.Services.AppLog.Warn("Font", "フォント状態の確認に失敗しました", ex);
        }
        if (FontLoader.InstalledThisRun)
        {
            EmulatorLauncher.Services.AppLog.Info("Font", "今回の起動でフォントを初めて登録しました。描画に反映されるのは次の起動からのため、一度だけ自動で起動し直します。");
            // 起動中に登録したフォントは、描画側(DirectWrite)にはまだ見えず既定の書体で表示される。
            // 初回だけ自分自身を起動し直して、登録済みの状態から始める(無限に繰り返さないよう引数で印を付ける)。
            try
            {
                string? exe = Environment.ProcessPath;
                bool already = Array.IndexOf(Environment.GetCommandLineArgs(), "--font-restarted") >= 0;
                if (!already && !string.IsNullOrEmpty(exe))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--font-restarted") { UseShellExecute = false });
                    EmulatorLauncher.Services.AppLog.Info("Font", "起動し直しました。");
                    Environment.Exit(0);
                    return;
                }
            }
            catch (Exception ex)
            {
                EmulatorLauncher.Services.AppLog.Warn("Font", "起動し直しに失敗しました。そのまま続けます。", ex);
            }
        }

        // バックアップからの復元の予約があれば、ほかの何かがデータを開く前に、ここで適用する。
        EmulatorLauncher.Services.BackupService.ApplyPendingRestore();

        // 画像フォルダ(機種アイコン・ロゴ・コレクション用アイコンの置き場所)が無ければ作る。
        // 利用者が自分で画像を置けるよう、初回起動のときに用意しておく。
        foreach (string sub in new[] { "Console", "Logo", "System" })
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(AppContext.BaseDirectory, "Images", sub));
            }
            catch (Exception ex)
            {
                EmulatorLauncher.Services.AppLog.Warn("App", $"画像フォルダを作成できませんでした: Images\\{sub}", ex);
            }
        }

        // ゲームパッド(SDL_INIT_GAMEPAD)はプロセス全体でここで一度だけ初期化する。
        // EmulationHost はゲーム実行中の映像/音声サブシステムだけを個別に
        // 初期化・解放し、このゲームパッドサブシステムには触れない。
        SdlGamepadInit.InitializeOnce();

        // アプリ設定「仮想解像度」: 各画面の固定キャンバスの大きさを差し替える(画面を作る前に行う)。
        try
        {
            var vr = EmulatorLauncher.Emulation.VirtualResolutions.Get(EmulatorLauncher.Emulation.AppPreferencesStore.Load().VirtualResolution);
            Resources["CanvasWidth"] = vr.Width;
            Resources["CanvasHeight"] = vr.Height;
        }
        catch
        {
            // 失敗しても、既定(1920x1080)のまま起動できる。
        }

        // アプリ設定「フォント」がシステム標準なら、アプリ全体の書体リソースを差し替える
        // (画面を作る前でないと反映されないため、MainWindowの生成より前に行う)。
        // システム標準でなくても、登録した同梱フォントをDirectWriteが見つけられなかったときは、
        // アプリのフォントフォルダから直接読み込む指定(ms-appx)へ切り替える(それでも読めなければ、従来どおり標準のフォントになる)。
        try
        {
            if (EmulatorLauncher.Emulation.AppPreferencesStore.Load().UseSystemFont)
            {
                ApplyAppFont(new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI Variable, Segoe UI, Yu Gothic UI, Meiryo UI"));
            }
            else if (FontLoader.SystemCollectionMissing)
            {
                ApplyAppFont(new Microsoft.UI.Xaml.Media.FontFamily(
                    $"ms-appx:///Assets/Fonts/#{FontLoader.FamilyName}, Segoe UI Variable, Segoe UI, Yu Gothic UI"));
                EmulatorLauncher.Services.AppLog.Info("Font", "同梱フォントがシステムに見つからないため、アプリのフォントフォルダから直接読み込む指定に切り替えました。");
            }
        }
        catch
        {
            // フォントの差し替えに失敗しても、既定のフォントのまま起動できる。
        }

        try
        {
            _window = new MainWindow();
            _window.Activate();

            // アプリ設定「起動時にフルスクリーン」が有効なら、フルスクリーンで始める。
            try
            {
                if (EmulatorLauncher.Emulation.AppPreferencesStore.Load().StartFullScreen
                    && _window is MainWindow mw && !mw.IsFullScreen)
                {
                    mw.ToggleFullScreen();
                }
            }
            catch (Exception ex)
            {
                EmulatorLauncher.Services.AppLog.Error("App", "起動時のフルスクリーン化に失敗しました", ex);
            }
        }
        catch (Exception ex)
        {
            // Application.UnhandledExceptionが発火する前(MainWindowのコンストラクタ内、
            // つまりConsoleSelectPageのInitializeComponent実行中など)に落ちるケースを
            // 捕まえるための診断用ログ。通常のOnUnhandledExceptionでは追えない。
            EmulatorLauncher.Services.AppLog.Error("App", "起動中に例外が発生しました", ex);
            try
            {
                string logPath = System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log");
                string content = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] OnLaunched中に例外{Environment.NewLine}"
                    + $"{ex}{Environment.NewLine}{Environment.NewLine}";
                AppendCrashLog(logPath, content);
            }
            catch
            {
                // ログ書き込み自体の失敗は無視する。
            }

            throw;
        }
    }

    /// <summary>crash.logへ1件追記する。増え続けないよう、新しい10件だけを残す(各件は「[日時]」の行で始まる)。</summary>
    private static void AppendCrashLog(string logPath, string entry)
    {
        const int keep = 10;
        string existing = System.IO.File.Exists(logPath) ? System.IO.File.ReadAllText(logPath) : string.Empty;
        var entries = new System.Collections.Generic.List<string>(
            System.Text.RegularExpressions.Regex.Split(existing, @"(?m)^(?=\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\])"));
        entries.RemoveAll(string.IsNullOrWhiteSpace);
        entries.Add(entry);
        if (entries.Count > keep) entries.RemoveRange(0, entries.Count - keep);
        System.IO.File.WriteAllText(logPath, string.Concat(entries));
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // WinUI3の未処理例外は、Visual Studioの出力ウィンドウだけでは型名しか分からず
        // メッセージ・スタックトレースが読めないことがあるため、実行ファイルと同じ場所に
        // crash.logとして詳細を書き出しておく(Exception.ToString()はメッセージ・型・
        // スタックトレース・内部例外まで含めて出力してくれる)。
        EmulatorLauncher.Services.AppLog.Error("App", "未処理の例外でアプリが停止します: " + e.Message, e.Exception);
        try
        {
            string logPath = System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log");
            string content = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]{Environment.NewLine}"
                + $"Message: {e.Message}{Environment.NewLine}"
                + $"{e.Exception}{Environment.NewLine}{Environment.NewLine}";
            AppendCrashLog(logPath, content);
        }
        catch
        {
            // ログ書き込み自体が失敗しても、後続のシャットダウン処理は必ず行う。
        }

        // 予期しない例外でプロセスが落ちる場合でも、SDLのゲームパッドサブシステムを
        // きちんと解放しておく(必須ではないが、後始末の一貫性のため)。
        SdlGamepadInit.Shutdown();
    }

    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);

            new App();
        });

        // Application.Start はメッセージループが終了するまでブロックするため、
        // ここに到達するのはアプリ終了時。ゲームパッドサブシステムを解放する。
        SdlGamepadInit.Shutdown();
    }
}
