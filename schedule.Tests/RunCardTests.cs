using System.IO;
using schedule.Models;
using schedule.Services;

namespace schedule.Tests
{
    /// <summary>流程卡（Run Card）：預設流程、站別日期連動、進度、暫停 / 異常、複製、Excel、條碼。</summary>
    public sealed class RunCardTests : IDisposable
    {
        private static readonly DateTime Today = new(2026, 10, 6);

        private static DateTime D(int month, int day) => new(2026, month, day);

        private readonly string _root = Path.Combine(Path.GetTempPath(), "WorkSchedule-Tests-" + Guid.NewGuid().ToString("N"));

        public RunCardTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private static Job NewJob(Action<Job>? setup = null)
        {
            var job = new Job { WorkOrder = "A-1", Model = "MCC", Quantity = 1 };
            setup?.Invoke(job);
            RunCardRules.Ensure(job);
            return job;
        }

        // ---------- 預設內容與補齊 ----------

        [Fact]
        public void Ensure_OldJob_GetsDefaultStepsAndChecks()
        {
            var job = new Job { Steps = null!, Checks = null!, Issues = null! };
            RunCardRules.Ensure(job);

            Assert.Equal(8, job.Steps.Count);
            Assert.Equal(new[] { StepLink.Material, StepLink.Wiring, StepLink.Dispatch, StepLink.Delivery },
                job.Steps.Where(s => s.Link != StepLink.None).Select(s => s.Link));
            Assert.NotEmpty(job.Checks);
            Assert.Empty(job.Issues);
        }

        [Fact]
        public void Ensure_PutsBackMissingLinkedStep_InOrder()
        {
            var job = new Job
            {
                Steps = new()
                {
                    new() { Name = "材料入場", Link = StepLink.Material },
                    new() { Name = "自訂", },
                    new() { Name = "交貨", Link = StepLink.Delivery },
                },
            };
            RunCardRules.Ensure(job);
            Assert.Equal(new[] { "材料入場", "自訂", "配電", "出料", "交貨" }, job.Steps.Select(s => s.Name));
        }

        [Fact]
        public void Ensure_DuplicateLink_KeepsOnlyFirst()
        {
            var job = new Job
            {
                Steps = new()
                {
                    new() { Name = "配電一", Link = StepLink.Wiring },
                    new() { Name = "配電二", Link = StepLink.Wiring },
                },
            };
            RunCardRules.Ensure(job);
            Assert.Equal(StepLink.None, job.Steps.Single(s => s.Name == "配電二").Link);
            Assert.Single(job.Steps, s => s.Link == StepLink.Wiring);
        }

        [Fact]
        public void Ensure_CertChecks_AddedWhenFlagged()
        {
            var job = NewJob();
            Assert.DoesNotContain(job.Checks, c => c.Name == "CE 文件");
            job.HasCE = true;
            RunCardRules.Ensure(job);
            RunCardRules.Ensure(job);
            Assert.Single(job.Checks, c => c.Name == "CE 文件");
        }

        // ---------- 站別日期連動 ----------

        [Fact]
        public void LinkedStep_ReadsAndWritesJobDates()
        {
            var job = NewJob(j => j.WiringPlan = D(10, 10));
            var wiring = job.Steps.Single(s => s.Link == StepLink.Wiring);
            Assert.Equal(D(10, 10), RunCardRules.PlanOf(job, wiring));

            RunCardRules.SetDone(job, wiring, D(10, 9));
            Assert.Equal(D(10, 9), job.WiringActual);
            Assert.Null(wiring.Done);

            var custom = job.Steps.First(s => s.Link == StepLink.None);
            RunCardRules.SetPlan(job, custom, D(10, 1));
            Assert.Equal(D(10, 1), custom.Plan);
        }

        // ---------- 狀態、進度、目前站別 ----------

        [Fact]
        public void StepState_CoversAllCases()
        {
            Assert.Equal(StepState.Skipped, RunCardRules.State(D(10, 1), null, null, skipped: true, Today));
            Assert.Equal(StepState.Done, RunCardRules.State(D(10, 9), null, D(10, 5), false, Today));
            Assert.Equal(StepState.Late, RunCardRules.State(D(10, 1), null, D(10, 5), false, Today));
            Assert.Equal(StepState.Overdue, RunCardRules.State(D(10, 1), D(9, 30), null, false, Today));
            Assert.Equal(StepState.InProgress, RunCardRules.State(D(10, 30), D(10, 1), null, false, Today));
            Assert.Equal(StepState.Soon, RunCardRules.State(D(10, 8), null, null, false, Today));
            Assert.Equal(StepState.Pending, RunCardRules.State(null, null, null, false, Today));
        }

        [Fact]
        public void Progress_CountsDoneAndSkipped_CurrentIsFirstUnfinished()
        {
            var job = NewJob(j => j.MaterialActual = D(10, 1));
            job.Steps[0].Done = D(9, 30);   // 圖面確認
            job.Steps[2].Skipped = true;    // 箱體組立

            Assert.Equal((3, 8), RunCardRules.Progress(job));
            Assert.Equal("配電", RunCardRules.CurrentStep(job)!.Name);
            Assert.Equal("配電 · 3/8", RunCardRules.ProgressText(job));

            foreach (var s in job.Steps) s.Skipped = true;
            Assert.Null(RunCardRules.CurrentStep(job));
            Assert.Equal("全部完成", RunCardRules.ProgressText(job));
        }

        [Fact]
        public void Progress_EarlierStepsWithoutDates_CountAsPassed()
        {
            // 舊資料：只有工令上的日期，配電已完成 → 前面沒填的站當成已過站
            var job = NewJob(j => j.WiringActual = D(10, 3));
            Assert.Equal("自主檢查 · 4/8", RunCardRules.ProgressText(job));
            Assert.Equal(new[] { true, true, true, true, false, false, false, false }, RunCardRules.Passed(job));

            job.DeliveryActual = D(10, 9); // 交貨了就全部完成
            Assert.Equal("全部完成", RunCardRules.ProgressText(job));
            Assert.Equal((8, 8), RunCardRules.Progress(job));
        }

        [Fact]
        public void StepOrderWarning_ChecksPlansInRoutingOrder()
        {
            var job = NewJob(j => j.WiringPlan = D(10, 10));
            job.Steps[0].Plan = D(10, 1);
            Assert.Null(RunCardRules.StepOrderWarning(job));

            job.Steps[2].Plan = D(10, 12); // 箱體組立比配電晚
            Assert.Equal("「箱體組立」的預計日期比下一站「配電」晚。", RunCardRules.StepOrderWarning(job));

            job.Steps[2].Skipped = true;   // 略過的站不比較
            Assert.Null(RunCardRules.StepOrderWarning(job));
        }

        // ---------- 暫停 / 異常 ----------

        [Fact]
        public void Attention_HoldOpenIssueOrFailedCheck()
        {
            var job = NewJob();
            Assert.False(RunCardRules.NeedsAttention(job));

            job.HoldReason = "等客戶確認圖面";
            Assert.True(RunCardRules.IsOnHold(job));
            Assert.Contains("暫停：等客戶確認圖面", RunCardRules.AttentionReasons(job));
            job.DeliveryActual = D(10, 1);
            Assert.False(RunCardRules.IsOnHold(job)); // 已交貨就不算暫停
            job.DeliveryActual = null;
            job.HoldReason = "";

            job.Issues.Add(new IssueRecord { Description = "缺斷路器" });
            Assert.True(ScheduleRules.Matches(job, JobFilter.Attention, Today));
            job.Issues[0].Closed = D(10, 5);
            Assert.False(RunCardRules.NeedsAttention(job));

            job.Checks[0].Result = CheckResult.Fail;
            Assert.Equal(new[] { "1 項檢驗不合格" }, RunCardRules.AttentionReasons(job));
        }

        [Fact]
        public void CheckSummary_ListsCounts()
        {
            var job = new Job
            {
                Checks = new()
                {
                    new() { Result = CheckResult.Pass }, new() { Result = CheckResult.Pass },
                    new() { Result = CheckResult.Fail }, new() { Result = CheckResult.Pending },
                },
            };
            Assert.Equal("2 項合格、1 項不合格、1 項未檢", RunCardRules.CheckSummary(job));
        }

        // ---------- 清單：搜尋、排序、看板 ----------

        [Fact]
        public void Search_MatchesNewFieldsAndIssues()
        {
            var job = NewJob(j => { j.ProjectName = "台北廠擴建"; j.OrderNo = "PO-778"; j.DrawingNo = "D-12 rev.B"; j.Sales = "王業務"; });
            job.Issues.Add(new IssueRecord { Description = "等銅排" });
            foreach (var keyword in new[] { "擴建", "po-778", "rev.b", "王業務", "銅排" })
                Assert.True(ScheduleRules.MatchesSearch(job, keyword), keyword);
            Assert.False(ScheduleRules.MatchesSearch(job, "配電")); // 站別名稱不算（每張都有）
        }

        [Fact]
        public void Sort_ByPriority_UrgentFirst_DeliveredLast()
        {
            var jobs = new[]
            {
                new Job { WorkOrder = "N", DeliveryPlan = D(10, 1) },
                new Job { WorkOrder = "U", Priority = JobPriority.Urgent, DeliveryPlan = D(10, 20) },
                new Job { WorkOrder = "R", Priority = JobPriority.Rush, DeliveryPlan = D(10, 30) },
                new Job { WorkOrder = "D", Priority = JobPriority.Rush, DeliveryActual = D(10, 2) },
            };
            Assert.Equal(new[] { "R", "U", "N", "D" }, ScheduleRules.Sort(jobs, JobSort.Priority).Select(j => j.WorkOrder));
        }

        [Fact]
        public void WorkInProcess_CountsCurrentStationOfUndeliveredJobs()
        {
            var a = NewJob(j => j.WorkOrder = "A");
            var b = NewJob(j => { j.WorkOrder = "B"; j.MaterialActual = D(10, 1); });
            b.Steps[0].Done = D(9, 30);
            var done = NewJob(j => { j.WorkOrder = "C"; j.DeliveryActual = D(10, 2); });

            var wip = RunCardRules.WorkInProcess(new[] { a, b, done });
            Assert.Equal(new[] { new CountItem("圖面確認", 1), new CountItem("箱體組立", 1) }, wip);

            var stats = DashboardStats.Compute(new[] { a, b, done }, Today);
            Assert.Equal(wip, stats.Stations);
        }

        // ---------- 複製 ----------

        [Fact]
        public void CopyAsNew_KeepsRoutingButClearsProgress()
        {
            var job = NewJob(j => { j.DeliveryPlan = D(10, 20); j.HoldReason = "等料"; j.Sales = "王"; });
            job.Steps.Insert(1, new RunStep { Name = "客製加工", Owner = "阿明", Done = D(10, 1), Signer = "主管" });
            job.Checks[0].Result = CheckResult.Pass;
            job.Issues.Add(new IssueRecord { Description = "x" });

            var copy = RunCardRules.CopyAsNew(job);
            Assert.NotEqual(job.Id, copy.Id);
            Assert.Equal("王", copy.Sales);
            Assert.Null(copy.DeliveryPlan);
            Assert.Equal("", copy.HoldReason);
            Assert.Empty(copy.Issues);
            var step = copy.Steps[1];
            Assert.Equal(("客製加工", "阿明", (DateTime?)null, ""), (step.Name, step.Owner, step.Done, step.Signer));
            Assert.All(copy.Checks, c => Assert.Equal(CheckResult.Pending, c.Result));
            // 原本那筆沒被動到
            Assert.Equal(D(10, 1), job.Steps[1].Done);
        }

        // ---------- 存檔 ----------

        [Fact]
        public void DataStore_RoundTripsRunCard()
        {
            var path = Path.Combine(_root, "schedule.json");
            var job = NewJob(j => { j.Priority = JobPriority.Rush; j.HoldReason = "等料"; });
            job.Steps[0].Owner = "阿明";
            job.Checks[0].Result = CheckResult.Fail;
            job.Issues.Add(new IssueRecord { Date = D(10, 3), Description = "缺料" });
            DataStore.Save(path, new ScheduleData { Jobs = { job } });

            var loaded = DataStore.Load(path).Jobs.Single();
            Assert.Equal(JobPriority.Rush, loaded.Priority);
            Assert.Equal("等料", loaded.HoldReason);
            Assert.Equal("阿明", loaded.Steps[0].Owner);
            Assert.Equal(CheckResult.Fail, loaded.Checks[0].Result);
            Assert.Equal("缺料", loaded.Issues.Single().Description);
        }

        // ---------- Excel ----------

        [Fact]
        public void Excel_RoundTripsRunCardSheets()
        {
            var path = Path.Combine(_root, "rc.xlsx");
            var job = NewJob(j =>
            {
                j.ProjectName = "擴建"; j.OrderNo = "PO-1"; j.DrawingNo = "D-1"; j.Sales = "王"; j.Priority = JobPriority.Urgent;
                j.HoldReason = "等料"; j.WiringPlan = D(10, 10);
            });
            job.Steps.Insert(1, new RunStep { Name = "客製", Owner = "阿明", Plan = D(10, 2), Done = D(10, 3), Skipped = false, Note = "n" });
            job.Steps[0].Skipped = true;
            job.Checks[0].Result = CheckResult.Fail;
            job.Checks[0].Measured = "0.5 MΩ";
            job.Issues.Add(new IssueRecord { Date = D(10, 4), Type = "缺料", Description = "缺銅排", Owner = "小陳", Closed = D(10, 5) });
            ExcelService.Export(new[] { job }, path, Today);

            var result = ExcelService.ImportWorkbook(path);
            var back = result.Jobs.Single();
            Assert.True(result.HasSteps && result.HasChecks && result.HasIssues);
            Assert.Equal(("擴建", "PO-1", "D-1", "王", JobPriority.Urgent, "等料"),
                (back.ProjectName, back.OrderNo, back.DrawingNo, back.Sales, back.Priority, back.HoldReason));
            Assert.Equal(job.Steps.Select(s => (s.Name, s.Link, s.Skipped)), back.Steps.Select(s => (s.Name, s.Link, s.Skipped)));
            var custom = back.Steps[1];
            Assert.Equal(("阿明", (DateTime?)D(10, 2), (DateTime?)D(10, 3), "n"), (custom.Owner, custom.Plan, custom.Done, custom.Note));
            Assert.Null(back.Steps.Single(s => s.Link == StepLink.Wiring).Plan); // 連動的日期在工令上
            Assert.Equal(D(10, 10), back.WiringPlan);
            Assert.Equal((CheckResult.Fail, "0.5 MΩ"), (back.Checks[0].Result, back.Checks[0].Measured));
            Assert.Equal(job.Checks.Count, back.Checks.Count);
            Assert.Equal(("缺料", "缺銅排", "小陳", (DateTime?)D(10, 5)), (back.Issues[0].Type, back.Issues[0].Description, back.Issues[0].Owner, back.Issues[0].Closed));
        }

        [Fact]
        public void Merge_OldExcelWithoutRunCard_KeepsExistingRunCard()
        {
            var existing = NewJob(j => { j.WorkOrder = "B"; j.ProjectName = "擴建"; j.Priority = JobPriority.Rush; });
            existing.Steps[0].Owner = "阿明";
            existing.Issues.Add(new IssueRecord { Description = "缺料" });

            // 舊版匯出的檔案：只有排程表、沒有新欄位
            var path = Path.Combine(_root, "old.xlsx");
            using (var wb = new ClosedXML.Excel.XLWorkbook())
            {
                var ws = wb.Worksheets.Add(ExcelService.SheetName);
                ws.Cell(1, 1).Value = "工令";
                ws.Cell(1, 2).Value = "機種";
                ws.Cell(2, 1).Value = "B";
                ws.Cell(2, 2).Value = "新機種";
                wb.SaveAs(path);
            }
            var result = ExcelService.ImportWorkbook(path);
            var list = new List<Job> { existing };
            ScheduleRules.Merge(list, result.Jobs, replace: false, keep: result.KeepMissing);

            var merged = list.Single();
            Assert.Equal("新機種", merged.Model);
            Assert.Equal(("擴建", JobPriority.Rush), (merged.ProjectName, merged.Priority));
            Assert.Equal("阿明", merged.Steps[0].Owner);
            Assert.Single(merged.Issues);
        }

        [Theory]
        [InlineData("合格", CheckResult.Pass)]
        [InlineData("ok", CheckResult.Pass)]
        [InlineData("NG", CheckResult.Fail)]
        [InlineData("不適用", CheckResult.NotApplicable)]
        [InlineData("", CheckResult.Pending)]
        public void ParseResult_AcceptsCommonWords(string text, CheckResult expected) =>
            Assert.Equal(expected, ExcelService.ParseResult(text));

        // ---------- 條碼 ----------

        [Fact]
        public void Code39_TableIsValid_AndEncodesWithStartStop()
        {
            Assert.True(Code39.CanEncode("w2-10a"));
            Assert.False(Code39.CanEncode("工令1"));
            Assert.False(Code39.CanEncode("A*B"));
            Assert.Null(Code39.Encode(""));

            var e = Code39.Encode("AB")!;
            // *AB* 共 4 個字元，每個 9 個元素，加上 3 個字元間隔
            Assert.Equal(4 * 9 + 3, e.Count);
            Assert.True(e[0].Bar);
            // 每個字元剛好 3 個寬元素
            for (int c = 0; c < 4; c++)
                Assert.Equal(3, e.Skip(c * 10).Take(9).Count(x => x.Wide));
        }
    }
}
