using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Windows.Foundation;
using EmulatorLauncher.Controls;
using EmulatorLauncher.Emulation;
using EmulatorLauncher.Converters;
using EmulatorLauncher.RomLibrary;
using EmulatorLauncher.Models;
using EmulatorLauncher.Input;
using EmulatorLauncher.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Navigation;

namespace EmulatorLauncher.Views;

// GameSelectPage の一部(partial class)。L/Rのページ送り・高速スクロール・オーバーレイ・ゲームパッド入力。
// 動作は変えず、大きくなったコードビハインドを役割ごとに分けただけ。
public sealed partial class GameSelectPage
{
    // ------------------------------------------------------------------
    // L/Rボタンでのページ送り
    // ------------------------------------------------------------------

    /// <summary>
    /// 1画面分スクロールし、選択も同じ行数だけ動かす(direction: +1=R/下へ、-1=L/上へ)。
    ///
    /// このページはカードが均等格子(セル高さ_gridItemHeight固定)なので、「1画面に完全に収まる
    /// 行数(rowsPerPage)」を求め、スクロール量はその行数ぶんの高さ、選択は rowsPerPage×列数
    /// だけインデックスを進める。スクロールと選択を同じ行数だけ動かすので、選択中カードは
    /// 画面上のほぼ同じ位置に留まったまま、中身だけが1画面分入れ替わって見える。
    ///
    /// 端に近くスクロールが途中で止まる場合(target が 0 / ScrollableHeight でクランプされる)も、
    /// 選択は rowsPerPage 行ぶん(足りなければ先頭/末尾のカードまで)動くので、結果として
    /// 選択カードは必ず画面内に入る。念のため、スクロール完了後に拡大分の余白も含めて
    /// 画面内に収まっているかを確認し直す(_bringSelectedIntoViewAfterScroll)。
    /// </summary>
    private void ScrollPage(int direction)
    {
        if (Games.Count == 0 || _selectedGameIndex < 0) return;

        AttachGridScrollViewer();
        ScrollViewer? scrollViewer = _gridScrollViewer;

        int rowsPerPage = 1;
        if (scrollViewer is not null && scrollViewer.ViewportHeight > 0 && _gridItemHeight > 0)
        {
            rowsPerPage = Math.Max(1, (int)(scrollViewer.ViewportHeight / _gridItemHeight));
        }

        int newIndex = Math.Clamp(_selectedGameIndex + direction * rowsPerPage * _columns, 0, Games.Count - 1);

        if (scrollViewer is null)
        {
            // ScrollViewerが見つからない(テンプレート未適用等)場合は、選択移動+通常の自動スクロールで代用。
            SetSelectedGameIndex(newIndex);
            return;
        }

        double baseOffset = _scrollTarget ?? scrollViewer.VerticalOffset;
        double target = Math.Clamp(baseOffset + direction * rowsPerPage * _gridItemHeight, 0, scrollViewer.ScrollableHeight);

        SetSelectedGameIndex(newIndex, bringIntoView: false);

        if (Math.Abs(target - baseOffset) > 1.0)
        {
            _scrollTarget = target;
            _bringSelectedIntoViewAfterScroll = true;
            scrollViewer.ChangeView(horizontalOffset: null, verticalOffset: target, zoomFactor: null, disableAnimation: false);
        }
        else
        {
            // もうスクロールできない(先頭/末尾のページ)。選択だけ先頭/末尾側へ動いたので、
            // 通常の選択移動と同じく画面内に収める。
            BringGameIntoView(Games[newIndex]);
        }
    }

    // ------------------------------------------------------------------
    // L/R押しっぱなしでの高速スクロール
    // ------------------------------------------------------------------

    /// <summary>
    /// L/Rが押された(GridモードのHandleGridInputから呼ばれる)。
    ///
    /// SdlGamepadServiceは押しっぱなし中にリピート入力を送ってくることがあり、MainWindowは
    /// それを通常の押下と区別せずOnGamepadButtonへ渡してくる。押しっぱなしスクロール中に届いた
    /// 同じ方向の入力はそのリピートとみなして無視する(でないと1画面分のページ送りが割り込み、
    /// 連続スクロールがガクつく)。逆方向が押された場合は、今のスクロールを止めてそちらへ切り替える。
    /// </summary>
    private void HandleShoulder(int direction)
    {
        if (IsHoldActive)
        {
            if (direction == _holdDirection) return;
            StopHoldScroll(bringIntoView: false);
        }

        // 押した瞬間は、今まで通り1画面分だけ送る。
        ScrollPage(direction);

        // 押し続けていれば、少し待ってから連続スクロールへ切り替わる(HoldTimer_Tick)。
        StartHoldScroll(direction);
    }

    private void StartHoldScroll(int direction)
    {
        _holdDirection = direction;
        _holdButton = direction > 0 ? GamepadButton.RightShoulder : GamepadButton.LeftShoulder;
        _holdScrolling = false;
        _holdClock.Restart();

        if (_holdTimer is null)
        {
            _holdTimer = DispatcherQueue.CreateTimer();
            _holdTimer.Interval = HoldTickInterval;
            _holdTimer.IsRepeating = true;
            _holdTimer.Tick += HoldTimer_Tick;
        }
        _holdTimer.Start();
    }

    /// <param name="bringIntoView">止めた後、選択カードを(拡大分も含めて)画面内に収め直すか。</param>
    private void StopHoldScroll(bool bringIntoView = true)
    {
        if (!IsHoldActive) return;

        _holdTimer!.Stop();
        _holdClock.Stop();

        bool wasScrolling = _holdScrolling;
        _holdScrolling = false;

        if (bringIntoView && wasScrolling && _selectedGame is not null)
        {
            BringGameIntoView(_selectedGame);
        }
    }

    /// <summary>
    /// 押しっぱなし中、約16msごとに呼ばれる。
    ///
    /// 1. 毎回SDLから直接ボタンの押下状態を読み(GamepadHoldState)、離されていれば止める。
    /// 2. 押し始めからHoldStartDelaySecondsが経ち、かつ押した瞬間の1画面分のページ送り
    ///    (アニメーション)が終わっていれば、連続スクロールを開始する。
    /// 3. 連続スクロール中は、経過時間に応じて加速しながら、アニメーション無しのChangeViewで
    ///    少しずつスクロール位置を進める。選択はスクロールした行数ぶん同じ列のまま動かすので、
    ///    選択カードは画面上の同じ段に留まって見える。
    /// 4. 末尾(Lなら先頭)に着いたら、選択を最終行(先頭行)の同じ列へ移して止める。
    /// </summary>
    private void HoldTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_mode != Mode.Grid || !GamepadHoldState.IsButtonHeld(_holdButton))
        {
            StopHoldScroll(bringIntoView: _mode == Mode.Grid);
            return;
        }

        ScrollViewer? scrollViewer = _gridScrollViewer;
        if (scrollViewer is null || Games.Count == 0 || _selectedGameIndex < 0 || _gridItemHeight <= 0)
        {
            StopHoldScroll(bringIntoView: false);
            return;
        }

        double now = _holdClock.Elapsed.TotalSeconds;

        if (!_holdScrolling)
        {
            // 押した瞬間のページ送り(アニメーション付きChangeView)の途中で連続スクロールを
            // 始めると位置が飛ぶため、それが終わる(_scrollTargetがnullに戻る)まで待つ。
            if (now < HoldStartDelaySeconds || _scrollTarget is not null) return;

            _holdScrolling = true;
            _holdStartOffset = _holdOffset = scrollViewer.VerticalOffset;
            _holdStartIndex = _selectedGameIndex;
            _holdScrollStartTime = now;
            _holdLastTickTime = now;
            return;
        }

        // タイマーの間隔は保証されないので、実際の経過時間で進める(引っかかった時に一気に
        // 飛ばないよう、1回あたり最大50ms分に制限)。
        double dt = Math.Min(now - _holdLastTickTime, 0.05);
        _holdLastTickTime = now;

        double speed = Math.Min(HoldMaxSpeed, HoldStartSpeed + HoldAcceleration * (now - _holdScrollStartTime));
        double maxOffset = scrollViewer.ScrollableHeight;
        _holdOffset = Math.Clamp(_holdOffset + _holdDirection * speed * dt, 0, maxOffset);

        scrollViewer.ChangeView(horizontalOffset: null, verticalOffset: _holdOffset, zoomFactor: null, disableAnimation: true);

        int column = _holdStartIndex % _columns;
        bool reachedEnd = _holdDirection > 0 ? _holdOffset >= maxOffset - 0.5 : _holdOffset <= 0.5;

        int newIndex;
        if (reachedEnd)
        {
            // 端まで着いた: 最終行(先頭行)の同じ列へ。最終行がその列まで埋まっていなければ最後のゲーム。
            int lastRow = (Games.Count - 1) / _columns;
            newIndex = _holdDirection > 0
                ? Math.Min(lastRow * _columns + column, Games.Count - 1)
                : Math.Min(column, Games.Count - 1);
        }
        else
        {
            int rowsScrolled = (int)Math.Round((_holdOffset - _holdStartOffset) / _gridItemHeight);
            newIndex = Math.Clamp(_holdStartIndex + rowsScrolled * _columns, 0, Games.Count - 1);
        }

        if (newIndex != _selectedGameIndex)
        {
            SetSelectedGameIndex(newIndex, bringIntoView: false);
        }

        if (reachedEnd)
        {
            StopHoldScroll();
        }
    }

    /// <summary>GridViewのテンプレート内部のScrollViewerを探し、ViewChangedを購読する(一度だけ)。</summary>
    private void AttachGridScrollViewer()
    {
        if (_gridScrollViewer is not null) return;

        _gridScrollViewer = FindDescendant<ScrollViewer>(GameGridView);
        if (_gridScrollViewer is not null)
        {
            _gridScrollViewer.ViewChanged += GridScrollViewer_ViewChanged;
        }
    }

    private void GridScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        // 年代ラベルはスクロール中(IsIntermediate)も追従させる(位置を引き直すだけで軽い)。
        UpdateYearGutter();

        if (e.IsIntermediate) return;

        _scrollTarget = null;

        if (_bringSelectedIntoViewAfterScroll)
        {
            _bringSelectedIntoViewAfterScroll = false;
            if (_selectedGame is not null)
            {
                BringGameIntoView(_selectedGame);
            }
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;

            T? found = FindDescendant<T>(child);
            if (found is not null) return found;
        }
        return null;
    }

    // ------------------------------------------------------------------
    // 詳細オーバーレイ (3枚目画像) / ソートオーバーレイ (4枚目画像)
    // 中身はControls/GameDetailOverlay、Controls/SortOverlayへ切り出し済み。
    // ------------------------------------------------------------------

    /// <summary>決定(A)で詳細画面を挟む設定か(アプリ設定「ゲーム起動方法」)。</summary>
    private readonly bool _launchViaDetail;

    private void ShowDetail()
    {
        if (Games.Count == 0 || _selectedGameIndex < 0) return;

        _mode = Mode.Detail;
        DetailOverlay.ReturnTargetResolver = game => FindGameCoverImage(_parentOf.TryGetValue(game, out RomFile? parent) ? parent : game);
        DetailOverlay.Show(Games[_selectedGameIndex], FindGameCoverImage(Games[_selectedGameIndex]), showLaunch: _launchViaDetail);
    }

    /// <summary>一覧のカード内のカバー画像(連結アニメーションの出発点/戻り先)。画面外で未生成ならnull。</summary>
    private UIElement? FindGameCoverImage(RomFile game)
    {
        if (GameGridView.ContainerFromItem(game) is not DependencyObject container) return null;
        Microsoft.UI.Xaml.Controls.Image? image = EmulatorLauncher.Controls.GameDetailOverlay.FindFirstImage(container);
        return image is { Visibility: Visibility.Visible, ActualWidth: > 0 } ? image : null;
    }

    /// <summary>ゲーム終了で戻ってきた時、ゲーム画面のカバー画像が元のカードへ縮んで戻る連結アニメーション。
    /// カードが生成されるまで少し待ってから開始する(画面外なら諦める)。</summary>
    private void PlayReturnCoverAnimation(RomFile game)
    {
        try
        {
            var animation = Microsoft.UI.Xaml.Media.Animation.ConnectedAnimationService.GetForCurrentView()
                .GetAnimation(GamePlayPage.ReturnCoverKey);
            if (animation is null) return;

            int tries = 0;
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(50);
            timer.IsRepeating = true;
            timer.Tick += (t, _) =>
            {
                tries++;
                if (FindGameCoverImage(game) is UIElement target)
                {
                    t.Stop();
                    try { animation.TryStart(target); } catch { }
                }
                else if (tries > 30)
                {
                    t.Stop();
                }
            };
            timer.Start();
        }
        catch { }
    }

    private bool CanUseTouchNav() => _mode == Mode.Grid && !DetailOverlay.IsOpen && !SortOverlay.IsOpen && !SystemSettings.IsOpen;

    private void ShowSort()
    {
        _mode = Mode.Sort;
        SortOverlay.Show();
    }

    /// <summary>ソートオーバーレイでAが押された。並び順の計算はGameListHelperに任せ、
    /// このページのGridViewへはMoveによるアニメーション付きで反映する
    /// (オーバーレイはこの後自分で閉じ、Closedで_modeがGridへ戻る)。</summary>
    private void SortOverlay_SortRequested(object? sender, SortMode mode)
    {
        // 以後の検索フィルタ(ApplyFilterAndSort)でもこのソート方法を引き継ぐ
        // (例: あいうえお順→最近遊んだ順に変更した後で検索しても、最近遊んだ順のまま絞り込む)。
        _currentSortMode = mode;

        List<RomFile> target = GameListHelper.Order(Games, mode);
        ReorderGamesWithAnimation(target);
        ApplyYearGutterMode();
        SetSelectedGameIndex(0);
    }

    /// <summary>
    /// Gamesの中身をtargetOrderの並びへ変形する。
    ///
    /// Games = new List(...)のような丸ごと差し替え(あるいはClear+Addによる一括更新)は
    /// CollectionChangedとしてはReset通知になり、GridViewは全コンテナを作り直してしまう
    /// (=既存コンテナのVisualが破棄され、EnsureContainerPositionAnimationで仕込んだ
    /// ImplicitAnimationsごと消えてしまう)ため、ソート時に「カードが元の位置から新しい位置へ
    /// 滑るように移動する」というアニメーションの要件に合わない。
    ///
    /// そこで、要素をひとつも作り直さず、ObservableCollection.Move(oldIndex, newIndex)だけを
    /// 使って並び替える。Move通知であれば既存のコンテナ(と、そこに仕込んだImplicitAnimations)を
    /// 保ったままItemsWrapGridがカードを再配置してくれるため、
    /// GameGridView_ContainerContentChanging/EnsureContainerPositionAnimationで仕込んだ
    /// "Offset"の暗黙アニメーションが効き、カードが滑るように新しい位置へ移動して見える。
    /// (なお、WinUI/UWPのGridViewが標準で持つReorderThemeTransitionは、ドラッグ&ドロップによる
    /// 並び替え専用でObservableCollection.Move()に対しては発火しないため、それには頼っていない)
    ///
    /// 計算量はO(n^2)だが(各要素についてIndexOfでO(n)検索)、最大1000件程度であれば
    /// 1回のソート操作で最大でも百万回程度の単純な参照比較で済み、実用上問題にならない。
    /// また画面内に実際に描画されている(仮想化で実体化済みの)カードだけがアニメーションの
    /// コストを払うため、画面外の大量のカードがあっても描画負荷は増えない。
    /// </summary>
    private bool _animateOffsets;

    private void ReorderGamesWithAnimation(IReadOnlyList<RomFile> targetOrder)
    {
        _animateOffsets = true;
        if (GameGridView.ItemsPanelRoot is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is GridViewItem item) EnsureContainerPositionAnimation(item);
            }
        }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
        {
            await System.Threading.Tasks.Task.Delay(500);
            _animateOffsets = false;
            if (GameGridView.ItemsPanelRoot is Panel p)
            {
                foreach (var child in p.Children)
                {
                    if (child is GridViewItem item)
                        ElementCompositionPreview.GetElementVisual(item).ImplicitAnimations = null;
                }
            }
        });

        for (int i = 0; i < targetOrder.Count; i++)
        {
            var item = targetOrder[i];
            int currentIndex = Games.IndexOf(item);
            if (currentIndex < 0) continue; // 基本的には発生しない想定(念のためのガード)
            if (currentIndex != i)
            {
                Games.Move(currentIndex, i);
            }
        }
    }

    // ------------------------------------------------------------------
    // ゲームパッド入力
    // ------------------------------------------------------------------

    public void OnGamepadButton(GamepadButton button)
    {
        // タッチパッド(PS4/PS5)でシステム設定パネルを開閉する。パネル表示中は入力をパネルへ渡す。
        if (button == GamepadButton.Touchpad)
        {
            if (SystemSettings.IsOpen) SystemSettings.Close();
            else if (_mode == Mode.Grid) SystemSettings.Show();
            return;
        }
        if (SystemSettings.IsOpen) { SystemSettings.HandleGamepadInput(button); return; }

        switch (_mode)
        {
            case Mode.Grid:
                HandleGridInput(button);
                break;
            case Mode.Detail:
                DetailOverlay.HandleGamepadInput(button);
                break;
            case Mode.Sort:
                SortOverlay.HandleGamepadInput(button);
                break;
        }
    }

    public void OnGamepadButtonLongPress(GamepadButton button)
    {
        if (_mode == Mode.Grid && button == GamepadButton.Y)
        {
            ShowSort();
        }
    }

    private void HandleGridInput(GamepadButton button)
    {
        // 年代ジャンプのスクロール中に、ZR/ZL以外が押されたら、そこで止めてからその操作を行う。
        if (_jumpTimer?.IsRunning == true && button != GamepadButton.LeftTrigger && button != GamepadButton.RightTrigger)
        {
            StopYearJump(finalize: true);
        }

        // 押しっぱなしスクロール中に他のボタンが押されたら、スクロールを止めてからその操作を行う。
        if (IsHoldActive && button != GamepadButton.LeftShoulder && button != GamepadButton.RightShoulder)
        {
            StopHoldScroll();
        }

        switch (button)
        {
            case GamepadButton.DPadLeft:
                MoveGameSelection(-1);
                break;
            case GamepadButton.DPadRight:
                MoveGameSelection(1);
                break;
            case GamepadButton.DPadUp:
                MoveGameSelection(-_columns);
                break;
            case GamepadButton.DPadDown:
                MoveGameSelection(_columns);
                break;
            case GamepadButton.RightShoulder:
                HandleShoulder(+1);
                break;
            case GamepadButton.LeftShoulder:
                HandleShoulder(-1);
                break;
            case GamepadButton.RightTrigger:
                JumpYearGroup(+1);
                break;
            case GamepadButton.LeftTrigger:
                JumpYearGroup(-1);
                break;
            case GamepadButton.X:
                if (!_launchViaDetail) ShowDetail();
                break;
            case GamepadButton.A:
                if (_launchViaDetail) ShowDetail();
                else StartGameDirect();
                break;
            case GamepadButton.Menu:
                if (_biosBootAvailable) StartBiosBoot();
                break;
            case GamepadButton.B:
                Frame.GoBack();
                break;
        }
    }

    /// <summary>
    /// ゲームのカードをクリック/タップしたとき。フォーカスされていないカードは、まず選択を移すだけ(誤起動を防ぐ)。
    /// 選択中のカードをもう一度押すと決定(Aと同じ: 詳細を開く/起動)。
    /// </summary>
    private void GameCard_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (_mode != Mode.Grid || SystemSettings.IsOpen) return;
        if (sender is not FrameworkElement { DataContext: RomFile game }) return;

        int index = Games.IndexOf(game);
        if (index < 0) return;

        if (index == _selectedGameIndex && ReferenceEquals(_selectedGame, game))
        {
            if (_launchViaDetail) ShowDetail();
            else StartGameDirect();
            return;
        }

        // 年代ジャンプや押しっぱなしスクロールの最中なら、止めてから選択を移す。
        if (_jumpTimer?.IsRunning == true) StopYearJump(finalize: true);
        if (IsHoldActive) StopHoldScroll(bringIntoView: false);
        SetSelectedGameIndex(index, bringIntoView: false);
    }

    /// <summary>
    /// GridView標準の矢印キー/PageUp・PageDown/Home・Endでの移動を止める。選択の移動とスクロールは、
    /// MainWindowのキーボード入力(ゲームパッドと同じ経路)が行う。標準の移動が重なると、
    /// アニメーション無しのスクロールが割り込み、画面が瞬間移動してしまう(マウスでカードを触った後などに起きる)。
    /// </summary>
    private void GameGridView_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Up:
            case Windows.System.VirtualKey.Down:
            case Windows.System.VirtualKey.Left:
            case Windows.System.VirtualKey.Right:
            case Windows.System.VirtualKey.PageUp:
            case Windows.System.VirtualKey.PageDown:
            case Windows.System.VirtualKey.Home:
            case Windows.System.VirtualKey.End:
                e.Handled = true;
                break;
        }
    }

    /// <summary>このコンソールのコアがゲーム無し起動(BIOS)に対応するとLauncherInfo.BiosBootで指定されているか。</summary>
    private bool _biosBootAvailable;

    /// <summary>ゲームを選ばずに本体(BIOS)だけを起動する。DBには保存しない擬似RomFileで起動する。</summary>
    private void StartBiosBoot()
    {
        if (string.IsNullOrEmpty(_console.Id)) return;
        var bios = new RomFile
        {
            No = -1,
            ConsoleId = _console.Id,
            Title = "本体(BIOS)",
            IsBiosBoot = true,
        };
        Frame.Navigate(typeof(GamePlayPage), bios);
    }

    private async void StartGameDirect()
    {
        RomFile? game = Games.Count == 0 ? null : Games[_selectedGameIndex];
        if (game is not null && AppPreferencesStore.Load().ResumeOnDirectLaunch
            && await GameDetailOverlay.HasSuspendStateAsync(game))
            GamePlayPage.ResumeOnNextLaunch = true;
        StartGame();
    }

    private void StartGame(UIElement? coverSource = null)
    {
        if (Games.Count == 0) return;

        // GamePlayPageからFrame.GoBackで戻ってきた時に、このゲームへフォーカスを
        // 復元できるようNo(一意なID)を覚えておく(詳細は_pendingRestoreGameNoの
        // フィールドコメント参照)。
        _pendingRestoreGameNo = Games[_selectedGameIndex].No;
        _pendingRestoreView = (CurrentViewKey(), _currentSortMode, SearchTextBox.Text ?? string.Empty);

        // カードのカバー画像が、ゲーム画面へ拡大していく連結アニメーションを仕込む。
        try
        {
            if ((coverSource ?? FindGameCoverImage(Games[_selectedGameIndex])) is UIElement cover)
                Microsoft.UI.Xaml.Media.Animation.ConnectedAnimationService.GetForCurrentView()
                    .PrepareToAnimate(GamePlayPage.LaunchCoverKey, cover);
        }
        catch { }

        // 詳細画面から起動するときは、詳細で選んだバージョン(クローン等)を起動する。
        RomFile launch = (DetailOverlay.IsOpen ? DetailOverlay.CurrentGame : null) ?? Games[_selectedGameIndex];
        Frame.Navigate(typeof(GamePlayPage), launch);
    }
}
