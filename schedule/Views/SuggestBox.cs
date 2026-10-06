using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using schedule.Services;

namespace schedule.Views
{
    /// <summary>
    /// 可以直接輸入、也可以從下拉選單挑之前輸入過的值的輸入框。
    /// 打字時只列出包含輸入文字的值；按 ▾ 或方向鍵 ↓ 列出全部。
    /// 用 ↑↓ 選、Enter 帶入、Esc 關閉；選單裡每一筆右邊的 × 或 Shift+Delete 可以把它從清單移除。
    /// 本身就是 TextBox，讀寫一樣用 Text。
    /// </summary>
    public class SuggestBox : TextBox
    {
        private Popup? _popup;
        private ListBox? _list;
        private FrameworkElement? _empty;
        private readonly PopupDismisser _dismisser;

        private IReadOnlyList<string> _items = Array.Empty<string>();
        private bool _showingAll;  // 目前列出的是全部（按 ▾），不是依輸入篩選
        private bool _userInput;   // 這次文字變動是使用者打的（程式設定 Text 時不要跳出選單）

        /// <summary>使用者從選單移除了一筆（呼叫端負責從記錄刪掉、再更新 Items）。</summary>
        public event Action<SuggestBox, string>? ItemRemoved;

        public SuggestBox()
        {
            _dismisser = new PopupDismisser(this, () => _popup);
            DataObject.AddPastingHandler(this, (_, _) => _userInput = true);
            // 中文輸入法選字中的文字也算使用者輸入（送出時不一定還會再變動一次）
            TextCompositionManager.AddPreviewTextInputStartHandler(this, (_, _) => _userInput = true);
            TextCompositionManager.AddPreviewTextInputUpdateHandler(this, (_, _) => _userInput = true);
        }

        /// <summary>下拉選單的內容（最近用過的在前）。</summary>
        public IReadOnlyList<string> Items
        {
            get => _items;
            set
            {
                _items = value;
                if (IsDropDownOpen) Open(_showingAll);
            }
        }

        public bool IsDropDownOpen => _popup?.IsOpen == true;

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            _popup = GetTemplateChild("PART_Popup") as Popup;
            _list = GetTemplateChild("PART_List") as ListBox;
            _empty = GetTemplateChild("PART_Empty") as FrameworkElement;
            if (GetTemplateChild("PART_Toggle") is ButtonBase toggle)
                toggle.Click += Toggle_Click;
            if (_list != null)
            {
                _list.PreviewMouseLeftButtonUp += List_MouseUp;
                _list.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(Remove_Click));
            }
        }

        // ---------- 開關選單 ----------

        /// <summary>all = 列出全部；否則只列出符合目前輸入的（沒有符合的就關掉）。</summary>
        private void Open(bool all)
        {
            if (_popup == null || _list == null) return;
            var text = Text.Trim();
            var matches = all ? _items.ToList() : InputHistory.Filter(_items, text);
            // 只剩一筆而且就是已經打好的字，不用再跳出來
            if (!all && (matches.Count == 0 || matches.Count == 1 && string.Equals(matches[0], text, StringComparison.OrdinalIgnoreCase)))
            {
                Close();
                return;
            }

            _showingAll = all;
            _list.ItemsSource = matches;
            // 列出全部時，先選到目前的值；依輸入篩選時不預選，免得按 Enter 時被換掉
            _list.SelectedIndex = all ? matches.FindIndex(v => string.Equals(v, text, StringComparison.OrdinalIgnoreCase)) : -1;
            if (_empty != null) _empty.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _list.Visibility = matches.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            _popup.IsOpen = true;
            if (_list.SelectedItem != null) _list.ScrollIntoView(_list.SelectedItem);
            else if (matches.Count > 0) _list.ScrollIntoView(matches[0]);
        }

        private void Close()
        {
            if (_popup != null) _popup.IsOpen = false;
        }

        private void Toggle_Click(object sender, RoutedEventArgs e)
        {
            if (IsDropDownOpen)
            {
                Close();
                return;
            }
            Focus();
            CaretIndex = Text.Length;
            Open(all: true);
        }

        /// <summary>帶入選到的值，游標放到最後。</summary>
        private void Accept(string value)
        {
            Text = value;
            CaretIndex = Text.Length;
            Close();
            Focus();
        }

        // ---------- 輸入 ----------

        protected override void OnPreviewTextInput(TextCompositionEventArgs e)
        {
            _userInput = true;
            base.OnPreviewTextInput(e);
        }

        protected override void OnTextChanged(TextChangedEventArgs e)
        {
            base.OnTextChanged(e);
            if (!_userInput) return; // 程式設定的（例如點了別筆工令）
            _userInput = false;
            if (IsKeyboardFocused) Open(all: false);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down when Keyboard.Modifiers == ModifierKeys.Alt:
                case Key.F4:
                    Toggle_Click(this, e);
                    e.Handled = true;
                    break;
                case Key.Down:
                    if (IsDropDownOpen) Move(+1);
                    else Open(all: true);
                    e.Handled = true;
                    break;
                case Key.Up when IsDropDownOpen:
                    Move(-1);
                    e.Handled = true;
                    break;
                case Key.Enter when IsDropDownOpen:
                    if (_list?.SelectedItem is string value) Accept(value);
                    else Close();
                    e.Handled = true;
                    break;
                case Key.Escape when IsDropDownOpen:
                    Close();
                    e.Handled = true;
                    break;
                case Key.Delete when IsDropDownOpen && Keyboard.Modifiers == ModifierKeys.Shift && _list?.SelectedItem is string selected:
                    ItemRemoved?.Invoke(this, selected);
                    e.Handled = true;
                    break;
                case Key.Back:
                case Key.Delete:
                    _userInput = true;
                    break;
                case Key.Tab:
                    Close();
                    break;
            }
            if (!e.Handled && Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.X or Key.Z or Key.Y)
                _userInput = true;
            base.OnPreviewKeyDown(e);
        }

        private void Move(int step)
        {
            if (_list == null || _list.Items.Count == 0) return;
            int i = _list.SelectedIndex < 0 ? (step > 0 ? 0 : _list.Items.Count - 1) : _list.SelectedIndex + step;
            _list.SelectedIndex = Math.Clamp(i, 0, _list.Items.Count - 1);
            _list.ScrollIntoView(_list.SelectedItem);
        }

        // ---------- 選單裡的滑鼠操作 ----------

        private void List_MouseUp(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            if (PopupDismisser.FindAncestor<ButtonBase>(source) != null) return; // 點的是 ×，交給 Remove_Click
            if (PopupDismisser.FindAncestor<ListBoxItem>(source)?.DataContext is string value)
            {
                Accept(value);
                e.Handled = true;
            }
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is string value)
                ItemRemoved?.Invoke(this, value);
            e.Handled = true;
            Focus();
        }

        // ---------- 離開輸入框時關掉選單（在外面點滑鼠、視窗移動等由 PopupDismisser 處理） ----------

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            _userInput = false;
            Close();
        }
    }
}
