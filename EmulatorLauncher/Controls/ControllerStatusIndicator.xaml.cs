using System;
using EmulatorLauncher.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmulatorLauncher.Controls;

/// <summary>接続中のコントローラー(1P/2P)の表示。</summary>
public sealed partial class ControllerStatusIndicator : UserControl
{
    // 点灯の滑らかさ(1回の更新あたりの変化量)。
    private const double RiseStep = 0.10;
    private const double FallStep = 0.035;

    // 入力が無いときの緑の四角の濃さ(完全には消さず、「ここにランプがある」と分かるようにする)。
    private const double IdleOpacity = 0.18;

    private readonly DispatcherQueueTimer _timer;
    private readonly double[] _levels = new double[GamepadSlots.MaxSlots];

    public ControllerStatusIndicator()
    {
        this.InitializeComponent();

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(33);
        _timer.Tick += (_, _) => UpdateState();

        Loaded += (_, _) =>
        {
            UpdateState();
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void UpdateState()
    {
        UpdateSlot(0, Pad1Panel, Pad1Light);
        UpdateSlot(1, Pad2Panel, Pad2Light);
    }

    private void UpdateSlot(int slot, StackPanel panel, UIElement light)
    {
        bool connected = GamepadSlots.IsConnected(slot);

        Visibility visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        if (panel.Visibility != visibility) panel.Visibility = visibility;

        if (!connected)
        {
            _levels[slot] = 0;
            return;
        }

        double target = GamepadSlots.IsActive(slot) ? 1.0 : 0.0;
        double level = _levels[slot];
        level = target > level ? Math.Min(target, level + RiseStep) : Math.Max(target, level - FallStep);
        _levels[slot] = level;

        light.Opacity = IdleOpacity + (1.0 - IdleOpacity) * level;
    }
}
