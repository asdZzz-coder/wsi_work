using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using schedule.Services;

namespace schedule.Views
{
    /// <summary>
    /// 日期輸入框（只有日期，沒有時間）：按右邊的日曆圖示、Alt+↓ 或 F4 從月曆挑日期，也可以直接打字
    /// （2026/10/06、10/6、20261006 都可以，離開欄位時整理成 2026/10/06）。
    /// 月曆下方有「今天」「清除」。本身就是 TextBox，讀寫一樣用 Text。
    /// </summary>
    public class DateBox : TextBox
    {
        private Popup? _popup;
        private Calendar? _calendar;
        private ButtonBase? _toggle;
        private bool _syncing; // 打開月曆時設定選取的日期，不要當成使用者點選
        private readonly PopupDismisser _dismisser;

        public DateBox()
        {
            _dismisser = new PopupDismisser(this, () => _popup);
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            if (_popup != null)
            {
                _popup.Opened -= Popup_Opened;
                _popup.Closed -= Popup_Closed;
            }
            _popup = GetTemplateChild("PART_Popup") as Popup;
            _calendar = GetTemplateChild("PART_Calendar") as Calendar;
            _toggle = GetTemplateChild("PART_Toggle") as ButtonBase;

            if (_toggle != null) _toggle.Click += (_, _) => Toggle();
            if (_popup != null)
            {
                _popup.Opened += Popup_Opened;
                _popup.Closed += Popup_Closed;
                if (_popup.Child is UIElement child) child.LostKeyboardFocus += (_, e) => CloseIfFocusLeft(e);
            }
            if (_calendar != null)
            {
                _calendar.PreviewMouseLeftButtonUp += Calendar_MouseUp;
                _calendar.PreviewKeyDown += Calendar_KeyDown;
            }
            if (GetTemplateChild("PART_Today") is ButtonBase today) today.Click += (_, _) => Pick(DateTime.Today);
            if (GetTemplateChild("PART_Clear") is ButtonBase clear) clear.Click += (_, _) => Pick(null);
        }

        public bool IsDropDownOpen => _popup?.IsOpen == true;

        /// <summary>目前輸入框裡的日期（空白或看不懂時為 null）。</summary>
        public DateTime? Value => TextFormat.TryParseOptionalDate(Text, out var d) ? d : null;

        private void Toggle()
        {
            if (_popup == null) return;
            _popup.IsOpen = !_popup.IsOpen;
        }

        private void Popup_Opened(object? sender, EventArgs e)
        {
            // 月曆打開時，按鈕不接受滑鼠：點按鈕只會讓月曆關掉，不會馬上又打開
            if (_toggle != null) _toggle.IsHitTestVisible = false;
            if (_calendar == null) return;
            _syncing = true;
            var value = Value;
            _calendar.DisplayMode = CalendarMode.Month;
            _calendar.SelectedDate = value;
            _calendar.DisplayDate = value ?? DateTime.Today;
            _syncing = false;
            // 讓方向鍵可以直接在月曆上移動
            Dispatcher.BeginInvoke(() => _calendar.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void Popup_Closed(object? sender, EventArgs e)
        {
            if (_toggle != null) _toggle.IsHitTestVisible = true;
        }

        /// <summary>選好日期（null = 清除），關掉月曆，游標回到輸入框。</summary>
        private void Pick(DateTime? date)
        {
            Text = TextFormat.Date(date);
            CaretIndex = Text.Length;
            if (_popup != null) _popup.IsOpen = false;
            // 等滑鼠放開、月曆處理完再把焦點拿回來，不然會被月曆搶回去
            Dispatcher.BeginInvoke(() => Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }

        // 用滑鼠點了某一天才帶入（換月份、換年份不算）
        private void Calendar_MouseUp(object sender, MouseButtonEventArgs e)
        {
            // 月曆按下時會抓住滑鼠，不放開的話下一次點別的地方要點兩下
            if (Mouse.Captured is CalendarItem or CalendarDayButton) Mouse.Capture(null);
            if (_syncing || FindDayButton(e.OriginalSource as DependencyObject) is not { } day) return;
            if (day.DataContext is DateTime date && !day.IsBlackedOut) Pick(date);
        }

        private void Calendar_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter when _calendar?.DisplayMode == CalendarMode.Month:
                    Pick(_calendar.SelectedDate ?? _calendar.DisplayDate);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    if (_popup != null) _popup.IsOpen = false;
                    Focus();
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>焦點離開輸入框和月曆（例如按 Tab 到下一格）時關掉月曆。</summary>
        private void CloseIfFocusLeft(KeyboardFocusChangedEventArgs e)
        {
            if (IsDropDownOpen && !_dismisser.Contains(e.NewFocus as DependencyObject)) _dismisser.Close();
        }

        private static CalendarDayButton? FindDayButton(DependencyObject? node)
        {
            for (; node != null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
                if (node is CalendarDayButton b) return b;
            return null;
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.F4 || (e.SystemKey == Key.Down && Keyboard.Modifiers == ModifierKeys.Alt))
            {
                Toggle();
                e.Handled = true;
            }
            base.OnPreviewKeyDown(e);
        }

        // 離開欄位時，看得懂的日期整理成統一格式（看不懂的留著，存檔時會提醒）
        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            CloseIfFocusLeft(e);
            if (IsDropDownOpen) return; // 焦點移到月曆上
            if (Text.Trim().Length > 0 && TextFormat.TryParseOptionalDate(Text, out var d) && d is { } date)
            {
                var normalized = TextFormat.Date(date);
                if (Text != normalized) Text = normalized;
            }
        }
    }
}
