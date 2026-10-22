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

// GamePlayPage の一部(partial class)。中断メニューの右側の枠(操作方法の確認・ディスク入れ替え・設定)。
// 動作は変えず、巨大だったGamePlayPage.xaml.csを役割ごとに分けただけ。
public sealed partial class GamePlayPage
{
    // ==================================================================
    // 操作方法の確認(いま使っているコントローラーの割り当てを、右側に見せる。「設定」と同じく、
    // カーソルを合わせている間だけ出る。決定しても別の画面へは進まない)
    // ==================================================================

    private void ShowControlsPreview()
    {
        string scope = ResolveControllerScope(_console?.Id ?? string.Empty);
        ControlsGuideBox.Child = new ControlsGuideView(scope, _game?.Custom);
        ControlsHotkeyText.Text = BuildHotkeySummary();
        ControlsHotkeyText.Visibility = ControlsHotkeyText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ControlsPanel.Visibility = Visibility.Visible;
    }

    private void HideControlsPanel()
    {
        if (ControlsPanel.Visibility == Visibility.Collapsed) return;
        ControlsPanel.Visibility = Visibility.Collapsed;
        ControlsGuideBox.Child = null;
    }

    /// <summary>「操作方法」の枠の上に出す、呼び出し操作の一覧(中断メニュー・早送り・巻き戻しの割り当て)。</summary>
    private string BuildHotkeySummary()
    {
        if (_hintHotkeys is not { } hotkeys) return string.Empty;

        static string Combo(GamepadButton[] buttons) => string.Join("+", buttons.Select(HintButtonLabel));
        bool rewind = IsRewindAvailable;
        var parts = new List<string>();
        if (hotkeys.Menu.Length > 0)
        {
            parts.Add($"中断メニュー {Combo(hotkeys.Menu)}" + (_menuSameAsRewind && rewind ? "(長押しで巻き戻し)" : string.Empty));
        }
        if (hotkeys.FastForward.Length > 0) parts.Add($"早送り {Combo(hotkeys.FastForward)}");
        if (hotkeys.Rewind.Length > 0 && !_menuSameAsRewind && rewind) parts.Add($"巻き戻し {Combo(hotkeys.Rewind)}");
        if (hotkeys.PointerRecenter.Length > 0) parts.Add($"照準を中央へ(ジャイロ) {Combo(hotkeys.PointerRecenter)}");
        return string.Join("    ", parts);
    }

    private void HandleSlotsInput(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.DPadUp:
                if (_slotIndex <= 0) EmulatorLauncher.Controls.ShakeAnimation.Shake(SlotsItemsControl.ContainerFromIndex(_slotIndex) as UIElement, vertical: true);
                else SetSlotIndex(_slotIndex - 1, slide: true);
                break;
            case GamepadButton.DPadDown:
                if (_slotIndex >= SaveSlots.Count - 1) EmulatorLauncher.Controls.ShakeAnimation.Shake(SlotsItemsControl.ContainerFromIndex(_slotIndex) as UIElement, vertical: true);
                else SetSlotIndex(_slotIndex + 1, slide: true);
                break;
            case GamepadButton.DPadRight:
                EmulatorLauncher.Controls.ShakeAnimation.Shake(SlotsItemsControl.ContainerFromIndex(_slotIndex) as UIElement, vertical: false);
                break;
            case GamepadButton.DPadLeft:
            case GamepadButton.B:
                ExitSlotsFocus();
                break;
            case GamepadButton.A:
                ExecuteSlotAction();
                break;
        }
    }

    private void ExecuteMenuAction(PauseMenuAction action)
    {
        switch (action)
        {
            case PauseMenuAction.Continue:
                HideMenu();
                break;
            case PauseMenuAction.Save:
                EnterSlotsFocus(saveMode: true);
                break;
            case PauseMenuAction.Load:
                EnterSlotsFocus(saveMode: false);
                break;
            case PauseMenuAction.Reset:
                _emuHost?.RequestReset();
                HideMenu();
                break;
            case PauseMenuAction.OpenSettings:
                EnterSettingsFocus();
                break;
            case PauseMenuAction.OpenSystemSettings:
                SystemSettings.Show();
                break;
            case PauseMenuAction.SwitchDisk:
                if (_emuHost is { DiskCount: >= 2 })
                {
                    EnterDiskFocus();
                }
                else
                {
                    // コアにディスク一覧が無い(FDSのボタン代行)ときは、従来どおり面を替えてメニューを閉じる。
                    _emuHost?.RequestNextDisk();
                    HideMenu();
                }
                break;
            case PauseMenuAction.ShowControls:
                break; // 右側に出ている表示を見るだけ(決定しても何も起きない)
            case PauseMenuAction.BackToTitleSelect:
                BackToTitleSelect();
                break;
        }
    }

    /// <summary>
    /// 画面比率設定を切り替える。選べる選択肢自体(2つか3つか)は起動中のコアによって変わるため、
    /// 固定のswitchではなく<see cref="BuildScreenLayoutSequence"/>が返す並びの中で
    /// 「次」へ進める。即座に設定ファイルへ保存し、実行中ならレイアウトも再計算する。
    /// </summary>
    private void CycleScreenLayout()
    {
        List<ScreenLayoutMode> sequence = BuildScreenLayoutSequence();
        int currentIndex = sequence.IndexOf(_settings.ScreenLayout);
        if (currentIndex < 0)
        {
            // 現在値がこのコアでは選べない並びに無い場合(例: LauncherDefaultが
            // Auto扱い/既に16:9と同値のため並びから省かれている等)は先頭からとみなす。
            currentIndex = 0;
        }

        _settings.ScreenLayout = sequence[(currentIndex + 1) % sequence.Count];

        // このゲームだけの設定として、標準(コア毎の値)との差分を保存する。
        string layoutValue = _settings.ScreenLayout.ToString();
        string layoutBase = _baseSettings.ScreenLayout.ToString();
        SaveGameCustom(o => RomCustom.SetSectionDiff(o, RomCustom.VideoKey, "aspect", layoutValue, layoutBase));

        ScheduleLayoutRecalc();
    }

    /// <summary>
    /// 中断メニューの画面比率設定で実際に選べる並びを、起動中のコアに応じて組み立てる。
    /// 常にAuto→16:9の2つは含み、LauncherDefault(このコアのLauncherInfo.DefaultAspectRatio)は
    /// 「値が固定されており(DefaultAspectRatioが"Auto"ではない)、かつ16:9そのものではない」
    /// 場合だけAutoと16:9の間に挟む。
    ///
    /// 例: ファミコン(4:3)は[Auto, LauncherDefault(4:3), Ratio16x9]の3択、
    /// GBA(3:2)は[Auto, LauncherDefault(3:2), Ratio16x9]の3択、
    /// PSP(16:9)は既に16:9と同じ値なので[Auto, Ratio16x9]の2択、
    /// MAME/ニンテンドーDS等(DefaultAspectRatio="Auto")も[Auto, Ratio16x9]の2択になる。
    /// </summary>
    private List<ScreenLayoutMode> BuildScreenLayoutSequence()
    {
        var sequence = new List<ScreenLayoutMode> { ScreenLayoutMode.Auto };

        if (_launcherDefaultAspectRatioLabel is not null
            && !string.Equals(_launcherDefaultAspectRatioLabel, "16:9", StringComparison.OrdinalIgnoreCase))
        {
            sequence.Add(ScreenLayoutMode.LauncherDefault);
        }

        sequence.Add(ScreenLayoutMode.Ratio16x9);
        return sequence;
    }

    /// <summary>
    /// 画質設定を なし → High Quality 3× scaler → なし … の順で切り替える。
    /// アスペクト比と同じく、即座に設定ファイルへ保存し、実行中のEmulationHostへも反映する
    /// (EmulationHost.PictureQualityは次に描画されるフレームから即座に効く)。
    /// 順次フィルターを追加していく想定なので、選択肢を増やすときはここのswitchと
    /// PictureQualityModeText()にケースを足すだけでよい。
    /// </summary>
    private void CyclePictureQuality()
    {
        _settings.PictureQuality = _settings.PictureQuality switch
        {
            PictureQualityMode.None => PictureQualityMode.HighQuality3x,
            PictureQualityMode.HighQuality3x => PictureQualityMode.Hq2x,
            PictureQualityMode.Hq2x => PictureQualityMode.None,
            _ => PictureQualityMode.None,
        };

        string qualityValue = _settings.PictureQuality.ToString();
        string qualityBase = _baseSettings.PictureQuality.ToString();
        SaveGameCustom(o => RomCustom.SetSectionDiff(o, RomCustom.VideoKey, "quality", qualityValue, qualityBase));

        if (_emuHost is not null)
            _emuHost.PictureQuality = _settings.PictureQuality;
    }

    /// <summary>
    /// 起動するコアに対応するcores/libretro.json側のカタログエントリを、コアのdllファイル名
    /// (LibretroCoreInfo.CoreName)で照合して_currentCatalogCoreへキャッシュする。
    /// 見つからない場合はnullのままで、その場合BuildSettingsItemsはコアオプションの項目を
    /// 一切追加しない。
    /// StartEmulationAsyncから、EmulationHost生成前に呼ぶ(実際にコアへ値を渡すのは
    /// 同じくStartEmulationAsync内、_emuHost.Start()の直前)。
    /// </summary>
    private void ResolveCurrentCoreOptions(string corePath)
    {
        string coreFileName = Path.GetFileName(corePath);
        // 同じコアを複数のコンソールが共用している(dolphin: gc/wii、flycast: dc/naomi)ため、
        // コア名だけで引くと別コンソールの定義(オプション一覧)を拾ってしまう。
        // まず「コンソールId+コア名」で探し、無ければコア名だけで探す。
        var catalog = LibretroCoreCatalogService.LoadCatalog();
        string consoleId = _console?.Id ?? string.Empty;
        _currentCatalogCore =
            catalog.FirstOrDefault(c => string.Equals(c.CoreName, coreFileName, StringComparison.OrdinalIgnoreCase)
                                        && string.Equals(c.ConsoleId, consoleId, StringComparison.OrdinalIgnoreCase))
            ?? catalog.FirstOrDefault(c => string.Equals(c.CoreName, coreFileName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 中断メニュー「設定」パネルで選択中のコアオプション項目(item.CoreOptionKeyで識別)を、
    /// Choicesの並びで次の値へ切り替える。アスペクト比/画質と同じく、切り替えるたびに
    /// 次の2つを行う:
    /// (a) cores/libretro.jsonへ永続化(PersistCoreOptionValueAsync。次回このコアで起動した
    ///     ときにResolveCurrentCoreOptions→StartEmulationAsyncのSetInitialCoreVariableで
    ///     同じ値を再適用できるように)。
    /// (b) 実行中のコアへ即座に反映(EmulationHost.RequestSetCoreVariable経由。実際にコア側へ
    ///     反映されるのは、コアが次にRETRO_ENVIRONMENT_GET_VARIABLE_UPDATEをポーリングした
    ///     タイミング)。
    /// _currentCatalogCore.Optionsは(SettingsPage側の編集も含め)常に同じインスタンスを
    /// LoadCatalog()経由で参照しているとは限らないため、Option.Valueへの書き込みは
    /// このインスタンス限りの即時反映用(表示・次回参照用のキャッシュ)であり、実際の
    /// 永続化はPersistCoreOptionValueAsyncが別途cores/libretro.jsonへ書き戻す。
    /// </summary>
    private void CycleCoreOption(SettingsMenuItem item)
    {
        if (_currentCatalogCore is null || string.IsNullOrEmpty(item.CoreOptionKey))
        {
            return;
        }

        LibretroCoreOptionInfo? option = _currentCatalogCore.Options.FirstOrDefault(
            o => string.Equals(o.Key, item.CoreOptionKey, StringComparison.Ordinal));
        if (option is null || option.Choices.Count == 0)
        {
            return;
        }

        string currentValue = GetEffectiveOptionValue(option);
        int currentIndex = option.Choices.FindIndex(c => string.Equals(c.Value, currentValue, StringComparison.Ordinal));
        int nextIndex = currentIndex < 0 ? 0 : (currentIndex + 1) % option.Choices.Count;
        LibretroCoreOptionChoice nextChoice = option.Choices[nextIndex];

        // cores/libretro.json(設定画面のコア詳細設定=標準値)は書き換えず、このゲームだけの
        // 差分としてRomFile.Customへ保存する。標準値に戻した場合は差分を消す。
        string baseValue = GetBaseOptionValue(option);
        string optionKey = option.Key;
        string newValue = nextChoice.Value;
        if (string.Equals(newValue, baseValue, StringComparison.Ordinal)) _gameOptions.Remove(optionKey);
        else _gameOptions[optionKey] = newValue;
        item.ValueText = nextChoice.Label;
        SaveGameCustom(o => RomCustom.SetSectionDiff(o, RomCustom.OptionsKey, optionKey, newValue, baseValue));

        // 再起動が必要な項目は、実行中のコアへは渡さない(設定を閉じたときの再起動で、起動時に渡す)。
        if (!option.RestartRequired)
        {
            _emuHost?.RequestSetCoreVariable(option.Key, nextChoice.Value);
        }

        // コントローラの形態を切り替えたら、その形態のボタン割り当てへ切り替える。
        if (ControllerProfiles.FindProfile(_console?.Id ?? string.Empty, _currentCatalogCore?.CoreName) is { } formProfile
            && !string.IsNullOrEmpty(formProfile.OptionKey)
            && string.Equals(formProfile.OptionKey, option.Key, StringComparison.Ordinal))
        {
            ApplyControllerMapping();
            // Wii以外は、コントローラのデバイスもその場で切り替える(再起動なし)。
            if (string.Equals(formProfile.OptionKey, EmulationHost.ControllerFormKey, StringComparison.Ordinal))
            {
                _emuHost?.RequestControllerPortDevice(ResolveControllerPortDevice());
                ApplyLightgunSettings();
            }
        }

        // このオプションの定義(cores/libretro.json)に"actions"が指定されていれば、
        // コア変数の変更と合わせて要求しておく(コアが本来自発的に知らせてくるべき
        // 変化を実際には知らせてこない、既知のコア個別の癖に対応するため。詳細は
        // LibretroCoreOptionInfo.ActionsおよびEmulationHost.RequestCoreOptionAction参照。
        // 特定コア名をここに書く必要は無く、JSON側の宣言だけで完結する)。
        foreach (string actionName in option.Actions)
        {
            _emuHost?.RequestCoreOptionAction(actionName);
        }
    }

    /// <summary>
    /// 変更後のコアオプション値をcores/libretro.jsonへ保存する(SettingsPage.PersistOptionValueAsyncと
    /// 同じ考え方。書き込みの完了を待たずに次のフレームから新しい値を使い始めてよいため、
    /// 呼び出し元では_ = ...で投げっぱなしにする)。
    /// </summary>
    private async Task PersistCoreOptionValueAsync(LibretroCoreInfo core, string optionKey, string newValue)
    {
        try
        {
            bool success = await LibretroCoreCatalogService.SetOptionValueAsync(core, optionKey, newValue);

            if (!success)
            {
                Debug.WriteLine(
                    $"[GamePlayPage] コアオプションの保存に失敗しました({core.CoreName}.{optionKey})。");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[GamePlayPage] コアオプションの保存中に例外が発生しました({core.CoreName}.{optionKey}): {ex.Message}");
        }
    }

    /// <summary>
    /// トップ画面のシステムアイコンバーの「フルスクリーン」アイコン(ConsoleSelectPage.xaml.cs の
    /// ActivateSystemIcon(2)参照)と同じ MainWindow.ToggleFullScreen() を呼ぶ。
    /// アスペクト比/画質/操作説明表示と異なり、ウィンドウの表示状態そのもの
    /// (AppWindow.Presenter.Kind)であってコアごとの設定ではないため、CoreSettingsへは
    /// 保存しない(値はMainWindow.IsFullScreenから毎回取得し直す)。
    /// </summary>
    private void ToggleFullscreenSetting()
    {
        AppServices.MainWindow?.ToggleFullScreen();
    }

    /// <summary>現在のウィンドウ状態(MainWindow.IsFullScreen)から「フルスクリーン」項目の表示テキストを返す。</summary>
    private static string FullscreenText() =>
        (AppServices.MainWindow?.IsFullScreen ?? false) ? "オン" : "オフ";

    /// <summary>
    /// 「操作説明表示」のON/OFFを切り替える。アスペクト比/画質と同じく、即座に設定ファイルへ
    /// 保存し、下部ガイドバー(PlayHintBar)の表示にも即座に反映する。
    /// 非表示時はApplyEmuHostLayout()側でも下部ガイドバー分の余白確保をやめるため、
    /// ScheduleLayoutRecalc()を呼んでゲーム画面のサイズにも即座に反映させる。
    /// </summary>
    private void ToggleShowControlGuide()
    {
        // ゲームごとではなく全体設定(アプリ設定)として保存する。
        // ただし、コア詳細設定でこのコアの表示を上書きしている場合は、その上書き値を書き換える
        // (そうしないと、トグルしても次回の起動で上書き値に戻ってしまう)。
        _showControlGuide = !_showControlGuide;
        if (_settings.ControlGuide is CoreSettings.ControlGuideOn or CoreSettings.ControlGuideOff)
        {
            _settings.ControlGuide = _showControlGuide ? CoreSettings.ControlGuideOn : CoreSettings.ControlGuideOff;
            CoreSettings stored = CoreSettingsStore.Load(_corePath);
            stored.ControlGuide = _settings.ControlGuide;
            CoreSettingsStore.Save(_corePath, stored);
        }
        else
        {
            AppPreferences preferences = AppPreferencesStore.Load();
            preferences.ShowControlGuide = _showControlGuide;
            AppPreferencesStore.Save(preferences);
        }

        ApplyShowControlGuide();
        ScheduleLayoutRecalc();
    }

    /// <summary>
    /// GamePlayPage.xaml側のRow 1(下部ガイドバー行)の元々の高さ(論理px)。「操作説明表示」が
    /// ONの間はこの値を、OFFの間は0をBottomBarRowDefinition.Heightへ設定する
    /// (ApplyShowControlGuide参照)。ApplyEmuHostLayout()が使うBottomBarHeightDip(70)とは
    /// 別の値であり、一致していなくてよい(そちらはWin32座標計算専用の予約高さであり、
    /// GetClientRectを基準に独自に計算されるため、実際のGridの行の高さには依存しない)。
    /// </summary>
    private const double BottomBarRowHeightDip = 70;

    /// <summary>
    /// 現在の _showControlGuide の値を、実際の下部ガイドバー(PlayHintBar、
    /// GamePlayPage.xaml内の「通常時 下部ガイドバー」)のVisibilityへ反映する。
    /// 設定読み込み直後(StartEmulationAsync)、トグル操作時(ToggleShowControlGuide)、
    /// 中断メニューを閉じたとき(HideMenu/CloseMenuImmediate)の3箇所から呼ばれる。
    ///
    /// 中断メニュー表示中(_pausedOverlay == PausedOverlay.Menu)は、設定のON/OFFに
    /// かかわらずPlayHintBarを強制的にCollapsedのままにする。ShowMenu()でいったん
    /// 隠した後、メニューを開いたまま「設定」→「操作説明表示」をONに切り替えると
    /// ToggleShowControlGuide()経由でこのメソッドが呼ばれるため、ここでガードを
    /// 入れておかないと、半透明の中断メニュー背景越しにPlayHintBarが再び透けて
    /// 見えてしまう(HideMenu/CloseMenuImmediateが呼ぶApplyShowControlGuide()で、
    /// メニューを閉じた瞬間にあらためて設定通りの表示へ戻る)。
    ///
    /// あわせて、Row 1(下部ガイドバー行)自体のGrid上の高さもOFF時は0にする。
    /// ApplyEmuHostLayout()側のWin32座標計算はウィンドウのクライアント領域全体
    /// (GetClientRect)を基準に独自に行っており、Gridの行境界には依存しないため、
    /// ライブ映像(SDLのネイティブウィンドウ)自体はこれが無くても正しく拡大される。
    /// しかし、中断メニューの背景に使う一時停止スクリーンショット(MenuBackgroundImage)は
    /// EmuHost(Border, Row 0のセル内の通常のWinUI要素)の子であり、通常のレイアウト/
    /// クリッピングに従うため、Row 0のセル自体を広げておかないとRow 1分だけ見切れて
    /// しまう。そのため、ここでRow 1の高さもあわせて0へ縮める(この高さ自体はメニュー
    /// 表示中でも設定通りに更新してよい。ゲーム画面レイアウトの計算に使われるだけで、
    /// PlayHintBar自体の見た目には影響しないため)。
    /// </summary>
    private void ApplyShowControlGuide()
    {
        PlayHintBar.Visibility = (_showControlGuide && _pausedOverlay != PausedOverlay.Menu)
            ? Visibility.Visible
            : Visibility.Collapsed;

        BottomBarRowDefinition.Height = _showControlGuide
            ? new GridLength(BottomBarRowHeightDip)
            : new GridLength(0);

        UpdateFastForwardLabel();
    }

    /// <summary>
    /// LauncherDefaultモードの表示名は起動中コアによって変わる(例: ファミコンなら"4:3"、
    /// GBAなら"3:2")ため、_launcherDefaultAspectRatioLabelを参照するインスタンスメソッドに
    /// している(staticには出来ない)。DefaultAspectRatioが"Auto"の機種(値なし)の場合は
    /// 「オート」と表示する(実際の挙動もAutoと同じになるため。GetDisplayAspectRatio参照)。
    /// </summary>
    private string ScreenLayoutModeText(ScreenLayoutMode mode) => mode switch
    {
        ScreenLayoutMode.Ratio16x9 => "16:9",
        ScreenLayoutMode.LauncherDefault => _launcherDefaultAspectRatioLabel ?? "オート",
        _ => "オート",
    };

    private static string PictureQualityModeText(PictureQualityMode mode) => mode switch
    {
        PictureQualityMode.HighQuality3x => "High Quality 3× scaler",
        PictureQualityMode.Hq2x => "HQ2x",
        _ => "なし",
    };

    private static string ShowControlGuideText(bool show) => show ? "オン" : "オフ";

    // ==================================================================
    // 中断メニュー「ディスク入れ替え」枠(右側。コアが持つディスクの一覧から、入れるディスクを選ぶ)
    // ==================================================================

    /// <summary>ディスク一覧を作り直す。いま入っているディスクには「(挿入中)」が付く。</summary>
    private void BuildDiskItems()
    {
        DiskItems.Clear();
        if (_emuHost is not null)
        {
            int count = _emuHost.DiskCount;
            int current = _emuHost.DiskIndex;
            for (int i = 0; i < count; i++)
            {
                DiskItems.Add(new DiskMenuItem { Index = i, Name = _emuHost.GetDiskName(i), IsCurrent = i == current });
            }
        }

        DiskItemsControl.ItemsSource = null;
        DiskItemsControl.ItemsSource = DiskItems;
    }

    /// <summary>メニューのカーソルが「ディスク入れ替え」にある間、フォーカス無しで一覧を見せる。</summary>
    private void ShowDiskPreview()
    {
        BuildDiskItems();
        DiskPanel.Visibility = Visibility.Visible;
    }

    private void EnterDiskFocus()
    {
        BuildDiskItems();
        if (DiskItems.Count == 0) return;
        _focus = FocusArea.Disks;
        DiskPanel.Visibility = Visibility.Visible;
        SetDiskIndex(Math.Max(DiskItems.FindIndex(d => d.IsCurrent), 0));
    }

    private void ExitDiskFocus()
    {
        _focus = FocusArea.Menu;
        foreach (var item in DiskItems) item.IsSelected = false;
    }

    private void HideDiskPanel()
    {
        if (DiskPanel.Visibility == Visibility.Collapsed) return;
        DiskPanel.Visibility = Visibility.Collapsed;
        foreach (var item in DiskItems) item.IsSelected = false;
    }

    private void SetDiskIndex(int index, bool slide = false)
    {
        if (DiskItems.Count == 0) return;
        index = Math.Clamp(index, 0, DiskItems.Count - 1);
        int old = _diskIndex;
        foreach (var item in DiskItems) item.IsSelected = false;
        DiskItems[index].IsSelected = true;
        _diskIndex = index;

        // 上下の操作で選択が動いたときは、選択の帯を前の行から滑らせる。
        if (slide) EmulatorLauncher.Controls.HighlightSlide.SlideListRow(DiskItemsControl, old, index);

        if (DiskItemsControl.ContainerFromIndex(index) is FrameworkElement container)
        {
            container.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        }
    }

    private void HandleDiskInput(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.DPadUp:
                if (_diskIndex <= 0) EmulatorLauncher.Controls.ShakeAnimation.Shake(DiskItemsControl.ContainerFromIndex(_diskIndex) as UIElement, vertical: true);
                else SetDiskIndex(_diskIndex - 1, slide: true);
                break;
            case GamepadButton.DPadDown:
                if (_diskIndex >= DiskItems.Count - 1) EmulatorLauncher.Controls.ShakeAnimation.Shake(DiskItemsControl.ContainerFromIndex(_diskIndex) as UIElement, vertical: true);
                else SetDiskIndex(_diskIndex + 1, slide: true);
                break;
            case GamepadButton.DPadRight:
                EmulatorLauncher.Controls.ShakeAnimation.Shake(DiskItemsControl.ContainerFromIndex(_diskIndex) as UIElement, vertical: false);
                break;
            case GamepadButton.DPadLeft:
            case GamepadButton.B:
                ExitDiskFocus();
                break;
            case GamepadButton.A:
                // 選んだディスクへ入れ替えて、メニューを閉じる(いま入っているディスクを選んだときは何も変わらない)。
                if (_diskIndex >= 0 && _diskIndex < DiskItems.Count)
                {
                    _emuHost?.RequestDiskSwitch(DiskItems[_diskIndex].Index);
                }
                ExitDiskFocus();
                HideMenu();
                break;
        }
    }

    // ==================================================================
    // 中断メニュー「設定」枠(右側。アスペクト比/画質など)
    // ==================================================================

    private void EnterSettingsFocus()
    {
        _focus = FocusArea.Settings;
        _restartSnapshot = CaptureRestartValues();
        BuildSettingsItems();
        SettingsPanel.Visibility = Visibility.Visible;
        SetSettingsIndex(0);
    }

    /// <summary>メニューのカーソルが「設定」にある間、フォーカス無し(項目を選択しない)で設定一覧を表示する。</summary>
    private void ShowSettingsPreview()
    {
        BuildSettingsItems();
        foreach (var item in SettingsItems) item.IsSelected = false;
        SettingsPanel.Visibility = Visibility.Visible;
    }

    private void ExitSettingsFocus()
    {
        _focus = FocusArea.Menu;

        // 設定を閉じる時に、再起動が必要な項目が開いた時から変わっていれば、まとめて1回だけ再起動する。
        if (_restartSnapshot is { } snapshot)
        {
            _restartSnapshot = null;
            Dictionary<string, string> now = CaptureRestartValues();
            bool changed = now.Count != snapshot.Count
                || snapshot.Any(kv => !now.TryGetValue(kv.Key, out string? v) || !string.Equals(v, kv.Value, StringComparison.Ordinal));
            if (changed)
            {
                _ = RestartCoreWithStateAsync();
            }
        }

        // メニューのカーソルは「設定」のままなので、一覧は隠さずに選択(フォーカス)だけ外す
        // (右側はプレビュー表示に戻る)。
        foreach (var item in SettingsItems) item.IsSelected = false;
    }

    private void HideSettingsPanel()
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        foreach (var item in SettingsItems) item.IsSelected = false;
    }

    /// <summary>
    /// 設定枠の項目を作り直す。「画質」はソフトウェアレンダリングのコアでのみ選択できる
    /// (HWレンダリング中は生ピクセルバッファが取れず、独自スケーラーを適用できないため)。
    /// 「フルスクリーン」「操作説明表示」はレンダリング方式に関係なく常に選択できる。
    /// </summary>
    private void BuildSettingsItems()
    {
        SettingsItems.Clear();

        SettingsItems.Add(new SettingsMenuItem
        {
            Action = SettingsMenuAction.AspectRatio,
            Label = "アスペクト比",
            ValueText = ScreenLayoutModeText(_settings.ScreenLayout),
        });

        if (_emuHost is not null && !_emuHost.IsHwRendered)
        {
            SettingsItems.Add(new SettingsMenuItem
            {
                Action = SettingsMenuAction.PictureQuality,
                Label = "画質",
                ValueText = PictureQualityModeText(_settings.PictureQuality),
            });
        }

        SettingsItems.Add(new SettingsMenuItem
        {
            Action = SettingsMenuAction.Fullscreen,
            Label = "フルスクリーン",
            ValueText = FullscreenText(),
        });

        SettingsItems.Add(new SettingsMenuItem
        {
            Action = SettingsMenuAction.ShowControlGuide,
            Label = "操作説明表示",
            ValueText = ShowControlGuideText(_showControlGuide),
        });

        // 起動中コアのcores/libretro.json側のOptionsを汎用的に列挙する。SettingsPageの
        // 「コア毎の設定」タブと同じ考え方で、コアの種類に関係なくOptionsに項目を足すだけで
        // ここにも自動的に出る。Hide=trueの項目、およびChoicesが空(切り替えようが無い)の
        // 項目は表示しない(前者はLibretroCoreOptionInfo.Hideのドキュメントコメント参照)。
        if (_currentCatalogCore is not null)
        {
            foreach (LibretroCoreOptionInfo option in _currentCatalogCore.Options)
            {
                if (option.Hide || option.Choices.Count == 0)
                {
                    continue;
                }

                string currentValue = GetEffectiveOptionValue(option);
                string displayValue = option.Choices.FirstOrDefault(
                    c => string.Equals(c.Value, currentValue, StringComparison.Ordinal))?.Label ?? currentValue;

                SettingsItems.Add(new SettingsMenuItem
                {
                    Action = SettingsMenuAction.CoreOption,
                    CoreOptionKey = option.Key,
                    Label = option.Label,
                    ValueText = displayValue,
                });
            }
        }

        // このゲームで標準(コア詳細設定)と違う設定があるときだけ、「標準にする」項目を末尾に並べる。
        if (HasDefaultableChanges())
        {
            SettingsItems.Add(CreateApplyDefaultsItem());
        }

        // Listの中身を書き換えただけではItemsControlに変更が伝わらない(x:BindはOneTime)ため、
        // SaveSlots/RewindFramesと同じ要領でItemsSourceを明示的に付け替えて反映させる。
        SettingsItemsControl.ItemsSource = null;
        SettingsItemsControl.ItemsSource = SettingsItems;

        // 前に下の方の項目を選んでスクロールしていても、作り直した一覧は先頭から見せる
        // (スクロール位置が残ったままだと、先頭の項目を選んでも枠の外になり、選択中の項目が見えない)。
        SettingsScrollViewer.ChangeView(null, 0, null, true);
        SettingsItemsControl.UpdateLayout();
    }

    private void SetSettingsIndex(int index, bool slide = false)
    {
        if (SettingsItems.Count == 0) return;
        index = Math.Clamp(index, 0, SettingsItems.Count - 1);
        int old = _settingsIndex;
        foreach (var item in SettingsItems) item.IsSelected = false;
        SettingsItems[index].IsSelected = true;
        _settingsIndex = index;

        // 上下の操作で選択が動いたときは、選択の帯を前の行から滑らせる。
        if (slide) EmulatorLauncher.Controls.HighlightSlide.SlideListRow(SettingsItemsControl, old, index);

        // 項目が枠に収まらないときは、選択中の項目が見える位置までスクロールする。
        // 一覧を作り直した直後は、項目の表示用の部品がまだ無いことがあるので、先にレイアウトを済ませる。
        FrameworkElement? container = SettingsItemsControl.ContainerFromIndex(index) as FrameworkElement;
        if (container is null)
        {
            SettingsItemsControl.UpdateLayout();
            container = SettingsItemsControl.ContainerFromIndex(index) as FrameworkElement;
        }

        container?.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
    }

    private void HandleSettingsInput(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.DPadUp:
                if (_settingsIndex <= 0) EmulatorLauncher.Controls.ShakeAnimation.Shake(SettingsItemsControl.ContainerFromIndex(_settingsIndex) as UIElement, vertical: true);
                else SetSettingsIndex(_settingsIndex - 1, slide: true);
                break;
            case GamepadButton.DPadDown:
                if (_settingsIndex >= SettingsItems.Count - 1) EmulatorLauncher.Controls.ShakeAnimation.Shake(SettingsItemsControl.ContainerFromIndex(_settingsIndex) as UIElement, vertical: true);
                else SetSettingsIndex(_settingsIndex + 1, slide: true);
                break;
            case GamepadButton.A:
            case GamepadButton.DPadRight:
                ExecuteSettingsAction();
                break;
            case GamepadButton.DPadLeft:
            case GamepadButton.B:
                ExitSettingsFocus();
                break;
        }
    }

    /// <summary>
    /// 選択中の設定項目の値を切り替える(アスペクト比/画質/フルスクリーン/操作説明表示 共通)。
    /// 値を変えたあとは、その項目のValueTextだけを更新して即座に表示へ反映する。
    /// </summary>
    private void ExecuteSettingsAction()
    {
        if (SettingsItems.Count == 0) return;
        var item = SettingsItems[_settingsIndex];

        switch (item.Action)
        {
            case SettingsMenuAction.AspectRatio:
                CycleScreenLayout();
                item.ValueText = ScreenLayoutModeText(_settings.ScreenLayout);
                break;
            case SettingsMenuAction.PictureQuality:
                CyclePictureQuality();
                item.ValueText = PictureQualityModeText(_settings.PictureQuality);
                break;
            case SettingsMenuAction.Fullscreen:
                ToggleFullscreenSetting();
                item.ValueText = FullscreenText();
                break;
            case SettingsMenuAction.ShowControlGuide:
                ToggleShowControlGuide();
                item.ValueText = ShowControlGuideText(_showControlGuide);
                break;
            case SettingsMenuAction.CoreOption:
                // CycleCoreOption内でitem.ValueTextの更新まで行う(表示ラベルはChoices側の
                // Labelを使うため、他のcaseのように呼び出し元で組み立て直す必要が無い)。
                CycleCoreOption(item);
                break;
            case SettingsMenuAction.ApplyDefaults:
                ApplyCurrentSettingsAsCoreDefaults();
                break;
        }

        // 画質・コアオプションを変えたら、「標準にする」項目を出す/消す。
        if (item.Action is SettingsMenuAction.PictureQuality or SettingsMenuAction.CoreOption or SettingsMenuAction.ApplyDefaults)
        {
            SyncApplyDefaultsItem();
        }
    }

    // ---- 「標準にする」(このゲームの設定を、コア詳細設定の標準へ反映) ----

    private static SettingsMenuItem CreateApplyDefaultsItem() => new()
    {
        Action = SettingsMenuAction.ApplyDefaults,
        Label = "この設定をコアの標準にする(決定ボタンで反映)",
    };

    /// <summary>
    /// このゲームの設定が、コア詳細設定の標準と違うか(画質、コアオプション)。アスペクト比は対象外。
    /// </summary>
    private bool HasDefaultableChanges()
    {
        if (_emuHost is not null && !_emuHost.IsHwRendered
            && _settings.PictureQuality != _baseSettings.PictureQuality)
        {
            return true;
        }

        if (_currentCatalogCore is null) return false;
        foreach (KeyValuePair<string, string> kv in _gameOptions)
        {
            LibretroCoreOptionInfo? option = _currentCatalogCore.Options.FirstOrDefault(
                o => string.Equals(o.Key, kv.Key, StringComparison.Ordinal));
            if (option is not null && !option.Hide
                && !string.Equals(kv.Value, GetBaseOptionValue(option), StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>設定の一覧の末尾の「標準にする」項目を、必要なら足し、不要なら外す(他の項目の位置は変えない)。</summary>
    private void SyncApplyDefaultsItem()
    {
        bool need = HasDefaultableChanges();
        int index = -1;
        for (int i = 0; i < SettingsItems.Count; i++)
        {
            if (SettingsItems[i].Action == SettingsMenuAction.ApplyDefaults) { index = i; break; }
        }

        if (need && index < 0)
        {
            SettingsItems.Add(CreateApplyDefaultsItem());
        }
        else if (!need && index >= 0)
        {
            SettingsItems.RemoveAt(index);
            if (_settingsIndex >= SettingsItems.Count) SetSettingsIndex(SettingsItems.Count - 1);
            else SetSettingsIndex(_settingsIndex);
        }
    }

    /// <summary>
    /// このゲームで変えた画質・コアオプションを、このコアの標準(config\<コア>.json の画質、
    /// cores/libretro.json のコアオプション値)へ書き込み、このゲームだけの差分は消す。
    /// 他のゲームも、次に起動したときからこの値になる。アスペクト比は対象外。
    /// </summary>
    private void ApplyCurrentSettingsAsCoreDefaults()
    {
        if (_settings.PictureQuality != _baseSettings.PictureQuality)
        {
            PictureQualityMode quality = _settings.PictureQuality;
            CoreSettings stored = CoreSettingsStore.Load(_corePath);
            stored.PictureQuality = quality;
            CoreSettingsStore.Save(_corePath, stored);
            _baseSettings.PictureQuality = quality;

            string qualityValue = quality.ToString();
            SaveGameCustom(o => RomCustom.SetSectionDiff(o, RomCustom.VideoKey, "quality", qualityValue, qualityValue));
        }

        if (_currentCatalogCore is not null)
        {
            foreach (KeyValuePair<string, string> kv in _gameOptions.ToList())
            {
                LibretroCoreOptionInfo? option = _currentCatalogCore.Options.FirstOrDefault(
                    o => string.Equals(o.Key, kv.Key, StringComparison.Ordinal));
                if (option is null) continue;

                string key = kv.Key;
                string value = kv.Value;
                _ = PersistCoreOptionValueAsync(_currentCatalogCore, key, value);   // option.Valueもその場で更新される
                _gameOptions.Remove(key);
                SaveGameCustom(o => RomCustom.SetSectionDiff(o, RomCustom.OptionsKey, key, value, value));
            }
        }
    }

    /// <summary>ステート画像を表示するときの比率。ロード画面の一覧と同じ規則(DS/3DSは4:3固定、他は画面比率設定)。</summary>
    private double GetStateImageAspect() => _console?.Id is "nds" or "3ds" ? 4.0 / 3.0 : GetDisplayAspectRatio();

    /// <summary>
    /// 現在選択中の画面比率設定(既定/Auto/4:3/16:9)から実際に使うアスペクト比(幅/高さ)を返す。
    /// EmuHostのレイアウト計算・巻き戻しプレビュー・サムネイルの箱サイズ計算など、
    /// アスペクト比が必要な箇所はすべてこれを共通で使う。
    /// </summary>
    private double GetDisplayAspectRatio()
    {
        double aspect = _settings.ScreenLayout switch
        {
            ScreenLayoutMode.Ratio16x9 => 16.0 / 9.0,
            // LauncherDefault: LauncherInfo.DefaultAspectRatio(Launchers.json)の値をそのまま使う
            // (StartEmulationAsyncで解決済み)。DefaultAspectRatioが"Auto"の機種
            // (MAME/ニンテンドーDS等、_launcherDefaultAspectRatioがnull)は、
            // Autoモードと同じくコアの申告値へフォールバックする。
            ScreenLayoutMode.LauncherDefault => _launcherDefaultAspectRatio ?? (_emuHost?.CoreReportedAspectRatio ?? (4.0 / 3.0)),
            // Auto、およびそれ以外(将来の値追加等)はコアが実行時に申告する値。
            _ => _emuHost?.CoreReportedAspectRatio ?? (4.0 / 3.0),
        };
        return aspect > 0 ? aspect : 4.0 / 3.0;
    }

    /// <summary>
    /// 巻き戻し画面の大きいプレビューのアスペクト比を、選択中の画面比率設定(Auto/4:3/16:9)に
    /// 合わせる。Viewboxが実際の表示サイズへの拡縮を自動でやってくれるため、ここでは
    /// RewindPreviewBoxのWidth/Height「比」だけ合わせればよい(絶対値に意味はない)。
    /// </summary>
    private void ApplyRewindPreviewAspect()
    {
        const double baseWidth = 400;
        RewindPreviewBox.Width = baseWidth;
        RewindPreviewBox.Height = baseWidth / GetDisplayAspectRatio();
    }

    private void ExecuteSlotAction()
    {
        if (_emuHost is null || SaveSlots.Count == 0) return;
        var selected = SaveSlots[_slotIndex];
        int slot = selected.SlotNumber;

        if (selected.IsAutoSave)
        {
            // オートセーブ枠はロード専用。
            if (!_slotsAreSaveMode) _emuHost.RequestLoadAutoSave();
        }
        else if (_slotsAreSaveMode)
        {
            _emuHost.RequestSaveState(slot);
            _ = RefreshSaveSlotsAfterWriteAsync();
        }
        else
        {
            _emuHost.RequestLoadState(slot);
        }

        ExitSlotsFocus();
        HideMenu();
    }

    /// <summary>
    /// セーブ書き込み完了までのわずかなI/O遅延を吸収してから一覧を再読み込みする
    /// (次回「ロード」画面を開いたときにHasDataが最新化された状態で表示されるように)。
    /// </summary>
    private async Task RefreshSaveSlotsAfterWriteAsync()
    {
        await Task.Delay(100);
        if (_emuHost is not null) await RefreshSaveSlotsAsync();
    }

    private bool _backToTitleInProgress;

    /// <summary>
    /// 「タイトル選択にもどる」。終了する前に、今の場面を再開用(suspend)ステートへ保存しておき、
    /// 後日、詳細画面の「再開」から続きで遊べるようにする。保存できない(ステートに対応しないコア等)
    /// ときや、時間がかかりすぎたときは、保存せずにそのまま終了する。
    /// </summary>
    private async void BackToTitleSelect()
    {
        if (_backToTitleInProgress) return;
        _backToTitleInProgress = true;
        // 「タイトルへ戻る」で画面が閉じるまでの時間の内訳を、動作ログで追えるようにする。
        var backWatch = System.Diagnostics.Stopwatch.StartNew();
        EmulatorLauncher.Services.AppLog.Info("Play", "タイトルへ戻る: 開始");
        try
        {
            EmulationHost? host = _emuHost;
            if (host is not null && !AppPreferencesStore.Load().ResumeEnabled)
            {
                // ゲーム再開が無効(標準): 再開用ステートは保存しない。前回までの古いデータも残さない。
                try { System.IO.Directory.Delete(SuspendState.GetFolder(_corePath, _romPathForRestart), recursive: true); }
                catch { }
            }
            else if (host is not null)
            {
                var saved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnSaved(object? s, bool ok) => saved.TrySetResult(ok);
                host.SuspendStateSaved += OnSaved;
                host.RequestSaveSuspendState();
                Task finished = await Task.WhenAny(saved.Task, Task.Delay(TimeSpan.FromSeconds(3)));
                host.SuspendStateSaved -= OnSaved;
                EmulatorLauncher.Services.AppLog.Info("Play",
                    $"再開用ステートの保存: {backWatch.ElapsedMilliseconds} ms経過 / "
                    + (finished != saved.Task ? "時間切れ(3秒)" : saved.Task.Result ? "成功" : "失敗"));

                // 今回保存できなかったときは、前回の古い再開用ステートを残さない(古い場面から再開してしまうため)。
                if (finished != saved.Task || !saved.Task.Result)
                {
                    try { System.IO.Directory.Delete(SuspendState.GetFolder(_corePath, _romPathForRestart), recursive: true); }
                    catch { }
                }
            }

            CloseMenuImmediate();
            CloseRewindImmediate();
            await TeardownEmulationAsync();
            EmulatorLauncher.Services.AppLog.Info("Play", $"タイトルへ戻る: 後始末が終わりました({backWatch.ElapsedMilliseconds} ms経過)。ゲーム選択へ戻ります");

            if (Frame.CanGoBack)
            {
                GoBackWithCoverAnimation();
            }
        }
        finally
        {
            _backToTitleInProgress = false;
        }
    }
}
