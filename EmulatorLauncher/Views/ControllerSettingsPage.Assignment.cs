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

// ControllerSettingsPage の一部(partial class)。ボタン割り当て(十字キー・スティック)・説明文・小さな道具。
// 動作は変えず、大きくなったコードビハインドを役割ごとに分けただけ。
public sealed partial class ControllerSettingsPage
{
    // ==================================================================
    // 十字キー・スティックの割り当て(コントローラーの入力 → ゲームの入力)
    //   行はコントローラー側の入力(十字キー/左スティック/右スティック)。値は、それがゲームのどの入力になるか。
    //   保存は「ゲームの入力(Up/Down/Left/Right、LS*、RS*) → コントローラーの入力」の形(ボタンと同じ)。
    //   入れ替え・左右反転・上下反転は、この4方向の割り当てをまとめて書き換えるだけ。
    // ==================================================================

    private static readonly string[] DirGroupNames = { "十字キー", "左スティック", "右スティック" };

    // ゲームの入力ごとの4方向のキー(上, 下, 左, 右)。
    private static readonly string[][] DirTargetKeys =
    {
        new[] { "Up", "Down", "Left", "Right" },
        new[] { "LSUp", "LSDown", "LSLeft", "LSRight" },
        new[] { "RSUp", "RSDown", "RSLeft", "RSRight" },
    };

    // コントローラーの入力ごとの4方向の物理入力(上, 下, 左, 右)。
    private static readonly GamepadButton[][] DirPhysical =
    {
        new[] { GamepadButton.DPadUp, GamepadButton.DPadDown, GamepadButton.DPadLeft, GamepadButton.DPadRight },
        new[] { GamepadButton.LeftStickUp, GamepadButton.LeftStickDown, GamepadButton.LeftStickLeft, GamepadButton.LeftStickRight },
        new[] { GamepadButton.RightStickUp, GamepadButton.RightStickDown, GamepadButton.RightStickLeft, GamepadButton.RightStickRight },
    };

    /// <summary>ゲームの入力(0=十字キー、1=左スティック、2=右スティック)1つ分の、今の割り当て。</summary>
    private readonly record struct DirState(int Source, bool FlipH, bool FlipV, bool Custom)
    {
        public const int None = -1;
    }

    private int StickCount() => _layout.Elements.Count(e => e.Shape == PadShape.Stick);

    /// <summary>このコンソールで、コントローラーの入力の行き先にできるゲームの入力(0=十字キーは常に、スティックは数だけ)。</summary>
    private List<int> AvailableTargets()
    {
        int sticks = StickCount();
        var list = new List<int> { 0 };
        if (sticks >= 1) list.Add(1);
        if (sticks >= 2) list.Add(2);
        return list;
    }

    private static int GroupOf(GamepadButton? button)
    {
        if (button is not GamepadButton b) return DirState.None;
        for (int g = 0; g < DirPhysical.Length; g++)
        {
            if (Array.IndexOf(DirPhysical[g], b) >= 0) return g;
        }

        return DirState.None;
    }

    private DirState ReadTarget(int target, Dictionary<string, GamepadButton?> assignments)
    {
        var v = new GamepadButton?[4];
        for (int d = 0; d < 4; d++) assignments.TryGetValue(DirTargetKeys[target][d], out v[d]);

        if (v.All(x => x is null)) return new DirState(DirState.None, false, false, false);

        int g = GroupOf(v[0]);
        if (g < 0 || v.Any(x => GroupOf(x) != g)) return new DirState(DirState.None, false, false, true);

        GamepadButton[] p = DirPhysical[g];
        bool vertNormal = v[0] == p[0] && v[1] == p[1];
        bool vertFlip = v[0] == p[1] && v[1] == p[0];
        bool horNormal = v[2] == p[2] && v[3] == p[3];
        bool horFlip = v[2] == p[3] && v[3] == p[2];
        if (!(vertNormal || vertFlip) || !(horNormal || horFlip)) return new DirState(DirState.None, false, false, true);

        return new DirState(g, horFlip, vertFlip, false);
    }

    private void WriteTarget(int target, int source, bool flipH, bool flipV)
    {
        for (int d = 0; d < 4; d++)
        {
            string key = DirTargetKeys[target][d];
            if (source < 0)
            {
                WriteButtonNone(key);
                continue;
            }

            int pd = d switch { 0 => flipV ? 1 : 0, 1 => flipV ? 0 : 1, 2 => flipH ? 3 : 2, _ => flipH ? 2 : 3 };
            WriteButton(key, DirPhysical[source][pd]);
        }
    }

    private DirState[] ReadAllTargets()
    {
        Dictionary<string, GamepadButton?> assignments = ControllerMappingService.GetAssignments(_consoleId, _gameCustom);
        return new[] { ReadTarget(0, assignments), ReadTarget(1, assignments), ReadTarget(2, assignments) };
    }

    /// <summary>コントローラーの入力(source)が、いまゲームのどの入力になっているか(なければ-1)。</summary>
    private static int DestinationOf(int source, DirState[] states)
    {
        for (int t = 0; t < states.Length; t++)
        {
            if (states[t].Source == source) return t;
        }

        return -1;
    }

    private string DescribeDirectionRow(int source)
    {
        DirState[] states = ReadAllTargets();
        int dest = DestinationOf(source, states);

        // スティックの無いコンソールでは、左スティックは十字キーの代わりとして働く(標準)。
        if (source == 1 && StickCount() == 0) return dest >= 0 ? "十字キーとしても使う" : "使わない";

        if (dest >= 0) return DirGroupNames[dest];
        return states.Any(s => s.Custom) ? "カスタム" : "なし";
    }

    private string DescribeFlipRow(int source, char axis)
    {
        DirState[] states = ReadAllTargets();
        int dest = DestinationOf(source, states);
        if (dest < 0) return "-";
        return (axis == 'H' ? states[dest].FlipH : states[dest].FlipV) ? "オン" : "オフ";
    }

    private async Task ActivateDirectionAsync(string key)
    {
        if (key.Length < 2) return;
        int g = key[1] - '0';
        if (g < 0 || g >= DirGroupNames.Length) return;

        if (key[0] == 'G')
        {
            await PickDirectionDestinationAsync(g);
            return;
        }

        // 左右反転/上下反転: 行き先の4方向を、左右(または上下)入れ替えて書き直す。
        DirState[] states = ReadAllTargets();
        int dest = DestinationOf(g, states);
        if (dest < 0) return;

        DirState st = states[dest];
        WriteTarget(dest, g, key[0] == 'H' ? !st.FlipH : st.FlipH, key[0] == 'V' ? !st.FlipV : st.FlipV);
        RefreshValues();
    }

    /// <summary>
    /// コントローラーの入力(source)が、ゲームのどの入力になるかを選ぶ。
    /// 行き先にすでに別の入力があれば入れ替える(例: 左スティック→右スティックを選ぶと、右スティックは左スティックになる)。
    /// 「なし」は、その入力を使わない(行き先は割り当てなしになる)。
    /// </summary>
    private async Task PickDirectionDestinationAsync(int source)
    {
        DirState[] states = ReadAllTargets();
        int current = DestinationOf(source, states);

        var items = new List<GamepadPickerItem>
        {
            new GamepadPickerItem { Label = "なし(使わない)", Tag = -1, IsChecked = current < 0 },
        };

        if (source == 1 && StickCount() == 0)
        {
            // スティックの無いコンソール: 左スティックを十字キーとしても使うか(標準は使う)。
            items.Clear();
            items.Add(new GamepadPickerItem { Label = "十字キーとしても使う(標準)", Tag = 1, IsChecked = current == 1 });
            items.Add(new GamepadPickerItem { Label = "使わない", Tag = -1, IsChecked = current < 0 });
        }
        else foreach (int t in AvailableTargets())
        {
            items.Add(new GamepadPickerItem
            {
                Label = "ゲームの" + DirGroupNames[t] + "にする",
                Tag = t,
                IsChecked = current == t,
            });
        }

        GamepadPickerItem? picked = await ChoiceOverlay.ShowAsync($"「{DirGroupNames[source]}」の使い方", items);
        if (picked?.Tag is not int newDest || newDest == current) return;

        if (newDest < 0)
        {
            WriteTarget(current, DirState.None, false, false);
        }
        else
        {
            DirState displaced = states[newDest];   // 行き先に今ある入力(あれば、元の行き先へ移す)
            WriteTarget(newDest, source, false, false);
            if (current >= 0)
            {
                if (displaced.Source >= 0 && !displaced.Custom)
                    WriteTarget(current, displaced.Source, displaced.FlipH, displaced.FlipV);
                else
                    WriteTarget(current, DirState.None, false, false);
            }
        }

        RefreshValues();
    }

    private string BuildDirectionHelp(string key)
    {
        if (key.Length < 2) return string.Empty;
        int g = key[1] - '0';
        if (g < 0 || g >= DirGroupNames.Length) return string.Empty;

        string text = key[0] switch
        {
            'G' when g == 1 && StickCount() == 0 =>
                "このコンソールにはスティックが無いので、左スティックは十字キーの代わりとして使えます(標準)。「使わない」にすると、十字キーだけで操作します。",
            'G' => $"コントローラの「{DirGroupNames[g]}」を、ゲームのどの入力にするかを選びます。すでにその入力を使っているものとは入れ替わります(例: 左スティックと右スティックを入れ替える)。",
            'H' => $"「{DirGroupNames[g]}」の左右を逆にします(左に倒すと右として伝わります)。",
            'V' => $"「{DirGroupNames[g]}」の上下を逆にします(上に倒すと下として伝わります)。",
            _ => string.Empty,
        };

        if (_gameMode) text += "\n変更はこのゲームだけに保存されます。";
        return text;
    }

    /// <summary>
    /// 呼び出し操作の組み合わせを決める。全画面の編集画面(HotkeyEditOverlay)で、左の物理ボタンを
    /// いくつでも選び、右の「適用」で確定する。何も選ばずに適用すると、その操作は使わない。Bで変更せず戻る。
    /// </summary>
    private async Task AssignHotkeyAsync(string hotkeyKey)
    {
        string name = HotkeyLabel(hotkeyKey);
        GamepadButton[] current = ControllerMappingService.GetHotkey(_consoleId, hotkeyKey, _gameCustom);

        GamepadButton[]? combo = await HotkeyEditor.ShowAsync(
            name,
            ControllerMappingService.AssignableHotkeyButtons,
            PadLabelLong,
            current,
            "選んだボタンをすべて同時に押すと「" + name + "」が動きます。いくつでも選べます。何も選ばずに適用すると、この操作は使いません。");
        if (combo is null) return;

        WriteHotkey(hotkeyKey, combo);
        RefreshValues();
    }

    private async Task ResetConsoleAsync()
    {
        bool ok = await ConfirmOverlay.ShowAsync(
            _gameMode
                ? "このゲームだけのボタン割り当て・連射・呼び出し操作を消して、コンソールの設定に戻しますか?"
                : $"「{CurrentConsoleName()}」のボタン割り当てと呼び出し操作を、初期状態に戻しますか?",
            _gameMode ? "標準に戻す" : "初期状態に戻す");
        if (!ok) return;

        if (_gameMode)
        {
            string scope = _consoleId;
            Mutate(c => ControllerMappingService.ClearGameConfig(c, scope));
        }
        else
        {
            MarkDirty();
            ControllerMappingService.ResetConsole(_consoleId);
        }

        RefreshValues();
    }

    /// <summary>設定が変わったあと、図とホットキーの表示、説明文を最新にする。</summary>
    private void RefreshValues()
    {
        Dictionary<string, GamepadButton?> assignments = ControllerMappingService.GetAssignments(_consoleId, _gameCustom);
        Dictionary<string, GamepadButton> turboMap = ControllerMappingService.GetTurbo(_consoleId, _gameCustom);
        foreach (KeyValuePair<string, TextBlock> kv in _padValueTexts)
        {
            if (Enum.TryParse(kv.Key, out GamepadButton physical))
            {
                kv.Value.Text = AssignedConsoleButtons(physical, assignments, turboMap);
            }
        }

        foreach (KeyValuePair<string, TextBlock> kv in _dirValueTexts)
        {
            kv.Value.Text = kv.Key[0] == 'G' ? DescribeDirectionRow(kv.Key[1] - '0') : DescribeFlipRow(kv.Key[1] - '0', kv.Key[0]);
        }

        foreach (KeyValuePair<string, TextBlock> kv in _hotkeyValueTexts)
        {
            kv.Value.Text = HotkeyValueText(kv.Key);
        }

        foreach (KeyValuePair<string, TextBlock> kv in _hotkeyLabelTexts)
        {
            kv.Value.Text = HotkeyLabel(kv.Key);
        }

        if (_consoleValueText is not null && !_gameMode) _consoleValueText.Text = CurrentConsoleName();
        if (_keyboardValueText is not null) _keyboardValueText.Text = KeyboardValueText();
        if (_port1ValueText is not null) _port1ValueText.Text = Port1ValueText();
        UpdateHelp();
        UpdatePadDiagram();
    }

    // ==================================================================
    // 説明文
    // ==================================================================

    private void UpdateHelp()
    {
        if (_helpText is null) return;

        if (_focusIndex < 0 || _focusIndex >= _items.Count)
        {
            _helpText.Text = string.Empty;
            return;
        }

        FocusItem item = _items[_focusIndex];
        switch (item.Kind)
        {
            case FocusKind.Console:
                _helpText.Text = $"設定するコンソールを選びます。ボタン割り当てと呼び出し操作は、{CurrentConsoleName()}のゲームすべてに使われます。";
                break;

            case FocusKind.Pad when item.Key is not null:
                _helpText.Text = BuildPadHelp(item.Key);
                break;

            case FocusKind.Direction when item.Key is not null:
                _helpText.Text = BuildDirectionHelp(item.Key);
                break;

            case FocusKind.Hotkey when item.Key is not null:
                _helpText.Text = BuildHotkeyHelp(item.Key);
                break;

            case FocusKind.Keyboard:
                _helpText.Text = "オンにすると、キーボードのキーをそのままゲーム機(パソコン)のキーとして渡します。\n"
                    + "このときキーボードはコントローラの代わりには使えません(コントローラは今までどおり使えます)。\n"
                    + "キーボードだけで遊ぶときは、F12キーで中断メニュー、F11キーで早送りの切り替えができます。";
                break;

            case FocusKind.Port1:
                _helpText.Text = "ポート1(2P側)に挿す装置を選びます(決定ボタンで切り替え)。\n"
                    + "ゲームがマウスを使うときは「マウス」にします(ポート1のマウスはパソコンのマウスで動かします)。\n"
                    + "装置を変えたときは、ゲームを起動し直すと反映されます。";
                break;

            case FocusKind.Check:
                _helpText.Text = "コントローラのボタンを押すと、図の対応するボタンが光ります。形の違うコントローラで、どのボタンがどれか確かめられます。\n十字キーの上下で、ほかの項目へ移動できます。";
                break;

            case FocusKind.Reset:
                _helpText.Text = (_gameMode
                    ? "このゲームだけの設定(ボタン割り当て・連射・呼び出し操作)をすべて消して、コンソールの設定に戻します。"
                    : "このコンソールのボタン割り当てと呼び出し操作を、すべて初期状態に戻します。")
                    + "\n※ 変更は「保存」を押すまで確定しません。";
                break;

            case FocusKind.Save:
                _helpText.Text = _saveError
                    ?? "ここまでの変更を保存します。ゲームで使うボタンに「なし」が残っていると保存できません。保存せずに戻ると、変更は捨てられます。";
                break;
        }
    }

    private string BuildPadHelp(string physicalName)
    {
        if (!Enum.TryParse(physicalName, out GamepadButton physical)) return string.Empty;

        string assigned = AssignedConsoleButtons(physical,
            ControllerMappingService.GetAssignments(_consoleId, _gameCustom), ControllerMappingService.GetTurbo(_consoleId, _gameCustom));

        bool isStick = physical >= GamepadButton.RightStickUp && physical <= GamepadButton.RightStickRight;
        if (isStick)
            return $"コントローラの「{PadLabel(physical)}」  現在: {assigned}\nAで、右スティックをその向きに倒したときの役割を選びます。"
                + (_gameMode ? "\n変更はこのゲームだけに保存されます。" : string.Empty);

        return $"コントローラの「{PadLabel(physical)}」ボタン  現在: {assigned}\nAで、このボタンの役割を選びます。「連射」も選べます。"
            + (_gameMode ? "\n変更はこのゲームだけに保存されます。" : string.Empty);
    }

    private string BuildHotkeyHelp(string hotkeyKey)
    {
        GamepadButton[] combo = ControllerMappingService.GetHotkey(_consoleId, hotkeyKey, _gameCustom);

        string text = hotkeyKey switch
        {
            ControllerMappingService.HotkeyMenu =>
                "ゲーム中にこの操作で中断メニューを開きます(巻き戻しと同じ組み合わせなら、短く押すと中断メニュー)。",
            ControllerMappingService.HotkeyRewind =>
                "ゲーム中にこの操作で巻き戻し画面を開きます(中断メニューと同じ組み合わせなら、長押しで巻き戻し)。",
            ControllerMappingService.HotkeyFastForward =>
                "押すたびに早送りの倍率を上げます(1倍→2倍→3倍→1倍…)。",
            ControllerMappingService.HotkeyFastForwardReverse =>
                "押すたびに早送りの倍率を下げます(3倍→2倍→1倍→3倍…)。",
            ControllerMappingService.HotkeyPointerRecenter =>
                "ジャイロでポインターを動かしているとき、押した瞬間にポインターを画面の中央へ戻します(Wii用)。",
            _ => string.Empty,
        };

        text += "\nAで、ボタンの組み合わせを選びます(いくつでも選べます)。";
        if (_gameMode) text += "\n変更はこのゲームだけに保存されます。";

        if (HotkeyOverlapsGameButtons(combo))
        {
            text += "\n※ ゲームのボタンと同じボタンを含むため、押すとゲームにも入力されます。";
        }

        return text;
    }

    private bool HotkeyOverlapsGameButtons(GamepadButton[] combo)
    {
        if (combo.Length == 0) return false;

        Dictionary<string, GamepadButton?> assignments = ControllerMappingService.GetAssignments(_consoleId, _gameCustom);
        Dictionary<string, GamepadButton> turboMap = ControllerMappingService.GetTurbo(_consoleId, _gameCustom);
        foreach (var (key, _) in Targets())
        {
            if (assignments.TryGetValue(key, out GamepadButton? physical) && physical is GamepadButton p && combo.Contains(p)) return true;
            if (turboMap.TryGetValue(key, out GamepadButton t) && combo.Contains(t)) return true;
        }

        return false;
    }

    // ==================================================================
    // 小さな道具
    // ==================================================================

    private static SolidColorBrush MakeBrush(string hex)
    {
        byte r = Convert.ToByte(hex.Substring(1, 2), 16);
        byte g = Convert.ToByte(hex.Substring(3, 2), 16);
        byte b = Convert.ToByte(hex.Substring(5, 2), 16);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
    }

    /// <summary>アプリ共通のブラシ(Styles/Colors.xaml)を引く。無ければ指定の色。</summary>
    private static Brush ResolveBrush(string key, byte r, byte g, byte b)
    {
        if (Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush brush) return brush;
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
    }
}
