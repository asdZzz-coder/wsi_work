namespace schedule.Models
{
    /// <summary>
    /// 一筆工令的排程。三個時間點（材料入場、配電、交期）都分成「預計」與「實際完成」兩個日期，
    /// 還沒排定或還沒完成時為 null。
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
        public DateTime? DeliveryPlan { get; set; }   // 預計交期
        public DateTime? DeliveryActual { get; set; } // 實際交貨

        public string InnerWiring { get; set; } = ""; // 盤內配電人員
        public string OuterWiring { get; set; } = ""; // 盤外配電人員
        public string Consumables { get; set; } = ""; // 耗材由誰提供
        public string Note { get; set; } = "";

        // 螢幕閱讀器與 UI 自動化讀到的名稱（不影響存檔）
        public override string ToString() => WorkOrder;
    }
}
