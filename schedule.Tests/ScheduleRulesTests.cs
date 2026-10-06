using schedule.Models;
using schedule.Services;

namespace schedule.Tests
{
    /// <summary>排程狀態、篩選、排序、匯入合併的自動測試。</summary>
    public class ScheduleRulesTests
    {
        private static readonly DateTime Today = new(2026, 10, 6);

        private static DateTime D(int month, int day) => new(2026, month, day);

        // ---------- 單一時間點的狀態 ----------

        [Fact]
        public void State_NoPlanNoActual_IsNone() =>
            Assert.Equal(MilestoneState.None, ScheduleRules.State(null, null, Today));

        [Theory]
        [InlineData(10, 20, MilestoneState.Pending)]
        [InlineData(10, 10, MilestoneState.Pending)] // 4 天後
        [InlineData(10, 9, MilestoneState.Soon)]     // 3 天後
        [InlineData(10, 6, MilestoneState.Soon)]     // 今天
        [InlineData(10, 5, MilestoneState.Overdue)]  // 昨天
        public void State_NotDone_DependsOnDaysLeft(int month, int day, MilestoneState expected) =>
            Assert.Equal(expected, ScheduleRules.State(D(month, day), null, Today));

        [Fact]
        public void State_DoneOnOrBeforePlan_IsDone()
        {
            Assert.Equal(MilestoneState.Done, ScheduleRules.State(D(10, 1), D(10, 1), Today));
            Assert.Equal(MilestoneState.Done, ScheduleRules.State(D(10, 1), D(9, 28), Today));
        }

        [Fact]
        public void State_DoneAfterPlan_IsLate() =>
            Assert.Equal(MilestoneState.Late, ScheduleRules.State(D(10, 1), D(10, 3), Today));

        [Fact]
        public void State_DoneWithoutPlan_IsDone() =>
            Assert.Equal(MilestoneState.Done, ScheduleRules.State(null, D(10, 3), Today));

        [Fact]
        public void State_IgnoresTimeOfDay() =>
            Assert.Equal(MilestoneState.Done, ScheduleRules.State(D(10, 1), D(10, 1).AddHours(17), Today.AddHours(9)));

        // ---------- 整筆工令的狀態 ----------

        [Fact]
        public void Status_DeliveredIsDone_EvenIfOtherStepsLate()
        {
            var job = new Job { WiringPlan = D(9, 1), DeliveryPlan = D(9, 10), DeliveryActual = D(9, 12) };
            Assert.Equal(JobStatus.Done, ScheduleRules.Status(job, Today));
        }

        [Fact]
        public void Status_AnyOverdueStep_IsOverdue()
        {
            var job = new Job { MaterialPlan = D(10, 1), DeliveryPlan = D(11, 1) };
            Assert.Equal(JobStatus.Overdue, ScheduleRules.Status(job, Today));

            job.MaterialActual = D(10, 2); // 材料到了（雖然晚了）就不算逾期
            Assert.Equal(JobStatus.Active, ScheduleRules.Status(job, Today));
        }

        [Fact]
        public void Status_NothingScheduled_IsActive() =>
            Assert.Equal(JobStatus.Active, ScheduleRules.Status(new Job(), Today));

        // ---------- 篩選 ----------

        [Theory]
        [InlineData(10, 6, true)]
        [InlineData(10, 13, true)]
        [InlineData(10, 14, false)]
        [InlineData(10, 5, false)] // 已經逾期，不算「7 天內交貨」
        public void DueSoon_IsWithinSevenDaysAndNotDelivered(int month, int day, bool expected) =>
            Assert.Equal(expected, ScheduleRules.IsDueSoon(new Job { DeliveryPlan = D(month, day) }, Today));

        [Fact]
        public void DueSoon_FalseOnceDelivered() =>
            Assert.False(ScheduleRules.IsDueSoon(new Job { DeliveryPlan = D(10, 8), DeliveryActual = D(10, 6) }, Today));

        [Fact]
        public void Matches_EachFilter()
        {
            var active = new Job { DeliveryPlan = D(12, 1) };
            var overdue = new Job { WiringPlan = D(10, 1), DeliveryPlan = D(12, 1) };
            var soon = new Job { DeliveryPlan = D(10, 8) };
            var done = new Job { DeliveryPlan = D(10, 1), DeliveryActual = D(10, 1) };
            var all = new[] { active, overdue, soon, done };

            Job[] Of(JobFilter f) => all.Where(j => ScheduleRules.Matches(j, f, Today)).ToArray();
            Assert.Equal(all, Of(JobFilter.All));
            Assert.Equal(new[] { active, soon }, Of(JobFilter.Active));
            Assert.Equal(new[] { overdue }, Of(JobFilter.Overdue));
            Assert.Equal(new[] { soon }, Of(JobFilter.DueSoon));
            Assert.Equal(new[] { done }, Of(JobFilter.Done));
        }

        [Theory]
        [InlineData("", true)]
        [InlineData("w2610", true)]   // 工令，不分大小寫
        [InlineData("MCC", true)]     // 機種
        [InlineData("台積", true)]     // 客戶
        [InlineData("阿明", true)]     // 盤內配電
        [InlineData("小陳", true)]     // 盤外配電
        [InlineData("公司", true)]     // 耗材
        [InlineData("急件", true)]     // 備註
        [InlineData("鴻海", false)]
        public void MatchesSearch_LooksAtTextFields(string keyword, bool expected)
        {
            var job = new Job
            {
                WorkOrder = "W2610-001", Model = "MCC-400", Customer = "台積電",
                InnerWiring = "阿明", OuterWiring = "小陳", Consumables = "公司", Note = "急件",
            };
            Assert.Equal(expected, ScheduleRules.MatchesSearch(job, keyword));
        }

        // ---------- 排序 ----------

        [Fact]
        public void Sort_ByDate_PutsMissingDatesLastThenByWorkOrder()
        {
            var a = new Job { WorkOrder = "A", DeliveryPlan = D(11, 1) };
            var b = new Job { WorkOrder = "B" };
            var c = new Job { WorkOrder = "C", DeliveryPlan = D(10, 10) };
            var d = new Job { WorkOrder = "D", DeliveryPlan = D(10, 10) };
            Assert.Equal(new[] { c, d, a, b }, ScheduleRules.Sort(new[] { a, b, d, c }, JobSort.DeliveryPlan));
        }

        [Fact]
        public void Sort_ByWorkOrder_ComparesNumbersByValue()
        {
            var jobs = new[] { "W2-10", "W2-9", "w2-1", "W10-1" }.Select(w => new Job { WorkOrder = w }).ToList();
            Assert.Equal(new[] { "w2-1", "W2-9", "W2-10", "W10-1" },
                ScheduleRules.Sort(jobs, JobSort.WorkOrder).Select(j => j.WorkOrder));
        }

        [Fact]
        public void Sort_ByCustomer_PutsBlankLast()
        {
            var jobs = new[] { "", "B", "A" }.Select(c => new Job { Customer = c }).ToList();
            Assert.Equal(new[] { "A", "B", "" }, ScheduleRules.Sort(jobs, JobSort.Customer).Select(j => j.Customer));
        }

        // ---------- 重複工令 / 匯入合併 ----------

        [Fact]
        public void FindDuplicate_IgnoresCaseSpacesAndTheEditedJob()
        {
            var a = new Job { WorkOrder = "W-001" };
            var jobs = new List<Job> { a };
            Assert.Same(a, ScheduleRules.FindDuplicate(jobs, " w-001 ", except: null));
            Assert.Null(ScheduleRules.FindDuplicate(jobs, "W-001", except: a));
            Assert.Null(ScheduleRules.FindDuplicate(jobs, "W-002", except: null));
        }

        [Fact]
        public void Merge_UpdatesSameWorkOrderKeepingIdAndAddsNew()
        {
            var old = new Job { WorkOrder = "W-001", Model = "舊" };
            var keep = new Job { WorkOrder = "W-009" };
            var existing = new List<Job> { old, keep };
            var imported = new[] { new Job { WorkOrder = "w-001", Model = "新" }, new Job { WorkOrder = "W-002" } };

            var (added, updated) = ScheduleRules.Merge(existing, imported, replace: false);

            Assert.Equal((1, 1), (added, updated));
            Assert.Equal(3, existing.Count);
            Assert.Equal("新", existing[0].Model);
            Assert.Equal(old.Id, existing[0].Id);
            Assert.Same(keep, existing[1]);
        }

        [Fact]
        public void Merge_ReplaceClearsExisting()
        {
            var existing = new List<Job> { new() { WorkOrder = "W-001" } };
            var (added, updated) = ScheduleRules.Merge(existing, new[] { new Job { WorkOrder = "W-002" } }, replace: true);
            Assert.Equal((1, 0), (added, updated));
            Assert.Equal("W-002", Assert.Single(existing).WorkOrder);
        }

        // ---------- 清單顯示 ----------

        [Fact]
        public void JobRow_ShowsShortDatesAndDashes()
        {
            var row = new JobRow(new Job
            {
                WorkOrder = "W-1", Quantity = 1200,
                DeliveryPlan = D(10, 9), MaterialPlan = new DateTime(2025, 12, 30), MaterialActual = new DateTime(2026, 1, 2),
            }, Today);

            Assert.Equal("1,200", row.Quantity);
            Assert.Equal("—", row.Customer);
            Assert.True(row.NoCert);
            Assert.Equal("10/09", row.Delivery.PlanText);
            Assert.Equal("—", row.Delivery.ActualText);
            Assert.Equal(MilestoneState.Soon, row.Delivery.State);
            Assert.Equal("25/12/30", row.Material.PlanText);
            Assert.Equal(MilestoneState.Late, row.Material.State);
            Assert.Contains("晚了 3 天", row.Material.ToolTip);
        }
    }
}
