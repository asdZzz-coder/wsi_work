using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using schedule.Models;
using schedule.Services;

namespace schedule.Views
{
    /// <summary>時間軸上的一天：日期標題，加上那天的各個時間點。</summary>
    public class TimelineDay
    {
        public TimelineDay(DateTime date, DateTime today, List<EventRow> events)
        {
            Date = date;
            Events = events;
            var md = date.Year == today.Year ? $"{date.Month}/{date.Day}" : $"{date.Year}/{date.Month}/{date.Day}";
            Title = $"{md}（{Timeline.Weekday(date)}）";
            Relative = Timeline.Relative(date, today);
            IsToday = date == today.Date;
            IsPast = date < today.Date;
        }

        public DateTime Date { get; }
        public string Title { get; }
        public string Relative { get; }
        public bool IsToday { get; }
        public bool IsPast { get; }
        public List<EventRow> Events { get; }
        public bool HasEvents => Events.Count > 0;
    }

    /// <summary>
    /// 時間軸：所有工令的材料入場、配電、交貨依日期排成一條線。
    /// 已完成的放在實際日期，還沒完成的放在預計日期；今天一定會出現，方便看前後的事。
    /// </summary>
    public partial class TimelineView : UserControl
    {
        /// <summary>使用者點了某個時間點。</summary>
        public event Action<Job>? JobClicked;

        private IReadOnlyList<Job> _jobs = Array.Empty<Job>();
        private Job? _selected;
        private DateTime _today = DateTime.Today;
        private bool _scrollToToday = true; // 第一次顯示、換範圍時捲到今天

        public TimelineView()
        {
            InitializeComponent();
        }

        /// <summary>沒有工令時顯示的文字。</summary>
        public string EmptyMessage { get; set; } = "沒有符合條件的工令";

        public void Show(IReadOnlyList<Job> jobs, Job? selected, DateTime today)
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
            if (Days == null) return; // InitializeComponent 期間

            bool recent = RangeRecent.IsChecked == true;
            bool hideDone = HideDoneBox.IsChecked == true;

            IEnumerable<TimelineEvent> events = Timeline.Events(_jobs, _today);
            if (recent) events = Timeline.Recent(events, _today);
            if (hideDone) events = events.Where(e => !e.IsDone);
            var list = events.ToList();

            var days = list
                .GroupBy(e => e.Date)
                .Select(g => new TimelineDay(g.Key, _today, g.Select(e => new EventRow(e, _today, ReferenceEquals(e.Job, _selected))).ToList()))
                .ToList();
            // 今天沒有事也放一個「今天」，看得出前後的位置
            if (!days.Any(d => d.IsToday))
            {
                int at = days.FindIndex(d => d.Date > _today);
                days.Insert(at < 0 ? days.Count : at, new TimelineDay(_today, _today, new List<EventRow>()));
            }

            Days.ItemsSource = days;

            bool none = _jobs.Count == 0;
            EmptyText.Text = EmptyMessage;
            EmptyText.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
            Scroll.Visibility = none ? Visibility.Collapsed : Visibility.Visible;

            int overdue = list.Count(e => e.State == MilestoneState.Overdue);
            var range = recent ? "前 2 週到未來 2 個月" : "全部日期";
            SummaryText.Text = $"{range}，共 {list.Count} 項" + (overdue > 0 ? $"，{overdue} 項逾期未完成" : "");

            if (_scrollToToday)
            {
                _scrollToToday = false;
                Dispatcher.BeginInvoke(ScrollToToday, DispatcherPriority.Loaded);
            }
        }

        private void ScrollToToday()
        {
            if (Days.ItemsSource is not List<TimelineDay> days) return;
            var today = days.FirstOrDefault(d => d.IsToday);
            if (today == null || Days.ItemContainerGenerator.ContainerFromItem(today) is not FrameworkElement container) return;
            if (Scroll.Content is not UIElement content || !container.IsDescendantOf(content)) return;
            var y = container.TranslatePoint(new Point(0, 0), content).Y;
            Scroll.ScrollToVerticalOffset(Math.Max(0, y - 8));
        }

        private void Today_Click(object sender, RoutedEventArgs e) => ScrollToToday();

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (Days == null) return; // InitializeComponent 期間
            _scrollToToday = true;
            Build();
        }

        // 點卡片（每張卡片是一個按鈕）：找出點到的是哪個時間點
        private void Card_Click(object sender, RoutedEventArgs e)
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is not EventRow row) return;
            _selected = row.Job;
            Build();
            JobClicked?.Invoke(row.Job);
        }
    }
}
