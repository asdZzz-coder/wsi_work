using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using schedule.Models;
using schedule.Services;
using schedule.Views;

namespace schedule.Tests
{
    /// <summary>
    /// 列印用的流程卡文件：能排版成 A4 頁面。
    /// 設定環境變數 WORKSCHEDULE_PRINT_PREVIEW 為資料夾時，會把每一頁存成 PNG，方便用眼睛檢查版面。
    /// </summary>
    public class RunCardPrinterTests
    {
        private const double A4Width = 793.7, A4Height = 1122.5; // 96 DPI

        private static void RunSta(Action action)
        {
            Exception? error = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { error = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (error != null) throw new Exception("STA 執行失敗", error);
        }

        private static Job SampleJob(string workOrder)
        {
            var job = new Job
            {
                WorkOrder = workOrder, Model = "MCC-400", Quantity = 2, Customer = "台積電", HasCE = true,
                ProjectName = "竹科 F18 擴建", OrderNo = "PO-26-0915", DrawingNo = "MCC-400-A rev.C", Sales = "林業務",
                Priority = JobPriority.Rush, InnerWiring = "阿明", OuterWiring = "小陳", Consumables = "公司",
                MaterialPlan = new DateTime(2026, 9, 28), MaterialActual = new DateTime(2026, 9, 29),
                WiringPlan = new DateTime(2026, 10, 3), DeliveryPlan = new DateTime(2026, 10, 11),
                HoldReason = "等客戶確認圖面", Note = "客戶要求盤面加裝電力監控錶。",
            };
            RunCardRules.Ensure(job);
            job.Steps[0].Owner = "王工";
            job.Steps[0].Done = new DateTime(2026, 9, 25);
            job.Steps[0].Signer = "李主管";
            job.Steps[2].Skipped = true;
            job.Checks[5].Measured = "0.6 MΩ";
            job.Checks[5].Result = CheckResult.Fail;
            job.Checks[0].Result = CheckResult.Pass;
            job.Issues.Add(new IssueRecord { Date = new DateTime(2026, 10, 8), Type = "缺料", Description = "缺 100A 斷路器 2 只", Owner = "小陳" });
            return job;
        }

        [Theory]
        [InlineData("W2610-001")]  // 印得出條碼
        [InlineData("工令一號")]    // 有中文：不印條碼，只印文字
        public void Build_PaginatesToA4(string workOrder)
        {
            RunSta(() =>
            {
                var doc = RunCardPrinter.Build(SampleJob(workOrder), A4Width, A4Height, new DateTime(2026, 10, 12, 9, 30, 0));
                var paginator = ((IDocumentPaginatorSource)doc).DocumentPaginator;
                paginator.ComputePageCount();
                var folder = Environment.GetEnvironmentVariable("WORKSCHEDULE_PRINT_PREVIEW");
                if (!string.IsNullOrEmpty(folder)) SavePages(paginator, folder, workOrder);
                Assert.InRange(paginator.PageCount, 1, 2);
            });
        }

        private static void SavePages(DocumentPaginator paginator, string folder, string workOrder)
        {
            {

                Directory.CreateDirectory(folder);
                for (int i = 0; i < paginator.PageCount; i++)
                {
                    var page = paginator.GetPage(i);
                    var host = new DrawingVisual();
                    using (var dc = host.RenderOpen())
                    {
                        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, A4Width, A4Height));
                        dc.DrawRectangle(new VisualBrush(page.Visual), null, new Rect(0, 0, A4Width, A4Height));
                    }
                    var bmp = new RenderTargetBitmap((int)A4Width, (int)A4Height, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(host);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bmp));
                    using var file = File.Create(Path.Combine(folder, $"runcard-{(workOrder.All(char.IsAscii) ? workOrder : "zh")}-{i + 1}.png"));
                    encoder.Save(file);
                }
            }
        }
    }
}
