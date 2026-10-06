using System.Windows;
using System.Windows.Controls;
using schedule.Models;
using schedule.Services;

namespace schedule.Views
{
    /// <summary>看板上的橫向長條（人員、客戶）。</summary>
    public class BarItem
    {
        public BarItem(string name, int count, int max, string toolTip)
        {
            Name = name;
            Count = count;
            Fill = new GridLength(count, GridUnitType.Star);
            Rest = new GridLength(Math.Max(max - count, 0), GridUnitType.Star);
            ToolTip = toolTip;
        }

        public string Name { get; }
        public int Count { get; }
        public GridLength Fill { get; }
        public GridLength Rest { get; }
        public string ToolTip { get; }
    }

    /// <summary>看板上「未來 8 週交貨」的一根柱子。</summary>
    public class WeekBar
    {
        public WeekBar(WeekBucket week, int max, DateTime today)
        {
            Count = week.Count;
            // 全部都是 0 時，柱子高度也是 0（只剩數字）
            Fill = new GridLength(week.Count, GridUnitType.Star);
            Rest = new GridLength(Math.Max(max - week.Count, 0) + (max == 0 ? 1 : 0), GridUnitType.Star);
            IsCurrent = week.Start == GanttMath.Monday(today);
            Label = $"{week.Start.Month}/{week.Start.Day}";
            int index = (int)(week.Start - GanttMath.Monday(today)).TotalDays / 7;
            SubLabel = index switch { 0 => "本週", 1 => "下週", _ => $"{index} 週後" };
            var end = week.Start.AddDays(6);
            ToolTip = $"{TextFormat.Date(week.Start)} ～ {TextFormat.Date(end)}：{week.Count} 筆";
        }

        public int Count { get; }
        public GridLength Fill { get; }
        public GridLength Rest { get; }
        public bool IsCurrent { get; }
        public string Label { get; }
        public string SubLabel { get; }
        public string ToolTip { get; }
    }

    /// <summary>
    /// 資訊看板：一眼看出目前的狀況 —— 各種數量、逾期與即將到期的項目、每週交貨量、人員與客戶分布。
    /// 看板一律看全部工令（不受左邊篩選、搜尋影響）。
    /// </summary>
    public partial class DashboardView : UserControl
    {
        /// <summary>同時列出的人員 / 客戶數上限，其餘合併成「其他」。</summary>
        private const int MaxBars = 8;

        /// <summary>使用者點了某個時間點。</summary>
        public event Action<Job>? JobClicked;

        /// <summary>使用者點了某位人員或客戶：切到清單、用這個名稱搜尋。</summary>
        public event Action<string>? SearchRequested;

        private IReadOnlyCollection<Job> _jobs = Array.Empty<Job>();
        private Job? _selected;
        private DateTime _today = DateTime.Today;

        public DashboardView()
        {
            InitializeComponent();
        }

        public void Show(IReadOnlyCollection<Job> jobs, Job? selected, DateTime today)
        {
            _jobs = jobs;
            _selected = selected;
            _today = today.Date;
            Build();
        }

        public void Select(Job? job)
        {
            _selected = job;
            Build();
        }

        private void Build()
        {
            var s = DashboardStats.Compute(_jobs, _today);

            ActiveText.Text = s.Active.ToString("#,0");
            ActiveSub.Text = $"數量合計 {s.ActiveQuantity:#,0} 台";
            OverdueText.Text = s.Overdue.ToString("#,0");
            DueSoonText.Text = s.DueSoon.ToString("#,0");
            DueSoonSub.Text = $"到 {TextFormat.ShortDate(_today.AddDays(ScheduleRules.DueSoonDays), _today)} 為止";
            MonthLabel.Text = $"{_today.Month} 月已交貨";
            MonthText.Text = s.DeliveredThisMonth.ToString("#,0");
            MonthSub.Text = "依實際交貨日期";
            OnTimeText.Text = s.OnTimeRate is { } rate ? $"{rate:0%}" : "—";
            OnTimeSub.Text = s.DeliveredWithPlan == 0 ? "還沒有交貨紀錄" : $"{s.DeliveredWithPlan} 筆中 {s.DeliveredOnTime} 筆準時";
            OnTimeText.SetResourceReference(ForegroundProperty, s.OnTimeRate switch
            {
                null => "SubtleBrush",
                >= 0.9 => "SuccessBrush",
                >= 0.7 => "WarningBrush",
                _ => "DangerBrush",
            });
            TotalText.Text = s.Total.ToString("#,0");
            TotalSub.Text = $"已完成 {s.Total - s.Active} 筆";

            Rows(OverdueList, OverdueEmpty, s.OverdueItems);
            OverdueHint.Text = s.OverdueItems.Count > 0 ? $"{s.OverdueItems.Count} 項" : "";
            Rows(UpcomingList, UpcomingEmpty, s.Upcoming);
            UpcomingHint.Text = s.Upcoming.Count > 0 ? $"{s.Upcoming.Count} 項" : "";
            Rows(RecentList, RecentEmpty, s.RecentDeliveries);

            int maxWeek = s.DeliveryWeeks.Count == 0 ? 0 : s.DeliveryWeeks.Max(w => w.Count);
            WeekChart.ItemsSource = s.DeliveryWeeks.Select(w => new WeekBar(w, maxWeek, _today)).ToList();
            WeekNote.Text = s.DeliveryOverdue > 0 ? $"另有 {s.DeliveryOverdue} 筆已過預計交期、還沒交貨。" : "";
            WeekNote.Visibility = s.DeliveryOverdue > 0 ? Visibility.Visible : Visibility.Collapsed;

            Bars(WorkloadList, WorkloadEmpty, s.Workload, "負責");
            Bars(CustomerList, CustomerEmpty, s.Customers, "客戶");
        }

        private void Rows(ItemsControl list, TextBlock empty, List<TimelineEvent> events)
        {
            list.ItemsSource = events.Select(e => new EventRow(e, _today, ReferenceEquals(e.Job, _selected), showDate: true)).ToList();
            empty.Visibility = events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void Bars(ItemsControl list, TextBlock empty, List<CountItem> items, string what)
        {
            var shown = items.Take(MaxBars).ToList();
            int others = items.Skip(MaxBars).Sum(c => c.Count);
            int max = Math.Max(items.Count == 0 ? 0 : items.Max(c => c.Count), others);
            var bars = shown
                .Select(c => new BarItem(c.Name, c.Count, max, $"{c.Name}：{c.Count} 筆未交貨的工令（點一下查看）"))
                .ToList();
            if (others > 0)
                bars.Add(new BarItem("其他", others, max, $"其他 {items.Count - MaxBars} 位{what}，合計 {others} 筆"));
            list.ItemsSource = bars;
            empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // 卡片、長條都是按鈕：依按鈕的資料決定要做什麼
        private void Card_Click(object sender, RoutedEventArgs e)
        {
            switch ((e.OriginalSource as FrameworkElement)?.DataContext)
            {
                case EventRow row:
                    _selected = row.Job;
                    Build();
                    JobClicked?.Invoke(row.Job);
                    break;
                case BarItem bar when bar.Name != "其他":
                    SearchRequested?.Invoke(bar.Name);
                    break;
            }
        }
    }
}
