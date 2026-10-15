using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
        private readonly ObservableCollection<DispatchRow> _dispatches;
        private readonly List<SyncRow> _syncRows;
        private readonly ICollectionView _syncView;
        private readonly Dictionary<string, PartInfo> _catalog;
        private readonly Dictionary<DispatchRow, PartInfo> _autoFilled = new();
        private readonly IReadOnlyList<string> _partNos, _partNames, _units;
        private bool _dirty;
        private bool _loading = true;
        private bool _closingAfterSave;

        private static readonly string[] DefaultUnits = { "個", "只", "支", "組", "套", "台", "條", "片", "包", "盒", "米", "捲", "式" };

        /// <summary>按「儲存」後的工令（Id 與原本相同）；沒儲存就關掉為 null。</summary>
        public Job? Result { get; private set; }

        /// <summary>按「儲存」時勾選要同步出料的其他工令 Id。</summary>
        public List<string> SyncIds { get; private set; } = new();

        public RunCardWindow(Job job, IReadOnlyList<string> people) : this(job, people, new[] { job }) { }

        /// <param name="allJobs">所有工令：出料同步的候選機台，以及料號、名稱的下拉選單。</param>
        public RunCardWindow(Job job, IReadOnlyList<string> people, IReadOnlyList<Job> allJobs)
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

            // 出料紀錄與同步
            _dispatches = new ObservableCollection<DispatchRow>(work.Dispatches.Select(d => new DispatchRow(d)));
            Watch(_dispatches);
            DispatchList.ItemsSource = _dispatches;
            _catalog = DispatchSync.Catalog(allJobs);
            _partNos = DispatchSync.Values(allJobs, d => d.PartNo);
            _partNames = DispatchSync.Values(allJobs, d => d.Name);
            _units = DispatchSync.Values(allJobs, d => d.Unit).Concat(DefaultUnits).Distinct().ToList();
            var partners = DispatchSync.Partners(allJobs, job).Select(p => p.Id).ToHashSet();
            _syncRows = SyncCandidates(job, allJobs, partners);
            foreach (var row in _syncRows) row.PropertyChanged += Row_Changed;
            _syncView = CollectionViewSource.GetDefaultView(_syncRows);
            _syncView.Filter = o => SyncMatches((SyncRow)o, SyncSearchBox.Text);
            SyncList.ItemsSource = _syncView;
            SyncEmpty.Visibility = _syncRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

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

            int items = _dispatches.Count(d => !d.IsEmpty);
            int synced = _syncRows.Count(r => r.IsChecked);
            TabDispatchText.Text = items > 0 ? $"出料紀錄（{items} 項）" : "出料紀錄";
            DispatchEmpty.Visibility = _dispatches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SyncCount.Text = synced > 0 ? $"已勾 {synced} 台" : "";
        }

        // ---------- 出料同步的候選機台 ----------

        /// <summary>其他工令：已勾的排最前面，再來是同機種、還沒交貨的。</summary>
        private static List<SyncRow> SyncCandidates(Job job, IReadOnlyList<Job> allJobs, HashSet<string> partners)
        {
            var model = job.Model.Trim();
            return allJobs
                .Where(j => j.Id != job.Id)
                .Select(j =>
                {
                    bool same = model.Length > 0 && string.Equals(j.Model.Trim(), model, StringComparison.OrdinalIgnoreCase);
                    return new SyncRow(j, SyncDetail(j, job, allJobs), partners.Contains(j.Id), same ? 0 : 1);
                })
                .OrderBy(r => !r.IsChecked)
                .ThenBy(r => r.SameModel)
                .ThenBy(r => r.Job.DeliveryActual != null)
                .ThenBy(r => r.WorkOrder, NaturalComparer.Instance)
                .ToList();
        }

        /// <summary>機種 · 客戶，加上已交貨、已有幾項出料、目前和哪幾台同步。</summary>
        private static string SyncDetail(Job other, Job job, IReadOnlyList<Job> allJobs)
        {
            var parts = new List<string>();
            var head = string.Join(" · ", new[] { other.Model, other.Customer }.Where(s => s.Trim().Length > 0).Select(s => s.Trim()));
            if (head.Length > 0) parts.Add(head);
            if (other.DeliveryActual != null) parts.Add("已交貨");
            if ((other.Dispatches?.Count ?? 0) > 0) parts.Add($"出料 {other.Dispatches!.Count} 項");
            var otherPartners = DispatchSync.Partners(allJobs, other).Where(p => p.Id != job.Id).Select(p => p.WorkOrder.Trim()).ToList();
            if (otherPartners.Count > 0 && other.DispatchGroup != job.DispatchGroup)
                parts.Add($"目前和 {string.Join("、", otherPartners.Take(3))}{(otherPartners.Count > 3 ? " 等" : "")} 同步");
            return string.Join("　", parts);
        }

        private static bool SyncMatches(SyncRow row, string text)
        {
            text = text.Trim();
            if (text.Length == 0 || row.IsChecked) return true;
            return new[] { row.Job.WorkOrder, row.Job.Model, row.Job.Customer, row.Job.ProjectName }
                .Any(s => s.Contains(text, StringComparison.OrdinalIgnoreCase));
        }

        private void SyncSearch_Changed(object sender, TextChangedEventArgs e) => _syncView?.Refresh();

        private void SyncSameModel_Click(object sender, RoutedEventArgs e)
        {
            int n = 0;
            foreach (var row in _syncRows.Where(r => r.SameModel == 0 && r.Job.DeliveryActual == null && !r.IsChecked))
            {
                row.IsChecked = true;
                n++;
            }
            StatusText.Text = n > 0 ? $"已勾選 {n} 台同機種、還沒交貨的工令"
                : _syncRows.Any(r => r.SameModel == 0) ? "同機種的工令都已經勾了（或已交貨）" : "沒有和這台同機種的工令";
        }

        private void SyncNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _syncRows) row.IsChecked = false;
            StatusText.Text = "這台不和其他機台同步出料（儲存後生效）";
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
            if (sender is DispatchRow row && e.PropertyName == nameof(DispatchRow.PartNo)) AutoFill(row);
            MarkDirty();
        }

        /// <summary>
        /// 打之前出過的料號時帶入名稱、規格、單位（只填空白的格子，或上次自動帶入、還沒改過的格子）。
        /// </summary>
        private void AutoFill(DispatchRow row)
        {
            _catalog.TryGetValue(row.PartNo.Trim(), out var info);
            _autoFilled.TryGetValue(row, out var previous);
            if (info == null && previous == null) return;

            static bool Replaceable(string current, string? auto) => current.Trim().Length == 0 || (auto != null && current == auto);
            if (Replaceable(row.Name, previous?.Name)) row.Name = info?.Name ?? "";
            if (Replaceable(row.Spec, previous?.Spec)) row.Spec = info?.Spec ?? "";
            if (Replaceable(row.Unit, previous?.Unit)) row.Unit = info?.Unit ?? "";
            if (info != null) _autoFilled[row] = info; else _autoFilled.Remove(row);
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
            DispatchPanel.Visibility = TabDispatch.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            UpdateTabHint();
        }

        private void UpdateTabHint()
        {
            TabHint.Text = TabChecks.IsChecked == true ? "量測值可以寫數字和單位，例如 50 MΩ。結果選「不合格」時，這張工令會列在「暫停 / 異常」。"
                : TabIssues.IsChecked == true ? "還沒結案的紀錄會讓工令列在「暫停 / 異常」，處理好後按 ✓ 結案。"
                : TabDispatch.IsChecked == true ? "打之前出過的料號，名稱、規格、單位會自動帶入。右邊勾選的機台會和這台共用同一份出料紀錄。"
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
            for (int i = 0; i < _dispatches.Count; i++) _dispatches[i].Number = i + 1;
        }

        private void AddDispatch_Click(object sender, RoutedEventArgs e)
        {
            var row = new DispatchRow(new DispatchItem());
            // 新的料件沿用上一筆的出料日期（通常同一天出好幾項）
            if (_dispatches.LastOrDefault() is { } last) row.DateText = last.DateText;
            _dispatches.Add(row);
            Renumber();
            FocusRow(DispatchList, row, "PartNoBox");
        }

        private void DispatchDelete_Click(object sender, RoutedEventArgs e)
        {
            if (RowOf<DispatchRow>(sender) is not { } row) return;
            var name = new[] { row.PartNo, row.Name }.FirstOrDefault(s => s.Trim().Length > 0)?.Trim();
            if (!row.IsEmpty && MessageBox.Show(this, $"要刪除「{name}」嗎？", "刪除料件", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No) != MessageBoxResult.Yes) return;
            _dispatches.Remove(row);
            _autoFilled.Remove(row);
            Renumber();
        }

        private void DispatchToday_Click(object sender, RoutedEventArgs e)
        {
            int n = 0;
            foreach (var row in _dispatches.Where(d => !d.IsEmpty && d.DateText.Trim().Length == 0))
            {
                row.DateText = Today;
                n++;
            }
            StatusText.Text = n == 0 ? "沒有出料日期空白的料件" : $"{n} 個料件的出料日期填了今天";
        }

        private void PrintDispatch_Click(object sender, RoutedEventArgs e)
        {
            var job = Build(validate: false);
            if (job == null) return;
            if (job.Dispatches.Count == 0)
            {
                MessageBox.Show(this, "還沒有出料紀錄可以列印。", "列印出料單", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var partners = _syncRows.Where(r => r.IsChecked).Select(r => r.WorkOrder.Trim()).ToList();
            RunCardPrinter.PrintDispatch(job, partners, this, s => StatusText.Text = s);
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
            box.Items = (box.Tag as string) switch
            {
                "IssueType" => IssueTypes,
                "PartNo" => _partNos,
                "PartName" => _partNames,
                "Unit" => _units,
                _ => _people,
            };
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

            job.Dispatches = new();
            for (int i = 0; i < _dispatches.Count; i++)
            {
                var row = _dispatches[i];
                if (row.IsEmpty) continue;
                if (validate)
                {
                    if (row.PartNo.Trim().Length == 0 && row.Name.Trim().Length == 0)
                        return Invalid(TabDispatch, $"第 {i + 1} 個料件還沒填料號或名稱。");
                    if (!TextFormat.TryParseOptionalDate(row.DateText, out _))
                        return Invalid(TabDispatch, $"第 {i + 1} 個料件的出料日期看不懂，請用 2026/10/06 或 10/6 這種寫法。");
                }
                job.Dispatches.Add(row.ToItem());
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

            // 新勾的機台原本有不一樣的出料紀錄：會被這台的取代，先確認
            var ids = _syncRows.Where(r => r.IsChecked).Select(r => r.Id).ToList();
            var overwrite = DispatchSync.WouldOverwrite(_syncRows.Select(r => r.Job), _original, ids, job.Dispatches);
            if (overwrite.Count > 0)
            {
                var names = string.Join("\n", overwrite.Take(8).Select(j => $"・{j.WorkOrder}（原本 {j.Dispatches.Count} 項）"));
                if (overwrite.Count > 8) names += $"\n…等 {overwrite.Count} 台";
                if (MessageBox.Show(this, $"下面這些機台原本有自己的出料紀錄，同步後會改成和「{job.WorkOrder}」一樣（{job.Dispatches.Count} 項）：\n\n{names}\n\n確定要同步嗎？",
                        "出料同步", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                {
                    TabDispatch.IsChecked = true;
                    return false;
                }
            }

            Result = job;
            SyncIds = ids;
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
