using System.IO;
using schedule.Models;
using schedule.Services;

namespace schedule.Tests
{
    /// <summary>出料日期（介於配電與交期之間）在各處的計算。</summary>
    public sealed class DispatchTests : IDisposable
    {
        private static readonly DateTime Today = new(2026, 10, 6);

        private static DateTime D(int month, int day) => new(2026, month, day);

        private readonly string _root = Path.Combine(Path.GetTempPath(), "WorkSchedule-Tests-" + Guid.NewGuid().ToString("N"));

        public DispatchTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        [Fact]
        public void Status_DispatchOverdue_IsOverdue()
        {
            var job = new Job { DispatchPlan = D(10, 5), DeliveryPlan = D(10, 20) };
            Assert.Equal(JobStatus.Overdue, ScheduleRules.Status(job, Today));

            job.DispatchActual = D(10, 5);
            Assert.Equal(JobStatus.Active, ScheduleRules.Status(job, Today));
        }

        [Fact]
        public void PlanOrderWarning_ChecksDispatchBetweenWiringAndDelivery()
        {
            Assert.Null(ScheduleRules.PlanOrderWarning(new Job { WiringPlan = D(10, 1), DispatchPlan = D(10, 5), DeliveryPlan = D(10, 9) }));
            Assert.Null(ScheduleRules.PlanOrderWarning(new Job { DispatchPlan = D(10, 5) }));
            Assert.Equal("預計配電日期比預計出料日期晚。",
                ScheduleRules.PlanOrderWarning(new Job { WiringPlan = D(10, 6), DispatchPlan = D(10, 5), DeliveryPlan = D(10, 9) }));
            Assert.Equal("預計出料日期比預計交期晚。",
                ScheduleRules.PlanOrderWarning(new Job { DispatchPlan = D(10, 10), DeliveryPlan = D(10, 9) }));
            Assert.Equal("預計材料入場日期比預計交期晚。",
                ScheduleRules.PlanOrderWarning(new Job { MaterialPlan = D(10, 10), DeliveryPlan = D(10, 9) }));
        }

        [Fact]
        public void Sort_ByDispatchPlan_EmptyLast()
        {
            var jobs = new[]
            {
                new Job { WorkOrder = "A" },
                new Job { WorkOrder = "B", DispatchPlan = D(10, 9) },
                new Job { WorkOrder = "C", DispatchPlan = D(10, 2) },
            };
            Assert.Equal(new[] { "C", "B", "A" }, ScheduleRules.Sort(jobs, JobSort.DispatchPlan).Select(j => j.WorkOrder));
        }

        [Fact]
        public void Timeline_HasDispatchEvent_OrderedBeforeDeliveryOnSameDay()
        {
            var job = new Job { WorkOrder = "A", DispatchPlan = D(10, 9), DeliveryPlan = D(10, 9) };
            var events = Timeline.Events(new[] { job }, Today);

            Assert.Equal(new[] { MilestoneKind.Dispatch, MilestoneKind.Delivery }, events.Select(e => e.Kind));
            Assert.Equal("出料", Timeline.KindName(MilestoneKind.Dispatch));
        }

        [Fact]
        public void Gantt_SegmentsGoThroughDispatch()
        {
            var job = new Job { MaterialPlan = D(10, 1), WiringPlan = D(10, 5), DispatchPlan = D(10, 8), DeliveryPlan = D(10, 9) };
            Assert.Equal(new[]
            {
                new GanttSegment(D(10, 1), D(10, 5), MilestoneKind.Material),
                new GanttSegment(D(10, 5), D(10, 8), MilestoneKind.Wiring),
                new GanttSegment(D(10, 8), D(10, 9), MilestoneKind.Dispatch),
            }, GanttMath.PlanSegments(job));

            // 已出料、還沒交貨：虛線從出料日畫到今天
            var shipped = new Job { WiringActual = D(10, 1), DispatchActual = D(10, 3), DeliveryPlan = D(10, 9) };
            Assert.Equal(new GanttSegment(D(10, 3), Today, MilestoneKind.Dispatch), GanttMath.Progress(shipped, Today));
            Assert.Contains(D(10, 3), GanttMath.AllDates(shipped));
        }

        [Fact]
        public void JobRow_HasDispatchCell()
        {
            var row = new JobRow(new Job { DispatchPlan = D(10, 8) }, Today);
            Assert.Equal(MilestoneState.Soon, row.Dispatch.State);
            Assert.StartsWith("出料", row.Dispatch.ToolTip);
        }

        [Fact]
        public void Excel_RoundTripsDispatch_AndOldFilesWithoutItStillImport()
        {
            var path = Path.Combine(_root, "a.xlsx");
            ExcelService.Export(new[] { new Job { WorkOrder = "A", Quantity = 1, DispatchPlan = D(10, 8), DispatchActual = D(10, 9) } }, path, Today);
            var (jobs, skipped) = ExcelService.Import(path);

            Assert.Equal(0, skipped);
            Assert.Equal((D(10, 8), D(10, 9)), (jobs[0].DispatchPlan, jobs[0].DispatchActual));

            // 舊版匯出的檔案沒有出料欄
            using (var wb = new ClosedXML.Excel.XLWorkbook())
            {
                var ws = wb.Worksheets.Add(ExcelService.SheetName);
                ws.Cell(1, 1).Value = "工令";
                ws.Cell(1, 2).Value = "預計交期";
                ws.Cell(2, 1).Value = "B";
                ws.Cell(2, 2).Value = "2026/10/20";
                wb.SaveAs(Path.Combine(_root, "old.xlsx"));
            }
            var (old, _) = ExcelService.Import(Path.Combine(_root, "old.xlsx"));
            Assert.Null(old.Single().DispatchPlan);
            Assert.Equal(D(10, 20), old.Single().DeliveryPlan);
        }
    }
}
