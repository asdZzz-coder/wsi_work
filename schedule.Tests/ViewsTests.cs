using schedule.Models;
using schedule.Services;

namespace schedule.Tests
{
    /// <summary>時間軸、甘特圖、資訊看板背後的計算。</summary>
    public class ViewsTests
    {
        private static readonly DateTime Today = new(2026, 10, 6); // 星期二

        private static DateTime D(int month, int day) => new(2026, month, day);

        // ---------- 時間軸 ----------

        [Fact]
        public void Events_UseActualDateWhenDone_OtherwisePlan()
        {
            var job = new Job { WorkOrder = "A", MaterialPlan = D(10, 1), MaterialActual = D(10, 3), WiringPlan = D(10, 10) };
            var events = Timeline.Events(new[] { job }, Today);

            Assert.Equal(2, events.Count); // 交期沒填，不算
            Assert.Equal((MilestoneKind.Material, D(10, 3), MilestoneState.Late), (events[0].Kind, events[0].Date, events[0].State));
            Assert.Equal((MilestoneKind.Wiring, D(10, 10), MilestoneState.Pending), (events[1].Kind, events[1].Date, events[1].State));
            Assert.Equal(2, events[0].LateDays);
        }

        [Fact]
        public void Events_SortedByDate_ThenMilestone_ThenWorkOrder()
        {
            var jobs = new[]
            {
                new Job { WorkOrder = "W-10", DeliveryPlan = D(10, 8) },
                new Job { WorkOrder = "W-9", DeliveryPlan = D(10, 8), WiringPlan = D(10, 8) },
                new Job { WorkOrder = "W-1", MaterialPlan = D(10, 20) },
            };
            var order = Timeline.Events(jobs, Today).Select(e => $"{e.Job.WorkOrder}:{e.Kind}");
            Assert.Equal(new[] { "W-9:Wiring", "W-9:Delivery", "W-10:Delivery", "W-1:Material" }, order);
        }

        [Fact]
        public void Recent_KeepsOldOverdue_DropsOldDoneAndFarFuture()
        {
            var jobs = new[]
            {
                new Job { WorkOrder = "old-overdue", MaterialPlan = D(8, 1) },
                new Job { WorkOrder = "old-done", MaterialPlan = D(8, 1), MaterialActual = D(8, 1) },
                new Job { WorkOrder = "recent-done", MaterialPlan = D(9, 25), MaterialActual = D(9, 25) },
                new Job { WorkOrder = "soon", MaterialPlan = D(11, 30) },
                new Job { WorkOrder = "far", MaterialPlan = D(12, 31) },
            };
            var names = Timeline.Recent(Timeline.Events(jobs, Today), Today).Select(e => e.Job.WorkOrder);
            Assert.Equal(new[] { "old-overdue", "recent-done", "soon" }, names);
        }

        [Theory]
        [InlineData(0, "今天")]
        [InlineData(1, "明天")]
        [InlineData(-1, "昨天")]
        [InlineData(3, "3 天後")]
        [InlineData(-5, "5 天前")]
        public void Relative_DescribesDistanceFromToday(int days, string expected) =>
            Assert.Equal(expected, Timeline.Relative(Today.AddDays(days), Today));

        [Fact]
        public void Weekday_IsChinese() => Assert.Equal("二", Timeline.Weekday(Today));

        // ---------- 甘特圖 ----------

        [Theory]
        [InlineData(10, 6, 10, 5)]  // 週二 → 週一
        [InlineData(10, 11, 10, 5)] // 週日算在前一週
        [InlineData(10, 5, 10, 5)]  // 週一本身
        public void Monday_StartsTheWeek(int month, int day, int expectedMonth, int expectedDay) =>
            Assert.Equal(D(expectedMonth, expectedDay), GanttMath.Monday(D(month, day)));

        [Fact]
        public void Range_Day_CoversAllDatesWithPadding_AlignedToWeeks()
        {
            var jobs = new[] { new Job { MaterialPlan = D(10, 1), DeliveryPlan = D(10, 20) } };
            var (start, end) = GanttMath.Range(jobs, Today, GanttZoom.Day);
            Assert.Equal(D(9, 21), start);  // 10/1 往前 7 天是 9/24（週四），對齊到週一
            Assert.Equal(D(11, 1), end);    // 10/20 往後 7 天是 10/27，對齊到那週的週日
        }

        [Fact]
        public void Range_WithoutJobs_StillShowsSomeWeeksAroundToday()
        {
            var (start, end) = GanttMath.Range(Array.Empty<Job>(), Today, GanttZoom.Day);
            Assert.Equal(DayOfWeek.Monday, start.DayOfWeek);
            Assert.True(start <= Today && end >= Today);
            Assert.Equal(35, (end - start).TotalDays + 1);
        }

        [Fact]
        public void Range_Month_AlignsToWholeMonths()
        {
            var jobs = new[] { new Job { MaterialPlan = D(9, 15), DeliveryPlan = D(11, 20) } };
            var (start, end) = GanttMath.Range(jobs, Today, GanttZoom.Month);
            Assert.Equal(D(8, 1), start);
            Assert.Equal(D(12, 31), end);
        }

        [Fact]
        public void X_IsDaysFromStartTimesDayWidth()
        {
            Assert.Equal(90, GanttMath.X(D(10, 8), D(10, 5), 30));
            Assert.Equal(105, GanttMath.CenterX(D(10, 8), D(10, 5), 30));
        }

        [Fact]
        public void PlanSegments_ConnectFilledPlans_ColoredByStartingMilestone()
        {
            var full = new Job { MaterialPlan = D(10, 1), WiringPlan = D(10, 5), DeliveryPlan = D(10, 9) };
            Assert.Equal(new[]
            {
                new GanttSegment(D(10, 1), D(10, 5), MilestoneKind.Material),
                new GanttSegment(D(10, 5), D(10, 9), MilestoneKind.Wiring),
            }, GanttMath.PlanSegments(full));

            var noWiring = new Job { MaterialPlan = D(10, 1), DeliveryPlan = D(10, 9) };
            Assert.Equal(new[] { new GanttSegment(D(10, 1), D(10, 9), MilestoneKind.Material) }, GanttMath.PlanSegments(noWiring));

            var onlyDelivery = new Job { DeliveryPlan = D(10, 9) };
            Assert.Empty(GanttMath.PlanSegments(onlyDelivery));
        }

        [Fact]
        public void PlanSegments_SkipReversedDates()
        {
            var reversed = new Job { MaterialPlan = D(10, 8), WiringPlan = D(10, 5), DeliveryPlan = D(10, 9) };
            Assert.Equal(new[] { new GanttSegment(D(10, 5), D(10, 9), MilestoneKind.Wiring) }, GanttMath.PlanSegments(reversed));
        }

        [Fact]
        public void Progress_RunsFromLastActualToToday_UntilDelivered()
        {
            var started = new Job { MaterialActual = D(9, 29), WiringPlan = D(10, 3), DeliveryPlan = D(10, 11) };
            Assert.Equal(new GanttSegment(D(9, 29), Today, MilestoneKind.Material), GanttMath.Progress(started, Today));

            Assert.Null(GanttMath.Progress(new Job { DeliveryPlan = D(10, 11) }, Today)); // 還沒開始
            started.DeliveryActual = D(10, 5);
            Assert.Null(GanttMath.Progress(started, Today)); // 已交貨
        }

        [Fact]
        public void Markers_OnePerPlanAndActualDate()
        {
            var job = new Job { MaterialPlan = D(10, 1), MaterialActual = D(10, 2), DeliveryPlan = D(10, 3) };
            var markers = GanttMath.Markers(job, Today);
            Assert.Equal(3, markers.Count);
            Assert.Contains(new GanttMarker(MilestoneKind.Material, D(10, 2), true, MilestoneState.Late), markers);
            Assert.Contains(new GanttMarker(MilestoneKind.Delivery, D(10, 3), false, MilestoneState.Overdue), markers);
        }

        // ---------- 資訊看板 ----------

        private static List<Job> DashboardJobs() => new()
        {
            new Job { WorkOrder = "done-on-time", Quantity = 1, DeliveryPlan = D(10, 3), DeliveryActual = D(10, 2), Customer = "中鋼" },
            new Job { WorkOrder = "done-late", Quantity = 1, DeliveryPlan = D(9, 20), DeliveryActual = D(9, 25) },
            new Job { WorkOrder = "overdue", Quantity = 2, DeliveryPlan = D(10, 1), Customer = "鴻海" },
            new Job { WorkOrder = "due-soon", Quantity = 3, DeliveryPlan = D(10, 9), Customer = "台積電",
                      InnerWiring = "阿明、小陳", OuterWiring = "小陳", WiringPlan = D(10, 6) },
            new Job { WorkOrder = "later", Quantity = 4, DeliveryPlan = D(10, 30), Customer = "台積電", InnerWiring = "阿明",
                      MaterialPlan = D(10, 13) },
        };

        [Fact]
        public void Dashboard_Counts()
        {
            var s = DashboardStats.Compute(DashboardJobs(), Today);
            Assert.Equal(5, s.Total);
            Assert.Equal(3, s.Active);
            Assert.Equal(1, s.Overdue);
            Assert.Equal(1, s.DueSoon);
            Assert.Equal(1, s.DeliveredThisMonth);
            Assert.Equal(9, s.ActiveQuantity);
            Assert.Equal(0.5, s.OnTimeRate);
            Assert.Equal(1, s.DeliveryOverdue);
        }

        [Fact]
        public void Dashboard_OnTimeRate_IsNullWithoutDeliveries() =>
            Assert.Null(DashboardStats.Compute(new[] { new Job { DeliveryPlan = D(10, 9) } }, Today).OnTimeRate);

        [Fact]
        public void Dashboard_Lists()
        {
            var s = DashboardStats.Compute(DashboardJobs(), Today);
            Assert.Equal(new[] { "overdue" }, s.OverdueItems.Select(e => e.Job.WorkOrder));
            // 今天的配電、10/9 的交貨在 7 天內；10/13 的材料入場剛好第 8 天，不算
            Assert.Equal(new[] { "due-soon:Wiring", "due-soon:Delivery" }, s.Upcoming.Select(e => $"{e.Job.WorkOrder}:{e.Kind}"));
            Assert.Equal(new[] { "done-on-time", "done-late" }, s.RecentDeliveries.Select(e => e.Job.WorkOrder));
        }

        [Fact]
        public void Dashboard_DeliveryWeeks_StartThisWeek_AndSkipOverdue()
        {
            var s = DashboardStats.Compute(DashboardJobs(), Today);
            Assert.Equal(DashboardStats.Weeks, s.DeliveryWeeks.Count);
            Assert.Equal(D(10, 5), s.DeliveryWeeks[0].Start);
            Assert.Equal(1, s.DeliveryWeeks[0].Count); // due-soon（overdue 的 10/1 雖然在本週，但已逾期，另外算）
            Assert.Equal(1, s.DeliveryWeeks[3].Count); // later：10/26 那週
            Assert.Equal(2, s.DeliveryWeeks.Sum(w => w.Count));
        }

        [Fact]
        public void Dashboard_Workload_CountsEachPersonOncePerJob()
        {
            var s = DashboardStats.Compute(DashboardJobs(), Today);
            Assert.Equal(new[] { new CountItem("阿明", 2), new CountItem("小陳", 1) }, s.Workload);
            Assert.Equal(new[] { new CountItem("台積電", 2), new CountItem("鴻海", 1) }, s.Customers);
        }

        [Fact]
        public void People_SplitsCommonSeparators() =>
            Assert.Equal(new[] { "阿明", "小陳", "老王", "阿華" }, DashboardStats.People(" 阿明、小陳 / 老王,阿華 "));

        // ---------- 時間軸 / 看板上的一列 ----------

        private static EventRow Row(Job job, MilestoneKind kind, bool showDate = false) =>
            new(Timeline.Events(new[] { job }, Today).Single(e => e.Kind == kind), Today, showDate: showDate);

        [Fact]
        public void EventRow_ChipTexts()
        {
            Assert.Equal("逾期 3 天", Row(new Job { WiringPlan = D(10, 3) }, MilestoneKind.Wiring).ChipText);
            Assert.Equal("今天到期", Row(new Job { WiringPlan = Today }, MilestoneKind.Wiring).ChipText);
            Assert.Equal("剩 2 天", Row(new Job { WiringPlan = D(10, 8) }, MilestoneKind.Wiring).ChipText);
            Assert.Equal("已完成", Row(new Job { WiringPlan = D(10, 3), WiringActual = D(10, 3) }, MilestoneKind.Wiring).ChipText);

            var late = Row(new Job { WiringPlan = D(10, 1), WiringActual = D(10, 3) }, MilestoneKind.Wiring);
            Assert.Equal("晚 2 天完成", late.ChipText);
            Assert.Equal("預計 10/01", late.SideNote);

            var early = Row(new Job { WiringPlan = D(10, 5), WiringActual = D(10, 3) }, MilestoneKind.Wiring);
            Assert.Equal("提前完成", early.ChipText);
        }

        [Fact]
        public void EventRow_Detail_AndDateForDashboard()
        {
            var job = new Job { WorkOrder = "W1", Model = "MCC-400", Customer = "台積電", Quantity = 1200, DeliveryPlan = D(10, 9) };
            var row = Row(job, MilestoneKind.Delivery, showDate: true);
            Assert.Equal("MCC-400 · 台積電 · 1,200 台", row.Detail);
            Assert.Equal("交貨", row.KindName);
            Assert.Equal("10/09（五）", row.SideNote);
            Assert.False(Row(job, MilestoneKind.Delivery).HasSideNote); // 時間軸已依日期分組，不再顯示
        }
    }
}
