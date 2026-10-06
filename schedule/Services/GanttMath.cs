using schedule.Models;

namespace schedule.Services
{
    /// <summary>甘特圖的縮放：每一天的寬度不同。</summary>
    public enum GanttZoom { Day, Week, Month }

    /// <summary>甘特圖上的一段橫條。From / To 都是「那一天」，畫的時候從 From 的中間畫到 To 的中間。</summary>
    public record GanttSegment(DateTime From, DateTime To, MilestoneKind StartsAt);

    /// <summary>甘特圖上的一個時間點記號。</summary>
    public record GanttMarker(MilestoneKind Kind, DateTime Date, bool IsActual, MilestoneState State);

    /// <summary>甘特圖的日期範圍與座標計算（不依賴畫面，方便自動測試）。</summary>
    public static class GanttMath
    {
        public static double DayWidth(GanttZoom zoom) => zoom switch
        {
            GanttZoom.Day => 30,
            GanttZoom.Week => 12,
            _ => 4,
        };

        /// <summary>工令所有有填的日期（預計與實際）。</summary>
        public static IEnumerable<DateTime> AllDates(Job j) =>
            Enum.GetValues<MilestoneKind>()
                .SelectMany(k => { var (plan, actual) = Timeline.Dates(j, k); return new[] { plan, actual }; })
                .Where(d => d != null).Select(d => d!.Value.Date);

        /// <summary>
        /// 要畫的日期範圍：涵蓋所有工令的日期與今天，前後各留一點空間；
        /// 依縮放對齊到週一（日、週）或月初（月），格線比較整齊。End 是最後一天（含）。
        /// </summary>
        public static (DateTime Start, DateTime End) Range(IEnumerable<Job> jobs, DateTime today, GanttZoom zoom)
        {
            var dates = jobs.SelectMany(AllDates).Append(today.Date).ToList();
            var min = dates.Min();
            var max = dates.Max();

            if (zoom == GanttZoom.Month)
            {
                var start = new DateTime(min.Year, min.Month, 1).AddMonths(-1);
                var endMonth = new DateTime(max.Year, max.Month, 1).AddMonths(2);
                return (start, endMonth.AddDays(-1));
            }

            int pad = zoom == GanttZoom.Day ? 7 : 14;
            var from = Monday(min.AddDays(-pad));
            var to = Monday(max.AddDays(pad)).AddDays(6);
            // 畫面很寬但資料很少時，至少畫出幾週，不要只有一小段
            int minDays = zoom == GanttZoom.Day ? 35 : 84;
            if ((to - from).TotalDays + 1 < minDays) to = from.AddDays(minDays - 1);
            return (from, to);
        }

        public static DateTime Monday(DateTime d)
        {
            int offset = ((int)d.DayOfWeek + 6) % 7; // 週一 = 0
            return d.Date.AddDays(-offset);
        }

        /// <summary>某天在圖上的左邊界（像素）。</summary>
        public static double X(DateTime date, DateTime start, double dayWidth) =>
            (date.Date - start.Date).TotalDays * dayWidth;

        /// <summary>某天在圖上的中心點（像素）。</summary>
        public static double CenterX(DateTime date, DateTime start, double dayWidth) =>
            X(date, start, dayWidth) + dayWidth / 2;

        /// <summary>
        /// 預計的橫條：依序連接有填的預計日期（材料入場 → 配電 → 出料 → 交期）。
        /// 每段的顏色看起點：從材料入場開始是「備料」，從配電開始是「配電」，從出料開始是「出料到交貨」。
        /// 日期前後顛倒的段落不畫。
        /// </summary>
        public static List<GanttSegment> PlanSegments(Job j) =>
            Segments(Enum.GetValues<MilestoneKind>().Select(k => (k, Timeline.Dates(j, k).Plan)).ToArray());

        /// <summary>實際的橫條：依序連接有填的實際日期。</summary>
        public static List<GanttSegment> ActualSegments(Job j) =>
            Segments(Enum.GetValues<MilestoneKind>().Select(k => (k, Timeline.Dates(j, k).Actual)).ToArray());

        private static List<GanttSegment> Segments((MilestoneKind Kind, DateTime? Date)[] points)
        {
            var list = new List<GanttSegment>();
            var filled = points.Where(p => p.Date != null).Select(p => (p.Kind, Date: p.Date!.Value.Date)).ToList();
            for (int i = 0; i + 1 < filled.Count; i++)
            {
                if (filled[i].Date > filled[i + 1].Date) continue;
                list.Add(new GanttSegment(filled[i].Date, filled[i + 1].Date, filled[i].Kind));
            }
            return list;
        }

        /// <summary>
        /// 還沒交貨、已經開始（有任何實際日期）的工令：從最後一個實際日期畫到今天的虛線，表示「進行到這裡」。
        /// </summary>
        public static GanttSegment? Progress(Job j, DateTime today)
        {
            if (j.DeliveryActual != null) return null;
            var last = Enum.GetValues<MilestoneKind>()
                .Where(k => k != MilestoneKind.Delivery)
                .Select(k => (Kind: k, Timeline.Dates(j, k).Actual))
                .Where(p => p.Actual != null)
                .Select(p => (p.Kind, Date: p.Actual!.Value.Date))
                .OrderBy(p => p.Date)
                .LastOrDefault();
            if (last == default || last.Date >= today.Date) return null;
            return new GanttSegment(last.Date, today.Date, last.Kind);
        }

        /// <summary>每個時間點的記號：預計日期一個（菱形），實際日期一個（圓點）。</summary>
        public static List<GanttMarker> Markers(Job j, DateTime today)
        {
            var list = new List<GanttMarker>();
            foreach (var kind in Enum.GetValues<MilestoneKind>())
            {
                var (plan, actual) = Timeline.Dates(j, kind);
                var state = ScheduleRules.State(plan, actual, today);
                if (plan is { } p) list.Add(new GanttMarker(kind, p.Date, false, state));
                if (actual is { } a) list.Add(new GanttMarker(kind, a.Date, true, state));
            }
            return list;
        }
    }
}
