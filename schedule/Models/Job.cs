namespace schedule.Models
{
    /// <summary>
    /// 一筆工令的排程。四個時間點（材料入場、配電、出料、交期）都分成「預計」與「實際完成」兩個日期，
    /// 還沒排定或還沒完成時為 null。另外有流程卡（Run Card）：流程站別、品管檢驗、異常 / 變更紀錄。
    /// </summary>
    public class Job
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string WorkOrder { get; set; } = "";   // 工令
        public string Model { get; set; } = "";       // 機種
        public int Quantity { get; set; }             // 數量
        public string Customer { get; set; } = "";    // 客戶
        public bool HasCE { get; set; }
        public bool HasTS { get; set; }

        public DateTime? MaterialPlan { get; set; }   // 預計材料入場
        public DateTime? MaterialActual { get; set; } // 實際材料入場
        public DateTime? WiringPlan { get; set; }     // 預計配電
        public DateTime? WiringActual { get; set; }   // 實際配電完成
        public DateTime? DispatchPlan { get; set; }   // 預計出料
        public DateTime? DispatchActual { get; set; } // 實際出料
        public DateTime? DeliveryPlan { get; set; }   // 預計交期
        public DateTime? DeliveryActual { get; set; } // 實際交貨

        public string InnerWiring { get; set; } = ""; // 盤內配電人員
        public string OuterWiring { get; set; } = ""; // 盤外配電人員
        public string Consumables { get; set; } = ""; // 耗材由誰提供
        public string Note { get; set; } = "";

        public string ProjectName { get; set; } = ""; // 案名 / 工程名稱
        public string OrderNo { get; set; } = "";     // 訂單號碼
        public string DrawingNo { get; set; } = "";   // 圖號 / 版次
        public string Sales { get; set; } = "";       // 業務
        public JobPriority Priority { get; set; }
        public string HoldReason { get; set; } = "";  // 暫停原因；有寫就算暫停中

        // 流程卡；舊資料沒有時由 RunCardRules.Ensure 補上預設內容
        public List<RunStep> Steps { get; set; } = new();
        public List<CheckItem> Checks { get; set; } = new();
        public List<IssueRecord> Issues { get; set; } = new();

        // 出料紀錄。DispatchGroup 相同（且不是空白）的工令共用同一份出料紀錄，改一台其他台跟著改
        public List<DispatchItem> Dispatches { get; set; } = new();
        public string DispatchGroup { get; set; } = "";

        // 螢幕閱讀器與 UI 自動化讀到的名稱（不影響存檔）
        public override string ToString() => WorkOrder;
    }
}
