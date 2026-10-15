using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using schedule.Models;
using schedule.Services;

namespace schedule.Views
{
    /// <summary>
    /// 列印流程卡（A4 直式、黑白）：工令條碼、基本資料、流程站別（留簽名欄）、品管檢驗、異常紀錄、簽核欄。
    /// 另外可以印出料單（料號、名稱、規格、數量…與同步機台）。
    /// 選「Microsoft Print to PDF」就能存成 PDF。
    /// </summary>
    public static class RunCardPrinter
    {
        private static readonly Brush Ink = Brushes.Black;
        private static readonly Brush Faint = Frozen(0x55);
        private static readonly Brush HeaderFill = Frozen(0xEE);
        private static readonly Brush Line = Frozen(0x77);

        private static Brush Frozen(byte gray)
        {
            var brush = new SolidColorBrush(Color.FromRgb(gray, gray, gray));
            brush.Freeze();
            return brush;
        }

        public static void Print(Job job, Window owner, Action<string>? status = null)
        {
            var dlg = new PrintDialog();
            try
            {
                dlg.PrintTicket.PageMediaSize = new PageMediaSize(PageMediaSizeName.ISOA4);
                dlg.PrintTicket.PageOrientation = PageOrientation.Portrait;
            }
            catch (Exception ex) when (ex is PrintQueueException or InvalidOperationException) { } // 沒有印表機時照預設
            if (dlg.ShowDialog() != true) return;

            try
            {
                var doc = Build(job, dlg.PrintableAreaWidth, dlg.PrintableAreaHeight, DateTime.Now);
                dlg.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, $"流程卡 {job.WorkOrder}");
                status?.Invoke($"已送出列印：流程卡 {job.WorkOrder}");
            }
            catch (Exception ex) when (ex is PrintQueueException or InvalidOperationException or System.IO.IOException)
            {
                MessageBox.Show(owner, $"列印失敗：{ex.Message}", "列印流程卡", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>產生流程卡文件（也給自動測試用）。</summary>
        public static FlowDocument Build(Job job, double pageWidth, double pageHeight, DateTime printedAt)
        {
            RunCardRules.Ensure(job);
            var doc = NewDocument(pageWidth, pageHeight);
            var width = pageWidth - doc.PagePadding.Left - doc.PagePadding.Right;
            doc.Blocks.Add(TitleBlock(width, job, printedAt));
            doc.Blocks.Add(InfoTable(width, job));
            doc.Blocks.Add(SectionTitle("一、流程站別"));
            doc.Blocks.Add(StepTable(width, job));
            doc.Blocks.Add(SectionTitle("二、品管檢驗"));
            doc.Blocks.Add(CheckTable(width, job));
            doc.Blocks.Add(SectionTitle("三、異常 / 變更紀錄"));
            doc.Blocks.Add(IssueTable(width, job));
            if (job.Note.Trim().Length > 0)
            {
                doc.Blocks.Add(SectionTitle("備註"));
                doc.Blocks.Add(new Paragraph(new Run(job.Note.Trim())) { Margin = new Thickness(0, 0, 0, 6) });
            }
            doc.Blocks.Add(SignTable(width));
            return doc;
        }

        // ---------- 出料單 ----------

        public static void PrintDispatch(Job job, IReadOnlyList<string> partners, Window owner, Action<string>? status = null)
        {
            var dlg = new PrintDialog();
            try
            {
                dlg.PrintTicket.PageMediaSize = new PageMediaSize(PageMediaSizeName.ISOA4);
                dlg.PrintTicket.PageOrientation = PageOrientation.Portrait;
            }
            catch (Exception ex) when (ex is PrintQueueException or InvalidOperationException) { }
            if (dlg.ShowDialog() != true) return;

            try
            {
                var doc = BuildDispatch(job, partners, dlg.PrintableAreaWidth, dlg.PrintableAreaHeight, DateTime.Now);
                dlg.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, $"出料單 {job.WorkOrder}");
                status?.Invoke($"已送出列印：出料單 {job.WorkOrder}");
            }
            catch (Exception ex) when (ex is PrintQueueException or InvalidOperationException or System.IO.IOException)
            {
                MessageBox.Show(owner, $"列印失敗：{ex.Message}", "列印出料單", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>產生出料單（料件多時會自動換頁）。partners 是同步出料的其他工令。</summary>
        public static FlowDocument BuildDispatch(Job job, IReadOnlyList<string> partners, double pageWidth, double pageHeight, DateTime printedAt)
        {
            var doc = NewDocument(pageWidth, pageHeight);
            var width = pageWidth - doc.PagePadding.Left - doc.PagePadding.Right;

            var title = NewTable(width, 1.6, 1);
            title.Margin = new Thickness(0, 0, 0, 8);
            title.BorderThickness = new Thickness(0);
            var top = AddRow(title);
            var heading = new Paragraph { Margin = new Thickness(0) };
            heading.Inlines.Add(new Run("出料單") { FontSize = 20, FontWeight = FontWeights.Bold });
            heading.Inlines.Add(new Run("　MATERIAL ISSUE") { FontSize = 12, Foreground = Faint });
            var left = new TableCell(heading) { BorderThickness = new Thickness(0) };
            left.Blocks.Add(new Paragraph(new Run($"列印時間 {printedAt:yyyy/MM/dd HH:mm}　·　共 {job.Dispatches.Count} 項"))
                { Margin = new Thickness(0, 4, 0, 0), Foreground = Faint, FontSize = 9.5 });
            top.Cells.Add(left);
            top.Cells.Add(BarcodeCell(job.WorkOrder));
            doc.Blocks.Add(title);

            const double label = 62, value = 116;
            var info = NewTable(width, label, value, label, value, label, value, label, value);
            info.Margin = new Thickness(0, 0, 0, 10);
            var items = new (string, string)[]
            {
                ("工令", job.WorkOrder), ("機種", job.Model), ("數量", job.Quantity > 0 ? $"{job.Quantity:#,0}" : ""), ("客戶", job.Customer),
                ("案名", job.ProjectName), ("圖號 / 版次", job.DrawingNo), ("預計出料", TextFormat.Date(job.DispatchPlan)), ("實際出料", TextFormat.Date(job.DispatchActual)),
            };
            for (int i = 0; i < items.Length; i += 4)
            {
                var row = AddRow(info);
                for (int k = i; k < i + 4; k++)
                {
                    row.Cells.Add(Cell(items[k].Item1, header: true));
                    row.Cells.Add(Cell(items[k].Item2, bold: k == 0));
                }
            }
            var syncRow = AddRow(info);
            syncRow.Cells.Add(Cell("同步機台", header: true));
            syncRow.Cells.Add(new TableCell(CellParagraph(partners.Count > 0 ? string.Join("、", partners) : "無（只有這台）", bold: partners.Count > 0))
                { ColumnSpan = 7, BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0.75, 0.75), Padding = new Thickness(4, 2.5, 4, 2.5) });
            doc.Blocks.Add(info);

            var table = NewTable(width, 26, 104, 128, 214, 46, 40, 74, 92);
            AddHeader(table, "序", "料號", "名稱", "規格", "數量", "單位", "出料日期", "備註");
            int no = 1;
            foreach (var d in job.Dispatches)
            {
                var row = AddRow(table);
                row.Cells.Add(Cell((no++).ToString(), center: true));
                row.Cells.Add(Cell(d.PartNo));
                row.Cells.Add(Cell(d.Name));
                row.Cells.Add(Cell(d.Spec, small: true));
                row.Cells.Add(Cell(d.Quantity, center: true));
                row.Cells.Add(Cell(d.Unit, center: true));
                row.Cells.Add(Cell(TextFormat.Date(d.Date)));
                row.Cells.Add(Cell(d.Note, small: true));
            }
            doc.Blocks.Add(table);

            var sign = NewTable(width, 1, 1, 1, 1);
            sign.Margin = new Thickness(0, 12, 0, 0);
            AddHeader(sign, "製表", "發料", "領料", "主管");
            var signRow = AddRow(sign);
            for (int c = 0; c < 4; c++) signRow.Cells.Add(Cell("", extraHeight: 30));
            doc.Blocks.Add(sign);
            return doc;
        }

        // ---------- 標題與條碼 ----------

        private static Block TitleBlock(double width, Job job, DateTime printedAt)
        {
            var table = NewTable(width, 1.6, 1);
            table.Margin = new Thickness(0, 0, 0, 8);
            table.BorderThickness = new Thickness(0);
            var row = AddRow(table);

            var title = new Paragraph { Margin = new Thickness(0) };
            title.Inlines.Add(new Run("工令流程卡") { FontSize = 20, FontWeight = FontWeights.Bold });
            title.Inlines.Add(new Run("　RUN CARD") { FontSize = 12, Foreground = Faint });
            var sub = new Paragraph { Margin = new Thickness(0, 4, 0, 0), Foreground = Faint, FontSize = 9.5 };
            sub.Inlines.Add(new Run($"列印時間 {printedAt:yyyy/MM/dd HH:mm}"));
            var (done, total) = RunCardRules.Progress(job);
            sub.Inlines.Add(new Run($"　·　進度 {done}/{total}　·　{(RunCardRules.CurrentStep(job) is { } s ? $"目前站別：{s.Name}" : "全部完成")}"));
            if (RunCardRules.IsOnHold(job)) sub.Inlines.Add(new Run($"　·　暫停：{job.HoldReason.Trim()}") { FontWeight = FontWeights.Bold, Foreground = Ink });
            row.Cells.Add(new TableCell(title) { BorderThickness = new Thickness(0) });
            row.Cells[0].Blocks.Add(sub);
            row.Cells.Add(BarcodeCell(job.WorkOrder));
            return table;
        }

        /// <summary>右上角的工令條碼（印不出條碼時只有文字）。</summary>
        private static TableCell BarcodeCell(string workOrder)
        {
            var right = new TableCell { BorderThickness = new Thickness(0), TextAlignment = TextAlignment.Center };
            if (Barcode(workOrder) is { } barcode)
                right.Blocks.Add(new BlockUIContainer(barcode) { Margin = new Thickness(0) });
            right.Blocks.Add(new Paragraph(new Run(workOrder) { FontSize = 13, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Consolas, Microsoft JhengHei UI") })
                { Margin = new Thickness(0, 2, 0, 0), TextAlignment = TextAlignment.Center });
            return right;
        }

        /// <summary>Code 39 條碼；工令有中文等不支援的字元時回傳 null（只印文字）。</summary>
        private static FrameworkElement? Barcode(string text)
        {
            var elements = Code39.Encode(text);
            if (elements == null) return null;
            const double narrow = 1.4, wide = 3.6, height = 38;
            double x = 0;
            var canvas = new Canvas { Height = height, SnapsToDevicePixels = true };
            foreach (var (bar, isWide) in elements)
            {
                double w = isWide ? wide : narrow;
                if (bar)
                {
                    var rect = new Rectangle { Width = w, Height = height, Fill = Ink };
                    Canvas.SetLeft(rect, x);
                    canvas.Children.Add(rect);
                }
                x += w;
            }
            canvas.Width = x;
            // 左右留白（靜區），掃描器才讀得到
            return new Border { Child = canvas, Padding = new Thickness(12, 0, 12, 0), Background = Brushes.White };
        }

        // ---------- 基本資料 ----------

        private static Block InfoTable(double width, Job job)
        {
            const double label = 62, value = 116;
            var table = NewTable(width, label, value, label, value, label, value, label, value);
            table.Margin = new Thickness(0, 0, 0, 10);
            var certs = string.Join(" / ", new[] { job.HasCE ? "CE" : "", job.HasTS ? "TS" : "" }.Where(c => c.Length > 0));
            var items = new (string, string)[]
            {
                ("工令", job.WorkOrder), ("機種", job.Model), ("數量", job.Quantity > 0 ? $"{job.Quantity:#,0}" : ""), ("客戶", job.Customer),
                ("案名", job.ProjectName), ("訂單號碼", job.OrderNo), ("圖號 / 版次", job.DrawingNo), ("業務", job.Sales),
                ("優先", RunCardRules.PriorityText(job.Priority)), ("認證", certs.Length > 0 ? certs : "無"), ("耗材提供", job.Consumables), ("預計交期", TextFormat.Date(job.DeliveryPlan)),
                ("盤內配電", job.InnerWiring), ("盤外配電", job.OuterWiring), ("實際交貨", TextFormat.Date(job.DeliveryActual)), ("檢驗結果", RunCardRules.CheckSummary(job)),
            };
            for (int i = 0; i < items.Length; i += 4)
            {
                var row = AddRow(table);
                for (int k = i; k < i + 4; k++)
                {
                    row.Cells.Add(Cell(items[k].Item1, header: true));
                    row.Cells.Add(Cell(items[k].Item2, bold: k == 0 || (k == 8 && job.Priority != JobPriority.Normal)));
                }
            }
            return table;
        }

        // ---------- 流程站別 ----------

        private static Block StepTable(double width, Job job)
        {
            var table = NewTable(width, 26, 108, 70, 78, 78, 78, 70, 186);
            AddHeader(table, "序", "站別", "負責人", "預計完成", "實際開始", "實際完成", "確認簽名", "備註");
            int no = 1;
            foreach (var s in job.Steps)
            {
                var row = AddRow(table);
                var note = s.Skipped ? ("（略過）" + s.Note.Trim()) : s.Note.Trim();
                row.Cells.Add(Cell((no++).ToString(), center: true));
                row.Cells.Add(Cell(s.Name, muted: s.Skipped));
                row.Cells.Add(Cell(s.Owner, muted: s.Skipped));
                row.Cells.Add(Cell(TextFormat.Date(RunCardRules.PlanOf(job, s)), muted: s.Skipped));
                row.Cells.Add(Cell(TextFormat.Date(s.Started), muted: s.Skipped));
                row.Cells.Add(Cell(TextFormat.Date(RunCardRules.DoneOf(job, s)), muted: s.Skipped));
                row.Cells.Add(Cell(s.Signer, muted: s.Skipped));
                row.Cells.Add(Cell(note, muted: s.Skipped));
            }
            return table;
        }

        // ---------- 品管檢驗 ----------

        private static Block CheckTable(double width, Job job)
        {
            var table = NewTable(width, 26, 116, 262, 74, 84, 64, 78);
            AddHeader(table, "序", "檢驗項目", "標準 / 規格", "量測值", "結果", "檢驗員", "日期");
            int no = 1;
            foreach (var c in job.Checks)
            {
                var row = AddRow(table);
                row.Cells.Add(Cell((no++).ToString(), center: true));
                row.Cells.Add(Cell(c.Name));
                row.Cells.Add(Cell(c.Standard, small: true));
                row.Cells.Add(Cell(c.Measured));
                // 還沒檢驗的留勾選框，可以手寫
                row.Cells.Add(Cell(c.Result == CheckResult.Pending ? "□合格 □不合格" : RunCardRules.ResultText(c.Result),
                    bold: c.Result == CheckResult.Fail, small: c.Result == CheckResult.Pending));
                row.Cells.Add(Cell(c.Inspector));
                row.Cells.Add(Cell(TextFormat.Date(c.Date)));
            }
            return table;
        }

        // ---------- 異常紀錄 ----------

        private static Block IssueTable(double width, Job job)
        {
            var table = NewTable(width, 78, 64, 226, 64, 200, 78);
            AddHeader(table, "日期", "類別", "說明", "負責人", "處理對策", "結案日期");
            foreach (var i in job.Issues)
            {
                var row = AddRow(table);
                row.Cells.Add(Cell(TextFormat.Date(i.Date)));
                row.Cells.Add(Cell(i.Type));
                row.Cells.Add(Cell(i.Description));
                row.Cells.Add(Cell(i.Owner));
                row.Cells.Add(Cell(i.Action));
                row.Cells.Add(Cell(i.Closed == null ? "未結案" : TextFormat.Date(i.Closed), bold: i.Closed == null));
            }
            // 留幾列空白給現場手寫
            for (int n = job.Issues.Count; n < 2; n++)
            {
                var row = AddRow(table);
                for (int c = 0; c < 6; c++) row.Cells.Add(Cell("", extraHeight: 6));
            }
            return table;
        }

        // ---------- 簽核 ----------

        private static Block SignTable(double width)
        {
            var table = NewTable(width, 1, 1, 1, 1);
            table.Margin = new Thickness(0, 10, 0, 0);
            AddHeader(table, "製表", "生產主管", "品管", "出貨確認");
            var row = AddRow(table);
            for (int c = 0; c < 4; c++) row.Cells.Add(Cell("", extraHeight: 30));
            return table;
        }

        // ---------- 表格小工具 ----------

        private static Paragraph SectionTitle(string text) => new(new Run(text))
        {
            FontSize = 12.5,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 8, 0, 4),
        };

        /// <summary>
        /// 欄寬依比例換算成固定寬度，加起來剛好是可列印的寬度（FlowDocument 的表格用比例欄寬時會排錯）。
        /// </summary>
        private static Table NewTable(double width, params double[] weights)
        {
            var table = new Table { CellSpacing = 0, BorderBrush = Line, BorderThickness = new Thickness(0.75, 0.75, 0, 0) };
            double sum = weights.Sum(), usable = width - 2; // 留一點給框線
            foreach (var w in weights) table.Columns.Add(new TableColumn { Width = new GridLength(Math.Floor(usable * w / sum)) });
            table.RowGroups.Add(new TableRowGroup());
            return table;
        }

        private static TableRow AddRow(Table table)
        {
            var row = new TableRow();
            table.RowGroups[0].Rows.Add(row);
            return row;
        }

        private static void AddHeader(Table table, params string[] titles)
        {
            var row = AddRow(table);
            foreach (var t in titles) row.Cells.Add(Cell(t, header: true));
        }

        private static FlowDocument NewDocument(double pageWidth, double pageHeight) => new()
        {
            PageWidth = pageWidth,
            PageHeight = pageHeight,
            PagePadding = new Thickness(40, 30, 40, 30),
            ColumnWidth = double.PositiveInfinity,
            FontFamily = new FontFamily("Microsoft JhengHei UI, Microsoft JhengHei, Segoe UI"),
            FontSize = 10.5,
            Foreground = Ink,
            Background = Brushes.White,
            TextAlignment = TextAlignment.Left,
        };

        private static Paragraph CellParagraph(string text, bool header = false, bool bold = false, bool center = false, bool muted = false, bool small = false) =>
            new(new Run(text))
            {
                Margin = new Thickness(0),
                FontWeight = header || bold ? FontWeights.Bold : FontWeights.Normal,
                FontSize = small ? 9 : header ? 9.5 : 10.5,
                Foreground = muted ? Faint : Ink,
                TextAlignment = center || header ? TextAlignment.Center : TextAlignment.Left,
                // 空白格也撐出可以手寫的高度
                LineHeight = 15,
            };

        private static TableCell Cell(string text, bool header = false, bool bold = false, bool center = false, bool muted = false, bool small = false, double extraHeight = 0)
        {
            var paragraph = CellParagraph(text, header, bold, center, muted, small);
            return new TableCell(paragraph)
            {
                BorderBrush = Line,
                BorderThickness = new Thickness(0, 0, 0.75, 0.75),
                Padding = new Thickness(4, 2.5, 4, 2.5 + extraHeight),
                Background = header ? HeaderFill : Brushes.White,
            };
        }
    }
}
