using System.IO;
using schedule.Models;
using schedule.Services;

namespace schedule.Tests
{
    /// <summary>出料紀錄：同步分組、複製到同組機台、料號帶入、搜尋、Excel、存檔。</summary>
    public sealed class DispatchSyncTests : IDisposable
    {
        private static readonly DateTime Today = new(2026, 10, 6);

        private readonly string _root = Path.Combine(Path.GetTempPath(), "WorkSchedule-Tests-" + Guid.NewGuid().ToString("N"));

        public DispatchSyncTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private static Job NewJob(string workOrder, string model = "MCC", params DispatchItem[] items)
        {
            var job = new Job { WorkOrder = workOrder, Model = model, Quantity = 1, Dispatches = items.ToList() };
            RunCardRules.Ensure(job);
            return job;
        }

        private static DispatchItem Item(string partNo, string name = "斷路器", string spec = "100A 3P") =>
            new() { PartNo = partNo, Name = name, Spec = spec, Quantity = "2", Unit = "只", Date = new DateTime(2026, 10, 5) };

        [Fact]
        public void Apply_CopiesItemsToCheckedJobs_AndGroupsThem()
        {
            var a = NewJob("A", items: Item("P-1"));
            var b = NewJob("B");
            var c = NewJob("C");
            var jobs = new List<Job> { a, b, c };

            var changed = DispatchSync.Apply(jobs, a, new[] { b.Id, c.Id });

            Assert.Equal(new[] { b, c }, changed);
            Assert.NotEmpty(a.DispatchGroup);
            Assert.All(jobs, j => Assert.Equal(a.DispatchGroup, j.DispatchGroup));
            Assert.Equal("P-1", b.Dispatches.Single().PartNo);
            Assert.NotSame(a.Dispatches[0], b.Dispatches[0]); // 複本，不是同一個物件
            Assert.Equal(new[] { b, c }, DispatchSync.Partners(jobs, a));
        }

        [Fact]
        public void Apply_EditOneMember_UpdatesTheOthers()
        {
            var a = NewJob("A", items: Item("P-1"));
            var b = NewJob("B");
            var jobs = new List<Job> { a, b };
            DispatchSync.Apply(jobs, a, new[] { b.Id });

            // 在 B 的流程卡改（存檔產生新的物件，同樣的 Id 與群組）
            var b2 = RunCardRules.Clone(b);
            b2.Dispatches.Add(Item("P-2", "端子"));
            jobs[1] = b2;
            var changed = DispatchSync.Apply(jobs, b2, new[] { a.Id });

            Assert.Equal(new[] { a }, changed);
            Assert.Equal(new[] { "P-1", "P-2" }, a.Dispatches.Select(d => d.PartNo));
        }

        [Fact]
        public void Apply_Unchecked_LeavesGroup_KeepsOwnCopy()
        {
            var a = NewJob("A", items: Item("P-1"));
            var b = NewJob("B");
            var c = NewJob("C");
            var jobs = new List<Job> { a, b, c };
            DispatchSync.Apply(jobs, a, new[] { b.Id, c.Id });

            DispatchSync.Apply(jobs, a, new[] { b.Id }); // 取消勾 C

            Assert.Equal("", c.DispatchGroup);
            Assert.Equal("P-1", c.Dispatches.Single().PartNo); // 原本複製過去的留著
            Assert.Equal(a.DispatchGroup, b.DispatchGroup);

            DispatchSync.Apply(jobs, a, Array.Empty<string>()); // 全部不勾
            Assert.All(jobs, j => Assert.Equal("", j.DispatchGroup));
        }

        [Fact]
        public void Apply_LeavingThreeGroup_OthersStaySynced()
        {
            var a = NewJob("A", items: Item("P-1"));
            var b = NewJob("B");
            var c = NewJob("C");
            var jobs = new List<Job> { a, b, c };
            DispatchSync.Apply(jobs, a, new[] { b.Id, c.Id });

            DispatchSync.Apply(jobs, a, Array.Empty<string>());

            Assert.Equal("", a.DispatchGroup);
            Assert.NotEmpty(b.DispatchGroup);
            Assert.Equal(b.DispatchGroup, c.DispatchGroup);
        }

        [Fact]
        public void Apply_MovesJobOutOfItsOldGroup()
        {
            var a = NewJob("A", items: Item("P-1"));
            var b = NewJob("B", items: Item("Q-1"));
            var c = NewJob("C");
            var jobs = new List<Job> { a, b, c };
            DispatchSync.Apply(jobs, b, new[] { c.Id }); // B、C 一組

            DispatchSync.Apply(jobs, a, new[] { b.Id });  // A 把 B 拉過來

            Assert.Equal(a.DispatchGroup, b.DispatchGroup);
            Assert.Equal("", c.DispatchGroup);            // C 只剩自己，不算同步
            Assert.Equal("P-1", b.Dispatches.Single().PartNo);
            Assert.Equal("Q-1", c.Dispatches.Single().PartNo);
        }

        [Fact]
        public void WouldOverwrite_OnlyNewPartnersWithDifferentItems()
        {
            var a = NewJob("A", items: Item("P-1"));
            var empty = NewJob("B");
            var same = NewJob("C", items: Item("P-1"));
            var different = NewJob("D", items: Item("X-9"));
            var jobs = new List<Job> { a, empty, same, different };

            var result = DispatchSync.WouldOverwrite(jobs, a, new[] { empty.Id, same.Id, different.Id }, a.Dispatches);
            Assert.Equal(new[] { different }, result);

            // 已經同組的不用再問
            DispatchSync.Apply(jobs, a, new[] { different.Id });
            different.Dispatches.Add(Item("Z"));
            Assert.Empty(DispatchSync.WouldOverwrite(jobs, a, new[] { different.Id }, a.Dispatches));
        }

        [Fact]
        public void Normalize_SingleMemberGroupIsCleared()
        {
            var a = NewJob("A");
            var b = NewJob("B");
            var jobs = new List<Job> { a, b };
            DispatchSync.Apply(jobs, a, new[] { b.Id });

            jobs.Remove(b); // 刪掉工令
            DispatchSync.Normalize(jobs);

            Assert.Equal("", a.DispatchGroup);
        }

        [Fact]
        public void Catalog_LatestJobWins_AndValuesAreDistinct()
        {
            var jobs = new List<Job>
            {
                NewJob("A", items: Item("P-1", "舊名稱", "舊規格")),
                NewJob("B", items: new[] { Item("p-1", "斷路器", "100A"), Item("P-2", "端子台", "") }),
            };

            var catalog = DispatchSync.Catalog(jobs);

            Assert.Equal(("斷路器", "100A", "只"), (catalog["P-1"].Name, catalog["P-1"].Spec, catalog["P-1"].Unit));
            Assert.Equal("端子台", catalog["P-2"].Name);
            Assert.Equal(new[] { "p-1", "P-2" }, DispatchSync.Values(jobs, d => d.PartNo)); // 不分大小寫只留一個，新的在前
        }

        [Fact]
        public void Search_FindsPartNoNameAndSpec()
        {
            var job = NewJob("A", items: Item("NFB-100", "無熔絲開關", "3P 100AF"));
            Assert.True(ScheduleRules.MatchesSearch(job, "nfb-100"));
            Assert.True(ScheduleRules.MatchesSearch(job, "無熔絲"));
            Assert.True(ScheduleRules.MatchesSearch(job, "100AF"));
            Assert.False(ScheduleRules.MatchesSearch(job, "電磁接觸器"));
        }

        [Fact]
        public void CopyAsNew_KeepsItemsWithoutDatesOrGroup()
        {
            var a = NewJob("A", items: Item("P-1"));
            var b = NewJob("B");
            DispatchSync.Apply(new List<Job> { a, b }, a, new[] { b.Id });

            var copy = RunCardRules.CopyAsNew(a);

            Assert.Equal("P-1", copy.Dispatches.Single().PartNo);
            Assert.Null(copy.Dispatches[0].Date);
            Assert.Equal("", copy.DispatchGroup);
        }

        [Fact]
        public void Ensure_OldJobWithoutDispatches()
        {
            var job = new Job { Dispatches = null!, DispatchGroup = null! };
            RunCardRules.Ensure(job);
            Assert.Empty(job.Dispatches);
            Assert.Equal("", job.DispatchGroup);
        }

        [Fact]
        public void DataStore_RoundTripsDispatches()
        {
            var a = NewJob("A", items: Item("P-1"));
            var b = NewJob("B");
            DispatchSync.Apply(new List<Job> { a, b }, a, new[] { b.Id });
            var path = Path.Combine(_root, "schedule.json");
            var data = new ScheduleData { Jobs = { a, b } };
            DataStore.Save(path, data);

            var loaded = DataStore.Load(path).Jobs;
            Assert.Equal("P-1", loaded[1].Dispatches.Single().PartNo);
            Assert.Equal(("100A 3P", "2", "只"), (loaded[0].Dispatches[0].Spec, loaded[0].Dispatches[0].Quantity, loaded[0].Dispatches[0].Unit));
            Assert.Equal(loaded[0].DispatchGroup, loaded[1].DispatchGroup);
            Assert.NotEmpty(loaded[0].DispatchGroup);
        }

        // ---------- Excel ----------

        [Fact]
        public void Excel_RoundTripsItemsAndSync()
        {
            var a = NewJob("A", items: new[] { Item("P-1"), Item("'P-2", "銅排", "30x5"), new DispatchItem { PartNo = "P-3", Name = "線材", Quantity = "一批" } });
            var b = NewJob("B");
            var c = NewJob("C");
            var jobs = new List<Job> { a, b, c };
            DispatchSync.Apply(jobs, a, new[] { b.Id });
            var path = Path.Combine(_root, "d.xlsx");
            ExcelService.Export(jobs, path, Today);

            var result = ExcelService.ImportWorkbook(path);
            Assert.True(result.HasDispatches);
            var back = result.Jobs.Single(j => j.WorkOrder == "A");
            Assert.Equal(new[] { "P-1", "'P-2", "P-3" }, back.Dispatches.Select(d => d.PartNo));
            Assert.Equal(("斷路器", "100A 3P", "2", "只", (DateTime?)new DateTime(2026, 10, 5)),
                (back.Dispatches[0].Name, back.Dispatches[0].Spec, back.Dispatches[0].Quantity, back.Dispatches[0].Unit, back.Dispatches[0].Date));
            Assert.Equal("一批", back.Dispatches[2].Quantity);
            Assert.Equal(new[] { "B" }, result.Sync[back]);

            // 完全以 Excel 為準：同步分組照「出料同步」欄重建
            var target = new List<Job>();
            ScheduleRules.Merge(target, result.Jobs, replace: true);
            DispatchSync.ApplyNames(target, result.Sync);
            var (ta, tb, tc) = (target.Single(j => j.WorkOrder == "A"), target.Single(j => j.WorkOrder == "B"), target.Single(j => j.WorkOrder == "C"));
            Assert.NotEmpty(ta.DispatchGroup);
            Assert.Equal(ta.DispatchGroup, tb.DispatchGroup);
            Assert.Equal("", tc.DispatchGroup);
        }

        [Fact]
        public void Merge_OldExcelWithoutDispatches_KeepsExisting()
        {
            var a = NewJob("A", items: Item("P-1"));
            var b = NewJob("B");
            var existing = new List<Job> { a, b };
            DispatchSync.Apply(existing, a, new[] { b.Id });
            var group = a.DispatchGroup;

            // 舊版檔案：沒有出料紀錄工作表，也沒有出料同步欄
            var path = Path.Combine(_root, "old.xlsx");
            using (var wb = new ClosedXML.Excel.XLWorkbook())
            {
                var ws = wb.Worksheets.Add(ExcelService.SheetName);
                ws.Cell(1, 1).Value = "工令";
                ws.Cell(1, 2).Value = "機種";
                ws.Cell(2, 1).Value = "A";
                ws.Cell(2, 2).Value = "MCC-2";
                wb.SaveAs(path);
            }
            var result = ExcelService.ImportWorkbook(path);
            Assert.False(result.HasDispatches);
            Assert.Empty(result.Sync);
            ScheduleRules.Merge(existing, result.Jobs, replace: false, keep: result.KeepMissing);

            var merged = existing.Single(j => j.WorkOrder == "A");
            Assert.Equal("MCC-2", merged.Model);
            Assert.Equal("P-1", merged.Dispatches.Single().PartNo);
            Assert.Equal(group, merged.DispatchGroup);
        }

        [Fact]
        public void ApplyNames_JoinsChainsAndClearsBlank()
        {
            var a = NewJob("A");
            var b = NewJob("B");
            var c = NewJob("C");
            var d = NewJob("D");
            var jobs = new List<Job> { a, b, c, d };
            DispatchSync.Apply(jobs, c, new[] { d.Id }); // 原本 C、D 一組

            DispatchSync.ApplyNames(jobs, new Dictionary<Job, List<string>>
            {
                [a] = new() { "b" },   // 不分大小寫
                [b] = new() { "C" },   // A-B、B-C 串成一組
                [d] = new(),           // D 這次空白：退出
                [c] = new() { "不存在的工令" },
            });

            Assert.NotEmpty(a.DispatchGroup);
            Assert.Equal(a.DispatchGroup, b.DispatchGroup);
            Assert.Equal(a.DispatchGroup, c.DispatchGroup);
            Assert.Equal("", d.DispatchGroup);
        }
    }
}
