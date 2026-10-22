using System;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace EmulatorLauncher.Controls;

/// <summary>
/// 縦スクロールのScrollViewerの上でマウスのホイールを回したとき、画面を勝手にスクロールさせずに、
/// ランチャーの操作(十字キー上下=フォーカス移動)として扱うための部品。
/// ScrollViewerはホイールを自分で処理してしまうので、その中身の要素(ScrollViewerより内側)で受け取って
/// <see cref="MainWindow.HandleLauncherWheel"/>へ渡す。フォーカスが動けば、各画面の処理で見える位置までスクロールされる。
/// テキストボックスに入力中など、ランチャー操作を受け付けない間は何もしない(ScrollViewer本来のスクロールになる)。
///
/// 使い方: 画面/オーバーレイのコンストラクタで InitializeComponent() の後に <see cref="AttachAll"/>(this) を呼ぶ。
/// </summary>
public static class LauncherWheel
{
    private static readonly ConditionalWeakTable<FrameworkElement, object> Attached = new();
    private static readonly ConditionalWeakTable<FrameworkElement, object> Roots = new();

    /// <summary>root以下のScrollViewerすべてに適用する。表示された時(Loaded/Visible)にも、もう一度探す。</summary>
    /// <param name="useNativeScroll">
    /// 省略可。trueを返したScrollViewer(引数はその中身の要素)では、ホイールをランチャー操作に変換せず、ScrollViewer本来のスクロールにする
    /// (例: 設定画面で左メニューにフォーカスがある間、右のパネルはマウスで普通にスクロールできるようにする)。
    /// </param>
    public static void AttachAll(FrameworkElement root, Func<FrameworkElement, bool>? useNativeScroll = null)
    {
        if (Roots.TryGetValue(root, out _)) return;
        Roots.Add(root, new object());

        root.Loaded += (_, _) => Walk(root, useNativeScroll);
        root.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) =>
        {
            if (root.Visibility == Visibility.Visible) Walk(root, useNativeScroll);
        });
        Walk(root, useNativeScroll);
    }

    private static void Walk(DependencyObject node, Func<FrameworkElement, bool>? native)
    {
        if (node is ScrollViewer sv) Attach(sv, native);
        if (node is ListViewBase list) HookList(list, native);

        int count;
        try { count = VisualTreeHelper.GetChildrenCount(node); }
        catch { return; }
        for (int i = 0; i < count; i++) Walk(VisualTreeHelper.GetChild(node, i), native);

        // テンプレート適用前のコントロールは、Contentを直接たどる。
        if (node is ContentControl { Content: DependencyObject content } && count == 0) Walk(content, native);
    }

    /// <summary>
    /// ListView/GridViewは、中のScrollViewerがテンプレートの中にあり、画面が表示されるまで作られないことがある。
    /// 表示されて大きさが決まった時点で、その中のScrollViewerを探して適用する(適用できたら以後は何もしない)。
    /// </summary>
    private static void HookList(ListViewBase list, Func<FrameworkElement, bool>? native)
    {
        if (ListHooked.TryGetValue(list, out _)) return;
        ListHooked.Add(list, new object());

        bool done = false;
        void TryAttach()
        {
            if (done) return;
            ScrollViewer? inner = FindScrollViewer(list);
            if (inner is null) return;
            done = true;
            Attach(inner, native);
        }

        list.Loaded += (_, _) => TryAttach();
        list.SizeChanged += (_, _) => TryAttach();
        TryAttach();
    }

    private static readonly ConditionalWeakTable<FrameworkElement, object> ListHooked = new();

    private static ScrollViewer? FindScrollViewer(DependencyObject node)
    {
        int count;
        try { count = VisualTreeHelper.GetChildrenCount(node); }
        catch { return null; }
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            if (child is ScrollViewer sv) return sv;
            ScrollViewer? found = FindScrollViewer(child);
            if (found is not null) return found;
        }
        return null;
    }

    private static void Attach(ScrollViewer sv, Func<FrameworkElement, bool>? native)
    {
        // 縦スクロールできないもの(横スクロール専用など)は、各画面が個別に扱っているので対象外。
        if (sv.VerticalScrollMode == ScrollMode.Disabled) return;
        if (sv.Content is not FrameworkElement content) return;
        if (Attached.TryGetValue(content, out _)) return;
        Attached.Add(content, new object());

        content.PointerWheelChanged += (_, e) =>
        {
            if (native is not null && native(content)) return;
            AppServices.MainWindow?.HandleLauncherWheel(e);
        };
    }
}
