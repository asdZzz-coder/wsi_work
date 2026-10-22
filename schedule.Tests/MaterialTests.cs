using System.IO;
using ClosedXML.Excel;
using schedule.Models;
using schedule.Services;

namespace schedule.Tests
{
    /// <summary>大倉庫：篩選、合併、貼上、出過的料、Excel、存檔。</summary>
    public sealed class MaterialTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "WorkSchedule-Tests-" + Guid.NewGuid().ToString("N"));

        public MaterialTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private static Material M(string partNo, string name, string spec = "", string category = "", string unit = "只") =>
            new() { PartNo = partNo, Name = name, Spec = spec, Category = category, Unit = unit };

        [Fact]
        public void Matches_AllKeywordsAndCategory()
        {
            var nfb = M("NFB-100", "無熔絲開關", "3P 100AF 75AT", "斷路器");
            Assert.True(MaterialRules.Matches(nfb, ""));
            Assert.True(MaterialRules.Matches(nfb, "nfb"));
            Assert.True(MaterialRules.Matches(nfb, "3P 75AT"));          // 兩個關鍵字都要符合
            Assert.False(MaterialRules.Matches(nfb, "3P 50AT"));
            Assert.True(MaterialRules.Matches(nfb, "開關", "斷路器"));
            Assert.False(MaterialRules.Matches(nfb, "開關", "端子"));
            Assert.True(MaterialRules.Matches(nfb, "  ", null));
        }

        [Fact]
        public void Sort_ByCategoryThenNaturalPartNo()
        {
            var sorted = MaterialRules.Sort(new[] { M("P-10", "a", category: "端子"), M("X", "b"), M("P-9", "c", category: "端子") })
                .Select(m => m.PartNo);
            Assert.Equal(new[] { "P-9", "P-10", "X" }, sorted); // 料號依數值；沒有分類的排最後
        }

        [Fact]
        public void Merge_AddsNewAndUpdatesSamePartNo_KeepsValuesWhenBlank()
        {
            var warehouse = new List<Material> { M("P-1", "舊名稱", "舊規格", "端子") };
            var (added, updated) = MaterialRules.Merge(warehouse, new[]
            {
                M(" p-1 ", "新名稱", "", ""),   // 同料號（不分大小寫）：名稱更新，規格 / 分類空白保留原本的
                M("P-2", "斷路器"),
                M("", ""),                      // 沒料號也沒名稱：略過
                M("", "銅排", "30x5"),           // 沒料號：用名稱 + 規格判斷
                M("", "銅排", "30x5"),
            });

            Assert.Equal((2, 1), (added, updated));
            Assert.Equal(3, warehouse.Count);
            Assert.Equal(("新名稱", "舊規格", "端子"), (warehouse[0].Name, warehouse[0].Spec, warehouse[0].Category));
        }

        [Fact]
        public void Find_ExceptSelf()
        {
            var a = M("P-1", "a");
            var list = new List<Material> { a, M("P-2", "b") };
            Assert.Null(MaterialRules.Find(list, M("P-1", "x"), except: a));
            Assert.NotNull(MaterialRules.Find(list, M("p-2", "x"), except: a));
        }

        [Fact]
        public void ParseTable_WithHeaders_AnyOrderAndAliases()
        {
            var items = MaterialRules.ParseTable("品名\t品號\t規格\t類別\r\n無熔絲開關\tNFB-100\t3P 100AF\t斷路器\r\n\r\n\t\t\t\r\n端子台\tTB-1\t\t端子\r\n");
            Assert.Equal(2, items.Count);
            Assert.Equal(("NFB-100", "無熔絲開關", "3P 100AF", "斷路器"), (items[0].PartNo, items[0].Name, items[0].Spec, items[0].Category));
            Assert.Equal("TB-1", items[1].PartNo);
        }

        [Fact]
        public void ParseTable_WithoutHeaders_UsesDefaultOrder()
        {
            var items = MaterialRules.ParseTable("NFB-100\t無熔絲開關\t3P 100AF\t只\t斷路器\n" + "TB-1\t端子台");
            Assert.Equal(("NFB-100", "無熔絲開關", "3P 100AF", "只", "斷路器"),
                (items[0].PartNo, items[0].Name, items[0].Spec, items[0].Unit, items[0].Category));
            Assert.Equal(("TB-1", "端子台", ""), (items[1].PartNo, items[1].Name, items[1].Spec));
            Assert.Empty(MaterialRules.ParseTable(""));
        }

        [Fact]
        public void FromDispatches_OnlyMissingPartNos()
        {
            var job = new Job
            {
                WorkOrder = "A",
                Dispatches =
                {
                    new DispatchItem { PartNo = "P-1", Name = "已在倉庫" },
                    new DispatchItem { PartNo = "P-2", Name = "端子台", Spec = "UK5N", Unit = "個" },
                    new DispatchItem { PartNo = "", Name = "沒有料號的不算" },
                },
            };
            var result = MaterialRules.FromDispatches(new[] { job }, new[] { M("p-1", "x") });
            Assert.Equal(("P-2", "端子台", "UK5N", "個"), (result.Single().PartNo, result[0].Name, result[0].Spec, result[0].Unit));
        }

        [Fact]
        public void ToDispatch_CopiesFieldsWithQuantityAndDate()
        {
            var item = MaterialRules.ToDispatch(M("P-1", "斷路器", "100A", unit: "只"), " 3 ", new DateTime(2026, 10, 15));
            Assert.Equal(("P-1", "斷路器", "100A", "只", "3", (DateTime?)new DateTime(2026, 10, 15)),
                (item.PartNo, item.Name, item.Spec, item.Unit, item.Quantity, item.Date));
        }

        [Fact]
        public void Excel_MaterialsRoundTrip_AndInsideMainWorkbook()
        {
            var materials = new List<Material> { M("NFB-100", "無熔絲開關", "3P 100AF", "斷路器"), M("'TB-1", "端子台", "", "端子", "個") };
            materials[0].Note = "常用";

            var path = Path.Combine(_root, "m.xlsx");
            ExcelService.ExportMaterials(materials, path);
            var back = ExcelService.ImportMaterials(path)!.OrderBy(m => m.PartNo == "NFB-100" ? 0 : 1).ToList(); // 匯出依分類排序
            Assert.Equal(new[] { "NFB-100", "'TB-1" }, back.Select(m => m.PartNo));
            Assert.Equal(("無熔絲開關", "3P 100AF", "只", "斷路器", "常用"), (back[0].Name, back[0].Spec, back[0].Unit, back[0].Category, back[0].Note));

            var main = Path.Combine(_root, "all.xlsx");
            ExcelService.Export(new[] { new Job { WorkOrder = "A" } }, main, new DateTime(2026, 10, 15), materials);
            var result = ExcelService.ImportWorkbook(main);
            Assert.Equal(2, result.Materials!.Count);

            ExcelService.Export(new[] { new Job { WorkOrder = "A" } }, main, new DateTime(2026, 10, 15));
            Assert.Null(ExcelService.ImportWorkbook(main).Materials); // 沒有大倉庫工作表
        }

        [Fact]
        public void ImportMaterials_FirstSheetWithAliasHeaders()
        {
            var path = Path.Combine(_root, "erp.xlsx");
            using (var wb = new XLWorkbook())
            {
                var ws = wb.Worksheets.Add("物料主檔");
                ws.Cell(1, 1).Value = "品號"; ws.Cell(1, 2).Value = "品名"; ws.Cell(1, 3).Value = "規格"; ws.Cell(1, 4).Value = "庫存";
                ws.Cell(2, 1).Value = 1001; ws.Cell(2, 2).Value = "電磁接觸器"; ws.Cell(2, 3).Value = "S-N35";
                ws.Cell(3, 2).Value = "";
                wb.SaveAs(path);
            }
            var items = ExcelService.ImportMaterials(path)!;
            Assert.Equal(("1001", "電磁接觸器", "S-N35"), (items.Single().PartNo, items[0].Name, items[0].Spec));

            var bad = Path.Combine(_root, "bad.xlsx");
            using (var wb = new XLWorkbook())
            {
                wb.Worksheets.Add("x").Cell(1, 1).Value = "隨便";
                wb.SaveAs(bad);
            }
            Assert.Null(ExcelService.ImportMaterials(bad));
        }

        [Fact]
        public void DataStore_RoundTripsMaterials_OldFileHasNone()
        {
            var path = Path.Combine(_root, "schedule.json");
            var data = new ScheduleData();
            data.Materials.Add(M("P-1", "斷路器", "100A", "斷路器"));
            DataStore.Save(path, data);
            var loaded = DataStore.Load(path);
            Assert.Equal(("P-1", "斷路器", "100A"), (loaded.Materials.Single().PartNo, loaded.Materials[0].Name, loaded.Materials[0].Spec));

            File.WriteAllText(path, "{\"Jobs\":[],\"Materials\":null}");
            Assert.Empty(DataStore.Load(path).Materials);
            File.WriteAllText(path, "{\"Jobs\":[]}");
            Assert.Empty(DataStore.Load(path).Materials);
        }
    }
}
