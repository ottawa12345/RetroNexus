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

// GamePlayPage の一部(partial class)。レイアウト(EmuHostのサイズ・位置をSDL描画用のネイティブ子ウィンドウへ反映する)。
// 動作は変えず、巨大だったGamePlayPage.xaml.csを役割ごとに分けただけ。
public sealed partial class GamePlayPage
{
    // ==================================================================
    // レイアウト: EmuHost(XAML)のサイズ・画面上の位置を、SDL描画用の
    // ネイティブ子ウィンドウへ反映する(emulator側のMainWindowと同じアルゴリズム)。
    // ==================================================================

    private void OnWindowBoundsChanged(object? sender, EventArgs e) => ScheduleLayoutRecalc();

    /// <summary>
    /// SDL描画用のネイティブウィンドウは"所有された(owned)"トップレベルウィンドウであり、
    /// Win32の仕様上、オーナー(MainWindow)を最小化しても自動的には隠れない(逆に
    /// タスクバーへ行かず画面上に浮いたまま残ってしまう)。そのため、最小化されたら
    /// 中断メニューを表示してゲームを一時停止させる(ShowMenu()が内部で
    /// EmulationHost.Pause()を呼び、ネイティブウィンドウも合わせて非表示になる)。
    ///
    /// ウィンドウを元に戻しても、意図せずゲームが再開してしまわないよう、
    /// あえて自動的には再開しない(ユーザーが中断メニューで明示的に
    /// 「つづける」を選ぶまで一時停止のままにする)。
    ///
    /// 復元(isMinimized=false)の通知を受け取った瞬間にも念のため
    /// ReassertNativeWindowHiddenIfPaused() を呼んでおく。Windows自身が
    /// 復元処理の一環でネイティブウィンドウを再表示してしまう問題
    /// (ReassertNativeWindowHiddenIfPausedのコメント参照)に対し、150ms
    /// ポーリングによる保険だけに頼るより、通知を受け取ったこの瞬間に
    /// 即座に対応した方が、再表示されてから隠れ直るまでの一瞬の"見えて
    /// しまう時間"をより短くできる(ポーリングはそれでも取りこぼした
    /// 場合の最終的な保険として引き続き働く)。
    /// </summary>
    private void OnMainWindowMinimizedStateChanged(bool isMinimized)
    {
        if (_emuHost is null) return;

        if (!isMinimized)
        {
            ReassertNativeWindowHiddenIfPaused();
            return;
        }

        if (_pausedOverlay != PausedOverlay.None) return;

        ShowMenu();
    }

    /// <summary>
    /// SDL描画用のネイティブウィンドウ(所有ウィンドウ)は、Pause()で明示的に
    /// SW_HIDEしていても、Windows側の「所有ウィンドウを持つウィンドウを
    /// 最小化→復元する」処理の一環として、**復元時にWindowsが自動的に
    /// 再表示してしまう**ことが実機検証で確認された(こちらが明示的に隠した
    /// 状態を、Windows自身の最小化/復元処理が上書きしてしまう形)。復元完了の
    /// タイミングはアニメーションの都合で厳密には特定しづらいため、単発の
    /// 再非表示だけでは取りこぼす可能性がある。そのため、中断メニュー/巻き戻し
    /// 画面表示中(=一時停止中)は、150msごとのポーリングで継続的に
    /// EmulationHost.Pause()を呼び直し、ネイティブウィンドウが隠れたままに
    /// なるよう保険をかけておく(Pause()は何度呼んでも安全)。
    /// </summary>
    private void ReassertNativeWindowHiddenIfPaused()
    {
        if (_pausedOverlay == PausedOverlay.None) return;
        _emuHost?.Pause();
    }

    /// <summary>
    /// ゲーム実行中にウィンドウが非アクティブになった(他のアプリへ切り替えられた)瞬間、
    /// 自動的に中断メニューを表示する。
    ///
    /// なぜ必要か: EmulationHostはゲーム実行中、SDLのキーボード/ゲームパッド入力を
    /// 専用スレッドで継続的にポーリングしている。この入力ポーリングはWindowsの
    /// フォーカス状態を見て自動的に止まるわけではないため、対策しないと、
    /// 別のウィンドウ/アプリを操作しているつもりのキー入力やマウス操作が、
    /// 裏でゲーム側にも渡ってしまうことがあった。ウィンドウが非アクティブに
    /// なった時点で中断メニューを表示してゲームを一時停止させることで、
    /// 少なくともゲームパッド入力の処理(レトロパッドへの反映)は止まる。
    ///
    /// 中断メニュー/巻き戻し画面がすでに開いている場合は何もしない
    /// (二重に処理しないため。最小化検知も同じ_pausedOverlayの仕組みを
    /// 使っているため、この1つのチェックで両方カバーできる)。
    /// アプリがアクティブに戻っても、意図せずゲームが再開してしまわないよう、
    /// あえて自動的には閉じない(ユーザーが明示的に「つづける」を選ぶまで
    /// 一時停止のままにする)。
    /// </summary>
    private void OnMainWindowAppActiveStateChanged(bool isActive)
    {
        _lastKnownAppActive = isActive;

        if (isActive) return;
        if (_emuHost is null) return;
        if (_pausedOverlay != PausedOverlay.None) return;
        if (!AppPreferencesStore.Load().AutoPauseOnDeactivate) return; // 自動中断が無効なら何もしない

        ShowMenu();
    }

    /// <summary>
    /// WM_ACTIVATEAPPの取りこぼし(クラス冒頭のコメント参照)に対する保険として、
    /// 150msごとに「今、前面にあるのは自分自身のプロセスのウィンドウか」を
    /// 直接確認する。状態が変化していた場合のみ、既存のOnMainWindowAppActiveStateChanged
    /// と同じ処理を行う(そちらとロジックを重複させないよう、そのまま呼び出す)。
    /// </summary>
    private void PollAppActiveState()
    {
        bool isActiveNow = IsForegroundWindowOwnProcess();
        if (isActiveNow == _lastKnownAppActive) return;

        OnMainWindowAppActiveStateChanged(isActiveNow);
    }

    /// <summary>現在の前面ウィンドウ(GetForegroundWindow)が、自分自身のプロセスに属しているかどうか。</summary>
    private static bool IsForegroundWindowOwnProcess()
    {
        nint foreground = Win32.GetForegroundWindow();
        if (foreground == nint.Zero) return false;

        Win32.GetWindowThreadProcessId(foreground, out uint foregroundProcessId);
        uint ownProcessId = (uint)Environment.ProcessId;
        return foregroundProcessId == ownProcessId;
    }

    /// <summary>
    /// このページ全体を包む Viewbox(Stretch="Uniform")が前提とする論理キャンバスの
    /// サイズ(DIP)。ConsoleSelectPage/GameSelectPageと同じ1920x1080。
    /// </summary>
    private static double LogicalCanvasWidthDip => (double)Application.Current.Resources["CanvasWidth"];
    private static double LogicalCanvasHeightDip => (double)Application.Current.Resources["CanvasHeight"];

    /// <summary>
    /// 下部ガイドバー(PlayHintBar / MenuOverlay内のヒントバー双方共通)の高さ。
    /// App.xaml の BottomBarStyle の Height、および上記の1920x1080論理キャンバス
    /// 内での値と必ず一致させること(Viewboxでスケーリングされる前の"論理"単位)。
    /// XAMLのレイアウトを経由せず直接この値を使うことで、Win32のクライアント領域
    /// サイズだけからエミュレータ画面用の領域(EmuHostContainerの行に相当する部分)を
    /// 計算できるようにしている。
    ///
    /// 「設定」→「操作説明表示」がOFF(CoreSettings.ShowControlGuide == false)の間は
    /// PlayHintBarが非表示になり画面下に何も描画されないため、ApplyEmuHostLayout()側でも
    /// この高さ分の余白確保をやめ、ゲーム画面をキャンバス全体(1080)まで拡大する
    /// (GetReservedBottomBarHeightDip参照)。
    /// </summary>
    private const double BottomBarHeightDip = 70;

    /// <summary>
    /// ApplyEmuHostLayout()がゲーム画面用領域の計算から差し引く、下部ガイドバー分の高さ。
    /// 「操作説明表示」がONの間だけBottomBarHeightDipを返し、OFFの間は0を返す
    /// (バーが非表示の間はその分の余白を確保する必要が無いため、ゲーム画面を
    /// その分だけ大きく表示できる)。
    /// </summary>
    private double GetReservedBottomBarHeightDip() => _guideAnimDip ?? (_showControlGuide ? BottomBarHeightDip : 0.0);

    // ------------------------------------------------------------------
    // 起動直後の操作説明の案内
    // 「操作説明表示」がOFFでも、ゲームを始めた直後だけ下部ガイドバー(中断メニューの出し方など)を
    // 見せて、しばらくしたら下へ滑らせて消し、その分だけゲーム画面を大きくする。
    // OFFのとき中断メニューの出し方が分からなくなるのを防ぐための案内。
    // ------------------------------------------------------------------
    private const int GuideIntroHoldMs = 3000;   // バーを見せておく時間
    private const int GuideIntroSlideMs = 450;   // 下へ消えて、ゲーム画面が広がる時間
    private const int GuideSlideInMs = 300;      // 早送り時に下から出てくる時間
    private double? _guideAnimDip;               // 案内中の、ガイドバー分の高さ(null=設定どおり)
    private DispatcherQueueTimer? _guideIntroTimer;
    private bool _guideIntroPlayed;              // 1回のプレイで1度だけ(コアの再起動では出さない)

    private enum GuidePhase { None, SlidingIn, Holding, SlidingOut }
    private GuidePhase _guidePhase = GuidePhase.None;
    private double _guideShown;                  // 0=隠れている 〜 1=完全に表示
    private double _guideFrom;                   // 現在のスライド開始時の _guideShown
    private DateTime _guidePhaseStartUtc;
    private bool _guideHoldForFastForward;       // 早送り中は出しっぱなしにする

    private void StartGuideIntro()
    {
        if (_showControlGuide || _guideIntroPlayed || _emuHost is null || _guideIntroTimer is not null) return;
        _guideIntroPlayed = true;
        BeginGuide(GuidePhase.Holding, 1.0);
    }

    /// <summary>案内の帯を表示状態にして、タイマーを動かす(起動直後の案内/早送り中の案内 共通)。</summary>
    private void BeginGuide(GuidePhase phase, double shown)
    {
        _guideShown = shown;
        _guideFrom = shown;
        _guidePhase = phase;
        _guidePhaseStartUtc = DateTime.UtcNow;

        BottomBarRowDefinition.Height = new GridLength(BottomBarRowHeightDip);
        PlayHintBar.Visibility = Visibility.Visible;
        ApplyGuideVisual();
        UpdateFastForwardLabel();

        DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => TickGuideIntro();
        _guideIntroTimer = timer;
        timer.Start();
    }

    private static double EaseOut(double t) => 1.0 - Math.Pow(1.0 - t, 3); // 後半でゆっくり止まる

    private void TickGuideIntro()
    {
        double elapsed = (DateTime.UtcNow - _guidePhaseStartUtc).TotalMilliseconds;
        switch (_guidePhase)
        {
            case GuidePhase.SlidingIn:
            {
                double t = Math.Min(1.0, elapsed / GuideSlideInMs);
                _guideShown = _guideFrom + (1.0 - _guideFrom) * EaseOut(t);
                if (t >= 1.0)
                {
                    _guidePhase = GuidePhase.Holding;
                    _guidePhaseStartUtc = DateTime.UtcNow;
                }
                break;
            }
            case GuidePhase.Holding:
                if (_guideHoldForFastForward) _guidePhaseStartUtc = DateTime.UtcNow; // 早送り中は数え始めない
                else if (elapsed >= GuideIntroHoldMs)
                {
                    _guidePhase = GuidePhase.SlidingOut;
                    _guideFrom = _guideShown;
                    _guidePhaseStartUtc = DateTime.UtcNow;
                }
                return;
            case GuidePhase.SlidingOut:
            {
                double t = Math.Min(1.0, elapsed / GuideIntroSlideMs);
                _guideShown = _guideFrom * (1.0 - EaseOut(t));
                ApplyGuideVisual();
                if (t >= 1.0) EndGuideIntro();
                return;
            }
        }
        ApplyGuideVisual();
    }

    /// <summary>_guideShown に合わせて、ゲーム画面の広さ・帯の位置と透明度を反映する(下へ滑らせながら薄くする)。</summary>
    private void ApplyGuideVisual()
    {
        _guideAnimDip = BottomBarHeightDip * _guideShown;
        foreach (UIElement el in new UIElement[] { PlayHintBar, FastForwardLabelBar })
        {
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(el, true);
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(el).Properties
                .InsertVector3("Translation", new System.Numerics.Vector3(0f, (float)(BottomBarRowHeightDip * (1.0 - _guideShown)), 0f));
            el.Opacity = _guideShown;
        }
        ScheduleLayoutRecalc();
    }

    /// <summary>
    /// HWレンダリングで早送りしている間、「操作説明表示」がOFFでも帯(早送り倍率つき)を下から出す。
    /// 早送りが終わったら、起動直後の案内と同じく数秒見せてから同じ動きで消す。
    /// </summary>
    private void UpdateGuideForFastForward()
    {
        if (_showControlGuide || _emuHost is null || !_emuHost.IsHwRendered || _pausedOverlay == PausedOverlay.Menu) return;

        if (_fastForwardMultiplierShown > 1)
        {
            _guideHoldForFastForward = true;
            if (_guideIntroTimer is null) BeginGuide(GuidePhase.SlidingIn, 0.0);
            else if (_guidePhase == GuidePhase.SlidingOut)
            {
                _guidePhase = GuidePhase.SlidingIn;
                _guideFrom = _guideShown;
                _guidePhaseStartUtc = DateTime.UtcNow;
            }
        }
        else if (_guideHoldForFastForward)
        {
            _guideHoldForFastForward = false;
            if (_guidePhase == GuidePhase.Holding) _guidePhaseStartUtc = DateTime.UtcNow; // ここから数秒
        }
    }

    /// <summary>案内を終える(中断メニューを開いたとき、ページを離れるときにも呼ぶ)。表示は設定どおりへ戻る。</summary>
    private void EndGuideIntro()
    {
        if (_guideIntroTimer is null && _guideAnimDip is null) return;

        _guideIntroTimer?.Stop();
        _guideIntroTimer = null;
        _guideAnimDip = null;
        _guidePhase = GuidePhase.None;
        _guideHoldForFastForward = false;

        foreach (UIElement el in new UIElement[] { PlayHintBar, FastForwardLabelBar })
        {
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(el).Properties
                .InsertVector3("Translation", System.Numerics.Vector3.Zero);
            el.Opacity = 1;
        }
        ApplyShowControlGuide();
        ScheduleLayoutRecalc();
    }

    /// <summary>
    /// EmuHost(SDL描画用のネイティブウィンドウ)のサイズ・画面上の位置を計算し、反映する。
    ///
    /// 重要: この計算は意図的に WinUI3 の XAML レイアウト(EmuHostContainer.ActualWidth /
    /// TransformToVisual 等)を一切使わず、Win32 API (GetClientRect / ClientToScreen) から
    /// 直接ウィンドウの現在のクライアント領域を取得して行っている。
    ///
    /// 理由: WinUI3のレイアウトは Measure → Arrange → Render → Composition → HWNDへの反映
    /// という流れを経て確定するが、ウィンドウの最大化/リストア(元に戻す)は「Win32レベルで
    /// 一気にHWNDがリサイズされる」操作であり、このHWNDリサイズはXAML側のレイアウト確定
    /// より先に起こる。そのため SizeChanged はもちろん、CompositionTarget.Rendering
    /// (そのフレームのXAMLレイアウトが確定した後に発生するイベント)で
    /// ActualWidth/ActualHeightを読んでも、最大化/リストアの直後はまだ古いサイズしか
    /// 返らないことがある(手動のドラッグリサイズは連続的にイベントが飛んでくるため
    /// 結果的に正しい値へ収束するが、最大化⇔リストアは一度の不連続なジャンプなので、
    /// この「追いつき待ち」だけで終わってしまいやすい)。
    ///
    /// 一方、Win32の GetClientRect は「今まさに」のクライアント領域を常に正しく返す
    /// (WM_SIZE等のウィンドウメッセージが届いた時点で、Win32側ではすでにHWNDの
    /// リサイズが完了しているため)。このメソッドの呼び出しタイミングは
    /// MainWindow.WindowBoundsChanged(NativeWindowSizeWatcherがWM_SIZE等を直接
    /// フックして発生させる)なので、XAML側のレイアウト確定を待つ必要が無い。
    ///
    /// Viewboxとの整合について: このページは ConsoleSelectPage/GameSelectPage と同じく、
    /// 1920x1080の固定キャンバスを Viewbox(Stretch="Uniform")で実ウィンドウサイズへ
    /// 一様スケーリングしている(GamePlayPage.xaml参照)。Win32から取得できる
    /// クライアント領域は"実際の"ウィンドウサイズ(DIP換算)なので、そのままでは
    /// この論理キャンバス上の子要素の位置とズレてしまう。そのため、実クライアント領域
    /// からViewboxの拡大率(viewboxScale)とレターボックス/ピラーボックスのオフセットを
    /// 逆算し、論理キャンバス内での計算結果(EmuHostContainerの位置・サイズ)を
    /// 実クライアント領域上の座標へ変換してからネイティブウィンドウへ反映する。
    /// </summary>
    private void ApplyEmuHostLayout()
    {
        if (_emuHost is null) return;
        if (AppServices.MainWindow is not { } window) return;
        if (!Win32.GetClientRect(window.Hwnd, out var clientRect)) return;

        int clientWidthPx = clientRect.Right - clientRect.Left;
        int clientHeightPx = clientRect.Bottom - clientRect.Top;
        if (clientWidthPx <= 0 || clientHeightPx <= 0) return;

        double scale = EmuHost.XamlRoot?.RasterizationScale ?? 1.0;
        double clientWidthDip = clientWidthPx / scale;
        double clientHeightDip = clientHeightPx / scale;

        // Viewboxの拡大率(1920x1080の論理キャンバスを実クライアント領域へ収める一様スケール)
        double viewboxScale = Math.Min(clientWidthDip / LogicalCanvasWidthDip, clientHeightDip / LogicalCanvasHeightDip);
        if (viewboxScale <= 0) return;

        double renderedWidthDip = LogicalCanvasWidthDip * viewboxScale;
        double renderedHeightDip = LogicalCanvasHeightDip * viewboxScale;
        double canvasOffsetXDip = (clientWidthDip - renderedWidthDip) / 2.0;
        double canvasOffsetYDip = (clientHeightDip - renderedHeightDip) / 2.0;

        // 1. 論理空間 (1920 x 1080) 上での EmuHostContainer 領域
        //    (Row 0: 1920 x (1080 - 下部ガイドバーの予約高さ)。「操作説明表示」がOFFの間は
        //    バー分の予約自体を無くし、ゲーム画面をキャンバス全体まで拡大する)
        double containerLogicalWidth = LogicalCanvasWidthDip;
        double containerLogicalHeight = LogicalCanvasHeightDip - GetReservedBottomBarHeightDip();
        if (containerLogicalHeight <= 0) return;

        // 2. 上記containerLogicalWidth x containerLogicalHeightの領域内に
        //    アスペクト比を保って収まる論理サイズを計算
        var (targetLogicalWidth, targetLogicalHeight) = ComputeAspectFitSize(containerLogicalWidth, containerLogicalHeight);

        // 3. Row 0 内での中央寄せ配置座標（論理座標）
        double targetLogicalX = (containerLogicalWidth - targetLogicalWidth) / 2.0;
        double targetLogicalY = (containerLogicalHeight - targetLogicalHeight) / 2.0;

        // 4. 論理座標を Viewbox の拡大率・レターボックスオフセットを考慮して実 DIP 座標へ換算
        double targetDipX = canvasOffsetXDip + targetLogicalX * viewboxScale;
        double targetDipY = canvasOffsetYDip + targetLogicalY * viewboxScale;
        double targetDipWidth = targetLogicalWidth * viewboxScale;
        double targetDipHeight = targetLogicalHeight * viewboxScale;

        // 5. 画面上の物理ピクセル座標へ換算
        var origin = new Win32.POINT { X = 0, Y = 0 };
        Win32.ClientToScreen(window.Hwnd, ref origin);

        int x = origin.X + (int)Math.Round(targetDipX * scale);
        int y = origin.Y + (int)Math.Round(targetDipY * scale);
        int targetWidthPx = Math.Max(0, (int)Math.Round(targetDipWidth * scale));
        int targetHeightPx = Math.Max(0, (int)Math.Round(targetDipHeight * scale));

        // デバッグ調査用: 実際に使われたアスペクト比・算出された目標サイズが
        // 変化した時だけログを出す(_layoutPollTimerによる定期呼び出しでスパムしないため)。
        double debugAspect = GetDisplayAspectRatio();
        if (_lastLoggedLayoutAspect is null
            || Math.Abs(_lastLoggedLayoutAspect.Value - debugAspect) > 0.0001
            || _lastLoggedLayoutWidthPx != targetWidthPx
            || _lastLoggedLayoutHeightPx != targetHeightPx)
        {
            Debug.WriteLine(
                $"[GamePlayPage] ApplyEmuHostLayout: ScreenLayout={_settings.ScreenLayout} "
                + $"aspect={debugAspect:F4} CoreReportedAspectRatio={_emuHost?.CoreReportedAspectRatio.ToString("F4") ?? "null"} "
                + $"clientPx={clientWidthPx}x{clientHeightPx} targetPx={targetWidthPx}x{targetHeightPx} at ({x},{y})");
            _lastLoggedLayoutAspect = debugAspect;
            _lastLoggedLayoutWidthPx = targetWidthPx;
            _lastLoggedLayoutHeightPx = targetHeightPx;
        }

        // 6. ネイティブウィンドウ Bounds 更新
        _emuHost.UpdateBounds(x, y, targetWidthPx, targetHeightPx);

        // 7. XAML 側の EmuHost Border の幅・高さ・配置（論理座標）を正確に同期
        EmuHost.Width = targetLogicalWidth;
        EmuHost.Height = targetLogicalHeight;
        EmuHost.HorizontalAlignment = HorizontalAlignment.Left;
        EmuHost.VerticalAlignment = VerticalAlignment.Top;
        EmuHost.Margin = new Thickness(targetLogicalX, targetLogicalY, 0, 0);
    }

    /// <summary>
    /// 指定した領域の中に、選択中のアスペクト比を保ったまま最大限大きく収まる
    /// サイズを計算する。単位は呼び出し側と揃っていればよい(このメソッド自体は
    /// 論理キャンバス単位・実DIP単位のどちらで呼ばれても正しく動作する)。
    /// </summary>
    private (double Width, double Height) ComputeAspectFitSize(double containerWidth, double containerHeight)
    {
        double aspect = GetDisplayAspectRatio();

        double targetW = containerHeight * aspect;
        double targetH = containerHeight;
        if (targetW > containerWidth)
        {
            targetW = containerWidth;
            targetH = containerWidth / aspect;
        }

        double w = Math.Max(0, Math.Min(targetW, containerWidth));
        double h = Math.Max(0, Math.Min(targetH, containerHeight));
        return (w, h);
    }
}
