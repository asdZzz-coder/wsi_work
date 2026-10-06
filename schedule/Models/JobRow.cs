using schedule.Services;

namespace schedule.Models
{
    /// <summary>清單上一個時間點的格子：預計 / 實際兩行，加上狀態（決定顏色）。</summary>
    public record MilestoneCell(string PlanText, string ActualText, MilestoneState State, string ToolTip);

    /// <summary>清單上的一列（只供畫面使用，不存檔）。每次重新整理時依「今天」重新計算。</summary>
    public class JobRow
    {
        public JobRow(Job job, DateTime today)
        {
            Job = job;
            Status = ScheduleRules.Status(job, today);
            Material = Cell("材料入場", job.MaterialPlan, job.MaterialActual, today);
            Wiring = Cell("配電", job.WiringPlan, job.WiringActual, today);
            Dispatch = Cell("出料", job.DispatchPlan, job.DispatchActual, today);
            Delivery = Cell("交期", job.DeliveryPlan, job.DeliveryActual, today);
        }

        public Job Job { get; }

        public string WorkOrder => Job.WorkOrder;
        public string Model => Dash(Job.Model);
        public string Quantity => Job.Quantity.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture);
        public string Customer => Dash(Job.Customer);
        public bool HasCE => Job.HasCE;
        public bool HasTS => Job.HasTS;
        public bool NoCert => !Job.HasCE && !Job.HasTS;
        public string InnerWiring => Dash(Job.InnerWiring);
        public string OuterWiring => Dash(Job.OuterWiring);
        public string Consumables => Dash(Job.Consumables);
        public bool HasNote => Job.Note.Trim().Length > 0;
        public string Note => Job.Note.Trim();

        public MilestoneCell Material { get; }
        public MilestoneCell Wiring { get; }
        public MilestoneCell Dispatch { get; }
        public MilestoneCell Delivery { get; }

        public JobStatus Status { get; }
        public string StatusText => ScheduleRules.StatusText(Status);

        // 螢幕閱讀器與 UI 自動化讀到的名稱
        public override string ToString() => WorkOrder;

        private static string Dash(string s) => s.Trim().Length == 0 ? "—" : s.Trim();

        private static MilestoneCell Cell(string name, DateTime? plan, DateTime? actual, DateTime today)
        {
            var state = ScheduleRules.State(plan, actual, today);
            var tip = $"{name}\n預計：{(plan == null ? "未排定" : TextFormat.Date(plan))}\n實際：{(actual == null ? "未完成" : TextFormat.Date(actual))}";
            if (plan is { } p && actual is { } a && a.Date > p.Date) tip += $"\n晚了 {(a.Date - p.Date).TotalDays:0} 天";
            else if (state == MilestoneState.Overdue) tip += $"\n已逾期 {(today.Date - plan!.Value.Date).TotalDays:0} 天";
            else if (state == MilestoneState.Soon) tip += (plan!.Value.Date - today.Date).TotalDays == 0 ? "\n今天到期" : $"\n剩 {(plan!.Value.Date - today.Date).TotalDays:0} 天";
            return new MilestoneCell(TextFormat.ShortDate(plan, today), TextFormat.ShortDate(actual, today), state, tip);
        }
    }
}
