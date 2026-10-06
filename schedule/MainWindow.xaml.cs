using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using schedule.Models;
using schedule.Services;
using static schedule.Services.TextFormat;

namespace schedule
{
    public partial class MainWindow : Window
    {
        private const string AppTitle = "工作排程表";

        private readonly ScheduleData _data;
        private readonly UpdateService _updater = new();

        // 目前在表單裡編輯的工令；null 表示表單是「新增」
        private Job? _selected;
        private JobFilter _filter = JobFilter.All;

        // 重建清單時會觸發 SelectionChanged，這段期間不要把表單清掉
        private bool _refreshing;

        public MainWindow()
        {
            InitializeComponent();
            // 小螢幕（例如筆電）上不要讓視窗超出可用範圍
            Height = Math.Min(Height, SystemParameters.WorkArea.Height - 20);
            Width = Math.Min(Width, SystemParameters.WorkArea.Width - 20);
            _data = DataStore.Load();
            ClearForm();
            Refresh();
            Title = _updater.IsInstalled ? $"{AppTitle} v{_updater.CurrentVersion}" : $"{AppTitle}（開發版）";
            StatusText.Text = $"版本 {_updater.CurrentVersion}";
            ThemeService.ThemeChanged += OnThemeChanged; // 切換主題（或系統深淺色改變）時更新標題列與按鈕
            UpdateThemeButton();
        }

        // ---------- 主題：跟隨系統 / 淺色 / 深色 ----------

        private void Theme_Click(object sender, RoutedEventArgs e)
        {
            ThemeService.Cycle();
            StatusText.Text = $"主題：{ThemeName(ThemeService.Mode)}";
        }

        private static string ThemeName(AppTheme mode) => mode switch
        {
            AppTheme.Light => "淺色",
            AppTheme.Dark => "深色",
            _ => "跟隨系統",
        };

        private void OnThemeChanged()
        {
            UpdateThemeButton();
            ApplyTitleBar();
        }

        private void UpdateThemeButton()
        {
            ThemeIcon.Text = ThemeService.Mode switch
            {
                AppTheme.Light => "", // 太陽
                AppTheme.Dark => "",  // 月亮
                _ => "",              // 電腦（跟隨系統）
            };
            ThemeButton.ToolTip = $"主題：{ThemeName(ThemeService.Mode)}（按一下切換）";
        }

        // ---------- Windows 11：標題列底色與視窗背景同色，看起來是一整片 ----------

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ApplyTitleBar();
        }

        private void ApplyTitleBar() => WindowTheme.ApplyTitleBar(this);

        // ---------- 啟動時檢查更新（詢問使用者） ----------

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            CleanupService.RunInBackground(); // 清掉更新後遺留的舊檔
            DesktopShortcutService.TidyUp(_updater.IsInstalled);     // 更新後：重複捷徑只留最新的、工作列釘選改指向新版
            DesktopShortcutService.EnsureOnce(_updater.IsInstalled); // 安裝版第一次開啟時補上桌面捷徑

            await CheckForUpdateAsync(manual: false);
        }

        private void Shortcut_Click(object sender, RoutedEventArgs e)
        {
            const string title = "桌面捷徑";
            try
            {
                if (DesktopShortcutService.Create(_updater.IsInstalled) == ShortcutResult.SourceNotFound)
                {
                    MessageBox.Show("找不到安裝版的開始功能表捷徑，請重新執行「安裝.cmd」後再試。", title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                StatusText.Text = "已在桌面建立捷徑";
                MessageBox.Show("已在桌面建立捷徑", title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or COMException)
            {
                MessageBox.Show($"建立桌面捷徑失敗：{ex.Message}", title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            await CheckForUpdateAsync(manual: true);
        }

        private async Task CheckForUpdateAsync(bool manual)
        {
            const string title = "檢查更新";
            if (!_updater.IsInstalled)
            {
                if (manual)
                    MessageBox.Show("目前是開發版（非安裝版），無法線上更新。", title);
                return;
            }

            try
            {
                var info = await _updater.CheckAsync();
                if (info == null)
                {
                    if (manual) MessageBox.Show($"目前已是最新版本（{_updater.CurrentVersion}）。", title);
                    return;
                }

                var answer = MessageBox.Show(
                    $"發現新版本 {info.Version}（目前 {_updater.CurrentVersion}）。\n\n是否現在更新？更新完成後程式會自動重新啟動。",
                    "有新版本", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;

                StatusText.Text = "下載更新中…";
                await _updater.DownloadAndLaunchAsync(info, p => Dispatcher.Invoke(() => StatusText.Text = $"下載更新中… {p}%"));
                // 安裝程式已啟動，結束本程式讓它能覆蓋檔案；安裝完成後會自動重新開啟
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"版本 {_updater.CurrentVersion}";
                // 啟動時的自動檢查失敗（例如沒網路）不打擾使用者
                if (manual) MessageBox.Show($"檢查更新失敗：{ex.Message}", title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ---------- 重新整理畫面 ----------

        private JobSort CurrentSort =>
            SortBox?.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<JobSort>(tag, out var s) ? s : JobSort.DeliveryPlan;

        /// <summary>重算各狀態數量、依篩選 / 搜尋 / 排序重建表格（保留原本的選取），並更新標題下的摘要。</summary>
        private void Refresh()
        {
            var today = DateTime.Today;
            var jobs = _data.Jobs;

            AllCount.Text = jobs.Count.ToString();
            ActiveCount.Text = jobs.Count(j => ScheduleRules.Matches(j, JobFilter.Active, today)).ToString();
            int overdue = jobs.Count(j => ScheduleRules.Matches(j, JobFilter.Overdue, today));
            OverdueCount.Text = overdue.ToString();
            DueSoonCount.Text = jobs.Count(j => ScheduleRules.Matches(j, JobFilter.DueSoon, today)).ToString();
            DoneCount.Text = jobs.Count(j => ScheduleRules.Matches(j, JobFilter.Done, today)).ToString();

            CountText.Text = jobs.Count == 0 ? "還沒有工令"
                : overdue > 0 ? $"共 {jobs.Count} 筆工令，{overdue} 筆逾期"
                : $"共 {jobs.Count} 筆工令";

            var keyword = SearchBox.Text;
            var rows = ScheduleRules.Sort(jobs, CurrentSort)
                .Where(j => ScheduleRules.Matches(j, _filter, today) && ScheduleRules.MatchesSearch(j, keyword))
                .Select(j => new JobRow(j, today))
                .ToList();

            _refreshing = true;
            try
            {
                JobList.ItemsSource = rows;
                JobList.SelectedItem = rows.FirstOrDefault(r => r.Job == _selected);
            }
            finally { _refreshing = false; }
            if (JobList.SelectedItem != null) JobList.ScrollIntoView(JobList.SelectedItem);

            EmptyText.Text = jobs.Count == 0
                ? "還沒有工令\n在右邊填好資料後按「新增」"
                : "沒有符合條件的工令";
        }

        private void Filter_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton { Tag: string tag } || !Enum.TryParse<JobFilter>(tag, out var f)) return;
            _filter = f;
            if (JobList != null) Refresh(); // InitializeComponent 期間 JobList 還沒建立
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

        private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (JobList != null) Refresh(); // InitializeComponent 期間 JobList 還沒建立
        }

        /// <summary>剛新增 / 修改的工令被目前的篩選或搜尋藏起來時，切回「全部」並清掉搜尋，才看得到它。</summary>
        private void EnsureVisible(Job job)
        {
            if (ScheduleRules.Matches(job, _filter, DateTime.Today) && ScheduleRules.MatchesSearch(job, SearchBox.Text)) return;
            _filter = JobFilter.All;
            FilterAll.IsChecked = true;
            SearchBox.Clear();
        }

        // ---------- 選取 / 表單 ----------

        private void JobList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_refreshing || JobList.SelectedItem is not JobRow row) return;
            _selected = row.Job;
            FillForm(row.Job);
        }

        private void NewForm_Click(object sender, RoutedEventArgs e)
        {
            JobList.SelectedItem = null;
            ClearForm();
            WorkOrderBox.Focus();
        }

        private void FillForm(Job j)
        {
            FormTitle.Text = $"編輯工令 {j.WorkOrder}";
            WorkOrderBox.Text = j.WorkOrder;
            ModelBox.Text = j.Model;
            QuantityBox.Text = j.Quantity > 0 ? j.Quantity.ToString() : "";
            CustomerBox.Text = j.Customer;
            CEBox.IsChecked = j.HasCE;
            TSBox.IsChecked = j.HasTS;
            MaterialPlanBox.Text = Date(j.MaterialPlan);
            MaterialActualBox.Text = Date(j.MaterialActual);
            WiringPlanBox.Text = Date(j.WiringPlan);
            WiringActualBox.Text = Date(j.WiringActual);
            DeliveryPlanBox.Text = Date(j.DeliveryPlan);
            DeliveryActualBox.Text = Date(j.DeliveryActual);
            InnerWiringBox.Text = j.InnerWiring;
            OuterWiringBox.Text = j.OuterWiring;
            ConsumablesBox.Text = j.Consumables;
            NoteBox.Text = j.Note;
        }

        private void ClearForm()
        {
            _selected = null;
            FormTitle.Text = "新增工令";
            foreach (var box in new[]
                     {
                         WorkOrderBox, ModelBox, QuantityBox, CustomerBox,
                         MaterialPlanBox, MaterialActualBox, WiringPlanBox, WiringActualBox, DeliveryPlanBox, DeliveryActualBox,
                         InnerWiringBox, OuterWiringBox, ConsumablesBox, NoteBox,
                     })
                box.Clear();
            CEBox.IsChecked = false;
            TSBox.IsChecked = false;
        }

        private void Today_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string name } && FindName(name) is TextBox box)
                box.Text = Date(DateTime.Today);
        }

        /// <summary>讀取並檢查表單；有錯誤時提示、把游標移到該欄位，並回傳 null。</summary>
        private Job? ReadForm(Job? editing)
        {
            const string title = "工令";
            var workOrder = WorkOrderBox.Text.Trim();
            if (workOrder.Length == 0)
                return Invalid(WorkOrderBox, "請輸入工令。", title);
            if (ModelBox.Text.Trim().Length == 0)
                return Invalid(ModelBox, "請輸入機種。", title);
            if (!TryParseInt(QuantityBox.Text, out var qty) || qty <= 0)
                return Invalid(QuantityBox, "請輸入數量（1 以上的整數）。", title);

            var job = new Job
            {
                WorkOrder = workOrder,
                Model = ModelBox.Text.Trim(),
                Quantity = qty,
                Customer = CustomerBox.Text.Trim(),
                HasCE = CEBox.IsChecked == true,
                HasTS = TSBox.IsChecked == true,
                InnerWiring = InnerWiringBox.Text.Trim(),
                OuterWiring = OuterWiringBox.Text.Trim(),
                Consumables = ConsumablesBox.Text.Trim(),
                Note = NoteBox.Text.TrimEnd(),
            };

            var dates = new (TextBox Box, string Name, Action<DateTime?> Set)[]
            {
                (MaterialPlanBox, "預計材料入場日期", v => job.MaterialPlan = v),
                (MaterialActualBox, "實際材料入場日期", v => job.MaterialActual = v),
                (WiringPlanBox, "預計配電日期", v => job.WiringPlan = v),
                (WiringActualBox, "實際配電完成日期", v => job.WiringActual = v),
                (DeliveryPlanBox, "預計交期", v => job.DeliveryPlan = v),
                (DeliveryActualBox, "實際交貨日期", v => job.DeliveryActual = v),
            };
            foreach (var (box, name, set) in dates)
            {
                if (!TryParseOptionalDate(box.Text, out var d))
                    return Invalid(box, $"{name}看不懂，請用 2026/10/06 或 10/6 這種寫法；還沒有就留空白。", title);
                set(d);
            }

            if (ScheduleRules.FindDuplicate(_data.Jobs, workOrder, editing) != null)
            {
                var ok = MessageBox.Show($"工令「{workOrder}」已經有一筆了，確定還要再存一筆同樣的工令嗎？", title,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (ok != MessageBoxResult.Yes) { WorkOrderBox.Focus(); return null; }
            }

            // 預計日期前後顛倒（例如材料比交期晚到）多半是打錯，提醒一下
            if (PlanOrderWarning(job) is { } warning)
            {
                var ok = MessageBox.Show($"{warning}\n\n確定要這樣存嗎？", title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (ok != MessageBoxResult.Yes) return null;
            }
            return job;
        }

        private static string? PlanOrderWarning(Job j)
        {
            if (j.MaterialPlan > j.WiringPlan) return "預計材料入場日期比預計配電日期晚。";
            if (j.WiringPlan > j.DeliveryPlan) return "預計配電日期比預計交期晚。";
            if (j.MaterialPlan > j.DeliveryPlan) return "預計材料入場日期比預計交期晚。";
            return null;
        }

        private static Job? Invalid(Control field, string message, string title)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
            field.Focus();
            if (field is TextBox tb) tb.SelectAll();
            return null;
        }

        // ---------- 新增 / 修改 / 刪除 ----------

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var job = ReadForm(editing: null);
            if (job == null) return;
            _data.Jobs.Add(job);
            _selected = job;
            FillForm(job);
            EnsureVisible(job);
            Persist($"已新增工令「{job.WorkOrder}」");
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_selected is not { } target)
            {
                MessageBox.Show("請先在左邊選一筆要修改的工令，或按「新增」。", "提示");
                return;
            }
            var edited = ReadForm(editing: target);
            if (edited == null) return;

            edited.Id = target.Id;
            _data.Jobs[_data.Jobs.IndexOf(target)] = edited;
            _selected = edited;
            FillForm(edited);
            EnsureVisible(edited);
            Persist($"已儲存工令「{edited.WorkOrder}」");
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_selected is not { } target)
            {
                MessageBox.Show("請先在左邊選一筆要刪除的工令。", "提示");
                return;
            }
            var ok = MessageBox.Show($"確定刪除工令「{target.WorkOrder}」（{target.Model}）？", "刪除",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (ok != MessageBoxResult.Yes) return;

            _data.Jobs.Remove(target);
            ClearForm();
            Persist($"已刪除工令「{target.WorkOrder}」");
        }

        private void Persist(string status)
        {
            try
            {
                DataStore.Save(_data);
                StatusText.Text = status;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"儲存失敗，資料尚未寫入硬碟：{ex.Message}", "儲存", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Refresh();
        }

        // ---------- Excel 匯出 / 匯入 ----------

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            const string title = "匯出 Excel";
            var dlg = new SaveFileDialog
            {
                Filter = "Excel 檔案 (*.xlsx)|*.xlsx",
                FileName = $"工作排程表_{DateTime.Now:yyyyMMdd}.xlsx",
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                ExcelService.Export(_data.Jobs, dlg.FileName, DateTime.Today);
                StatusText.Text = "匯出完成";
                MessageBox.Show($"匯出完成，共 {_data.Jobs.Count} 筆工令。", title);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"匯出失敗：{ex.Message}", title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            const string title = "匯入 Excel";
            var dlg = new OpenFileDialog { Filter = "Excel 檔案 (*.xlsx)|*.xlsx" };
            if (dlg.ShowDialog() != true) return;

            List<Job> imported;
            int skipped;
            try
            {
                (imported, skipped) = ExcelService.Import(dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"匯入失敗：{ex.Message}", title, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (imported.Count == 0)
            {
                var why = skipped > 0 ? $"有 {skipped} 列的數量或日期讀不懂。\n\n" : "";
                MessageBox.Show($"這個檔案裡沒有可匯入的工令。\n\n{why}第一列需為標題列，至少要有「工令」欄（可先用「匯出 Excel」產生一份範本）。", title);
                return;
            }

            var skippedText = skipped > 0 ? $"\n（另有 {skipped} 列沒有工令、或數量 / 日期讀不懂，會略過）" : "";
            var mode = MessageBox.Show(
                $"讀到 {imported.Count} 筆工令。{skippedText}\n\n" +
                "是 = 合併到現有資料（工令相同的以 Excel 為準更新）\n" +
                "否 = 清除現有資料，完全以 Excel 為準\n" +
                "取消 = 不匯入",
                title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (mode == MessageBoxResult.Cancel) return;

            var (added, updated) = ScheduleRules.Merge(_data.Jobs, imported, replace: mode == MessageBoxResult.No);

            ClearForm();
            Persist($"匯入完成，新增 {added} 筆、更新 {updated} 筆");
            MessageBox.Show($"匯入完成，新增 {added} 筆、更新 {updated} 筆。", title);
        }
    }
}
