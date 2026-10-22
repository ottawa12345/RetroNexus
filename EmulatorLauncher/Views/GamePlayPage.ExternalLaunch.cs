using EmulatorLauncher.RomLibrary;
using EmulatorLauncher.Emulation;
using EmulatorLauncher.Interop;
using EmulatorLauncher.Models;
using EmulatorLauncher.Input;
using EmulatorLauncher.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Storage;

namespace EmulatorLauncher.Views;

// GamePlayPage の一部(partial class)。外部コマンド(Launchers.jsonのexePath/args)での起動。
// 動作は変えず、巨大だったGamePlayPage.xaml.csを役割ごとに分けただけ。
public sealed partial class GamePlayPage
{
    // ==================================================================
    // 外部コマンド起動(Launchers.jsonのexePath/args/workingPath)
    // ==================================================================
    // 外部アプリの実行中は、ランチャー側は
    //   ・プロセスの終了監視(ExternalAppSession.Exited)
    //   ・ZL+ZRの同時長押し監視(SdlGamepadService.BeginComboWatch。通常のメニュー操作は無効)
    // だけを行う。どちらか先に起きた方でセッションを終え、ゲームパッド操作をメニューへ戻して
    // ゲーム選択画面へ戻る。ZL+ZR長押しの場合は先にプロセスツリーを強制終了する。
    // SdlGamepadServiceは止めない(EmulationHostが無いのでSDLイベントポンプの競合が無く、
    // 止めるとコンボ監視ができないため)。

    private void StartExternalApp(LauncherInfo launcher, string romPath)
    {
        string exePath = SampleData.ResolveExePath(launcher.ExePath);
        string arguments = SampleData.BuildExternalArguments(launcher.Args, romPath);
        string? workingDir = SampleData.ResolveWorkingPath(launcher.WorkingPath, exePath);

        EmulatorLauncher.Services.AppLog.Info("Play", $"外部アプリ起動: {launcher.Name} / exe={exePath} / args={arguments}");
        Debug.WriteLine($"[GamePlayPage] 外部アプリ起動: exe=\"{exePath}\" args={arguments} cwd={workingDir ?? "(既定)"}");

        // libretro起動用の下部ガイドバー(操作説明)は外部アプリ中は不要なので詰める。
        BottomBarRowDefinition.Height = new GridLength(0);

        var session = new EmulatorLauncher.Services.ExternalAppSession();
        session.Exited += exitCode => DispatcherQueue.TryEnqueue(() => OnExternalAppExited(session, exitCode));

        _externalSession = session;
        _externalLauncher = launcher;

        try
        {
            _externalStartedUtc = DateTime.UtcNow;
            session.Start(exePath, arguments, workingDir);
            BeginPlayTracking(external: true);
        }
        catch (Exception ex)
        {
            CleanupExternalApp();
            EmulatorLauncher.Services.AppLog.Warn("Play", $"外部アプリを起動できませんでした: {launcher.Name} / exe={exePath}", ex);
            ShowExternalStatus($"{launcher.Name} を起動できません。\n{exePath}\n{ex.Message}\n\n「ランチャー管理」で実行ファイルの場所を設定してください。\nBボタンで戻ります。");
            return;
        }

        _externalProgressHandler = progress => DispatcherQueue.TryEnqueue(() =>
        {
            if (_externalSession is null) return;
            ShowExternalStatus(progress > 0
                ? $"L2 + R2 を長押し中… {(int)(progress * 100)}%"
                : BuildExternalRunningText());
        });

        if (AppServices.Gamepad is { } gamepad)
        {
            gamepad.ComboHoldProgress += _externalProgressHandler;
            gamepad.BeginComboWatch(GamepadButton.LeftTrigger, GamepadButton.RightTrigger, ExternalKillHold, OnExternalKillRequested);
        }

        ShowExternalStatus(BuildExternalRunningText());
    }

    private string BuildExternalRunningText()
        => $"{_externalLauncher?.Name} を実行中です\n\nL2 + R2 を長押しすると強制終了してランチャーに戻ります";

    private void ShowExternalStatus(string text)
    {
        // 案内文は文字だけを出す。カバー画像(NO IMAGEの枠など)が残っていると文字と重なって読みにくい。
        LaunchCoverImage.Visibility = Visibility.Collapsed;
        ExternalStatusText.Text = text;
        ExternalStatusText.Visibility = Visibility.Visible;
    }

    /// <summary>ZL+ZR長押しが成立した(UIスレッド)。プロセスを強制終了する。終了後の後始末はExited側で行う。</summary>
    private void OnExternalKillRequested()
    {
        ShowExternalStatus("強制終了しています…");
        _externalSession?.Kill();
    }

    /// <summary>外部アプリが終了した(UIスレッド)。ゲームパッド操作を戻してゲーム選択画面へ戻る。</summary>
    private void OnExternalAppExited(EmulatorLauncher.Services.ExternalAppSession session, int? exitCode)
    {
        if (!ReferenceEquals(_externalSession, session)) return;

        string name = _externalLauncher?.Name ?? "外部アプリ";
        EmulatorLauncher.Services.AppLog.Info("Play", $"外部アプリが終了しました: {name} / 終了コード={(exitCode?.ToString() ?? "不明")}");
        bool quickFail = exitCode is not (null or 0)
                         && DateTime.UtcNow - _externalStartedUtc < ExternalQuickFailWindow;

        // すぐに失敗した起動はプレイとして記録しない。
        if (quickFail) _playRecorded = true;
        else FinishPlaySession();

        CleanupExternalApp();

        // 外部アプリが前面を占有していた可能性があるので、ランチャーを前面へ戻す。
        try { AppServices.MainWindow?.Activate(); } catch { }

        if (quickFail)
        {
            ShowExternalStatus($"{name} がすぐに終了しました(終了コード {exitCode})。\nLaunchers.jsonの exePath / args を確認してください。\n\nBボタンで戻ります。");
            return;
        }

        if (Frame.CanGoBack)
        {
            GoBackWithCoverAnimation();
        }
    }

    /// <summary>外部アプリ関連の購読・監視を解除する(プロセスの強制終了はしない)。冪等。</summary>
    private void CleanupExternalApp()
    {
        if (AppServices.Gamepad is { } gamepad)
        {
            if (_externalProgressHandler is not null) gamepad.ComboHoldProgress -= _externalProgressHandler;
            gamepad.EndComboWatch();
        }

        _externalProgressHandler = null;

        var session = _externalSession;
        _externalSession = null;
        session?.Dispose();
    }

    /// <summary>コア/ROMの読み込み失敗など、回復不能なエラー。プレイを続行できない。</summary>
    /// <summary>「再起動が必要」な項目の、いまの実効値(キー→値)。キー順に並べて、比べやすくしてある。</summary>
    private Dictionary<string, string> CaptureRestartValues()
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (_currentCatalogCore is not null)
        {
            foreach (LibretroCoreOptionInfo option in _currentCatalogCore.Options)
            {
                if (option.RestartRequired) values[option.Key] = GetEffectiveOptionValue(option);
            }
        }
        return new Dictionary<string, string>(values);
    }

    /// <summary>
    /// 今の場面を再起動用ステートへ保存し、コアを作り直して、同じ場面から再開する。
    /// 保存に失敗したときは再起動せず、そのまま中断メニューに残る(次回の起動から反映される)。
    /// </summary>
    private async Task RestartCoreWithStateAsync()
    {
        EmulationHost? host = _emuHost;
        if (_restartInProgress || host is null || string.IsNullOrEmpty(_romPathForRestart) && !(_game?.IsBiosBoot ?? false)) return;
        _restartInProgress = true;

        try
        {
            // 1. 今の状態を保存(中断メニューで一時停止中でも、エミュレーションスレッドが処理する)。
            var saved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnSaved(object? s, bool ok) => saved.TrySetResult(ok);
            host.RestartStateSaved += OnSaved;
            host.RequestSaveRestartState();
            Task finished = await Task.WhenAny(saved.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            host.RestartStateSaved -= OnSaved;
            if (finished != saved.Task || !saved.Task.Result)
            {
                Debug.WriteLine("[GamePlayPage] 再起動用ステートの保存に失敗したため、再起動を見送りました");
                return;
            }

            // 2. 中断メニューを閉じ、いまのコアを止める(SDLのイベント処理は、新しいコアがすぐ引き継ぐので
            //    メニュー用ゲームパッドは再開しない)。プレイ時間の記録は続ける。
            CloseMenuImmediate();
            host.StatusChanged -= OnEmuStatusChanged;
            host.CoreReady -= OnEmuCoreReady;
            host.GeometryChanged -= OnEmuGeometryChanged;
            host.MenuToggleRequested -= OnEmuMenuToggleRequested;
            host.SystemPanelRequested -= OnEmuSystemPanelRequested;
            host.RewindToggleRequested -= OnEmuRewindToggleRequested;
            host.RewindBecameUnavailable -= OnEmuRewindBecameUnavailable;
            host.FastForwardMultiplierChanged -= OnEmuFastForwardMultiplierChanged;
            host.PausedMenuButtonPressed -= OnPausedMenuButtonPressed;
            host.PausedMenuButtonLongPressed -= OnPausedMenuButtonLongPressed;
            host.PausedMenuButtonRepeated -= OnPausedMenuButtonPressed;
            await Task.Run(host.Stop);
            _emuHost = null;

            // 3. 新しい値で、同じゲームをもう一度起動し、保存した場面から再開する。
            StartHostCore(_corePath, _romPathForRestart, resumeFromRestartState: true);
        }
        finally
        {
            _restartInProgress = false;
        }
    }

    /// <summary>
    /// コアや起動処理から届いた生のエラーメッセージに、原因の見当と対処を添える。
    /// すでに「対処:」を含むメッセージ(起動前の確認で作ったもの)はそのまま返す。
    /// </summary>
    private static string AddFailureHint(string message)
    {
        if (message.Contains("対処:", StringComparison.Ordinal)) return message;

        string? hint = null;
        if (message.Contains("retro_load_game", StringComparison.Ordinal))
        {
            hint = "考えられる原因: BIOSファイルが足りない(コアの system フォルダに必要なBIOSを置く)、ROMが壊れている、このコアに対応していない形式、のいずれかです。別のコアやランチャーで試すこともできます。";
        }
        else if (message.Contains("BIOS", StringComparison.OrdinalIgnoreCase))
        {
            hint = "対処: コアの system フォルダに、必要なBIOSファイルを置いてください。";
        }
        else if (message.Contains("DLL", StringComparison.OrdinalIgnoreCase)
                 || message.Contains("Win32", StringComparison.OrdinalIgnoreCase)
                 || message.Contains("0x8007007E", StringComparison.OrdinalIgnoreCase)
                 || message.Contains("0x8007000B", StringComparison.OrdinalIgnoreCase)
                 || message.Contains("モジュールが見つかりません", StringComparison.Ordinal))
        {
            hint = "対処: コアのファイルが壊れているか、必要なライブラリが足りない可能性があります。「コア管理」でコアを入れ直してください。";
        }
        else if (message.Contains("アーカイブ内に有効なROM", StringComparison.Ordinal))
        {
            hint = "対処: zip の中に、このコンソールのROMが入っているか確認してください。";
        }
        else if (message.Contains("ROMファイルが見つかりません", StringComparison.Ordinal))
        {
            hint = "対処: ドライブの接続を確認するか、「ゲームフォルダの更新」で一覧を整理してください。";
        }
        else if (message.Contains("アクセスが拒否", StringComparison.Ordinal)
                 || message.Contains("Access to the path", StringComparison.OrdinalIgnoreCase))
        {
            hint = "対処: ファイルやフォルダへのアクセス権限、または他のアプリがファイルを使用中でないか確認してください。";
        }

        return hint is null ? message : message + "\n\n" + hint;
    }

    private void HandleFatalError(string message)
    {
        EmulatorLauncher.Services.AppLog.Error("Play", "起動できませんでした: " + message);
        message = AddFailureHint(message);
        TeardownEmulation();

        // 起動に失敗したとき、黒い画面のまま何も表示されず操作もできなくなるのを防ぐ。
        // 理由を画面中央に出し、Bボタンでゲーム選択へ戻れる(OnGamepadButton。
        // TeardownEmulationでメニュー用のゲームパッドが再開済み)。
        LaunchCoverImage.Visibility = Visibility.Collapsed;
        ExternalStatusText.Text = message + "\n\nBボタンでもどる";
        ExternalStatusText.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// EmulationHostの描画用ネイティブウィンドウ(所有ウィンドウ)は、生成された瞬間には
    /// まだUI側から一度もUpdateBounds()を受け取っていない可能性がある(生成直後は
    /// 座標(0,0)・サイズ0で作られ、SDLイベントループの次の周回でApplyPendingBoundsIfAny
    /// により正しい位置へ補正される設計だが、コア初期化の重い処理と競合すると体感できる
    /// 時間だけ画面外/画面隅に表示されたままに見えることがある)。
    /// CoreReady(コアのロードとゲームの起動が完了した通知)のタイミングで念のため
    /// もう一度レイアウトを計算し直し、確実に正しい位置・サイズへ揃える。
    /// </summary>
    private void OnEmuCoreReady(object? sender, EventArgs e)
    {
        // コアオプションの予約はStartEmulationAsync側でStart()より前に
        // EmulationHost.SetInitialCoreVariableへ渡し済み(こちらのCoreReadyのタイミングでは
        // 既にretro_load_game/retro_get_system_av_info/HWレンダリング用FBO確保まで完了して
        // しまっているため、ここでRequestSetCoreVariableしても手遅れだった。詳細は
        // EmulationHost.SetInitialCoreVariable/CoreEnvironmentSupport.SetInitialOverrideの
        // ドキュメントコメント参照)。

        DispatcherQueue.TryEnqueue(() =>
        {
            ScheduleLayoutRecalc();

            // HWレンダリングのコア(N64等)は巻き戻し機能自体が無効(EmulationHost側の
            // 既存の制約。負荷が高いため)。操作説明にも出さず、そもそも巻き戻し画面へ
            // 遷移させない(ShowRewind側にも同じ判定を入れて二重に防いでいる)。
            if (_emuHost is not null)
            {
                //RewindHintPanel.Visibility = _emuHost.IsHwRendered ? Visibility.Collapsed : Visibility.Visible;
                UpdateMenuHintLabel(); // HWレンダリングなら「(長押しで巻き戻し)」を外す
                StartGuideIntro();     // 操作説明がOFFのときだけ、始めの数秒だけ見せる
            }
        });
    }

    /// <summary>
    /// コアが実行中にジオメトリ(アスペクト比)を再申告してきたときの通知。
    /// GetDisplayAspectRatio()がCoreReportedAspectRatioにフォールバックする設定
    /// (ScreenLayoutMode.Auto、または LauncherDefault だが _launcherDefaultAspectRatio が
    /// 未解決=null。ニンテンドーDS等、Launchers.jsonのDefaultAspectRatioが"Auto"の機種はここに
    /// 該当する)のときだけ、実際に表示アスペクト比が変わるためレイアウトの再計算が必要になる。
    /// これを見落とすと、DSコアの画面レイアウト(Top/Bottom等)を実行中に切り替えたときに
    /// ウィンドウサイズが古いままになり、いったんエミュレーションを止めて開き直すまで直らない。
    /// </summary>
    private void OnEmuGeometryChanged(object? sender, EventArgs e)
    {
        Debug.WriteLine(
            $"[GamePlayPage] OnEmuGeometryChanged 受信: ScreenLayout={_settings.ScreenLayout} "
            + $"_launcherDefaultAspectRatio={_launcherDefaultAspectRatio?.ToString("F4") ?? "null"} "
            + $"CoreReportedAspectRatio={_emuHost?.CoreReportedAspectRatio.ToString("F4") ?? "null"}");

        DispatcherQueue.TryEnqueue(() =>
        {
            bool usesCoreReportedAspect =
                _settings.ScreenLayout == ScreenLayoutMode.Auto ||
                (_settings.ScreenLayout == ScreenLayoutMode.LauncherDefault && _launcherDefaultAspectRatio is null);

            Debug.WriteLine($"[GamePlayPage] OnEmuGeometryChanged (UIスレッド): usesCoreReportedAspect={usesCoreReportedAspect} => " +
                (usesCoreReportedAspect ? "ScheduleLayoutRecalc()を呼びます" : "何もしません(この設定ではコア申告値を使わないため)"));

            if (usesCoreReportedAspect)
                ScheduleLayoutRecalc();
        });
    }

    private void OnEmuStatusChanged(object? sender, string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            //StatusText.Text = message;

            // EmulationHostは起動失敗やコア内部エラーを例外として投げず、
            // "エラー: ..." という StatusChanged メッセージとスレッドの自然終了だけで
            // 表現するため、ここで検知して後始末する(EmulationHost.RunLoop 参照)。
            if (message.StartsWith("エラー", StringComparison.Ordinal))
            {
                HandleFatalError(message);
            }
        });
    }
}
