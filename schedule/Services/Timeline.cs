using schedule.Models;

namespace schedule.Services
{
    /// <summary>工令的四個時間點（依先後順序）。</summary>
    public enum MilestoneKind { Material, Wiring, Dispatch, Delivery }

    /// <summary>
    /// 時間軸上的一件事：某工令的某個時間點。已完成的放在實際日期，還沒完成的放在預計日期。
    /// </summary>
    public record TimelineEvent(Job Job, MilestoneKind Kind, DateTime Date, DateTime? Plan, DateTime? Actual, MilestoneState State)
    {
        public bool IsDone => Actual != null;

        /// <summary>還沒完成、已過預計日期幾天（沒逾期為 0）。</summary>
        public int OverdueDays(DateTime today) =>
            State == MilestoneState.Overdue ? (int)(today.Date - Plan!.Value.Date).TotalDays : 0;

        /// <summary>完成日比預計晚幾天（準時或沒有預計日期為 0）。</summary>
        public int LateDays => State == MilestoneState.Late ? (int)(Actual!.Value.Date - Plan!.Value.Date).TotalDays : 0;
    }

    /// <summary>時間軸、看板共用：把工令拆成一個個時間點。</summary>
    public static class Timeline
    {
        public static string KindName(MilestoneKind kind) => kind switch
        {
            MilestoneKind.Material => "材料入場",
            MilestoneKind.Wiring => "配電",
            MilestoneKind.Dispatch => "出料",
            _ => "交貨",
        };

        public static (DateTime? Plan, DateTime? Actual) Dates(Job j, MilestoneKind kind) => kind switch
        {
            MilestoneKind.Material => (j.MaterialPlan, j.MaterialActual),
            MilestoneKind.Wiring => (j.WiringPlan, j.WiringActual),
            MilestoneKind.Dispatch => (j.DispatchPlan, j.DispatchActual),
            _ => (j.DeliveryPlan, j.DeliveryActual),
        };

        /// <summary>每個有日期的時間點一件事，依日期、時間點先後、工令排序。</summary>
        public static List<TimelineEvent> Events(IEnumerable<Job> jobs, DateTime today)
        {
            var list = new List<TimelineEvent>();
            foreach (var job in jobs)
            {
                foreach (var kind in Enum.GetValues<MilestoneKind>())
                {
                    var (plan, actual) = Dates(job, kind);
                    if ((actual ?? plan) is not { } date) continue;
                    list.Add(new TimelineEvent(job, kind, date.Date, plan?.Date, actual?.Date, ScheduleRules.State(plan, actual, today)));
                }
            }
            return list
                .OrderBy(e => e.Date)
                .ThenBy(e => e.Kind)
                .ThenBy(e => e.Job.WorkOrder, NaturalComparer.Instance)
                .ToList();
        }

        /// <summary>
        /// 「近期」範圍：前 PastDays 天到後 FutureDays 天；還沒完成的逾期項目不論多久以前都保留，免得被漏看。
        /// </summary>
        public static IEnumerable<TimelineEvent> Recent(IEnumerable<TimelineEvent> events, DateTime today,
            int pastDays = 14, int futureDays = 60)
        {
            var from = today.Date.AddDays(-pastDays);
            var to = today.Date.AddDays(futureDays);
            return events.Where(e => e.State == MilestoneState.Overdue || (e.Date >= from && e.Date <= to));
        }

        /// <summary>日期標題的說明：今天、明天、3 天後、2 天前…</summary>
        public static string Relative(DateTime date, DateTime today)
        {
            var days = (int)(date.Date - today.Date).TotalDays;
            return days switch
            {
                0 => "今天",
                1 => "明天",
                -1 => "昨天",
                > 0 => $"{days} 天後",
                _ => $"{-days} 天前",
            };
        }

        public static string Weekday(DateTime date) => "日一二三四五六"[(int)date.DayOfWeek].ToString();
    }
}
