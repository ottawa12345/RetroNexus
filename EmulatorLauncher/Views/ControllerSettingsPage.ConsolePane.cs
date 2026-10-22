using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EmulatorLauncher.Controls;
using EmulatorLauncher.Converters;
using EmulatorLauncher.Input;
using EmulatorLauncher.Models;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;

namespace EmulatorLauncher.Views;

// ControllerSettingsPage の一部(partial class)。「コントローラ設定」の枠(画面の組み立て・フォーカス移動・操作)。
// 動作は変えず、大きくなったコードビハインドを役割ごとに分けただけ。
public sealed partial class ControllerSettingsPage
{
    // ==================================================================
    // コントローラ設定: 画面の組み立て
    // ==================================================================

    /// <summary>
    /// コアがプリセット(コントローラの形態)を持つコンソールは、コンソールの項目の代わりに、コアと形態ごとの項目
    /// ("Dolphin (Wii) ▸ Wiiリモコン+ヌンチャク"。IDは"wii@dolphin/nunchuk")を並べる。
    /// 同じコンソールでもコアが違えばボタンの意味が違うので、設定はコアごとに別になる。
    /// プリセットを持たないコンソールは今まで通り、コンソール単位(パススルー)。外部ランチャーのコンソールは除く。
    /// </summary>
    private static List<ConsoleInfo> ExpandFormScopes(List<ConsoleInfo> consoles)
    {
        var result = new List<ConsoleInfo>(consoles.Count + 12);
        foreach (ConsoleInfo c in consoles)
        {
            // 外部ランチャー(Wii U/PS3等)はこのアプリがコントローラを扱えないので、一覧に出さない(libretroコアのみ対象)。
            if (SampleData.ResolveLauncher(c.Id, c.LauncherName)?.IsExternal == true) continue;

            List<ConsoleControllerProfile> profiles = ControllerProfiles.ProfilesOf(c.Id).ToList();
            if (profiles.Count == 0)
            {
                result.Add(c);
                continue;
            }

            foreach (ConsoleControllerProfile profile in profiles)
            {
                string consoleLabel = string.IsNullOrEmpty(profile.ConsoleLabel) ? c.Name : profile.ConsoleLabel;
                string coreLabel = string.IsNullOrEmpty(profile.CoreLabel) ? profile.CoreKey : profile.CoreLabel;
                foreach (ControllerForm form in profile.Forms)
                {
                    result.Add(new ConsoleInfo
                    {
                        Id = ControllerProfiles.MakeScope(profile, form.Id),
                        Name = $"{coreLabel} ({consoleLabel}) ▸ {form.Label}",
                        Maker = c.Maker,
                    });
                }
            }
        }

        return result;
    }

    private string CurrentConsoleName()
    {
        if (_consoles.Count == 0) return "(コンソールがありません)";
        return _consoles.FirstOrDefault(c => c.Id == _consoleId)?.Name ?? _consoleId;
    }

    /// <summary>ボタンの表記(PS系なら○✕□△)。設定ファイルの物理ボタン名(GamepadButton)を表示用にする。</summary>
    private string PadLabel(GamepadButton button) => ControllerGlyphs.GetButtonLabel(button, _padStyle);

    /// <summary>選択画面で使う、少し長い表記。PSは記号だけで足りるのでそのまま、それ以外は従来の説明つき。</summary>
    private string PadLabelLong(GamepadButton button)
        => _padStyle == ControllerGlyphs.PadStyle.PlayStation
            ? PadLabel(button)
            : ControllerGlyphs.GetButtonDisplayName(button, _padStyle);

    /// <summary>呼び出し操作の行の右側に出す文字(組み合わせ)。</summary>
    private string HotkeyValueText(string key)
        => DescribeCombo(ControllerMappingService.GetHotkey(_consoleId, key, _gameCustom));

    /// <summary>
    /// 呼び出し操作の項目名。巻き戻しが中断メニューと同じ組み合わせのときは、
    /// 「長押し」で巻き戻しになる(短く押すと中断メニュー)ので、項目名に「(長押し)」を付ける。
    /// </summary>
    private string HotkeyLabel(string key)
    {
        string name = ControllerMappingService.GetHotkeyDisplayName(key);
        if (key == ControllerMappingService.HotkeyRewind)
        {
            GamepadButton[] combo = ControllerMappingService.GetHotkey(_consoleId, key, _gameCustom);
            if (combo.Length > 0
                && combo.SequenceEqual(ControllerMappingService.GetHotkey(_consoleId, ControllerMappingService.HotkeyMenu, _gameCustom)))
            {
                name += "(長押し)";
            }
        }
        return name;
    }

    private string DescribeCombo(IReadOnlyList<GamepadButton> buttons)
        => buttons.Count == 0 ? "なし" : string.Join(" + ", buttons.Select(PadLabel));

    /// <summary>
    /// この物理ボタンの今の役割の表示。連射なら「B連射」、通常の割り当てならコンソール側のボタン名
    /// (複数なら「・」でつなぐ)、どちらも無ければ「なし」。
    /// </summary>
    private string AssignedConsoleButtons(GamepadButton physical, Dictionary<string, GamepadButton?> assignments,
        Dictionary<string, GamepadButton> turbo)
    {
        List<(string Key, string Label)> targets = Targets();
        List<string> names = targets
            .Where(e => turbo.TryGetValue(e.Key, out GamepadButton t) && t == physical)
            .Select(e => e.Label + "連射")
            .ToList();

        names.AddRange(targets
            .Where(e => assignments.TryGetValue(e.Key, out GamepadButton? p) && p == physical)
            .Select(e => e.Label));

        return names.Count == 0 ? "なし" : string.Join("・", names);
    }

    /// <summary>
    /// このコンソールで、ボタンの割り当て先にできるもの(キーと表示名)。コンソールのボタン。
    /// </summary>
    private List<(string Key, string Label)> Targets()
    {
        var list = new List<(string Key, string Label)>();
        foreach (PadElement e in _layout.Elements)
        {
            if (e.Key is not null) list.Add((e.Key, e.Label));
        }

        // 形態(プリセット)が持つ方向の入力(右スティック↑↓←→等)も、割り当て先として出す。
        if (ControllerProfiles.FindForm(_consoleId) is { } form)
        {
            foreach (KeyValuePair<string, string> kv in form.Buttons)
            {
                if (ControllerMappingService.IsDirectionKey(kv.Key) && !list.Any(t => t.Key == kv.Key))
                {
                    list.Add((kv.Key, kv.Value));
                }
            }
        }

        return list;
    }

    /// <summary>選択中のコンソールに合わせて、コントローラ設定の中身を作り直す。</summary>
    private void BuildConsolePane()
    {
        ConsoleCanvas.Children.Clear();
        ListCanvas.Children.Clear();
        _items.Clear();
        _padValueTexts.Clear();
        _dirValueTexts.Clear();
        _hotkeyValueTexts.Clear();
        _hotkeyLabelTexts.Clear();
        _keyboardValueText = null;
        _saveValueText = null;

        _layout = ConsoleControllerLayouts.Get(_consoleId);
        _padStyle = ControllerGlyphs.GetPrimaryPadStyle();

        // 1行目: コンソールの選択
        FocusItem consoleRow = AddRow(FocusKind.Console, null, 0, 0, PaneWidth, RowHeight,
            _gameMode ? "ゲーム" : "コンソール", _gameMode ? _gameTitle : CurrentConsoleName(), false, out TextBlock consoleValue);
        _consoleValueText = consoleValue;
        if (_gameMode) _items.Remove(consoleRow);   // ゲーム別モードでは選べない(表示だけ)

        // 左: 今使っているコントローラーの図(コードで描く。画像ファイルは使わない)。
        // ボタン名と割り当て先のコンソールのボタン名を重ね、右の一覧で選んでいる行のボタンを光らせる。
        _padDiagram = new PadDiagram(_padStyle);
        Canvas.SetLeft(_padDiagram, 0);
        Canvas.SetTop(_padDiagram, ImageTop);
        ConsoleCanvas.Children.Add(_padDiagram);

        // その下: このコンソールのコントローラー(参照用。コードで描く)。選んだボタンの割り当て先を光らせる。
        var consoleCaption = new TextBlock
        {
            Text = $"参照: {_layout.Name}",
            FontSize = 16,
            FontFamily = PadDiagram.SystemFont,
            Foreground = ResolveBrush("AppTextSecondaryBrush", 0x6B, 0x6B, 0x6B),
        };
        Canvas.SetLeft(consoleCaption, 4);
        Canvas.SetTop(consoleCaption, ConsolePadTop - 26);
        ConsoleCanvas.Children.Add(consoleCaption);

        _consolePad = new ConsolePadView(ConsoleControllerLayouts.GetReference(_consoleId));
        Canvas.SetLeft(_consolePad, (ImageWidth - ConsolePadView.ViewWidth) / 2);
        Canvas.SetTop(_consolePad, ConsolePadTop);
        ConsoleCanvas.Children.Add(_consolePad);
        ConsoleCanvas.Height = HelpTop + 130;

        // 右: コントローラーのボタンごとの割り当て(コンソールのどのボタンにするか)
        AddHeader("ボタン・右スティックの割り当て(コントローラ → コンソールのボタン)", ButtonsHeaderY);

        Dictionary<string, GamepadButton?> assignments = ControllerMappingService.GetAssignments(_consoleId, _gameCustom);
        Dictionary<string, GamepadButton> turboMap = ControllerMappingService.GetTurbo(_consoleId, _gameCustom);
        double y = ButtonsTop;
        foreach (GamepadButton physical in ControllerMappingService.AssignablePhysicalButtons)
        {
            AddRow(FocusKind.Pad, physical.ToString(), RightColumnX, y, RightColumnWidth, ListRowHeight,
                PadLabel(physical), AssignedConsoleButtons(physical, assignments, turboMap), false, out TextBlock valueText);
            _padValueTexts[physical.ToString()] = valueText;
            y += ListRowHeight;
        }

        // 十字キー・左スティックは割り当て変更の対象にしない(右スティックだけ、上のボタン一覧の中で割り当てられる)。

        // その下: ゲーム中の呼び出し操作(特殊割り当て)
        y += 24;
        AddHeader("特殊割り当て(ゲーム中の呼び出し操作)", y);
        y += 40;
        foreach (string key in ControllerMappingService.HotkeyKeys.Where(k => ControllerProfiles.IsHotkeyAvailable(_consoleId, k)))
        {
            AddRow(FocusKind.Hotkey, key, RightColumnX, y, RightColumnWidth, ListRowHeight,
                HotkeyLabel(key),
                HotkeyValueText(key),
                false, out TextBlock hotkeyValue);
            _hotkeyValueTexts[key] = hotkeyValue;
            y += ListRowHeight;
        }

        // キーボード入力をコアへ渡す設定(controllerProfiles.jsonの"keyboard"に載っているコンソールだけ)。
        if (ControllerProfiles.FindKeyboardOption(_consoleId) is not null)
        {
            y += 16;
            AddRow(FocusKind.Keyboard, null, RightColumnX, y, RightColumnWidth, ListRowHeight,
                "キーボード入力をコアへ", KeyboardValueText(), false, out TextBlock keyboardValue);
            _keyboardValueText = keyboardValue;
            y += ListRowHeight;
        }

        // ポート1(2P側)の装置(controllerProfiles.jsonの"port1"に載っているコンソールだけ)。
        _port1ValueText = null;
        if (ControllerProfiles.FindPort1Option(_consoleId) is not null)
        {
            if (ControllerProfiles.FindKeyboardOption(_consoleId) is null) y += 16;
            AddRow(FocusKind.Port1, null, RightColumnX, y, RightColumnWidth, ListRowHeight,
                "ポート1の装置(2P側)", Port1ValueText(), false, out TextBlock port1Value);
            _port1ValueText = port1Value;
            y += ListRowHeight;
        }

        y += 16;
        AddRow(FocusKind.Check, null, RightColumnX, y, RightColumnWidth, ListRowHeight,
            "ボタン確認", "押して確認", false, out _);
        y += ListRowHeight;
        AddRow(FocusKind.Reset, null, RightColumnX, y, RightColumnWidth, ListRowHeight,
            _gameMode ? "標準に戻す(コンソールの設定にする)" : "初期状態に戻す", string.Empty, true, out _);
        y += ListRowHeight;
        AddRow(FocusKind.Save, null, RightColumnX, y, RightColumnWidth, ListRowHeight,
            "保存", _dirty ? "変更あり" : "変更なし", false, out TextBlock saveValue);
        _saveValueText = saveValue;
        y += ListRowHeight + 16;
        ListCanvas.Height = Math.Max(0, y - ListTop);

        // 説明文(選んでいる部品に合わせて書き換える)
        _helpText = new TextBlock
        {
            Width = ImageWidth,
            FontSize = 24,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ResolveBrush("AppTextSecondaryBrush", 0x6B, 0x6B, 0x6B),
        };
        Canvas.SetLeft(_helpText, 4);
        Canvas.SetTop(_helpText, HelpTop);
        ConsoleCanvas.Children.Add(_helpText);

        _focusIndex = 0;
        ConsoleScroll.ChangeView(null, 0, null, true);
        UpdateItemFocus();
    }

    private void AddHeader(string text, double y)
    {
        var header = new TextBlock
        {
            Text = text,
            FontSize = 24,
            Foreground = ResolveBrush("AppTextSecondaryBrush", 0x6B, 0x6B, 0x6B),
        };
        Canvas.SetLeft(header, 4);
        Canvas.SetTop(header, y - ListTop);
        ListCanvas.Children.Add(header);
    }

    /// <summary>項目名(左) + 青い値(右)の行を作る(コンソール行、呼び出し操作の行、「初期状態に戻す」行)。</summary>
    private FocusItem AddRow(FocusKind kind, string? key, double x, double y, double w, double h,
        string label, string value, bool danger, out TextBlock valueText)
    {
        var root = new Grid { Width = w, Height = h };

        var border = new Border
        {
            BorderThickness = new Thickness(4),
            CornerRadius = new CornerRadius(0),
            Margin = new Thickness(0, 0, 0, 1),
            Background = TransparentBrush,
            BorderBrush = TransparentBrush,
        };

        var content = new Grid { Margin = new Thickness(28, 0, 28, 1) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 30,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = danger
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE5, 0x39, 0x50))
                : ResolveBrush("AppTextPrimaryBrush", 0x2D, 0x2D, 0x2D),
        };
        if (kind == FocusKind.Hotkey && key is not null) _hotkeyLabelTexts[key] = labelText;

        valueText = new TextBlock
        {
            Text = value,
            FontSize = 30,
            Margin = new Thickness(24, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ResolveBrush("SwitchSelectedBlueBrush", 0x30, 0x50, 0xEF),
        };
        Grid.SetColumn(valueText, 1);

        content.Children.Add(labelText);
        content.Children.Add(valueText);

        var line = new Rectangle { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Fill = MakeBrush("#C8C8C8") };

        root.Children.Add(border);
        root.Children.Add(content);
        root.Children.Add(line);

        // 右の一覧(x >= RightColumnX かつ コンソール行より下)はスクロールする側のキャンバスへ置く。
        // フォーカス移動用の座標(FocusItem)は、スクロール位置に関係なく画面全体の座標のまま持つ。
        if (kind == FocusKind.Console)
        {
            Canvas.SetLeft(root, x);
            Canvas.SetTop(root, y);
            ConsoleCanvas.Children.Add(root);
        }
        else
        {
            Canvas.SetLeft(root, x - RightColumnX);
            Canvas.SetTop(root, y - ListTop);
            ListCanvas.Children.Add(root);
        }

        var item = new FocusItem(kind, key, x, y, w, h, border);
        root.Tapped += (_, e) =>
        {
            e.Handled = true;
            OnItemTapped(item);
        };
        _items.Add(item);
        return item;
    }

    // ==================================================================
    // コントローラ設定: フォーカス移動
    // ==================================================================

    private void OnItemTapped(FocusItem item)
    {
        if (_busy) return;

        int index = _items.IndexOf(item);
        if (index < 0) return;

        _categoryIndex = 1;
        _region = Region.Console;
        _focusIndex = index;
        UpdateCategoryVisuals();
        _ = ActivateItemAsync(item);
    }

    /// <summary>
    /// 十字キーの方向(dx, dy)で、いちばん近い部品へフォーカスを動かす。動けたらtrue。
    /// 目安は「その方向への距離 + ずれの大きさ」で、ずれは重みを付けて数える(まっすぐ先にある部品を優先する)。
    /// コンソール行は横長なので、上下の移動だけで行き来する。
    /// </summary>
    private bool MoveFocus(int dx, int dy)
    {
        int best = FindNeighbor(_focusIndex, dx, dy);
        if (best < 0) return false;

        _focusIndex = best;
        UpdateItemFocus();
        return true;
    }

    private int FindNeighbor(int from, int dx, int dy)
    {
        if (from < 0 || from >= _items.Count) return -1;

        FocusItem a = _items[from];
        double acx = a.X + a.W / 2;
        double acy = a.Y + a.H / 2;

        int best = -1;
        double bestScore = double.MaxValue;

        for (int i = 0; i < _items.Count; i++)
        {
            if (i == from) continue;

            FocusItem b = _items[i];
            double bcx = b.X + b.W / 2;
            double bcy = b.Y + b.H / 2;

            double primary;
            double offAxis;
            double centerDiff;

            if (dx != 0)
            {
                if (a.Kind == FocusKind.Console || b.Kind == FocusKind.Console) continue;
                if (dx > 0 ? bcx <= acx : bcx >= acx) continue;

                primary = Math.Max(0, dx > 0 ? b.X - (a.X + a.W) : a.X - (b.X + b.W));
                offAxis = IntervalGap(a.Y, a.Y + a.H, b.Y, b.Y + b.H);
                centerDiff = Math.Abs(bcy - acy);
            }
            else
            {
                if (dy > 0 ? bcy <= acy : bcy >= acy) continue;

                primary = Math.Max(0, dy > 0 ? b.Y - (a.Y + a.H) : a.Y - (b.Y + b.H));
                offAxis = IntervalGap(a.X, a.X + a.W, b.X, b.X + b.W);
                centerDiff = Math.Abs(bcx - acx);
            }

            double score = primary + offAxis * 4 + centerDiff * 0.25;
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        return best;
    }

    /// <summary>2つの区間[a1,a2]と[b1,b2]の隙間(重なっていれば0)。</summary>
    private static double IntervalGap(double a1, double a2, double b1, double b2)
    {
        if (b1 > a2) return b1 - a2;
        if (a1 > b2) return a1 - b2;
        return 0;
    }

    private void UpdateItemFocus()
    {
        for (int i = 0; i < _items.Count; i++)
        {
            ApplyFocus(_items[i], _region == Region.Console && i == _focusIndex);
        }

        SyncCheckTimer();
        UpdateHelp();
        UpdatePadDiagram();
        EnsureFocusedVisible();
    }

    /// <summary>「ボタン確認」の行にフォーカスがあるときだけ、押下状態の読み取りタイマーを動かす。</summary>
    private void SyncCheckTimer()
    {
        bool on = _region == Region.Console && _focusIndex >= 0 && _focusIndex < _items.Count
            && _items[_focusIndex].Kind == FocusKind.Check;
        if (on)
        {
            if (!_checkTimer.IsEnabled) _checkTimer.Start();
        }
        else
        {
            _checkTimer.Stop();
            _checkHeld.Clear();
        }
    }

    /// <summary>今押されているボタンを読み、変わったときだけ図を更新する(物理ボタンそのもの。A/B入れ替えの影響は受けない)。</summary>
    private void PollCheckButtons()
    {
        bool on = _region == Region.Console && _focusIndex >= 0 && _focusIndex < _items.Count
            && _items[_focusIndex].Kind == FocusKind.Check && _padDiagram is not null && !_busy;
        if (!on)
        {
            SyncCheckTimer();
            return;
        }

        var held = new HashSet<GamepadButton>();
        foreach (GamepadButton b in _padDiagram!.Buttons.Concat(PadDiagram.StickDirections))
        {
            if (GamepadHoldState.IsButtonHeld(b)) held.Add(b);
        }

        if (held.SetEquals(_checkHeld)) return;
        _checkHeld = held;
        UpdatePadDiagram();
    }

    /// <summary>コントローラーの図の、割り当て先の表示と光るボタンを、今の設定と選択行に合わせる。</summary>
    private void UpdatePadDiagram()
    {
        if (_padDiagram is null) return;

        string Assigned(GamepadButton b)
        {
            if (!_padValueTexts.TryGetValue(b.ToString(), out TextBlock? t)) return string.Empty;
            return t.Text == "なし" ? string.Empty : t.Text;
        }

        var lit = new HashSet<GamepadButton>();
        if (_region == Region.Console && _focusIndex >= 0 && _focusIndex < _items.Count)
        {
            FocusItem item = _items[_focusIndex];
            if (item.Kind == FocusKind.Check)
            {
                foreach (GamepadButton h in _checkHeld) lit.Add(h);   // 今押しているボタンを光らせる
            }
            else if (item.Kind == FocusKind.Pad && item.Key is not null && Enum.TryParse(item.Key, out GamepadButton b))
            {
                lit.Add(b);
            }
            else if (item.Kind == FocusKind.Hotkey && item.Key is not null)
            {
                foreach (GamepadButton h in ControllerMappingService.GetHotkey(_consoleId, item.Key, _gameCustom)) lit.Add(h);
            }
        }

        _padDiagram.Update(Assigned, lit);

        // 選んでいる物理ボタンの割り当て先(連射含む)を、コンソール側の図でも光らせる
        var litKeys = new List<string>();
        if (_consolePad is not null && lit.Count > 0)
        {
            Dictionary<string, GamepadButton?> assignments = ControllerMappingService.GetAssignments(_consoleId, _gameCustom);
            Dictionary<string, GamepadButton> turboMap = ControllerMappingService.GetTurbo(_consoleId, _gameCustom);
            foreach ((string key, string _) in Targets())
            {
                if ((assignments.TryGetValue(key, out GamepadButton? p) && p is GamepadButton pb && lit.Contains(pb))
                    || (turboMap.TryGetValue(key, out GamepadButton t) && lit.Contains(t)))
                {
                    litKeys.Add(key);
                }
            }
        }

        _consolePad?.Update(litKeys);
    }

    /// <summary>フォーカス中の行が見える位置まで、右の領域をスクロールする。</summary>
    private void EnsureFocusedVisible()
    {
        if (_region != Region.Console || _focusIndex < 0 || _focusIndex >= _items.Count) return;

        FocusItem item = _items[_focusIndex];
        double view = ConsoleScroll.ViewportHeight;
        if (view <= 0) return;

        if (item.Kind == FocusKind.Console) return;

        double offset = ConsoleScroll.VerticalOffset;
        double top = item.Y - ListTop - 12;
        double bottom = item.Y - ListTop + item.H + 12;
        if (top < offset) ConsoleScroll.ChangeView(null, Math.Max(0, top), null, false);
        else if (bottom > offset + view) ConsoleScroll.ChangeView(null, bottom - view, null, false);
    }

    private void ApplyFocus(FocusItem item, bool focused)
    {
        item.Visual.Background = focused ? FocusFillBrush() : TransparentBrush;
        item.Visual.BorderBrush = focused ? GetFocusBrush() : TransparentBrush;
        FocusSlide.SetIsFocused(item.Visual, focused);
    }

    private Brush GetFocusBrush()
        => (Brush)_focusBrushConverter.Convert(true, typeof(Brush), null!, string.Empty);

    // ==================================================================
    // コントローラ設定: 操作(A / クリック)
    // ==================================================================

    private async Task ActivateItemAsync(FocusItem item)
    {
        if (_busy) return;
        _busy = true;

        try
        {
            switch (item.Kind)
            {
                case FocusKind.Console:
                    await PickConsoleAsync();
                    break;
                case FocusKind.Pad:
                    await AssignPadButtonAsync(item);
                    break;
                case FocusKind.Direction:
                    if (item.Key is not null) await ActivateDirectionAsync(item.Key);
                    break;
                case FocusKind.Hotkey:
                    if (item.Key is not null) await AssignHotkeyAsync(item.Key);
                    break;
                case FocusKind.Keyboard:
                    ToggleKeyboard();
                    break;
                case FocusKind.Port1:
                    CyclePort1Device();
                    break;
                case FocusKind.Check:
                    break;   // 押して確認するだけの行(操作は何も起きない)
                case FocusKind.Reset:
                    await ResetConsoleAsync();
                    break;
                case FocusKind.Save:
                    await SaveEditsAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ControllerSettingsPage] 操作に失敗しました: {ex}");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task PickConsoleAsync()
    {
        if (_consoles.Count == 0) return;

        List<GamepadPickerItem> items = _consoles
            .Select(c => new GamepadPickerItem { Label = c.Name, Tag = c.Id, IsChecked = c.Id == _consoleId })
            .ToList();

        GamepadPickerItem? picked = await ChoiceOverlay.ShowAsync("コンソールを選択", items);
        if (picked?.Tag is string id && id != _consoleId)
        {
            if (!await ResolveDirtyAsync()) return;

            _consoleId = id;
            _lastConsoleId = id;
            // 設定画面のカテゴリ切り替えと同じく、一覧をフェードアウト→組み直し→フェードインする。
            await _paneFader.SwitchAsync(BuildConsolePane, ConsolePanel);
        }
    }

    /// <summary>
    /// コントローラーのボタン(行)の役割を選ぶ。選択肢は「なし」、コンソールのボタン(通常)、
    /// コンソールのボタン+「連射」(押している間、約15回/秒で押す/離す)。
    ///  ・通常: 選んだコンソールのボタンがこれまで使っていたコントローラーのボタンと、この行のボタンに
    ///    ついていたコンソールのボタンが入れ替わる(使っていたボタンが無ければ、そのボタンは割り当てなしになる)。
    ///  ・連射: この行のボタンにあった通常の割り当ては外れる(割り当てなし)。そのキーの通常の割り当ては残る。
    ///  ・なし: この行のボタンの通常の割り当て・連射を外す。
    /// </summary>
    private async Task AssignPadButtonAsync(FocusItem item)
    {
        if (item.Key is null || !Enum.TryParse(item.Key, out GamepadButton physical)) return;

        Dictionary<string, GamepadButton?> assignments = ControllerMappingService.GetAssignments(_consoleId, _gameCustom);
        Dictionary<string, GamepadButton> turboMap = ControllerMappingService.GetTurbo(_consoleId, _gameCustom);
        List<(string Key, string Label)> keyed = Targets();

        bool hasAny = keyed.Any(e => (assignments.TryGetValue(e.Key!, out GamepadButton? p) && p == physical)
                                     || (turboMap.TryGetValue(e.Key!, out GamepadButton t) && t == physical));

        var items = new List<GamepadPickerItem>
        {
            new GamepadPickerItem { Label = "なし(使わない)", Tag = "none", IsChecked = !hasAny },
        };
        foreach ((string Key, string Label) e in keyed)
        {
            items.Add(new GamepadPickerItem
            {
                Label = e.Label,
                Tag = "n:" + e.Key,
                IsChecked = assignments.TryGetValue(e.Key!, out GamepadButton? p) && p == physical,
            });
        }

        foreach ((string Key, string Label) e in keyed.Where(k => !ControllerMappingService.IsDirectionKey(k.Key)))
        {
            items.Add(new GamepadPickerItem
            {
                Label = e.Label + " 連射",
                Tag = "t:" + e.Key,
                IsChecked = turboMap.TryGetValue(e.Key!, out GamepadButton t) && t == physical,
            });
        }

        GamepadPickerItem? picked = await ChoiceOverlay.ShowAsync($"「{PadLabel(physical)}」に割り当てるボタン", items);
        if (picked?.Tag is not string tag) return;

        // まず、この行のボタンの連射を外す(どの役割を選んでも、連射は1つの役割として置き換える)。
        foreach ((string Key, string Label) e in keyed)
        {
            if (turboMap.TryGetValue(e.Key!, out GamepadButton t) && t == physical)
            {
                WriteTurbo(e.Key!, null);
            }
        }

        if (tag == "none")
        {
            foreach ((string Key, string Label) e in keyed)
            {
                if (assignments.TryGetValue(e.Key!, out GamepadButton? p) && p == physical)
                {
                    WriteButtonNone(e.Key!);
                }
            }
        }
        else if (tag.StartsWith("t:", StringComparison.Ordinal))
        {
            string key = tag.Substring(2);

            // この行のボタンにあった通常の割り当ては外す。
            foreach ((string Key, string Label) e in keyed)
            {
                if (assignments.TryGetValue(e.Key!, out GamepadButton? p) && p == physical)
                {
                    WriteButtonNone(e.Key!);
                }
            }

            WriteTurbo(key, physical);
        }
        else if (tag.StartsWith("n:", StringComparison.Ordinal))
        {
            string key = tag.Substring(2);
            assignments.TryGetValue(key, out GamepadButton? previous);
            if (previous == physical) { RefreshValues(); return; }

            // この行のボタンにあった通常の割り当ては、選んだキーが使っていたボタンへ移す(入れ替え)。
            foreach ((string Key, string Label) e in keyed)
            {
                if (e.Key == key) continue;
                if (assignments.TryGetValue(e.Key!, out GamepadButton? other) && other == physical)
                {
                    if (previous is GamepadButton moved) WriteButton(e.Key!, moved);
                    else WriteButtonNone(e.Key!);
                }
            }

            WriteButton(key, physical);
        }

        RefreshValues();
    }
}
