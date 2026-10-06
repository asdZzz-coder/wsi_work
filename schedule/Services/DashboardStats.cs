using schedule.Models;

namespace schedule.Services
{
    /// <summary>長條圖的一項，例如某位人員負責幾筆工令。</summary>
    public record CountItem(string Name, int Count);

    /// <summary>某一週（從週一開始）預計要交貨、還沒交的工令數。</summary>
    public record WeekBucket(DateTime Start, int Count);

    /// <summary>資訊看板上的所有數字（不依賴畫面，方便自動測試）。</summary>
    public class DashboardStats
    {
        /// <summary>「接下來」清單看幾天（含今天）。</summary>
        public const int UpcomingDays = 7;

        /// <summary>交貨週數長條圖畫幾週（含本週）。</summary>
        public const int Weeks = 8;

        public int Total { get; private init; }
        public int Active { get; private init; }          // 進行中（含逾期）＝還沒交貨
        public int Overdue { get; private init; }
        public int DueSoon { get; private init; }          // 7 天內要交貨
        public int DeliveredThisMonth { get; private init; }
        public int DeliveredOnTime { get; private init; }  // 已交貨且有預計交期的工令中，準時的筆數
        public int DeliveredWithPlan { get; private init; }
        public int ActiveQuantity { get; private init; }   // 還沒交貨的工令數量合計

        /// <summary>準時交貨率（0–1）；還沒有可以比較的交貨紀錄時為 null。</summary>
        public double? OnTimeRate => DeliveredWithPlan == 0 ? null : (double)DeliveredOnTime / DeliveredWithPlan;

        public List<TimelineEvent> OverdueItems { get; private init; } = new();
        public List<TimelineEvent> Upcoming { get; private init; } = new();
        public List<TimelineEvent> RecentDeliveries { get; private init; } = new();
        public List<WeekBucket> DeliveryWeeks { get; private init; } = new();
        public int DeliveryOverdue { get; private init; }  // 預計交期已過、還沒交貨
        public List<CountItem> Workload { get; private init; } = new();
        public List<CountItem> Customers { get; private init; } = new();

        public static DashboardStats Compute(IReadOnlyCollection<Job> jobs, DateTime today)
        {
            today = today.Date;
            var active = jobs.Where(j => j.DeliveryActual == null).ToList();
            var delivered = jobs.Where(j => j.DeliveryActual != null).ToList();
            var withPlan = delivered.Where(j => j.DeliveryPlan != null).ToList();
            var events = Timeline.Events(jobs, today);
            var thisWeek = GanttMath.Monday(today);

            return new DashboardStats
            {
                Total = jobs.Count,
                Active = active.Count,
                Overdue = jobs.Count(j => ScheduleRules.Status(j, today) == JobStatus.Overdue),
                DueSoon = jobs.Count(j => ScheduleRules.IsDueSoon(j, today)),
                DeliveredThisMonth = delivered.Count(j => j.DeliveryActual!.Value.Year == today.Year && j.DeliveryActual.Value.Month == today.Month),
                DeliveredWithPlan = withPlan.Count,
                DeliveredOnTime = withPlan.Count(j => j.DeliveryActual!.Value.Date <= j.DeliveryPlan!.Value.Date),
                ActiveQuantity = active.Sum(j => j.Quantity),

                // 逾期最久的排最前面
                OverdueItems = events.Where(e => e.State == MilestoneState.Overdue).OrderBy(e => e.Date).ToList(),
                Upcoming = events
                    .Where(e => !e.IsDone && e.Date >= today && e.Date < today.AddDays(UpcomingDays))
                    .ToList(),
                RecentDeliveries = events
                    .Where(e => e.Kind == MilestoneKind.Delivery && e.IsDone)
                    .OrderByDescending(e => e.Date)
                    .Take(6)
                    .ToList(),
                DeliveryWeeks = Enumerable.Range(0, Weeks)
                    .Select(w => thisWeek.AddDays(7 * w))
                    .Select(start => new WeekBucket(start, active.Count(j =>
                        j.DeliveryPlan is { } p && p.Date >= start && p.Date < start.AddDays(7) && p.Date >= today)))
                    .ToList(),
                DeliveryOverdue = active.Count(j => j.DeliveryPlan is { } p && p.Date < today),
                Workload = Count(active.SelectMany(j => People(j.InnerWiring).Concat(People(j.OuterWiring)).Distinct(StringComparer.CurrentCultureIgnoreCase))),
                Customers = Count(active.Select(j => j.Customer.Trim()).Where(c => c.Length > 0)),
            };
        }

        /// <summary>同一格寫了好幾個人（例如「小明、阿華」）時分開算。</summary>
        public static IEnumerable<string> People(string text) =>
            text.Split(new[] { '、', ',', '，', '/', '／', ';', '；', '&', '＆', '+', '＋' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        /// <summary>多到少排序；同數量依名稱排。</summary>
        private static List<CountItem> Count(IEnumerable<string> names) =>
            names.GroupBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => new CountItem(g.First(), g.Count()))
                .OrderByDescending(c => c.Count)
                .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
    }
}
