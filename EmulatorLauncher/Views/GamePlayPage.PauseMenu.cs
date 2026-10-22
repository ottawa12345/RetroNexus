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

// GamePlayPage の一部(partial class)。中断メニュー(開閉・早送り・巻き戻し画面・項目ナビゲーション・パッド入力)。
// 動作は変えず、巨大だったGamePlayPage.xaml.csを役割ごとに分けただけ。
public sealed partial class GamePlayPage
{
    // ==================================================================
    // 中断メニューの開閉(ZL+ZR同時押しで開く。ヒント表示は下部ガイドバー参照)
    // ==================================================================

    private void OnEmuMenuToggleRequested(object? sender, EventArgs e)
    {
        // 呼び出しはEmulationHostのスレッドから行われるため、UIスレッドへ転送する。
        DispatcherQueue.TryEnqueue(ShowMenu);
    }

    private void OnEmuSystemPanelRequested(object? sender, EventArgs e)
    {
        // タッチパッド: 中断メニューを開き、その上にシステム設定パネルを出す(Bで閉じると中断メニューへ戻る)。
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_pausedOverlay != PausedOverlay.None) return;
            ShowMenu();
            if (_pausedOverlay != PausedOverlay.Menu) return;
            SystemSettings.Show();
            _touchpadOpenedUtc = DateTime.UtcNow;
        });
    }

    // 同じ押下が一時停止後のメニュー側の入力(OnPausedMenuButtonPressed)にも届き、開いた直後に閉じてしまうのを防ぐ。
    private DateTime _touchpadOpenedUtc;

    private void ShowMenu()
    {
        if (_pausedOverlay != PausedOverlay.None || _emuHost is null) return;
        EmulatorLauncher.Services.SoundEffects.Play("accept"); // ZL+ZRで中断メニューを開く
        _pausedOverlay = PausedOverlay.Menu;
        _focus = FocusArea.Menu;

        _emuHost.Pause();
        UpdateMenuBackground();
        UpdateDiskMenuItem();
        HideControlsPanel();
        SetMenuIndex(0);
        HideSlotsPanel();
        HideSettingsPanel();
        HideDiskPanel();

        // 「操作説明表示」がONだと、下部ガイドバー(PlayHintBar)の「ZL+ZR 中断メニュー」表示が
        // 中断メニューの半透明の背景(MenuScrimBrush)越しに透けて見えてしまい邪魔になるため、
        // メニュー表示中はいったん隠す。値自体(_showControlGuide)は変更しないので、
        // メニューを閉じるときにApplyShowControlGuide()で元の表示状態へ戻す。
        EndGuideIntro(); // 起動直後の案内の最中にメニューを開いたときは、案内を終える
        PlayHintBar.Visibility = Visibility.Collapsed;
        UpdateFastForwardLabel(); // 早送り倍率表示も、メニュー表示中は隠す

        MenuOverlay.Visibility = Visibility.Visible;
        AnimateOpacity(MenuOverlay, 0, 1, 200);
    }

    /// <summary>
    /// ディスクが2つ以上あるコア(複数枚組、FDSのA面/B面など)のときだけ、中断メニューへ「ディスク入れ替え」を足す。
    /// 決定すると右側にディスクの一覧が出て、入れるディスクを選べる。
    /// MenuItemsControlはx:BindのOneTimeなので、項目数が変わったらItemsSourceを付け替える。
    /// </summary>
    private void UpdateDiskMenuItem()
    {
        if (_emuHost is null) return;

        Debug.WriteLine($"[Disk] メニュー表示: DiskCount={_emuHost.DiskCount} DiskIndex={_emuHost.DiskIndex} HasDiskSwitch={_emuHost.HasDiskSwitch}");

        bool changed = false;
        changed |= EnsureDiskMenuItem(PauseMenuAction.SwitchDisk, _emuHost.HasDiskSwitch, _emuHost.GetDiskMenuText());
        if (changed) RefreshMenuItemsSource();
    }

    /// <summary>ディスク関連のメニュー項目を、必要なら追加・不要なら削除・あれば文言更新する。項目数が変わったらtrue。</summary>
    private bool EnsureDiskMenuItem(PauseMenuAction action, bool wanted, string text)
    {
        var existing = MenuItems.Find(i => i.Action == action);

        if (!wanted)
        {
            if (existing is null) return false;
            MenuItems.Remove(existing);
            return true;
        }

        if (existing is not null)
        {
            existing.Text = text;
            return false;
        }

        int resetIndex = MenuItems.FindIndex(i => i.Action == PauseMenuAction.Reset);
        int at = resetIndex >= 0 ? resetIndex : MenuItems.Count;
        MenuItems.Insert(at, new PauseMenuItem { Text = text, Action = action });
        return true;
    }

    private void RefreshMenuItemsSource()
    {
        MenuItemsControl.ItemsSource = null;
        MenuItemsControl.ItemsSource = MenuItems;
    }

    private void HideMenu()
    {
        if (_pausedOverlay != PausedOverlay.Menu) return;
        _pausedOverlay = PausedOverlay.None;
        if (SystemSettings.IsOpen) SystemSettings.Close();

        var storyboard = AnimateOpacity(MenuOverlay, 1, 0, 180);
        storyboard.Completed += (_, _) =>
        {
            MenuOverlay.Visibility = Visibility.Collapsed;
            // ShowMenu()で隠したPlayHintBarを、「操作説明表示」設定に従って元へ戻す。
            ApplyShowControlGuide();
            _emuHost?.Resume();
        };
    }

    /// <summary>ページ離脱時などにアニメーションを待たず即座にメニューを閉じる。</summary>
    private void CloseMenuImmediate()
    {
        if (SystemSettings.IsOpen) SystemSettings.Close();
        HideControlsPanel();
        if (_pausedOverlay == PausedOverlay.Menu) _pausedOverlay = PausedOverlay.None;
        MenuOverlay.Visibility = Visibility.Collapsed;
        MenuOverlay.Opacity = 0;
        MenuBackgroundImage.Source = null;
        InfoPanel.Visibility = Visibility.Collapsed;
        HideSlotsPanel();
        HideSettingsPanel();
        HideDiskPanel();
        // ShowMenu()で隠したPlayHintBarを、「操作説明表示」設定に従って元へ戻す。
        ApplyShowControlGuide();
    }

    // ==================================================================
    // 早送り(R+ZR同時押しで 1倍→2倍→3倍→1倍 … を順方向に、L+ZL同時押しで
    // 1倍→3倍→2倍→1倍 … を逆方向に巡回)
    // ==================================================================

    /// <summary>
    /// 早送り倍率が切り替わるたびにEmulationHostから発生する。呼び出しは
    /// EmulationHostのスレッドから行われるため、UIスレッドへ転送する。
    ///
    /// 注意: 画面上の倍率表示(「2」「3」「5」の数字バッジ)は、ここ(XAML側)では
    /// 出していない。ライブ中のゲーム画面はSDLが作成した別ウィンドウ(_childHwnd。
    /// MainWindow/GamePlayPageのXAMLとは別のWS_POPUPウィンドウとしてWinUI3の
    /// DirectComposition層の"上"に重なっている。EmulationHost.RunLoopのコメント
    /// 「Airspace問題」参照)なので、EmuHostContainer上にXAML側で何か重ねて置いても、
    /// ゲーム画面が実際に表示されている間は(レターボックス/ピラーボックスの余白部分を
    /// 除いて)その下に隠れてしまい見えないため。
    ///
    /// 代わりに、倍率インジケーターはSDL_Renderer側(EmulationHost.Video.cs の
    /// DrawFastForwardIndicatorIfActive/DrawSevenSegmentDigit)で、ゲーム画面を
    /// 描画した直後・SDL_RenderPresentの直前に、7セグメント風の塗りつぶし矩形として
    /// 直接描いている(フォント読み込み不要、ゲーム画面と同じレイヤーに乗るためAirspace
    /// 問題も発生しない)。このハンドラはUIスレッド側でのデバッグ用ログ出力のみを行う。
    /// (HWレンダリング(raw GLパス)のコアには未対応。EmulationHost.Video.cs内の
    /// DrawFastForwardIndicatorIfActiveのコメント参照)
    /// </summary>
    private void OnEmuFastForwardMultiplierChanged(object? sender, int multiplier)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            Debug.WriteLine($"[GamePlayPage] 早送り倍率変更: {multiplier}倍");
            _fastForwardMultiplierShown = multiplier;
            UpdateGuideForFastForward();
            UpdateFastForwardLabel();
        });
    }

    /// <summary>画面左下(下部ガイドバーの行)に出す早送り倍率表示の、現在の倍率。1以下なら非表示。</summary>
    private int _fastForwardMultiplierShown = 1;

    /// <summary>
    /// 早送り中(2倍以上)かつ「操作説明表示」がONで、中断メニューが開いていない間だけ、
    /// 画面左下に「▶▶ N倍速」を表示する。
    /// </summary>
    private void UpdateFastForwardLabel()
    {
        bool show = _fastForwardMultiplierShown > 1
            && (_showControlGuide || _guideAnimDip is not null)
            && _pausedOverlay != PausedOverlay.Menu;

        if (show)
            FastForwardLabelText.Text = $"▶▶ {_fastForwardMultiplierShown}倍速";

        FastForwardLabelBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    // ==================================================================
    // 巻き戻し画面の開閉(ZL+ZR同時長押しで開く)
    // ==================================================================

    private void OnEmuRewindToggleRequested(object? sender, EventArgs e)
    {
        // 呼び出しはEmulationHostのスレッドから行われるため、UIスレッドへ転送する。
        DispatcherQueue.TryEnqueue(ShowRewind);
    }

    private void ShowRewind()
    {
        if (_pausedOverlay != PausedOverlay.None || _emuHost is null) return;
        EmulatorLauncher.Services.SoundEffects.Play("accept"); // ZL+ZR長押しで巻き戻し画面を開く
        if (!IsRewindAvailable) return;
        _pausedOverlay = PausedOverlay.Rewind;

        _emuHost.Pause();

        RewindOverlay.Visibility = Visibility.Visible;

        LoadRewindFrames();
        ApplyRewindPreviewAspect();

        AnimateOpacity(RewindOverlay, 0, 1, 200);
    }

    private void HideRewind()
    {
        if (_pausedOverlay != PausedOverlay.Rewind) return;
        _pausedOverlay = PausedOverlay.None;

        var storyboard = AnimateOpacity(RewindOverlay, 1, 0, 180);
        storyboard.Completed += (_, _) =>
        {
            RewindOverlay.Visibility = Visibility.Collapsed;
            _emuHost?.Resume();
        };
    }

    /// <summary>ページ離脱時などにアニメーションを待たず即座に巻き戻し画面を閉じる。</summary>
    private void CloseRewindImmediate()
    {
        if (_pausedOverlay == PausedOverlay.Rewind) _pausedOverlay = PausedOverlay.None;
        RewindOverlay.Visibility = Visibility.Collapsed;
        RewindOverlay.Opacity = 0;
        RewindPreviewImage.Source = null;
    }

    /// <summary>
    /// EmulationHost.GetRewindFrames() (メモリ上のリングバッファ)から一覧を取得し、
    /// フィルムストリップ表示用のビューモデルへ変換する。ファイルI/Oが無いため同期処理。
    /// HWレンダリングのコア(N64など)では巻き戻しキャプチャ自体が無効なため、
    /// その場合は一覧が空になり、XAML側で案内メッセージを表示する。
    /// </summary>
    private void LoadRewindFrames()
    {
        RewindFrames.Clear();
        double thumbHeight = ThumbnailBoxWidth / GetDisplayAspectRatio();

        if (_emuHost is not null)
        {
            var frames = _emuHost.GetRewindFrames();
            for (int i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                RewindFrames.Add(new RewindFrameInfo
                {
                    Index = i,
                    Thumbnail = CreateBitmapFromBgra(frame.ScreenshotBgra, frame.ScreenshotWidth, frame.ScreenshotHeight),
                    ThumbnailWidth = ThumbnailBoxWidth,
                    ThumbnailHeight = thumbHeight,
                });
            }
        }

        RewindItemsControl.ItemsSource = null;
        RewindItemsControl.ItemsSource = RewindFrames;

        // 直近(最新)のフレームを初期選択にする。
        SetRewindIndex(RewindFrames.Count - 1);
    }

    private void SetRewindIndex(int index, bool animate = false)
    {
        if (RewindFrames.Count == 0)
        {
            _rewindIndex = -1;
            HasSelectedRewindFrame = false;
            RewindPreviewImage.Source = null;
            Bindings.Update();
            return;
        }

        index = Math.Clamp(index, 0, RewindFrames.Count - 1);
        int previousIndex = _rewindIndex;
        foreach (var f in RewindFrames) f.IsSelected = false;
        RewindFrames[index].IsSelected = true;
        _rewindIndex = index;

        HasSelectedRewindFrame = true;
        RewindPreviewImage.Source = RewindFrames[index].Thumbnail;
        Bindings.Update();

        ScrollSelectedRewindItemIntoView(animate);
        ApplyRewindScale(previousIndex, animate);
    }

    /// <summary>選択中のコマは少し拡大して見せる。動かすときは、元のコマが縮み、新しいコマが拡大するアニメーションにする。</summary>
    private void ApplyRewindScale(int previousIndex, bool animate)
    {
        const float selectedScale = 1.12f;
        if (animate)
        {
            if (previousIndex >= 0 && previousIndex != _rewindIndex)
                EmulatorLauncher.Controls.PanelFlow.ScaleTo(RewindItemsControl.ContainerFromIndex(previousIndex) as UIElement, 1f, true);
            EmulatorLauncher.Controls.PanelFlow.ScaleTo(RewindItemsControl.ContainerFromIndex(_rewindIndex) as UIElement, selectedScale, true);
            return;
        }

        for (int i = 0; i < RewindFrames.Count; i++)
            EmulatorLauncher.Controls.PanelFlow.ScaleTo(RewindItemsControl.ContainerFromIndex(i) as UIElement, i == _rewindIndex ? selectedScale : 1f, false);
    }

    /// <summary>選択中のサムネイルがフィルムストリップの横スクロール範囲内(中央)に見えるようにする。
    /// animate=trueなら滑らかにスクロールする(開いた瞬間だけは、最初から正しい位置に見せる)。</summary>
    private void ScrollSelectedRewindItemIntoView(bool animate = false)
    {
        if (_rewindIndex < 0) return;

        RewindFilmstripScroller.UpdateLayout();

        double viewportCenter = RewindFilmstripScroller.ActualWidth / 2;
        double target;
        if (RewindItemsControl.ContainerFromIndex(_rewindIndex) is FrameworkElement container && container.ActualWidth > 0)
        {
            // 実際の位置から求める(スクロール量+ビューポート内での位置+コマの半分)。
            double x = container.TransformToVisual(RewindFilmstripScroller).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
            target = RewindFilmstripScroller.HorizontalOffset + x + container.ActualWidth / 2 - viewportCenter;
        }
        else
        {
            const double itemWidth = ThumbnailBoxWidth + 14; // ItemTemplateのWidth + StackPanelのSpacing
            target = (_rewindIndex * itemWidth) + (itemWidth / 2) - viewportCenter;
        }

        RewindFilmstripScroller.ChangeView(Math.Max(0, target), null, null, disableAnimation: !animate);
    }

    private void HandleRewindInput(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.DPadLeft:
                if (_rewindIndex <= 0) EmulatorLauncher.Controls.ShakeAnimation.Shake(RewindItemsControl.ContainerFromIndex(_rewindIndex) as UIElement, vertical: false);
                else SetRewindIndex(_rewindIndex - 1, animate: true);
                break;
            case GamepadButton.DPadRight:
                if (_rewindIndex < 0 || _rewindIndex >= RewindFrames.Count - 1) EmulatorLauncher.Controls.ShakeAnimation.Shake(RewindItemsControl.ContainerFromIndex(_rewindIndex) as UIElement, vertical: false);
                else SetRewindIndex(_rewindIndex + 1, animate: true);
                break;
            case GamepadButton.A:
                if (_rewindIndex >= 0) _emuHost?.RequestRewindTo(_rewindIndex);
                HideRewind();
                break;
            case GamepadButton.B:
                HideRewind();
                break;
        }
    }

    private static Storyboard AnimateOpacity(FrameworkElement target, double from, double to, int milliseconds)
    {
        var storyboard = new Storyboard();
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
        storyboard.Begin();
        return storyboard;
    }

    /// <summary>一時停止した瞬間のゲーム画面を中断メニューの背景に薄く表示する。</summary>
    private void UpdateMenuBackground()
    {
        if (_emuHost is null || !_emuHost.TryGetPausedThumbnail(out var bgra, out int w, out int h))
        {
            MenuBackgroundImage.Source = null;
            return;
        }

        MenuBackgroundImage.Source = CreateBitmapFromBgra(bgra, w, h);
    }

    /// <summary>
    /// BGRAピクセル配列(EmulationHost.TryGetPausedThumbnail / RewindFrame.ScreenshotBgra が返す形式)
    /// からWriteableBitmapを作る共通ヘルパー。
    /// </summary>
    private static WriteableBitmap? CreateBitmapFromBgra(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length == 0) return null;

        var bitmap = new WriteableBitmap(width, height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(bgra, 0, bgra.Length);
        }

        return bitmap;
    }

    // ==================================================================
    // メニュー項目ナビゲーション
    // ==================================================================

    private void SetMenuIndex(int index)
    {
        if (MenuItems.Count == 0) return;
        index = Math.Clamp(index, 0, MenuItems.Count - 1);
        foreach (var item in MenuItems) item.IsSelected = false;
        MenuItems[index].IsSelected = true;
        _menuIndex = index;

        // 「セーブ」「ロード」「設定」にカーソルがある間は、まだフォーカスは移さずに右側の
        // スロット一覧/設定一覧を先に表示しておく(Switchの中断メニューと同じ。
        // A/十字キー右で右側へフォーカスが移る)。
        UpdateSlotsPreview();
    }

    /// <summary>スロット一覧のプレビュー読み込みの世代番号。カーソルが素早く動いた際、
    /// 古い読み込みの完了が新しい状態を上書きしないようにするために使う。</summary>
    private int _slotsPreviewVersion;

    /// <summary>
    /// 現在のメニュー項目が「セーブ」「ロード」なら、右側にスロット一覧を、「設定」なら設定一覧を、
    /// それぞれフォーカス無しで表示する。それ以外の項目なら右側を隠す。
    /// 右側(スロット/設定)にフォーカスがある間は何もしない。
    /// </summary>
    private void UpdateSlotsPreview()
    {
        if (_focus == FocusArea.Slots || _focus == FocusArea.Settings || _focus == FocusArea.Disks) return;
        if (_menuIndex < 0 || _menuIndex >= MenuItems.Count) return;

        PauseMenuAction action = MenuItems[_menuIndex].Action;
        InfoPanel.Visibility = Visibility.Collapsed;
        if (action != PauseMenuAction.ShowControls) HideControlsPanel();
        if (action == PauseMenuAction.SwitchDisk && _emuHost is { DiskCount: >= 2 })
        {
            HideSlotsPanel();
            HideSettingsPanel();
            ShowDiskPreview();
            return;
        }
        HideDiskPanel();
        if (action == PauseMenuAction.OpenSettings)
        {
            HideSlotsPanel();
            ShowSettingsPreview();
            return;
        }
        if (action == PauseMenuAction.ShowControls)
        {
            HideSlotsPanel();
            HideSettingsPanel();
            ShowControlsPreview();
            return;
        }

        HideSettingsPanel();
        if (action != PauseMenuAction.Save && action != PauseMenuAction.Load)
        {
            HideSlotsPanel();
            string? info = GetMenuActionDescription(action);
            if (info is not null)
            {
                InfoText.Text = info;
                InfoPanel.Visibility = Visibility.Visible;
            }
            return;
        }

        _slotsAreSaveMode = action == PauseMenuAction.Save;
        SlotsPanel.Visibility = Visibility.Visible;
        _ = LoadSlotsPreviewAsync();
    }

    /// <summary>中断メニューの項目にカーソルがあるとき、右側に出す説明文(右側に枠を持つ項目はnull)。</summary>
    private static string? GetMenuActionDescription(PauseMenuAction action) => action switch
    {
        PauseMenuAction.Continue => "ゲームを再開します。",
        PauseMenuAction.Reset => "ゲームをリセットして、最初から起動し直します。\n\nセーブしたデータやステートは消えません。",
        PauseMenuAction.BackToTitleSelect => "ゲームを終了して、ゲーム選択の画面にもどります。",
        PauseMenuAction.SwitchDisk => "ディスクを次のものに入れ替えます。\n\nゲームの指示に合わせて切り替えてください。",
        PauseMenuAction.OpenSystemSettings => "本体の音量や画面の明るさなどを設定します。",
        _ => null,
    };

    private async Task LoadSlotsPreviewAsync()
    {
        int version = ++_slotsPreviewVersion;
        await RefreshSaveSlotsAsync();

        // その間にカーソルが別項目へ移った(=世代が進んだ)場合は、この結果は使わない。
        if (version != _slotsPreviewVersion) return;

        // 読み込み中にスロット側へフォーカスが移っていた場合は、作り直された一覧に選択を付け直す。
        if (_focus == FocusArea.Slots) SetSlotIndex(_slotIndex);
    }

    private void EnterSlotsFocus(bool saveMode)
    {
        _focus = FocusArea.Slots;
        _slotsAreSaveMode = saveMode;
        //SlotsModeText.Text = saveMode ? "セーブ先のスロットを選択" : "ロードするスロットを選択";
        SlotsPanel.Visibility = Visibility.Visible;

        _ = LoadSlotsAndSelectFirstAsync();
    }

    private async Task LoadSlotsAndSelectFirstAsync()
    {
        await RefreshSaveSlotsAsync();
        SetSlotIndex(0);
    }

    private void HideSlotsPanel()
    {
        _slotsPreviewVersion++;
        SlotsPanel.Visibility = Visibility.Collapsed;
        foreach (var slot in SaveSlots) slot.IsSelected = false;
    }

    private void ExitSlotsFocus()
    {
        _focus = FocusArea.Menu;

        // メニューのカーソルは「セーブ」/「ロード」のままなので、一覧は隠さずに
        // 選択(フォーカス)だけ外す(右側はプレビュー表示に戻る)。
        foreach (var slot in SaveSlots) slot.IsSelected = false;
    }

    private void SetSlotIndex(int index, bool slide = false)
    {
        if (SaveSlots.Count == 0) return;
        index = Math.Clamp(index, 0, SaveSlots.Count - 1);
        int old = _slotIndex;
        foreach (var slot in SaveSlots) slot.IsSelected = false;
        SaveSlots[index].IsSelected = true;
        _slotIndex = index;

        // 上下の操作で選択が動いたときは、選択の帯を前の行から滑らせる。
        if (slide) EmulatorLauncher.Controls.HighlightSlide.SlideListRow(SlotsItemsControl, old, index);
    }

    /// <summary>
    /// EmulationHost.GetSaveSlots() (statesaveフォルダの実データ)から一覧を取得し、
    /// XAML側でバインドしている表示用モデル(Models.SaveSlotInfo)へ詰め替える。
    /// サムネイル画像の読み込みを含むため非同期。
    /// </summary>
    private async Task RefreshSaveSlotsAsync()
    {
        var slots = new System.Collections.Generic.List<SaveSlotInfo>();
        // DS/3DSのステートセーブ画像は4:3固定(画面レイアウトや表示比率設定の影響を受けない)。
        bool fixed43 = _console?.Id is "nds" or "3ds";
        double slotAspect = fixed43 ? 4.0 / 3.0 : GetDisplayAspectRatio();
        // 縦長の画面(縦シューティング等)だと枠が縦に伸びてスロット一覧が画面に収まらないので、
        // 高さは4:3相当(ThumbnailBoxWidth*3/4)までに制限し、足りない分は幅を狭めて比率を保つ。
        double thumbHeight = Math.Min(ThumbnailBoxWidth / slotAspect, ThumbnailBoxWidth * 3.0 / 4.0);
        double thumbWidth = thumbHeight * slotAspect;

        if (_emuHost is not null)
        {
            foreach (var slot in _emuHost.GetSaveSlots())
            {
                var info = new SaveSlotInfo
                {
                    SlotNumber = slot.Slot,
                    HasData = slot.HasData,
                    SavedAt = slot.SavedAt,
                    ThumbnailWidth = thumbWidth,
                    ThumbnailHeight = thumbHeight,
                };

                if (slot.ScreenshotPath is not null)
                {
                    info.Thumbnail = await TryLoadThumbnailAsync(slot.ScreenshotPath);
                }

                slots.Add(info);
            }

            // 「ロード」画面だけ、末尾にオートセーブ(約5分毎に自動で保存される枠)を出す。データが無ければ出さない。
            // 「セーブ」画面には出さない(手動で書き込む先ではないため)。
            if (!_slotsAreSaveMode)
            {
                var auto = _emuHost.GetAutoSaveSlotEntry();
                if (auto.HasData)
                {
                    var info = new SaveSlotInfo
                    {
                        SlotNumber = 0,
                        IsAutoSave = true,
                        HasData = true,
                        SavedAt = auto.SavedAt,
                        ThumbnailWidth = thumbWidth,
                        ThumbnailHeight = thumbHeight,
                    };
                    if (auto.ScreenshotPath is not null)
                    {
                        info.Thumbnail = await TryLoadThumbnailAsync(auto.ScreenshotPath);
                    }
                    slots.Add(info);
                }
            }
        }

        SaveSlots.Clear();
        foreach (var info in slots) SaveSlots.Add(info);

        // Listの中身を書き換えただけではItemsControlに変更が伝わらない(x:BindはOneTime)ため、
        // GameSelectPageの一覧更新と同じ要領でItemsSourceを明示的に付け替えて反映させる。
        SlotsItemsControl.ItemsSource = null;
        SlotsItemsControl.ItemsSource = SaveSlots;
    }

    /// <summary>
    /// EmulationHostが生成したセーブスロットのスクリーンショット(.bmp)を読み込む。
    ///
    /// 注意: BitmapImage.UriSource に同じファイルパスを繰り返し指定すると、ファイルの
    /// 中身が上書き更新されていてもOS側の画像キャッシュから古い画像が返ってくることが
    /// ある(セーブファイルは同じパスへ上書き保存されるため、この問題を踏みやすい)。
    /// それを避けるため、StorageFile + ストリーム経由で毎回明示的に読み込み直す。
    ///
    /// ファイルが壊れている/書き込み中などの理由で失敗しても一覧全体を巻き添えで
    /// 壊さないよう例外は握りつぶし、プレースホルダー表示(ThumbnailText)へ
    /// フォールバックする。
    /// </summary>
    private static async Task<BitmapImage?> TryLoadThumbnailAsync(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return null;

            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenAsync(FileAccessMode.Read);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    // ==================================================================
    // ゲームパッド入力(一時停止中、EmulationHostから転送されるメニュー操作)
    // ==================================================================

    private void OnPausedMenuButtonPressed(GamepadButton button)
    {
        // 中断メニューも、ランチャーの他の画面と同じく「操作ボタンの入れ替え」(A/B)に従う。
        button = ControllerMappingService.TranslateForLauncher(button);

        DispatcherQueue.TryEnqueue(() =>
        {
            if (button == GamepadButton.Touchpad)
            {
                if (_pausedOverlay != PausedOverlay.Menu) return;
                if (DateTime.UtcNow - _touchpadOpenedUtc < TimeSpan.FromMilliseconds(600)) return;
                if (SystemSettings.IsOpen) SystemSettings.Close();
                else if (_focus == FocusArea.Menu) SystemSettings.Show();
                return;
            }

            // 中断メニュー/巻き戻し画面の操作音(移動=move、決定=accept、戻る=back)。
            if (_pausedOverlay != PausedOverlay.None)
            {
                switch (button)
                {
                    case GamepadButton.DPadUp:
                    case GamepadButton.DPadDown:
                    case GamepadButton.DPadLeft:
                    case GamepadButton.DPadRight:
                        EmulatorLauncher.Services.SoundEffects.Play("move");
                        break;
                    case GamepadButton.A:
                        EmulatorLauncher.Services.SoundEffects.Play("accept");
                        break;
                    case GamepadButton.B:
                        EmulatorLauncher.Services.SoundEffects.Play("back");
                        break;
                }
            }

            // システム設定パネルが開いている間は、入力をすべてパネルへ渡す(Bで閉じると中断メニューへ戻る)。
            if (_pausedOverlay == PausedOverlay.Menu && SystemSettings.IsOpen)
            {
                SystemSettings.HandleGamepadInput(button);
                return;
            }

            switch (_pausedOverlay)
            {
                case PausedOverlay.Menu:
                    switch (_focus)
                    {
                        case FocusArea.Menu:
                            HandleMenuInput(button);
                            break;
                        case FocusArea.Slots:
                            HandleSlotsInput(button);
                            break;
                        case FocusArea.Settings:
                            HandleSettingsInput(button);
                            break;
                        case FocusArea.Disks:
                            HandleDiskInput(button);
                            break;
                    }
                    break;
                case PausedOverlay.Rewind:
                    HandleRewindInput(button);
                    break;
            }
        });
    }

    private void OnPausedMenuButtonLongPressed(GamepadButton button)
    {
        // 現状、中断メニューに長押し操作は割り当てていない。
    }

    private void HandleMenuInput(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.DPadUp:
                MoveMenuCursor(-1);
                break;
            case GamepadButton.DPadDown:
                MoveMenuCursor(1);
                break;
            case GamepadButton.DPadLeft:
                // これ以上左へは行けない。
                EmulatorLauncher.Controls.ShakeAnimation.Shake(MenuItemsControl.ContainerFromIndex(_menuIndex) as UIElement, vertical: false);
                break;
            case GamepadButton.A:
            case GamepadButton.DPadRight:
                ExecuteMenuAction(MenuItems[_menuIndex].Action);
                break;
            case GamepadButton.B:
                HideMenu();
                break;
        }
    }

    /// <summary>メニューのカーソルを上下に動かす。選択の帯が滑り、右側の枠は動かした向きに合わせて流れ込む。
    /// 端でこれ以上動かせないときは、選択行を揺らす。</summary>
    private void MoveMenuCursor(int delta)
    {
        int old = _menuIndex;
        int target = old + delta;
        if (target < 0 || target >= MenuItems.Count)
        {
            EmulatorLauncher.Controls.ShakeAnimation.Shake(MenuItemsControl.ContainerFromIndex(old) as UIElement, vertical: true);
            return;
        }

        SetMenuIndex(target);
        EmulatorLauncher.Controls.HighlightSlide.SlideElement(MenuRowBackground(target), MenuRowBackground(old));

        FrameworkElement? panel = SlotsPanel.Visibility == Visibility.Visible ? SlotsPanel
            : SettingsPanel.Visibility == Visibility.Visible ? SettingsPanel
            : DiskPanel.Visibility == Visibility.Visible ? DiskPanel
            : ControlsPanel.Visibility == Visibility.Visible ? ControlsPanel : null;
        EmulatorLauncher.Controls.PanelFlow.In(panel, delta);
    }

    /// <summary>メニュー行の、選択の帯(行の先頭の要素)。</summary>
    private UIElement? MenuRowBackground(int index)
    {
        if (MenuItemsControl.ContainerFromIndex(index) is not DependencyObject container) return null;
        if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(container) == 0) return null;
        return Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(container, 0) is Microsoft.UI.Xaml.Controls.Grid g && g.Children.Count > 0
            ? g.Children[0] : null;
    }
}
