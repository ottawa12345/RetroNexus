using System;
using System.Collections.Generic;
using System.Linq;
using EmulatorLauncher.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace EmulatorLauncher.Views;

/// <summary>
/// ゲーム中の中断メニュー「操作方法の確認」の、右側に出す中身。いま使っているコントローラーの図(ボタンごとの
/// 割り当て先つき)と、このコンソールのコントローラー(参照)を、黒い背景用の配色で縦に並べる。見るだけで、設定は変えられない。
/// 割り当ては、コントローラー設定画面と同じ ControllerMappingService から読む(ゲーム別の設定があればそれを使う)。
/// </summary>
public sealed class ControlsGuideView : StackPanel
{
    public ControlsGuideView(string scope, string? gameCustomJson)
    {
        Orientation = Orientation.Vertical;
        Width = PadDiagram.DiagramWidth;
        Spacing = 10;

        ControllerGlyphs.PadStyle style = ControllerGlyphs.GetPrimaryPadStyle();
        ControllerLayout layout = ConsoleControllerLayouts.Get(scope);

        // いま使っているコントローラー。割り当て先の名前を、ボタンごとに札で添える。
        Dictionary<string, GamepadButton?> assignments = ControllerMappingService.GetAssignments(scope, gameCustomJson);
        Dictionary<string, GamepadButton> turbo = ControllerMappingService.GetTurbo(scope, gameCustomJson);
        List<(string Key, string Label)> targets = Targets(layout, scope);

        var pad = new PadDiagram(style, dark: true);
        pad.Update(b => AssignedText(b, targets, assignments, turbo), new List<GamepadButton>());
        Children.Add(pad);

        // 参照: このコンソールのコントローラー
        Children.Add(MakeText($"参照: {layout.Name}", 16, 0x9A));
        var reference = new ConsolePadView(ConsoleControllerLayouts.GetReference(scope), dark: true)
        {
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Children.Add(reference);
    }

    private static TextBlock MakeText(string text, double size, byte gray) => new()
    {
        Text = text,
        FontSize = size,
        FontFamily = PadDiagram.SystemFont,
        Foreground = new SolidColorBrush(Color.FromArgb(255, gray, gray, gray)),
        TextWrapping = TextWrapping.Wrap,
        IsTextScaleFactorEnabled = false,
    };

    /// <summary>このコンソールで、ボタンの割り当て先になるもの(キーと表示名)。コントローラー設定画面と同じ。</summary>
    private static List<(string Key, string Label)> Targets(ControllerLayout layout, string scope)
    {
        var list = new List<(string Key, string Label)>();
        foreach (PadElement e in layout.Elements)
        {
            if (e.Key is not null) list.Add((e.Key, e.Label));
        }

        if (ControllerProfiles.FindForm(scope) is { } form)
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

    /// <summary>物理ボタン1つの割り当て先(連射は「〇連射」)。割り当てが無ければ空文字(札を出さない)。</summary>
    private static string AssignedText(GamepadButton physical, List<(string Key, string Label)> targets,
        Dictionary<string, GamepadButton?> assignments, Dictionary<string, GamepadButton> turbo)
    {
        List<string> names = targets
            .Where(e => turbo.TryGetValue(e.Key, out GamepadButton t) && t == physical)
            .Select(e => e.Label + "連射")
            .ToList();

        names.AddRange(targets
            .Where(e => assignments.TryGetValue(e.Key, out GamepadButton? p) && p == physical)
            .Select(e => e.Label));

        return string.Join("・", names);
    }
}
