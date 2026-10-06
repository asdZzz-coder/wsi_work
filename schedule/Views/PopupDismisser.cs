using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace schedule.Views
{
    /// <summary>
    /// 輸入框附帶的下拉選單 / 月曆（StaysOpen 的 Popup）：在外面點滑鼠、視窗移動或縮放、切到別的程式、
    /// 外面捲動時關掉。不用 StaysOpen=False，是因為那樣在外面點一下只會關掉選單，點到的東西沒反應。
    /// </summary>
    internal sealed class PopupDismisser
    {
        private readonly FrameworkElement _owner;
        private readonly Func<Popup?> _popup;
        private Window? _window;

        public PopupDismisser(FrameworkElement owner, Func<Popup?> popup)
        {
            _owner = owner;
            _popup = popup;
            owner.Loaded += (_, _) => Attach();
            owner.Unloaded += (_, _) => Detach();
        }

        private bool IsOpen => _popup()?.IsOpen == true;

        public void Close()
        {
            if (_popup() is { } p) p.IsOpen = false;
        }

        /// <summary>這個元素在輸入框或選單裡面。</summary>
        public bool Contains(DependencyObject? node) => IsInside(node, _owner) || IsInside(node, _popup()?.Child);

        private void Attach()
        {
            Detach();
            _window = Window.GetWindow(_owner);
            if (_window == null) return;
            _window.Deactivated += Window_Changed;
            _window.LocationChanged += Window_Changed;
            _window.SizeChanged += Window_Changed;
            _window.PreviewMouseDown += Window_PreviewMouseDown;
            _window.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Window_ScrollChanged));
        }

        private void Detach()
        {
            if (_window == null) return;
            _window.Deactivated -= Window_Changed;
            _window.LocationChanged -= Window_Changed;
            _window.SizeChanged -= Window_Changed;
            _window.PreviewMouseDown -= Window_PreviewMouseDown;
            _window.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Window_ScrollChanged));
            _window = null;
            Close();
        }

        private void Window_Changed(object? sender, EventArgs e) => Close();

        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (IsOpen && !Contains(e.OriginalSource as DependencyObject)) Close();
        }

        // 外面捲動時選單會留在原地，直接關掉（選單自己的捲動不算）
        private void Window_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (!IsOpen || IsInside(e.OriginalSource as DependencyObject, _popup()?.Child)) return;
            if (e.VerticalChange != 0 || e.HorizontalChange != 0) Close();
        }

        public static bool IsInside(DependencyObject? node, DependencyObject? container)
        {
            if (container == null) return false;
            for (; node != null; node = ParentOf(node))
                if (node == container) return true;
            return false;
        }

        public static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
        {
            for (; node != null; node = ParentOf(node))
                if (node is T match) return match;
            return null;
        }

        private static DependencyObject? ParentOf(DependencyObject node) =>
            node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
    }
}
