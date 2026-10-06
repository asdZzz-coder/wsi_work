using System.IO;
using ClosedXML.Excel;
using schedule.Models;

namespace schedule.Services
{
    /// <summary>
    /// Excel 匯出 / 匯入：一張工作表「排程表」，一列一個工令。
    /// 匯入時依第一列的標題找欄位（欄位順序可以調換），「狀態」是算出來的，只匯出、不匯入。
    /// </summary>
    public static class ExcelService
    {
        public const string SheetName = "排程表";

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
        private const string HStatus = "狀態";

        internal static readonly string[] Headers =
        {
            HWorkOrder, HModel, HQuantity, HCustomer, HCE, HTS,
            HMaterialPlan, HMaterialActual, HWiringPlan, HWiringActual, HDispatchPlan, HDispatchActual, HDeliveryPlan, HDeliveryActual,
            HInner, HOuter, HConsumables, HNote, HStatus,
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

        /// <summary>標題在第幾欄（從 1 開始）。</summary>
        internal static int Col(string header) => Array.IndexOf(Headers, header) + 1;

        // ---------- 匯出 ----------

        /// <summary>依預計交期排序匯出所有工令。</summary>
        public static void Export(IEnumerable<Job> jobs, string path, DateTime today)
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add(SheetName);
            for (int c = 0; c < Headers.Length; c++)
                ws.Cell(1, c + 1).Value = Headers[c];
            ws.Row(1).Style.Font.Bold = true;
            ws.SheetView.FreezeRows(1);

            int r = 2;
            foreach (var j in ScheduleRules.Sort(jobs, JobSort.DeliveryPlan))
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
                ws.Cell(r, Col(HStatus)).SetValue(ScheduleRules.StatusText(ScheduleRules.Status(j, today)));
                r++;
            }
            ws.Column(Col(HCE)).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Column(Col(HTS)).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Columns().AdjustToContents();
            wb.SaveAs(path);
        }

        private static void SetDate(IXLCell cell, DateTime date)
        {
            cell.SetValue(date.Date);
            cell.Style.DateFormat.Format = "yyyy/mm/dd";
        }

        // 開頭的 ' 會被 ClosedXML 當成 Excel 的「文字前綴」而吞掉，多加一個才能原樣保留
        private static string Escape(string s) => s.StartsWith('\'') ? "'" + s : s;

        // ---------- 匯入 ----------

        /// <summary>
        /// 讀取 Excel 的第一張工作表（優先找名為「排程表」的）。
        /// 沒有工令、或數量 / 日期讀不懂的列會略過並計入 Skipped；空白列直接略過。
        /// </summary>
        public static (List<Job> Jobs, int Skipped) Import(string path)
        {
            // 允許讀取「正在被 Excel 開啟」的檔案，否則會因檔案被鎖定而失敗
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var wb = new XLWorkbook(stream);
            var result = new List<Job>();
            int skipped = 0;

            if (!wb.Worksheets.TryGetWorksheet(SheetName, out var ws))
                ws = wb.Worksheets.FirstOrDefault();
            if (ws == null) return (result, 0);

            var col = HeaderColumns(ws);
            if (!col.ContainsKey(HWorkOrder)) return (result, 0);

            foreach (var row in ws.RowsUsed().Skip(1))
            {
                if (IsBlank(row)) continue;
                var job = ReadRow(row, col);
                if (job == null) { skipped++; continue; }
                result.Add(job);
            }
            return (result, skipped);
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
            };
            foreach (var (header, _, set) in DateColumns)
            {
                if (!TryDate(Cell(row, col, header), out var date)) return null;
                set(job, date);
            }
            return job;
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

        /// <summary>CE / TS 欄：V、是、有、Y、yes、true、1、○、✓ 都算有；其他（含空白）算沒有。</summary>
        internal static bool ParseYes(string text) =>
            text.Trim().ToLowerInvariant() is "v" or "是" or "有" or "y" or "yes" or "true" or "1" or "○" or "o" or "✓" or "✔" or "ce" or "ts";
    }
}
