using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using schedule.Models;
using schedule.Services;

namespace schedule.Views
{
    /// <summary>
    /// 流程卡（Run Card）：一張工令從圖面到交貨的每一站、品管檢驗、異常 / 變更紀錄，以及暫停。
    /// 編輯的是複本，按「儲存」才產生新的工令（Result）交給主視窗存檔；有對應時間點的站別會改到工令上的日期。
    /// </summary>
    public partial class RunCardWindow : Window
    {
        private static readonly string[] IssueTypes = { "缺料", "設計變更", "客戶變更", "品質異常", "製程異常", "其他" };

        private readonly Job _original;
        private readonly IReadOnlyList<string> _people;
        private readonly ObservableCollection<StepRow> _steps;
        private readonly ObservableCollection<CheckRow> _checks;
        private readonly ObservableCollection<IssueRow> _issues;
        private bool _dirty;
        private bool _loading = true;
        private bool _closingAfterSave;

        /// <summary>按「儲存」後的工令（Id 與原本相同）；沒儲存就關掉為 null。</summary>
        public Job? Result { get; private set; }

        public RunCardWindow(Job job, IReadOnlyList<string> people)
        {
            InitializeComponent();
            // 小螢幕（例如筆電）上不要讓視窗超出可用範圍
            Height = Math.Min(Height, SystemParameters.WorkArea.Height - 20);
            Width = Math.Min(Width, SystemParameters.WorkArea.Width - 20);

            _original = job;
            _people = people;
            var work = RunCardRules.Clone(job);
            RunCardRules.Ensure(work);

            _steps = new ObservableCollection<StepRow>(work.Steps.Select(s => new StepRow(s, work)));
            _checks = new ObservableCollection<CheckRow>(work.Checks.Select(c => new CheckRow(c)));
            _issues = new ObservableCollection<IssueRow>(work.Issues.Select(i => new IssueRow(i)));
            Watch(_steps);
            Watch(_checks);
            Watch(_issues);
            StepList.ItemsSource = _steps;
            CheckList.ItemsSource = _checks;
            IssueList.ItemsSource = _issues;

            HoldBox.IsChecked = work.HoldReason.Trim().Length > 0;
            HoldReasonBox.Text = work.HoldReason;
            HoldReasonBox.IsEnabled = HoldBox.IsChecked == true;

            Title = $"流程卡 - {job.WorkOrder}";
            FillHeader(work);
            Renumber();
            RefreshSummary();
            UpdateTabHint();
            _loading = false;

            Closing += OnClosing;
            ThemeService.ThemeChanged += ApplyTitleBar;
            Closed += (_, _) => ThemeService.ThemeChanged -= ApplyTitleBar;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ApplyTitleBar();
        }

        private void ApplyTitleBar() => WindowTheme.ApplyTitleBar(this);

        // ---------- 上方的工令資料 ----------

        private void FillHeader(Job job)
        {
            WorkOrderText.Text = job.WorkOrder;
            if (job.Priority != JobPriority.Normal)
            {
                PriorityBadge.Visibility = Visibility.Visible;
                PriorityText.Text = RunCardRules.PriorityText(job.Priority);
            }
            var certs = new[] { job.HasCE ? "CE" : "", job.HasTS ? "TS" : "" }.Where(c => c.Length > 0).ToList();
            if (certs.Count > 0)
            {
                CertBadge.Visibility = Visibility.Visible;
                CertText.Text = string.Join(" · ", certs);
            }
            SubtitleText.Text = string.Join("　·　", new[]
            {
                job.Model, job.Quantity > 0 ? $"{job.Quantity:#,0} 台" : "", job.Customer,
            }.Where(s => s.Trim().Length > 0));

            var info = new (string Label, string Value)[]
            {
                ("案名", job.ProjectName), ("訂單號碼", job.OrderNo), ("圖號", job.DrawingNo), ("業務", job.Sales),
                ("盤內配電", job.InnerWiring), ("盤外配電", job.OuterWiring), ("耗材提供", job.Consumables),
                ("預計交期", TextFormat.Date(job.DeliveryPlan)),
            };
            foreach (var (label, value) in info.Where(i => i.Value.Trim().Length > 0))
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
                panel.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("InfoLabel") });
                panel.Children.Add(new TextBlock { Text = value.Trim(), Style = (Style)FindResource("InfoValue") });
                InfoPanel.Children.Add(panel);
            }
        }

        /// <summary>進度、目前站別、分頁上的數字。</summary>
        private void RefreshSummary()
        {
            int total = _steps.Count;
            var passed = RunCardRules.Passed(_steps.Select(r => r.IsFinished).ToList());
            for (int i = 0; i < total; i++) _steps[i].ImplicitlyPassed = passed[i] && !_steps[i].IsFinished;
            int done = passed.Count(p => p);
            ProgressCount.Text = $"{done} / {total} 站";
            ProgressFill.Width = new GridLength(done, GridUnitType.Star);
            ProgressRest.Width = new GridLength(Math.Max(total - done, 0) + (total == 0 ? 1 : 0), GridUnitType.Star);
            int currentIndex = Array.IndexOf(passed, false);
            var current = currentIndex < 0 ? null : _steps[currentIndex];
            CurrentStepText.Text = current == null ? "全部完成" : $"目前站別：{current.Name.Trim()}";

            TabStepsText.Text = $"流程站別 {done}/{total}";
            int failed = _checks.Count(c => c.IsFail);
            int pending = _checks.Count(c => c.Result == CheckResult.Pending);
            TabChecksText.Text = failed > 0 ? $"品管檢驗（{failed} 不合格）" : pending > 0 ? $"品管檢驗（{pending} 未檢）" : "品管檢驗";
            int open = _issues.Count(i => i.IsOpen);
            TabIssuesText.Text = open > 0 ? $"異常 / 變更（{open} 未結案）" : _issues.Count > 0 ? $"異常 / 變更（{_issues.Count}）" : "異常 / 變更";
            IssueEmpty.Visibility = _issues.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static DateTime? Parse(string text) => TextFormat.TryParseOptionalDate(text, out var d) ? d : null;

        // ---------- 修改追蹤 ----------

        private void Watch<T>(ObservableCollection<T> rows) where T : EditRow
        {
            foreach (var row in rows) row.PropertyChanged += Row_Changed;
            rows.CollectionChanged += (_, e) =>
            {
                if (e.NewItems != null) foreach (T row in e.NewItems) row.PropertyChanged += Row_Changed;
                if (e.OldItems != null && e.Action == NotifyCollectionChangedAction.Remove)
                    foreach (T row in e.OldItems) row.PropertyChanged -= Row_Changed;
                MarkDirty();
            };
        }

        private void Row_Changed(object? sender, PropertyChangedEventArgs e)
        {
            // 算出來的屬性（狀態等）不算修改
            if (e.PropertyName is "State" or "StateText" or "StateTag" or "IsFail" or "IsOpen" or "Number" or "ImplicitlyPassed") return;
            MarkDirty();
        }

        private void MarkDirty()
        {
            if (_loading) return;
            _dirty = true;
            RefreshSummary();
        }

        private void Field_Changed(object sender, TextChangedEventArgs e) => MarkDirty();

        private void Hold_Changed(object sender, RoutedEventArgs e)
        {
            HoldReasonBox.IsEnabled = HoldBox.IsChecked == true;
            if (_loading) return;
            MarkDirty();
            if (HoldBox.IsChecked == true && HoldReasonBox.Text.Trim().Length == 0) HoldReasonBox.Focus();
        }

        // ---------- 分頁 ----------

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (StepsPanel == null) return; // InitializeComponent 期間
            StepsPanel.Visibility = TabSteps.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            ChecksPanel.Visibility = TabChecks.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            IssuesPanel.Visibility = TabIssues.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            UpdateTabHint();
        }

        private void UpdateTabHint()
        {
            TabHint.Text = TabChecks.IsChecked == true ? "量測值可以寫數字和單位，例如 50 MΩ。結果選「不合格」時，這張工令會列在「暫停 / 異常」。"
                : TabIssues.IsChecked == true ? "還沒結案的紀錄會讓工令列在「暫停 / 異常」，處理好後按 ✓ 結案。"
                : "預計完成日期請依流程順序填；按日曆圖示選日期，也可以直接打 10/6。";
        }

        // ---------- 列上的按鈕 ----------

        private static T? RowOf<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;

        private static string Today => TextFormat.Date(DateTime.Today);

        private void StepDone_Click(object sender, RoutedEventArgs e)
        {
            if (RowOf<StepRow>(sender) is not { } row) return;
            if (row.DoneText.Trim().Length > 0)
            {
                StatusText.Text = $"「{row.Name}」已經填了完成日期 {row.DoneText}";
                return;
            }
            row.DoneText = Today;
            row.Skipped = false;
            StatusText.Text = $"「{row.Name}」今天完成";
        }

        private void StepUp_Click(object sender, RoutedEventArgs e) => MoveStep(RowOf<StepRow>(sender), -1);

        private void StepDown_Click(object sender, RoutedEventArgs e) => MoveStep(RowOf<StepRow>(sender), +1);

        private void MoveStep(StepRow? row, int delta)
        {
            if (row == null) return;
            int from = _steps.IndexOf(row), to = from + delta;
            if (to < 0 || to >= _steps.Count) return;
            _steps.Move(from, to);
            Renumber();
        }

        private void StepDelete_Click(object sender, RoutedEventArgs e)
        {
            if (RowOf<StepRow>(sender) is not { CanDelete: true } row) return;
            var name = row.Name.Trim().Length > 0 ? $"「{row.Name.Trim()}」" : "這一站";
            if (MessageBox.Show(this, $"要刪除{name}嗎？", "刪除站別", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                != MessageBoxResult.Yes) return;
            _steps.Remove(row);
            Renumber();
        }

        private void AddStep_Click(object sender, RoutedEventArgs e)
        {
            var row = new StepRow(new RunStep(), new Job());
            // 一般插在「出料」前面（出料、交貨通常是最後兩站），沒有出料就放最後
            int at = _steps.ToList().FindIndex(r => r.Link == StepLink.Dispatch);
            if (at < 0) _steps.Add(row); else _steps.Insert(at, row);
            Renumber();
            FocusRow(StepList, row, "NameBox");
            StatusText.Text = "已新增站別，可以用 ↑ ↓ 調整位置";
        }

        private void Renumber()
        {
            for (int i = 0; i < _steps.Count; i++) _steps[i].Number = i + 1;
            for (int i = 0; i < _checks.Count; i++) _checks[i].Number = i + 1;
        }

        private void CheckDelete_Click(object sender, RoutedEventArgs e)
        {
            if (RowOf<CheckRow>(sender) is not { } row) return;
            _checks.Remove(row);
            Renumber();
        }

        private void AddCheck_Click(object sender, RoutedEventArgs e)
        {
            var row = new CheckRow(new CheckItem());
            _checks.Add(row);
            Renumber();
            FocusRow(CheckList, row, null);
        }

        private void PassAll_Click(object sender, RoutedEventArgs e)
        {
            var inspector = DefaultInspectorBox.Text.Trim();
            int n = 0;
            foreach (var row in _checks.Where(c => c.Result == CheckResult.Pending))
            {
                row.ResultIndex = (int)CheckResult.Pass;
                if (row.DateText.Trim().Length == 0) row.DateText = Today;
                if (row.Inspector.Trim().Length == 0 && inspector.Length > 0) row.Inspector = inspector;
                n++;
            }
            StatusText.Text = n == 0 ? "沒有未檢的項目" : $"{n} 個項目設為合格";
        }

        private void AddIssue_Click(object sender, RoutedEventArgs e)
        {
            var row = new IssueRow(new IssueRecord { Date = DateTime.Today });
            _issues.Add(row);
            FocusRow(IssueList, row, null);
        }

        private void IssueClose_Click(object sender, RoutedEventArgs e)
        {
            if (RowOf<IssueRow>(sender) is not { } row) return;
            if (row.ClosedText.Trim().Length == 0) row.ClosedText = Today;
        }

        private void IssueDelete_Click(object sender, RoutedEventArgs e)
        {
            if (RowOf<IssueRow>(sender) is not { } row) return;
            if (!row.IsEmpty && MessageBox.Show(this, "要刪除這筆紀錄嗎？", "刪除紀錄", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No) != MessageBoxResult.Yes) return;
            _issues.Remove(row);
        }

        /// <summary>新加的一列：捲到看得見，游標放到第一個輸入框（或指定名稱的那個）。</summary>
        private static void FocusRow(ItemsControl list, object row, string? name)
        {
            list.Dispatcher.BeginInvoke(() =>
            {
                if (list.ItemContainerGenerator.ContainerFromItem(row) is not ContentPresenter presenter) return;
                presenter.BringIntoView();
                var box = name != null
                    ? presenter.ContentTemplate.FindName(name, presenter) as Control
                    : FindFirst<TextBox>(presenter);
                box?.Focus();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private static T? FindFirst<T>(DependencyObject node) where T : DependencyObject
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
                if (child is T found) return found;
                if (FindFirst<T>(child) is { } deeper) return deeper;
            }
            return null;
        }

        /// <summary>下拉選單的內容：Tag="People" 是人員清單，Tag="IssueType" 是異常類別。</summary>
        private void Suggest_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not SuggestBox box) return;
            box.Items = box.Tag as string == "IssueType" ? IssueTypes : _people;
        }

        // ---------- 儲存 / 取消 / 列印 ----------

        /// <summary>
        /// 依畫面上的內容產生新的工令（其他欄位照原本）。validate = true 時檢查日期與必填，有問題就提示並回傳 null；
        /// false（列印用）時看不懂的日期當成空白。
        /// </summary>
        private Job? Build(bool validate)
        {
            var job = RunCardRules.Clone(_original);
            job.Steps = new();
            for (int i = 0; i < _steps.Count; i++)
            {
                var row = _steps[i];
                var label = $"第 {i + 1} 站「{row.Name.Trim()}」";
                if (validate)
                {
                    if (row.Name.Trim().Length == 0) return Invalid(TabSteps, $"第 {i + 1} 站還沒填站別名稱。");
                    foreach (var (text, what) in new[] { (row.PlanText, "預計完成"), (row.StartedText, "實際開始"), (row.DoneText, "實際完成") })
                        if (!TextFormat.TryParseOptionalDate(text, out _))
                            return Invalid(TabSteps, $"{label}的{what}日期看不懂，請用 2026/10/06 或 10/6 這種寫法；還沒有就留空白。");
                }
                var step = row.ToStep();
                RunCardRules.SetPlan(job, step, Parse(row.PlanText));
                RunCardRules.SetDone(job, step, Parse(row.DoneText));
                job.Steps.Add(step);
            }

            job.Checks = new();
            for (int i = 0; i < _checks.Count; i++)
            {
                var row = _checks[i];
                if (row.Name.Trim().Length == 0 && row.Measured.Trim().Length == 0) continue; // 空白列不存
                if (validate)
                {
                    if (row.Name.Trim().Length == 0) return Invalid(TabChecks, $"第 {i + 1} 個檢驗項目還沒填名稱。");
                    if (!TextFormat.TryParseOptionalDate(row.DateText, out _))
                        return Invalid(TabChecks, $"檢驗項目「{row.Name.Trim()}」的日期看不懂。");
                }
                job.Checks.Add(row.ToItem());
            }

            job.Issues = new();
            foreach (var row in _issues)
            {
                if (row.IsEmpty) continue;
                if (validate && (!TextFormat.TryParseOptionalDate(row.DateText, out _) || !TextFormat.TryParseOptionalDate(row.ClosedText, out _)))
                    return Invalid(TabIssues, $"異常紀錄「{row.Description.Trim()}」的日期看不懂。");
                job.Issues.Add(row.ToRecord());
            }

            var reason = HoldReasonBox.Text.Trim();
            if (HoldBox.IsChecked == true)
            {
                if (validate && reason.Length == 0)
                {
                    MessageBox.Show(this, "請填暫停原因，例如：等客戶確認圖面、缺料。", "流程卡", MessageBoxButton.OK, MessageBoxImage.Information);
                    HoldReasonBox.Focus();
                    return null;
                }
                job.HoldReason = reason.Length > 0 ? reason : "暫停";
            }
            else job.HoldReason = "";
            return job;
        }

        private Job? Invalid(RadioButton tab, string message)
        {
            tab.IsChecked = true;
            MessageBox.Show(this, message, "流程卡", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        private bool TrySave()
        {
            var job = Build(validate: true);
            if (job == null) return false;
            if (RunCardRules.StepOrderWarning(job) is { } warning &&
                MessageBox.Show(this, $"{warning}\n\n確定要這樣存嗎？", "流程卡", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
                return false;
            Result = job;
            _dirty = false;
            return true;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!TrySave()) return;
            _closingAfterSave = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        private void OnClosing(object? sender, CancelEventArgs e)
        {
            if (_closingAfterSave || !_dirty) return;
            var answer = MessageBox.Show(this, "流程卡有修改還沒儲存，要儲存嗎？", "流程卡",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (answer == MessageBoxResult.Cancel || (answer == MessageBoxResult.Yes && !TrySave()))
                e.Cancel = true;
        }

        private void Print_Click(object sender, RoutedEventArgs e)
        {
            var job = Build(validate: false);
            if (job == null) return;
            RunCardPrinter.Print(job, this, s => StatusText.Text = s);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
            {
                Save_Click(this, e);
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.P)
            {
                Print_Click(this, e);
                e.Handled = true;
            }
            base.OnPreviewKeyDown(e);
        }
    }
}
