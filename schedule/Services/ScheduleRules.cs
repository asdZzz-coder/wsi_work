using schedule.Models;

namespace schedule.Services
{
    /// <summary>單一時間點（例如配電）的狀態。</summary>
    public enum MilestoneState
    {
        None,     // 沒有預計日期、也還沒完成
        Pending,  // 還沒到預計日期
        Soon,     // 預計日期在 SoonDays 天內（含今天）
        Overdue,  // 已過預計日期但還沒完成
        Done,     // 已完成（準時或沒有預計日期）
        Late,     // 已完成，但比預計日期晚
    }

    /// <summary>整筆工令的狀態：交貨了就是已完成；任一時間點逾期就是逾期；其他為進行中。</summary>
    public enum JobStatus { Active, Overdue, Done }

    /// <summary>上方的篩選卡片。</summary>
    public enum JobFilter { All, Active, Overdue, DueSoon, Done }

    /// <summary>清單排序方式。</summary>
    public enum JobSort { DeliveryPlan, MaterialPlan, WiringPlan, DispatchPlan, WorkOrder, Customer }

    /// <summary>排程的判斷規則（狀態、篩選、排序），不依賴畫面，方便自動測試。</summary>
    public static class ScheduleRules
    {
        /// <summary>預計日期在幾天內算「即將到期」。</summary>
        public const int SoonDays = 3;

        /// <summary>「7 天內交貨」篩選的天數。</summary>
        public const int DueSoonDays = 7;

        public static MilestoneState State(DateTime? plan, DateTime? actual, DateTime today)
        {
            if (actual is { } a) return plan is { } p && a.Date > p.Date ? MilestoneState.Late : MilestoneState.Done;
            if (plan is not { } due) return MilestoneState.None;
            var days = (due.Date - today.Date).TotalDays;
            if (days < 0) return MilestoneState.Overdue;
            return days <= SoonDays ? MilestoneState.Soon : MilestoneState.Pending;
        }

        public static IEnumerable<(DateTime? Plan, DateTime? Actual)> Milestones(Job j) =>
            new[] { (j.MaterialPlan, j.MaterialActual), (j.WiringPlan, j.WiringActual), (j.DispatchPlan, j.DispatchActual), (j.DeliveryPlan, j.DeliveryActual) };

        /// <summary>
        /// 預計日期前後顛倒（應該是 材料入場 ≤ 配電 ≤ 出料 ≤ 交期）時的提醒文字，多半是打錯；沒問題回傳 null。
        /// 沒填的跳過，只比較有填的。
        /// </summary>
        public static string? PlanOrderWarning(Job j)
        {
            var plans = new (string Name, DateTime? Date)[]
            {
                ("預計材料入場日期", j.MaterialPlan), ("預計配電日期", j.WiringPlan), ("預計出料日期", j.DispatchPlan), ("預計交期", j.DeliveryPlan),
            }.Where(p => p.Date != null).ToList();
            for (int i = 0; i < plans.Count; i++)
                for (int k = i + 1; k < plans.Count; k++)
                    if (plans[i].Date!.Value.Date > plans[k].Date!.Value.Date)
                        return $"{plans[i].Name}比{plans[k].Name}晚。";
            return null;
        }

        public static JobStatus Status(Job job, DateTime today)
        {
            if (job.DeliveryActual != null) return JobStatus.Done;
            return Milestones(job).Any(m => State(m.Plan, m.Actual, today) == MilestoneState.Overdue)
                ? JobStatus.Overdue
                : JobStatus.Active;
        }

        public static string StatusText(JobStatus status) => status switch
        {
            JobStatus.Done => "已完成",
            JobStatus.Overdue => "逾期",
            _ => "進行中",
        };

        /// <summary>還沒交貨、預計交期在今天到 DueSoonDays 天內。</summary>
        public static bool IsDueSoon(Job job, DateTime today)
        {
            if (job.DeliveryActual != null || job.DeliveryPlan is not { } due) return false;
            var days = (due.Date - today.Date).TotalDays;
            return days >= 0 && days <= DueSoonDays;
        }

        public static bool Matches(Job job, JobFilter filter, DateTime today) => filter switch
        {
            JobFilter.Active => Status(job, today) == JobStatus.Active,
            JobFilter.Overdue => Status(job, today) == JobStatus.Overdue,
            JobFilter.DueSoon => IsDueSoon(job, today),
            JobFilter.Done => Status(job, today) == JobStatus.Done,
            _ => true,
        };

        /// <summary>搜尋：工令、機種、客戶、人員、耗材、備註任一包含關鍵字（不分大小寫）。</summary>
        public static bool MatchesSearch(Job job, string keyword)
        {
            keyword = keyword.Trim();
            if (keyword.Length == 0) return true;
            return new[] { job.WorkOrder, job.Model, job.Customer, job.InnerWiring, job.OuterWiring, job.Consumables, job.Note }
                .Any(s => s.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>依日期排序時，沒有日期的排最後；日期相同再依工令。</summary>
        public static IEnumerable<Job> Sort(IEnumerable<Job> jobs, JobSort sort)
        {
            IOrderedEnumerable<Job> ByDate(Func<Job, DateTime?> key) =>
                jobs.OrderBy(j => key(j) == null).ThenBy(j => key(j));

            var ordered = sort switch
            {
                JobSort.MaterialPlan => ByDate(j => j.MaterialPlan),
                JobSort.WiringPlan => ByDate(j => j.WiringPlan),
                JobSort.DispatchPlan => ByDate(j => j.DispatchPlan),
                JobSort.WorkOrder => jobs.OrderBy(j => j.WorkOrder, NaturalComparer.Instance),
                JobSort.Customer => jobs.OrderBy(j => j.Customer.Length == 0).ThenBy(j => j.Customer, StringComparer.CurrentCultureIgnoreCase),
                _ => ByDate(j => j.DeliveryPlan),
            };
            return ordered.ThenBy(j => j.WorkOrder, NaturalComparer.Instance);
        }

        /// <summary>工令重複（不分大小寫、忽略前後空白）。except 是正在修改的那筆。</summary>
        public static Job? FindDuplicate(IEnumerable<Job> jobs, string workOrder, Job? except) =>
            jobs.FirstOrDefault(j => j != except &&
                string.Equals(j.WorkOrder.Trim(), workOrder.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// 把匯入的工令併進 existing。replace = true 時先清空，完全以匯入為準；
        /// 否則工令相同的以匯入內容取代（保留原本的 Id），其他新增。
        /// </summary>
        public static (int Added, int Updated) Merge(List<Job> existing, IEnumerable<Job> imported, bool replace)
        {
            if (replace) existing.Clear();
            int added = 0, updated = 0;
            foreach (var item in imported)
            {
                var same = FindDuplicate(existing, item.WorkOrder, except: null);
                if (same == null)
                {
                    existing.Add(item);
                    added++;
                    continue;
                }
                item.Id = same.Id;
                existing[existing.IndexOf(same)] = item;
                updated++;
            }
            return (added, updated);
        }
    }

    /// <summary>字串裡的數字依數值比較：W2-9 排在 W2-10 前面。</summary>
    public sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            x ??= ""; y ??= "";
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                    while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                    var a = x[si..i].TrimStart('0');
                    var b = y[sj..j].TrimStart('0');
                    int c = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
                    if (c != 0) return c;
                }
                else
                {
                    int c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
