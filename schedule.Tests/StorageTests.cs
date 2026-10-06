using System.IO;
using ClosedXML.Excel;
using schedule.Models;
using schedule.Services;

namespace schedule.Tests
{
    /// <summary>存檔（JSON）與 Excel 匯出 / 匯入的自動測試，全部在暫存資料夾裡進行。</summary>
    public sealed class StorageTests : IDisposable
    {
        private static readonly DateTime Today = new(2026, 10, 6);

        private readonly string _root = Path.Combine(Path.GetTempPath(), "WorkSchedule-Tests-" + Guid.NewGuid().ToString("N"));

        public StorageTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private static List<Job> Sample() => new()
        {
            new Job
            {
                WorkOrder = "W2610-001", Model = "MCC-400", Quantity = 2, Customer = "台積電", HasCE = true,
                MaterialPlan = new DateTime(2026, 9, 26), MaterialActual = new DateTime(2026, 9, 27),
                WiringPlan = new DateTime(2026, 10, 3),
                DeliveryPlan = new DateTime(2026, 10, 11),
                InnerWiring = "阿明", OuterWiring = "小陳", Consumables = "公司", Note = "急件\n第二行",
            },
            new Job
            {
                WorkOrder = "'0012", Model = "PLC-200", Quantity = 1, HasCE = true, HasTS = true,
                DeliveryPlan = new DateTime(2026, 9, 1), DeliveryActual = new DateTime(2026, 9, 3),
            },
        };

        // ---------- JSON ----------

        [Fact]
        public void DataStore_RoundTrips()
        {
            var path = Path.Combine(_root, "schedule.json");
            var data = new ScheduleData { Jobs = Sample() };

            DataStore.Save(path, data);
            var loaded = DataStore.Load(path);

            Assert.Equal(2, loaded.Jobs.Count);
            var a = loaded.Jobs[0];
            Assert.Equal(data.Jobs[0].Id, a.Id);
            Assert.Equal("W2610-001", a.WorkOrder);
            Assert.Equal(new DateTime(2026, 9, 27), a.MaterialActual);
            Assert.Null(a.WiringActual);
            Assert.Equal("急件\n第二行", a.Note);
            Assert.True(loaded.Jobs[1].HasTS);
            Assert.False(File.Exists(path + ".tmp"));
            // 中文直接存成中文，用記事本打開看得懂
            Assert.Contains("台積電", File.ReadAllText(path));
        }

        [Fact]
        public void DataStore_MissingFile_ReturnsEmpty() =>
            Assert.Empty(DataStore.Load(Path.Combine(_root, "none.json")).Jobs);

        [Fact]
        public void DataStore_CorruptFile_IsBackedUpAndStartsEmpty()
        {
            var path = Path.Combine(_root, "schedule.json");
            File.WriteAllText(path, "{ 壞掉的檔案");

            Assert.Empty(DataStore.Load(path).Jobs);
            Assert.False(File.Exists(path));
            Assert.Single(Directory.GetFiles(_root, "schedule.json.corrupt-*"));
        }

        // ---------- Excel ----------

        [Fact]
        public void Excel_RoundTrips()
        {
            var path = Path.Combine(_root, "export.xlsx");
            var jobs = Sample();
            ExcelService.Export(jobs, path, Today);

            var (imported, skipped) = ExcelService.Import(path);

            Assert.Equal(0, skipped);
            Assert.Equal(2, imported.Count);
            // 匯出依預計交期排序：已交貨的 '0012（9/1）在前
            var b = imported[0];
            Assert.Equal("'0012", b.WorkOrder);
            Assert.True(b.HasCE && b.HasTS);
            Assert.Equal(new DateTime(2026, 9, 3), b.DeliveryActual);

            var a = imported[1];
            Assert.Equal("W2610-001", a.WorkOrder);
            Assert.Equal("MCC-400", a.Model);
            Assert.Equal(2, a.Quantity);
            Assert.Equal("台積電", a.Customer);
            Assert.True(a.HasCE);
            Assert.False(a.HasTS);
            Assert.Equal(new DateTime(2026, 9, 26), a.MaterialPlan);
            Assert.Equal(new DateTime(2026, 9, 27), a.MaterialActual);
            Assert.Equal(new DateTime(2026, 10, 3), a.WiringPlan);
            Assert.Null(a.WiringActual);
            Assert.Equal(new DateTime(2026, 10, 11), a.DeliveryPlan);
            Assert.Null(a.DeliveryActual);
            Assert.Equal(("阿明", "小陳", "公司"), (a.InnerWiring, a.OuterWiring, a.Consumables));
            Assert.Equal("急件\n第二行", a.Note);
        }

        [Fact]
        public void Excel_ExportWritesStatusAndRealDates()
        {
            var path = Path.Combine(_root, "export.xlsx");
            ExcelService.Export(Sample(), path, Today);

            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheet(ExcelService.SheetName);
            Assert.Equal(ExcelService.Headers, ws.Row(1).CellsUsed().Select(c => c.GetString()).ToArray());
            Assert.Equal("已完成", ws.Cell(2, ExcelService.Col("狀態")).GetString());
            Assert.Equal("逾期", ws.Cell(3, ExcelService.Col("狀態")).GetString()); // 預計配電 10/3 還沒做
            Assert.Equal(XLDataType.DateTime, ws.Cell(3, ExcelService.Col("預計交期")).DataType);
        }

        [Fact]
        public void Excel_ImportFindsColumnsByHeaderAndParsesLooseValues()
        {
            var path = Path.Combine(_root, "manual.xlsx");
            using (var wb = new XLWorkbook())
            {
                var ws = wb.Worksheets.Add("工作表1");
                string[] headers = { "預計交期", "工令", "數量", "CE", "TS", "實際交貨", "其他欄位" };
                for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
                // 文字日期、只有月日、全形數字、「是」
                ws.Cell(2, 1).Value = "2026/11/5"; ws.Cell(2, 2).Value = "A-1"; ws.Cell(2, 3).Value = "３"; ws.Cell(2, 4).Value = "是";
                // Excel 日期序號（沒設日期格式）
                ws.Cell(3, 1).Value = new DateTime(2026, 12, 1).ToOADate(); ws.Cell(3, 2).Value = "A-2"; ws.Cell(3, 5).Value = "V";
                // 空白列略過、不計入 skipped
                // 沒有工令 → 略過
                ws.Cell(5, 1).Value = "2026/1/1"; ws.Cell(5, 3).Value = 1;
                // 日期看不懂 → 略過
                ws.Cell(6, 1).Value = "下週"; ws.Cell(6, 2).Value = "A-3";
                wb.SaveAs(path);
            }

            var (jobs, skipped) = ExcelService.Import(path);

            Assert.Equal(2, skipped);
            Assert.Equal(new[] { "A-1", "A-2" }, jobs.Select(j => j.WorkOrder));
            Assert.Equal(new DateTime(2026, 11, 5), jobs[0].DeliveryPlan);
            Assert.Equal(3, jobs[0].Quantity);
            Assert.True(jobs[0].HasCE);
            Assert.False(jobs[0].HasTS);
            Assert.Equal(new DateTime(2026, 12, 1), jobs[1].DeliveryPlan);
            Assert.True(jobs[1].HasTS);
            Assert.Equal("", jobs[1].Model);
        }

        [Fact]
        public void Excel_ImportWithoutWorkOrderColumn_ReturnsNothing()
        {
            var path = Path.Combine(_root, "other.xlsx");
            using (var wb = new XLWorkbook())
            {
                var ws = wb.Worksheets.Add("x");
                ws.Cell(1, 1).Value = "名稱";
                ws.Cell(2, 1).Value = "abc";
                wb.SaveAs(path);
            }
            var (jobs, skipped) = ExcelService.Import(path);
            Assert.Empty(jobs);
            Assert.Equal(0, skipped);
        }

        [Theory]
        [InlineData("V", true)]
        [InlineData(" v ", true)]
        [InlineData("是", true)]
        [InlineData("有", true)]
        [InlineData("Yes", true)]
        [InlineData("1", true)]
        [InlineData("", false)]
        [InlineData("否", false)]
        [InlineData("無", false)]
        public void ParseYes(string text, bool expected) => Assert.Equal(expected, ExcelService.ParseYes(text));
    }
}
