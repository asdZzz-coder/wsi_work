using System.IO;
using ClosedXML.Excel;
using schedule.Models;

namespace schedule.Services
{
    /// <summary>匯入結果：讀到的工令，以及檔案裡有哪些欄位 / 工作表（舊版檔案缺的資料，合併時保留原本的）。</summary>
    public record ExcelImport(List<Job> Jobs, int Skipped, IReadOnlySet<string> Columns, bool HasSteps, bool HasChecks, bool HasIssues)
    {
        /// <summary>
        /// 合併時用：匯入檔沒有的欄位、工作表（或工作表裡沒有這張工令的列），從原本那筆搬到匯入的那筆。
        /// </summary>
        public void KeepMissing(Job existing, Job imported)
        {
            foreach (var (header, get, set) in ExcelService.TextColumns)
                if (!Columns.Contains(header)) set(imported, get(existing));
            if (!Columns.Contains(ExcelService.PriorityHeader)) imported.Priority = existing.Priority;
            if (!HasSteps || imported.Steps.Count == 0) imported.Steps = existing.Steps;
            if (!HasChecks || imported.Checks.Count == 0) imported.Checks = existing.Checks;
            if (!HasIssues || imported.Issues.Count == 0) imported.Issues = existing.Issues;
        }
    }

    /// <summary>
    /// Excel 匯出 / 匯入。工作表「排程表」一列一個工令；另外三張工作表放流程卡：
    /// 「流程站別」「品管檢驗」「異常紀錄」，用工令欄對應回工令。
    /// 匯入時依第一列的標題找欄位（欄位順序可以調換），「目前站別」「狀態」是算出來的，只匯出、不匯入。
    /// </summary>
    public static class ExcelService
    {
        public const string SheetName = "排程表";
        public const string StepSheet = "流程站別";
        public const string CheckSheet = "品管檢驗";
        public const string IssueSheet = "異常紀錄";

        private const string HWorkOrder = "工令";
        private const string HModel = "機種";
        private const string HQuantity = "數量";
        private const string HCustomer = "客戶";
        private const string HCE = "CE";
        private const string HTS = "TS";
        private const string HMaterialPlan = "預計材料入場";
        private const string HMaterialActual = "實際材料入場";
        private const string HWiringPlan = "預計配電";
        private const string HWiringActual = "實際配電完成";
        private const string HDispatchPlan = "預計出料";
        private const string HDispatchActual = "實際出料";
        private const string HDeliveryPlan = "預計交期";
        private const string HDeliveryActual = "實際交貨";
        private const string HInner = "盤內配電";
        private const string HOuter = "盤外配電";
        private const string HConsumables = "耗材提供";
        private const string HNote = "備註";
        private const string HProject = "案名";
        private const string HOrder = "訂單號碼";
        private const string HDrawing = "圖號";
        private const string HSales = "業務";
        internal const string PriorityHeader = "優先";
        private const string HHold = "暫停原因";
        private const string HCurrent = "目前站別";
        private const string HStatus = "狀態";

        internal static readonly string[] Headers =
        {
            HWorkOrder, HModel, HQuantity, HCustomer, HCE, HTS,
            HMaterialPlan, HMaterialActual, HWiringPlan, HWiringActual, HDispatchPlan, HDispatchActual, HDeliveryPlan, HDeliveryActual,
            HInner, HOuter, HConsumables, HNote,
            HProject, HOrder, HDrawing, HSales, PriorityHeader, HHold, HCurrent, HStatus,
        };

        private static readonly (string Header, Func<Job, DateTime?> Get, Action<Job, DateTime?> Set)[] DateColumns =
        {
            (HMaterialPlan, j => j.MaterialPlan, (j, v) => j.MaterialPlan = v),
            (HMaterialActual, j => j.MaterialActual, (j, v) => j.MaterialActual = v),
            (HWiringPlan, j => j.WiringPlan, (j, v) => j.WiringPlan = v),
            (HWiringActual, j => j.WiringActual, (j, v) => j.WiringActual = v),
            (HDispatchPlan, j => j.DispatchPlan, (j, v) => j.DispatchPlan = v),
            (HDispatchActual, j => j.DispatchActual, (j, v) => j.DispatchActual = v),
            (HDeliveryPlan, j => j.DeliveryPlan, (j, v) => j.DeliveryPlan = v),
            (HDeliveryActual, j => j.DeliveryActual, (j, v) => j.DeliveryActual = v),
        };

        /// <summary>v1.3 加的文字欄位（舊版匯出的檔案沒有，匯入時保留原本的值）。</summary>
        internal static readonly (string Header, Func<Job, string> Get, Action<Job, string> Set)[] TextColumns =
        {
            (HProject, j => j.ProjectName, (j, v) => j.ProjectName = v),
            (HOrder, j => j.OrderNo, (j, v) => j.OrderNo = v),
            (HDrawing, j => j.DrawingNo, (j, v) => j.DrawingNo = v),
            (HSales, j => j.Sales, (j, v) => j.Sales = v),
            (HHold, j => j.HoldReason, (j, v) => j.HoldReason = v),
        };

        private static readonly string[] StepHeaders = { HWorkOrder, "序", "站別", "對應", "負責人", "預計完成", "實際開始", "實際完成", "確認人", "略過", HNote };
        private static readonly string[] CheckHeaders = { HWorkOrder, "項目", "標準", "量測值", "結果", "檢驗員", "日期", HNote };
        private static readonly string[] IssueHeaders = { HWorkOrder, "日期", "類別", "說明", "負責人", "處理對策", "結案日期" };

        /// <summary>標題在第幾欄（從 1 開始）。</summary>
        internal static int Col(string header) => Array.IndexOf(Headers, header) + 1;

        // ---------- 匯出 ----------

        /// <summary>依預計交期排序匯出所有工令與流程卡。</summary>
        public static void Export(IEnumerable<Job> jobs, string path, DateTime today)
        {
            var sorted = ScheduleRules.Sort(jobs, JobSort.DeliveryPlan).ToList();
            using var wb = new XLWorkbook();
            var ws = AddSheet(wb, SheetName, Headers);

            int r = 2;
            foreach (var j in sorted)
            {
                ws.Cell(r, Col(HWorkOrder)).SetValue(Escape(j.WorkOrder));
                ws.Cell(r, Col(HModel)).SetValue(Escape(j.Model));
                ws.Cell(r, Col(HQuantity)).SetValue(j.Quantity);
                ws.Cell(r, Col(HCustomer)).SetValue(Escape(j.Customer));
                ws.Cell(r, Col(HCE)).SetValue(j.HasCE ? "V" : "");
                ws.Cell(r, Col(HTS)).SetValue(j.HasTS ? "V" : "");
                foreach (var (header, get, _) in DateColumns)
                    if (get(j) is { } d) SetDate(ws.Cell(r, Col(header)), d);
                ws.Cell(r, Col(HInner)).SetValue(Escape(j.InnerWiring));
                ws.Cell(r, Col(HOuter)).SetValue(Escape(j.OuterWiring));
                ws.Cell(r, Col(HConsumables)).SetValue(Escape(j.Consumables));
                ws.Cell(r, Col(HNote)).SetValue(Escape(j.Note));
                foreach (var (header, get, _) in TextColumns)
                    ws.Cell(r, Col(header)).SetValue(Escape(get(j)));
                ws.Cell(r, Col(PriorityHeader)).SetValue(j.Priority == JobPriority.Normal ? "" : RunCardRules.PriorityText(j.Priority));
                ws.Cell(r, Col(HCurrent)).SetValue(RunCardRules.ProgressText(j));
                ws.Cell(r, Col(HStatus)).SetValue(RunCardRules.IsOnHold(j) ? "暫停" : ScheduleRules.StatusText(ScheduleRules.Status(j, today)));
                r++;
            }
            ws.Column(Col(HCE)).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Column(Col(HTS)).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Columns().AdjustToContents();

            var steps = AddSheet(wb, StepSheet, StepHeaders);
            r = 2;
            foreach (var j in sorted)
            {
                int no = 1;
                foreach (var s in j.Steps ?? new())
                {
                    steps.Cell(r, 1).SetValue(Escape(j.WorkOrder));
                    steps.Cell(r, 2).SetValue(no++);
                    steps.Cell(r, 3).SetValue(Escape(s.Name));
                    steps.Cell(r, 4).SetValue(RunCardRules.LinkName(s.Link));
                    steps.Cell(r, 5).SetValue(Escape(s.Owner));
                    if (RunCardRules.PlanOf(j, s) is { } plan) SetDate(steps.Cell(r, 6), plan);
                    if (s.Started is { } started) SetDate(steps.Cell(r, 7), started);
                    if (RunCardRules.DoneOf(j, s) is { } done) SetDate(steps.Cell(r, 8), done);
                    steps.Cell(r, 9).SetValue(Escape(s.Signer));
                    steps.Cell(r, 10).SetValue(s.Skipped ? "V" : "");
                    steps.Cell(r, 11).SetValue(Escape(s.Note));
                    r++;
                }
            }
            steps.Columns().AdjustToContents();

            var checks = AddSheet(wb, CheckSheet, CheckHeaders);
            r = 2;
            foreach (var j in sorted)
                foreach (var c in j.Checks ?? new())
                {
                    checks.Cell(r, 1).SetValue(Escape(j.WorkOrder));
                    checks.Cell(r, 2).SetValue(Escape(c.Name));
                    checks.Cell(r, 3).SetValue(Escape(c.Standard));
                    checks.Cell(r, 4).SetValue(Escape(c.Measured));
                    checks.Cell(r, 5).SetValue(RunCardRules.ResultText(c.Result));
                    checks.Cell(r, 6).SetValue(Escape(c.Inspector));
                    if (c.Date is { } d) SetDate(checks.Cell(r, 7), d);
                    checks.Cell(r, 8).SetValue(Escape(c.Note));
                    r++;
                }
            checks.Columns().AdjustToContents();

            var issues = AddSheet(wb, IssueSheet, IssueHeaders);
            r = 2;
            foreach (var j in sorted)
                foreach (var i in j.Issues ?? new())
                {
                    issues.Cell(r, 1).SetValue(Escape(j.WorkOrder));
                    if (i.Date is { } d) SetDate(issues.Cell(r, 2), d);
                    issues.Cell(r, 3).SetValue(Escape(i.Type));
                    issues.Cell(r, 4).SetValue(Escape(i.Description));
                    issues.Cell(r, 5).SetValue(Escape(i.Owner));
                    issues.Cell(r, 6).SetValue(Escape(i.Action));
                    if (i.Closed is { } closed) SetDate(issues.Cell(r, 7), closed);
                    r++;
                }
            issues.Columns().AdjustToContents();

            wb.SaveAs(path);
        }

        private static IXLWorksheet AddSheet(XLWorkbook wb, string name, string[] headers)
        {
            var ws = wb.Worksheets.Add(name);
            for (int c = 0; c < headers.Length; c++)
                ws.Cell(1, c + 1).Value = headers[c];
            ws.Row(1).Style.Font.Bold = true;
            ws.SheetView.FreezeRows(1);
            return ws;
        }

        private static void SetDate(IXLCell cell, DateTime date)
        {
            cell.SetValue(date.Date);
            cell.Style.DateFormat.Format = "yyyy/mm/dd";
        }

        // 開頭的 ' 會被 ClosedXML 當成 Excel 的「文字前綴」而吞掉，多加一個才能原樣保留
        private static string Escape(string s) => s.StartsWith('\'') ? "'" + s : s;

        // ---------- 匯入 ----------

        /// <summary>只要工令清單時用（舊的呼叫方式）。</summary>
        public static (List<Job> Jobs, int Skipped) Import(string path)
        {
            var result = ImportWorkbook(path);
            return (result.Jobs, result.Skipped);
        }

        /// <summary>
        /// 讀取 Excel 的第一張工作表（優先找名為「排程表」的），再讀流程卡的三張工作表（有的話）。
        /// 沒有工令、或數量 / 日期讀不懂的列會略過並計入 Skipped；空白列直接略過。
        /// 流程卡工作表裡日期讀不懂的格子當成空白。
        /// </summary>
        public static ExcelImport ImportWorkbook(string path)
        {
            // 允許讀取「正在被 Excel 開啟」的檔案，否則會因檔案被鎖定而失敗
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var wb = new XLWorkbook(stream);
            var result = new List<Job>();
            var none = new HashSet<string>();
            int skipped = 0;

            if (!wb.Worksheets.TryGetWorksheet(SheetName, out var ws))
                ws = wb.Worksheets.FirstOrDefault();
            if (ws == null) return new ExcelImport(result, 0, none, false, false, false);

            var col = HeaderColumns(ws);
            if (!col.ContainsKey(HWorkOrder)) return new ExcelImport(result, 0, none, false, false, false);

            foreach (var row in ws.RowsUsed().Skip(1))
            {
                if (IsBlank(row)) continue;
                var job = ReadRow(row, col);
                if (job == null) { skipped++; continue; }
                result.Add(job);
            }

            // 流程卡：依工令找回工令（同一個工令重複時，接在第一筆）
            var byOrder = new Dictionary<string, Job>(StringComparer.OrdinalIgnoreCase);
            foreach (var j in result) byOrder.TryAdd(j.WorkOrder.Trim(), j);
            bool hasSteps = ReadSheet(wb, StepSheet, byOrder, ReadStep);
            bool hasChecks = ReadSheet(wb, CheckSheet, byOrder, ReadCheck);
            bool hasIssues = ReadSheet(wb, IssueSheet, byOrder, ReadIssue);

            return new ExcelImport(result, skipped, new HashSet<string>(col.Keys, StringComparer.OrdinalIgnoreCase), hasSteps, hasChecks, hasIssues);
        }

        private static Job? ReadRow(IXLRow row, Dictionary<string, int> col)
        {
            var workOrder = Text(row, col, HWorkOrder).Trim();
            if (workOrder.Length == 0) return null;

            int quantity = 0;
            if (Cell(row, col, HQuantity) is { } qCell && qCell.GetFormattedString().Trim().Length > 0)
            {
                if (qCell.DataType == XLDataType.Number) quantity = (int)Math.Round(qCell.GetDouble());
                else if (!TextFormat.TryParseInt(qCell.GetFormattedString(), out quantity)) return null;
            }

            var job = new Job
            {
                WorkOrder = workOrder,
                Model = Text(row, col, HModel).Trim(),
                Quantity = quantity,
                Customer = Text(row, col, HCustomer).Trim(),
                HasCE = ParseYes(Text(row, col, HCE)),
                HasTS = ParseYes(Text(row, col, HTS)),
                InnerWiring = Text(row, col, HInner).Trim(),
                OuterWiring = Text(row, col, HOuter).Trim(),
                Consumables = Text(row, col, HConsumables).Trim(),
                Note = Text(row, col, HNote),
                Priority = ParsePriority(Text(row, col, PriorityHeader)),
            };
            foreach (var (header, _, set) in TextColumns)
                set(job, Text(row, col, header).Trim());
            foreach (var (header, _, set) in DateColumns)
            {
                if (!TryDate(Cell(row, col, header), out var date)) return null;
                set(job, date);
            }
            return job;
        }

        /// <summary>讀一張流程卡工作表；沒有這張工作表（或沒有工令欄）回傳 false。</summary>
        private static bool ReadSheet(XLWorkbook wb, string name, Dictionary<string, Job> byOrder,
            Action<IXLRow, Dictionary<string, int>, Job> read)
        {
            if (!wb.Worksheets.TryGetWorksheet(name, out var ws)) return false;
            var col = HeaderColumns(ws);
            if (!col.ContainsKey(HWorkOrder)) return false;
            foreach (var row in ws.RowsUsed().Skip(1))
            {
                if (IsBlank(row)) continue;
                if (byOrder.TryGetValue(Text(row, col, HWorkOrder).Trim(), out var job)) read(row, col, job);
            }
            return true;
        }

        private static void ReadStep(IXLRow row, Dictionary<string, int> col, Job job)
        {
            var name = Text(row, col, "站別").Trim();
            if (name.Length == 0) return;
            var linkText = Text(row, col, "對應").Trim();
            var link = Enum.GetValues<StepLink>().FirstOrDefault(l => l != StepLink.None && RunCardRules.LinkName(l) == linkText);
            var step = new RunStep
            {
                Name = name,
                Link = job.Steps.Any(s => s.Link == link) ? StepLink.None : link,
                Owner = Text(row, col, "負責人").Trim(),
                Started = LenientDate(Cell(row, col, "實際開始")),
                Signer = Text(row, col, "確認人").Trim(),
                Skipped = ParseYes(Text(row, col, "略過")),
                Note = Text(row, col, HNote),
            };
            // 有對應時間點的站別，日期以「排程表」那張為準
            if (step.Link == StepLink.None)
            {
                step.Plan = LenientDate(Cell(row, col, "預計完成"));
                step.Done = LenientDate(Cell(row, col, "實際完成"));
            }
            job.Steps.Add(step);
        }

        private static void ReadCheck(IXLRow row, Dictionary<string, int> col, Job job)
        {
            var name = Text(row, col, "項目").Trim();
            if (name.Length == 0) return;
            job.Checks.Add(new CheckItem
            {
                Name = name,
                Standard = Text(row, col, "標準").Trim(),
                Measured = Text(row, col, "量測值").Trim(),
                Result = ParseResult(Text(row, col, "結果")),
                Inspector = Text(row, col, "檢驗員").Trim(),
                Date = LenientDate(Cell(row, col, "日期")),
                Note = Text(row, col, HNote),
            });
        }

        private static void ReadIssue(IXLRow row, Dictionary<string, int> col, Job job)
        {
            var description = Text(row, col, "說明").Trim();
            var type = Text(row, col, "類別").Trim();
            if (description.Length == 0 && type.Length == 0) return;
            job.Issues.Add(new IssueRecord
            {
                Date = LenientDate(Cell(row, col, "日期")),
                Type = type,
                Description = description,
                Owner = Text(row, col, "負責人").Trim(),
                Action = Text(row, col, "處理對策").Trim(),
                Closed = LenientDate(Cell(row, col, "結案日期")),
            });
        }

        /// <summary>第一列標題 → 欄號。</summary>
        private static Dictionary<string, int> HeaderColumns(IXLWorksheet ws)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in ws.Row(1).CellsUsed())
                map.TryAdd(cell.GetFormattedString().Trim(), cell.Address.ColumnNumber);
            return map;
        }

        private static IXLCell? Cell(IXLRow row, Dictionary<string, int> col, string header) =>
            col.TryGetValue(header, out var c) ? row.Cell(c) : null;

        private static string Text(IXLRow row, Dictionary<string, int> col, string header) =>
            Cell(row, col, header)?.GetFormattedString() ?? "";

        private static bool IsBlank(IXLRow row) => row.CellsUsed().All(c => c.GetFormattedString().Trim().Length == 0);

        /// <summary>空白儲存格 → null（成功）；日期、Excel 日期序號或看得懂的文字 → 日期；其他 → 失敗。</summary>
        private static bool TryDate(IXLCell? cell, out DateTime? date)
        {
            date = null;
            if (cell == null || cell.GetFormattedString().Trim().Length == 0) return true;
            if (cell.DataType == XLDataType.DateTime) { date = cell.GetDateTime().Date; return true; }
            if (cell.DataType == XLDataType.Number)
            {
                // 沒有設定日期格式的日期儲存格，讀出來是 Excel 的序號
                try { date = DateTime.FromOADate(cell.GetDouble()).Date; return true; }
                catch (ArgumentException) { return false; }
            }
            return TextFormat.TryParseOptionalDate(cell.GetFormattedString(), out date);
        }

        private static DateTime? LenientDate(IXLCell? cell) => TryDate(cell, out var d) ? d : null;

        /// <summary>CE / TS 欄：V、是、有、Y、yes、true、1、○、✓ 都算有；其他（含空白）算沒有。</summary>
        internal static bool ParseYes(string text) =>
            text.Trim().ToLowerInvariant() is "v" or "是" or "有" or "y" or "yes" or "true" or "1" or "○" or "o" or "✓" or "✔" or "ce" or "ts";

        internal static JobPriority ParsePriority(string text) => text.Trim() switch
        {
            "特急" or "最急" => JobPriority.Rush,
            "急件" or "急" or "急單" => JobPriority.Urgent,
            _ => JobPriority.Normal,
        };

        internal static CheckResult ParseResult(string text) => text.Trim().ToUpperInvariant() switch
        {
            "合格" or "OK" or "PASS" or "V" or "○" or "✓" => CheckResult.Pass,
            "不合格" or "NG" or "FAIL" or "X" or "✗" => CheckResult.Fail,
            "不適用" or "NA" or "N/A" or "-" => CheckResult.NotApplicable,
            _ => CheckResult.Pending,
        };
    }
}
