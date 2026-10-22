using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using EmulatorLauncher.Input;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace EmulatorLauncher.Controls;

/// <summary>並び替え画面の1項目(システム画面の項目、または空白)。</summary>
public sealed class SystemOrderEntry : INotifyPropertyChanged
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public SystemItemCatalog.Kind Kind { get; set; } = SystemItemCatalog.Kind.Tile;
    /// <summary>横6マスのうちの幅。</summary>
    public int Span { get; set; } = 2;
    /// <summary>タイルの最小の高さ(システム画面と同じ値。プレビューの縦の比率に使う)。</summary>
    public double BaseHeight { get; set; } = 152;

    public bool IsSpacer => Kind == SystemItemCatalog.Kind.Spacer;

    public string KindText => Kind switch
    {
        SystemItemCatalog.Kind.Stat => "表示のみ",
        SystemItemCatalog.Kind.Slider => "スライダー",
        SystemItemCatalog.Kind.Spacer => "空白",
        _ => "タイル",
    };

    private bool _isFocused;
    public bool IsFocused
    {
        get => _isFocused;
        set { if (_isFocused == value) return; _isFocused = value; Raise(nameof(IsFocused)); }
    }

    private bool _isGrabbed;
    public bool IsGrabbed
    {
        get => _isGrabbed;
        set { if (_isGrabbed == value) return; _isGrabbed = value; Raise(nameof(IsGrabbed)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 「システム項目の並び替え」の全画面。使い方・操作はSystemItemsOrderOverlay.xamlのコメント参照。
/// 右のプレビューは、システム画面と同じ詰め方(横6マス、タイルは左から詰め、スライダーは1行)で小さく描き、
/// 並びを変えるたびに各ブロックを新しい位置へアニメーションで動かす。
/// </summary>
public sealed partial class SystemItemsOrderOverlay : UserControl
{
    private const int MaxSpacers = 12;
    private const double RowHeight = 84;

    // プレビューの寸法(システム画面の内側の幅636を、PreviewWidthへ縮小して描く)。
    private const double PreviewWidth = 420;
    private const double PanelWidth = 636;
    private const double Scale = PreviewWidth / PanelWidth;
    private const double Gap = 14 * Scale;
    private static readonly double Unit = (PreviewWidth - Gap * 5) / 6;
    private const int MoveMilliseconds = 240;

    public ObservableCollection<SystemOrderEntry> Items { get; } = new();

    public bool IsOpen { get; private set; }

    public LauncherSaveButtonState AddState { get; } = new();
    public LauncherSaveButtonState DeleteState { get; } = new();
    public LauncherSaveButtonState SaveState { get; } = new();

    private enum Zone { List, Buttons }
    private Zone _zone = Zone.List;
    private int _buttonIndex;   // 0=空白を追加 1=空白を削除 2=保存する

    private int _selectedIndex;
    private SystemOrderEntry? _grabbed;
    private int _grabStartIndex;
    private List<string> _originalOrder = new();
    private TaskCompletionSource<IReadOnlyList<string>?>? _tcs;

    private readonly Dictionary<SystemOrderEntry, Border> _boxes = new();
    private readonly Dictionary<SystemOrderEntry, Vector3> _positions = new();

    public SystemItemsOrderOverlay()
    {
        this.InitializeComponent();
        EmulatorLauncher.Controls.LauncherWheel.AttachAll(this);
        Visibility = Visibility.Collapsed;
        OverlayFade.Attach(this);

        Brush focusBrush = (Brush)new EmulatorLauncher.Converters.BoolToBrushConverter().Convert(true, typeof(Brush), null!, string.Empty);
        foreach (FrameworkElement frame in new FrameworkElement[] { ListFocusFrame, PreviewFocusFrame, ButtonFocusFrame })
        {
            ElementCompositionPreview.SetIsTranslationEnabled(frame, true);
            if (frame is Border b) b.BorderBrush = focusBrush;
        }
    }

    private readonly Dictionary<FrameworkElement, Vector3> _frameTargets = new();
    private readonly Dictionary<FrameworkElement, bool> _frameShown = new();

    /// <summary>枠(フォーカス表示)を、新しい位置へなめらかに動かす。初めて出すときは動かさず、その位置へ出す。</summary>
    private void SlideFrame(FrameworkElement frame, double x, double y, bool show, bool animate)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(frame);
        var target = new Vector3((float)x, (float)y, 0f);
        bool wasShown = _frameShown.TryGetValue(frame, out bool s) && s;

        if (show && animate && wasShown && _frameTargets.TryGetValue(frame, out Vector3 from) && from != target)
        {
            visual.Properties.InsertVector3("Translation", from);
            Compositor c = visual.Compositor;
            Vector3KeyFrameAnimation anim = c.CreateVector3KeyFrameAnimation();
            anim.InsertKeyFrame(1f, target, c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1.0f)));
            anim.Duration = TimeSpan.FromMilliseconds(180);
            visual.StartAnimation("Translation", anim);
        }
        else
        {
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", target);
        }
        _frameTargets[frame] = target;
        _frameShown[frame] = show;
        frame.Opacity = show ? 1 : 0;
    }

    private static bool Dark => EmulatorLauncher.Views.AppThemeRouter.IsDarkActive;

    /// <summary>画面を表示し、保存して戻る(B)まで待つ。保存したときだけ新しい項目Id順、変更なし/キャンセルならnullを返す。</summary>
    public Task<IReadOnlyList<string>?> ShowAsync(string title, IEnumerable<SystemOrderEntry> entries)
    {
        TitleText.Text = title;
        _frameShown.Clear();
        _frameTargets.Clear();

        foreach (Border box in _boxes.Values) PreviewCanvas.Children.Remove(box);
        _boxes.Clear();
        _positions.Clear();

        Items.Clear();
        foreach (SystemOrderEntry e in entries)
        {
            e.IsFocused = false;
            e.IsGrabbed = false;
            Items.Add(e);
        }

        _originalOrder = Items.Select(i => i.Id).ToList();
        _grabbed = null;
        _selectedIndex = 0;
        _zone = Zone.List;
        _buttonIndex = 0;

        PreviewFrame.Background = Dark
            ? new SolidColorBrush(Color.FromArgb(255, 0x18, 0x1D, 0x2B))
            : (Brush)Application.Current.Resources["AppSurfaceBrush"];

        UpdateAll(animate: false);
        ListScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        PreviewScroll.ChangeView(null, 0, null, disableAnimation: true);

        Visibility = Visibility.Visible;
        IsOpen = true;

        _tcs = new TaskCompletionSource<IReadOnlyList<string>?>();
        return _tcs.Task;
    }

    private bool HasChanges() => !Items.Select(i => i.Id).SequenceEqual(_originalOrder);

    /// <summary>変更があるまま戻ろうとしたとき、捨ててよいか確認する(不意に戻って変更が消えないようにする)。</summary>
    private bool IsDirty() => HasChanges();

    private async Task ConfirmDiscardAsync()
    {
        bool discard = await ConfirmOverlay.ShowAsync("保存していない変更があります。変更を捨てて戻りますか?", "変更を捨てる", "編集を続ける");
        if (discard && IsOpen) Close(save: false);
    }

    private void Close(bool save)
    {
        if (!IsOpen) return;

        Visibility = Visibility.Collapsed;
        IsOpen = false;

        IReadOnlyList<string>? result = save && HasChanges() ? Items.Select(i => i.Id).ToList() : null;
        TaskCompletionSource<IReadOnlyList<string>?>? tcs = _tcs;
        _tcs = null;
        tcs?.TrySetResult(result);
    }

    // ---- 入力 ----

    public void HandleGamepadInput(GamepadButton button)
    {
        if (ConfirmOverlay.IsOpen) { ConfirmOverlay.HandleGamepadInput(button); return; }
        switch (button)
        {
            case GamepadButton.DPadUp:
                if (_zone == Zone.Buttons) { _buttonIndex = Math.Max(0, _buttonIndex - 1); UpdateAll(animate: false); }
                else Move(-1);
                break;
            case GamepadButton.DPadDown:
                if (_zone == Zone.Buttons) { _buttonIndex = Math.Min(2, _buttonIndex + 1); UpdateAll(animate: false); }
                else Move(1);
                break;
            case GamepadButton.DPadRight:
                if (_zone == Zone.List && _grabbed is null && Items.Count > 0)
                {
                    _zone = Zone.Buttons;
                    UpdateAll(animate: false);
                }
                break;
            case GamepadButton.DPadLeft:
                if (_zone == Zone.Buttons) { _zone = Zone.List; UpdateAll(animate: false); }
                break;
            case GamepadButton.A:
                if (_zone == Zone.Buttons) ActivateButton(_buttonIndex);
                else ToggleGrab();
                break;
            case GamepadButton.X:
                if (_zone == Zone.List) AddSpacer();
                break;
            case GamepadButton.Y:
                if (_zone == Zone.List) DeleteSpacer();
                break;
            case GamepadButton.B:
                if (_grabbed is not null)
                {
                    // つかむ前の位置へ戻して置く。
                    Items.Move(_selectedIndex, _grabStartIndex);
                    _selectedIndex = _grabStartIndex;
                    Release();
                }
                else if (_zone == Zone.Buttons)
                {
                    _zone = Zone.List;
                    UpdateAll(animate: false);
                }
                else if (HasChanges())
                {
                    _ = ConfirmDiscardAsync();
                }
                else
                {
                    Close(save: false);
                }
                break;
        }
    }

    private void ActivateButton(int index)
    {
        switch (index)
        {
            case 0: AddSpacer(); break;
            case 1: DeleteSpacer(); break;
            case 2: if (SaveState.IsEnabled) Close(save: true); break;
        }
    }

    private void ToggleGrab()
    {
        if (Items.Count == 0) return;
        if (_grabbed is null)
        {
            _selectedIndex = Math.Clamp(_selectedIndex, 0, Items.Count - 1);
            _grabbed = Items[_selectedIndex];
            _grabStartIndex = _selectedIndex;
            _grabbed.IsGrabbed = true;
            UpdateAll(animate: false);
        }
        else
        {
            Release();
        }
    }

    private void Release()
    {
        if (_grabbed is not null) _grabbed.IsGrabbed = false;
        _grabbed = null;
        UpdateAll(animate: true);
        BringSelectedIntoView();
    }

    private void Move(int delta)
    {
        if (Items.Count == 0) return;
        int current = Math.Clamp(_selectedIndex, 0, Items.Count - 1);
        int next = current + delta;

        if (_grabbed is not null)
        {
            // つかんでいる間は端で止める(折り返さない)。
            if (next < 0 || next >= Items.Count) return;
            Items.Move(current, next);
            _selectedIndex = next;
        }
        else
        {
            _selectedIndex = GamepadListNavigator.Move(current, delta, Items.Count, wrap: true);
        }

        UpdateAll(animate: true);
        BringSelectedIntoView();
    }

    /// <summary>選んだ項目の下に空白を足す。</summary>
    private void AddSpacer()
    {
        if (_grabbed is not null) return;
        if (Items.Count(i => i.IsSpacer) >= MaxSpacers) return;

        var entry = new SystemOrderEntry
        {
            Id = SystemItemCatalog.NewSpacerId(),
            Label = "空白",
            Kind = SystemItemCatalog.Kind.Spacer,
            Span = 2,
            BaseHeight = 0,
        };
        int at = Items.Count == 0 ? 0 : Math.Min(_selectedIndex + 1, Items.Count);
        Items.Insert(at, entry);
        _selectedIndex = at;
        UpdateAll(animate: true);
        BringSelectedIntoView();
    }

    /// <summary>選んでいる項目が空白なら削除する(空白以外の項目は削除できない)。</summary>
    private void DeleteSpacer()
    {
        if (_grabbed is not null || Items.Count == 0) return;
        _selectedIndex = Math.Clamp(_selectedIndex, 0, Items.Count - 1);
        if (!Items[_selectedIndex].IsSpacer) return;

        Items.RemoveAt(_selectedIndex);
        _selectedIndex = Math.Clamp(_selectedIndex, 0, Math.Max(0, Items.Count - 1));
        UpdateAll(animate: true);
        BringSelectedIntoView();
    }

    private void ItemRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SystemOrderEntry item }) return;
        int index = Items.IndexOf(item);
        if (index < 0) return;

        if (_grabbed is null)
        {
            _zone = Zone.List;
            _selectedIndex = index;
            UpdateAll(animate: false);
        }
        else if (index != _selectedIndex && _selectedIndex >= 0 && _selectedIndex < Items.Count)
        {
            // つかんでいる最中に別の行をクリックしたら、つかんでいる行をそこへ動かして置く。
            Items.Move(_selectedIndex, index);
            _selectedIndex = index;
            UpdateAll(animate: true);
        }
        ToggleGrab();
    }

    private void AddButton_Tapped(object sender, TappedRoutedEventArgs e) { _zone = Zone.Buttons; _buttonIndex = 0; ActivateButton(0); }
    private void DeleteButton_Tapped(object sender, TappedRoutedEventArgs e) { _zone = Zone.Buttons; _buttonIndex = 1; ActivateButton(1); }
    private void SaveButton_Tapped(object sender, TappedRoutedEventArgs e) { _zone = Zone.Buttons; _buttonIndex = 2; ActivateButton(2); }

    // ---- 表示の更新 ----

    private void UpdateAll(bool animate)
    {
        if (Items.Count > 0) _selectedIndex = Math.Clamp(_selectedIndex, 0, Items.Count - 1);

        for (int i = 0; i < Items.Count; i++)
            Items[i].IsFocused = _zone == Zone.List && i == _selectedIndex;

        bool idle = _grabbed is null;
        bool onSpacer = Items.Count > 0 && Items[_selectedIndex].IsSpacer;
        AddState.IsEnabled = idle && Items.Count(i => i.IsSpacer) < MaxSpacers;
        DeleteState.IsEnabled = idle && onSpacer;
        SaveState.IsEnabled = idle && HasChanges();
        AddState.IsFocused = _zone == Zone.Buttons && _buttonIndex == 0;
        DeleteState.IsFocused = _zone == Zone.Buttons && _buttonIndex == 1;
        SaveState.IsFocused = _zone == Zone.Buttons && _buttonIndex == 2;

        NormalHints.Visibility = _zone == Zone.List && idle ? Visibility.Visible : Visibility.Collapsed;
        GrabHints.Visibility = !idle ? Visibility.Visible : Visibility.Collapsed;
        ButtonHints.Visibility = _zone == Zone.Buttons && idle ? Visibility.Visible : Visibility.Collapsed;

        RelayoutPreview(animate);

        // 一覧・プレビュー・右のボタンのフォーカス枠をスライドさせる。
        bool listFocus = _zone == Zone.List && Items.Count > 0;
        SlideFrame(ListFocusFrame, 0, _selectedIndex * RowHeight, listFocus, animate: true);

        bool buttonFocus = _zone == Zone.Buttons;
        double[] tops = { 0, 84, 168 };
        double[] heights = { 72, 72, 88 };
        ButtonFocusFrame.Height = heights[_buttonIndex];
        SlideFrame(ButtonFocusFrame, 0, tops[_buttonIndex], buttonFocus, animate: true);
    }

    private void BringSelectedIntoView()
    {
        double top = _selectedIndex * RowHeight;
        double bottom = top + RowHeight;
        double offset = ListScrollViewer.VerticalOffset;
        double viewport = ListScrollViewer.ViewportHeight;
        if (viewport > 0)
        {
            double? target = null;
            if (top < offset) target = top;
            else if (bottom > offset + viewport) target = bottom - viewport;
            if (target.HasValue) ListScrollViewer.ChangeView(null, Math.Max(0, target.Value), null, disableAnimation: true);
        }

        // プレビューも、選んでいるブロックが見えるようにスクロールする。
        if (_selectedIndex >= 0 && _selectedIndex < Items.Count
            && _positions.TryGetValue(Items[_selectedIndex], out Vector3 pos) && _boxes.TryGetValue(Items[_selectedIndex], out Border? box))
        {
            // Canvasの余白(14)と、選択ブロックの拡大(約7%)・枠の分も含めて、少し余裕を持たせて見えるようにする。
            double pTop = pos.Y + 14 - 12, pBottom = pos.Y + 14 + box.Height + 12;
            double pOffset = PreviewScroll.VerticalOffset, pView = PreviewScroll.ViewportHeight;
            if (pView > 0)
            {
                double? t = null;
                if (pTop < pOffset) t = pTop;
                else if (pBottom > pOffset + pView) t = pBottom - pView;
                if (t.HasValue) PreviewScroll.ChangeView(null, Math.Max(0, t.Value), null, disableAnimation: false);
            }
        }
    }

    // ---- プレビュー(システム画面と同じ詰め方で小さく描き、位置の変化をアニメーションする) ----

    private Border GetBox(SystemOrderEntry entry)
    {
        if (_boxes.TryGetValue(entry, out Border? existing)) return existing;

        var box = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(3),
            Padding = new Thickness(6, 4, 6, 4),
        };
        var text = new TextBlock
        {
            Text = entry.IsSpacer ? "空白" : entry.Label,
            FontSize = 17,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI Variable, Yu Gothic UI, Segoe UI"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        box.Child = text;
        ElementCompositionPreview.SetIsTranslationEnabled(box, true);
        Canvas.SetLeft(box, 0);
        Canvas.SetTop(box, 0);
        PreviewCanvas.Children.Add(box);
        _boxes[entry] = box;
        return box;
    }

    private void RelayoutPreview(bool animate)
    {
        _previewAnimate = true;
        // 削除された項目のブロックを外す。
        foreach (SystemOrderEntry gone in _boxes.Keys.Where(e => !Items.Contains(e)).ToList())
        {
            PreviewCanvas.Children.Remove(_boxes[gone]);
            _boxes.Remove(gone);
            _positions.Remove(gone);
        }

        double y = 0;
        var pending = new List<(SystemOrderEntry Entry, int Cell)>();
        int used = 0;

        void Flush()
        {
            if (pending.Count == 0) return;
            double rowBase = pending.Max(p => p.Entry.BaseHeight);
            if (rowBase <= 0) rowBase = 108;   // 空白だけの行
            double h = rowBase * Scale;
            foreach (var (entry, cell) in pending)
                Place(entry, cell * (Unit + Gap), y, entry.Span * Unit + (entry.Span - 1) * Gap, h, animate);
            y += h + Gap;
            pending.Clear();
            used = 0;
        }

        foreach (SystemOrderEntry entry in Items)
        {
            if (entry.Kind == SystemItemCatalog.Kind.Slider)
            {
                Flush();
                double h = SystemItemCatalog.SliderHeight * Scale;
                Place(entry, 0, y, PreviewWidth, h, animate);
                y += h + Gap;
                continue;
            }
            if (used + entry.Span > 6) Flush();
            pending.Add((entry, used));
            used += entry.Span;
        }
        Flush();

        PreviewCanvas.Width = PreviewWidth;
        PreviewCanvas.Height = Math.Max(0, y - Gap);
        RefreshBoxVisuals();
    }

    private void Place(SystemOrderEntry entry, double x, double y, double w, double h, bool animate)
    {
        Border box = GetBox(entry);
        box.Width = w;
        box.Height = h;

        var target = new Vector3((float)x, (float)y, 0f);
        Visual visual = ElementCompositionPreview.GetElementVisual(box);
        if (animate && _positions.TryGetValue(entry, out Vector3 from) && from != target)
        {
            // 今の位置から新しい位置へ、なめらかに動かす。
            visual.Properties.InsertVector3("Translation", from);
            Compositor compositor = visual.Compositor;
            CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1.0f));
            Vector3KeyFrameAnimation anim = compositor.CreateVector3KeyFrameAnimation();
            anim.InsertKeyFrame(1f, target, ease);
            anim.Duration = TimeSpan.FromMilliseconds(MoveMilliseconds);
            visual.StartAnimation("Translation", anim);
        }
        else
        {
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", target);
        }
        _positions[entry] = target;
    }

    private void RefreshBoxVisuals()
    {
        bool dark = Dark;
        Color tileBg = dark ? Color.FromArgb(255, 0x26, 0x2E, 0x44) : Color.FromArgb(255, 0xE6, 0xE8, 0xEE);
        Color tileFg = dark ? Color.FromArgb(255, 0xE8, 0xEA, 0xF0) : Color.FromArgb(255, 0x22, 0x22, 0x22);
        Color focus = Color.FromArgb(255, 0x1F, 0xC8, 0xE8);
        Color grabBg = Color.FromArgb(255, 0x3B, 0x49, 0xB5);

        for (int i = 0; i < Items.Count; i++)
        {
            SystemOrderEntry entry = Items[i];
            if (!_boxes.TryGetValue(entry, out Border? box)) continue;

            bool selected = i == _selectedIndex;
            bool grabbed = ReferenceEquals(entry, _grabbed);
            var text = (TextBlock)box.Child;

            if (entry.IsSpacer)
            {
                // 空白は、枠だけの薄いブロック(実際の画面では何も表示されない)。
                box.Background = new SolidColorBrush(Color.FromArgb(grabbed ? (byte)0x50 : (byte)0x14, 0x80, 0x88, 0x99));
                box.BorderBrush = new SolidColorBrush(Color.FromArgb(0x70, 0x80, 0x88, 0x99));
                text.Foreground = new SolidColorBrush(Color.FromArgb(0xA0, 0x80, 0x88, 0x99));
            }
            else
            {
                box.Background = new SolidColorBrush(grabbed ? grabBg : tileBg);
                box.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                text.Foreground = new SolidColorBrush(grabbed ? Microsoft.UI.Colors.White : tileFg);
            }
            // つかんでいるブロックは手前へ。
            Canvas.SetZIndex(box, grabbed ? 2 : (selected ? 1 : 0));

            // 選んでいるブロックは、少しだけ大きくする(アニメーション)。
            Visual v = ElementCompositionPreview.GetElementVisual(box);
            v.CenterPoint = new Vector3((float)(box.Width / 2), (float)(box.Height / 2), 0f);
            float scale = selected ? 1.07f : 1f;
            Compositor comp = v.Compositor;
            Vector3KeyFrameAnimation sa = comp.CreateVector3KeyFrameAnimation();
            sa.InsertKeyFrame(1f, new Vector3(scale, scale, 1f), comp.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1.0f)));
            sa.Duration = TimeSpan.FromMilliseconds(180);
            v.StartAnimation("Scale", sa);
        }

        // プレビューのフォーカス枠(選んでいるブロックへスライドし、同じだけ大きくなる)。
        if (_selectedIndex >= 0 && _selectedIndex < Items.Count
            && _positions.TryGetValue(Items[_selectedIndex], out Vector3 pos) && _boxes.TryGetValue(Items[_selectedIndex], out Border? sel))
        {
            PreviewFocusFrame.Width = sel.Width;
            PreviewFocusFrame.Height = sel.Height;
            Visual fv = ElementCompositionPreview.GetElementVisual(PreviewFocusFrame);
            fv.CenterPoint = new Vector3((float)(sel.Width / 2), (float)(sel.Height / 2), 0f);
            fv.Scale = new Vector3(1.07f, 1.07f, 1f);
            SlideFrame(PreviewFocusFrame, pos.X, pos.Y, true, animate: _previewAnimate);
        }
        else
        {
            SlideFrame(PreviewFocusFrame, 0, 0, false, animate: false);
        }
    }

    private bool _previewAnimate;
}
