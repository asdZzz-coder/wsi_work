using System.Globalization;
using schedule.Services;

namespace schedule.Models
{
    /// <summary>時間軸、看板清單上的一列（只供畫面使用）：某工令的某個時間點。</summary>
    public class EventRow
    {
        /// <param name="showDate">看板用：右下角顯示日期（時間軸已經依日期分組，不用再顯示）。</param>
        public EventRow(TimelineEvent e, DateTime today, bool selected = false, bool showDate = false)
        {
            Event = e;
            IsSelected = selected;
            _showDate = showDate;

            var j = e.Job;
            KindName = Timeline.KindName(e.Kind);
            Icon = e.Kind switch
            {
                MilestoneKind.Material => "", // 箱子
                MilestoneKind.Wiring => "",   // 閃電
                MilestoneKind.Dispatch => "", // 往外的箭頭
                _ => "",                      // 旗子
            };
            Detail = string.Join(" · ", new[]
            {
                j.Model.Trim(),
                j.Customer.Trim(),
                j.Quantity > 0 ? $"{j.Quantity.ToString("#,0", CultureInfo.InvariantCulture)} 台" : "",
            }.Where(s => s.Length > 0));
            DateText = $"{TextFormat.ShortDate(e.Date, today)}（{Timeline.Weekday(e.Date)}）";

            var days = (int)(e.Date - today.Date).TotalDays;
            (ChipText, PlanNote) = e.State switch
            {
                MilestoneState.Overdue => ($"逾期 {e.OverdueDays(today)} 天", ""),
                MilestoneState.Soon => (days == 0 ? "今天到期" : $"剩 {days} 天", ""),
                MilestoneState.Late => ($"晚 {e.LateDays} 天完成", $"預計 {TextFormat.ShortDate(e.Plan, today)}"),
                MilestoneState.Done when e.Plan is { } p && p > e.Date => ("提前完成", $"預計 {TextFormat.ShortDate(p, today)}"),
                MilestoneState.Done => ("已完成", ""),
                _ => ($"剩 {days} 天", ""),
            };
        }

        public TimelineEvent Event { get; }
        public Job Job => Event.Job;

        public string WorkOrder => Job.WorkOrder;
        public string KindName { get; }
        public string Icon { get; }
        public string Detail { get; }
        public bool HasDetail => Detail.Length > 0;
        public string DateText { get; }

        /// <summary>給 StateChip 樣式用（Overdue / Soon / Done…）。</summary>
        public string StateName => Event.State.ToString();
        public string ChipText { get; }

        /// <summary>完成日和預計不同時，附上預計日期。</summary>
        public string PlanNote { get; }
        public bool HasPlanNote => PlanNote.Length > 0;

        /// <summary>狀態標籤下方的小字：看板顯示日期，時間軸顯示預計日期（完成日和預計不同時）。</summary>
        public string SideNote => _showDate ? (HasPlanNote ? $"{DateText}・{PlanNote}" : DateText) : PlanNote;
        public bool HasSideNote => SideNote.Length > 0;
        private readonly bool _showDate;

        public bool IsSelected { get; }

        public override string ToString() => $"{WorkOrder} {KindName}";
    }
}
