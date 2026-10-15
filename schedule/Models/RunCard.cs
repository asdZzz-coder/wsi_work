namespace schedule.Models
{
    /// <summary>工令的優先等級。</summary>
    public enum JobPriority { Normal, Urgent, Rush }

    /// <summary>
    /// 流程站別和工令上原本四個時間點的對應：有對應的站別，預計 / 實際完成日期就是工令上的那兩個日期
    /// （改流程卡會改到工令，清單、甘特圖、時間軸也跟著變）。
    /// </summary>
    public enum StepLink { None, Material, Wiring, Dispatch, Delivery }

    /// <summary>品管檢驗項目的結果。</summary>
    public enum CheckResult { Pending, Pass, Fail, NotApplicable }

    /// <summary>流程卡上的一個站別（工序）。</summary>
    public class RunStep
    {
        public string Name { get; set; } = "";
        public StepLink Link { get; set; }
        public string Owner { get; set; } = "";       // 負責人
        public DateTime? Plan { get; set; }           // 預計完成（有對應時間點的站別不用這格）
        public DateTime? Started { get; set; }        // 實際開始
        public DateTime? Done { get; set; }           // 實際完成（有對應時間點的站別不用這格）
        public string Signer { get; set; } = "";      // 確認人
        public bool Skipped { get; set; }             // 這張工令不用做這站
        public string Note { get; set; } = "";
    }

    /// <summary>品管檢驗的一個項目。</summary>
    public class CheckItem
    {
        public string Name { get; set; } = "";
        public string Standard { get; set; } = "";    // 標準 / 規格
        public string Measured { get; set; } = "";    // 量測值
        public CheckResult Result { get; set; }
        public string Inspector { get; set; } = "";
        public DateTime? Date { get; set; }
        public string Note { get; set; } = "";
    }

    /// <summary>異常 / 變更紀錄（缺料、設計變更、客戶變更、品質異常…）。</summary>
    public class IssueRecord
    {
        public DateTime? Date { get; set; }
        public string Type { get; set; } = "";
        public string Description { get; set; } = "";
        public string Owner { get; set; } = "";
        public string Action { get; set; } = "";      // 處理對策
        public DateTime? Closed { get; set; }         // 結案日期；空白 = 還沒結案
    }

    /// <summary>出料紀錄的一筆：這台機台出了哪個料。</summary>
    public class DispatchItem
    {
        public string PartNo { get; set; } = "";      // 料號
        public string Name { get; set; } = "";        // 名稱
        public string Spec { get; set; } = "";        // 規格
        public string Quantity { get; set; } = "";    // 數量（文字：2、1.5、一批都可以）
        public string Unit { get; set; } = "";        // 單位
        public DateTime? Date { get; set; }           // 出料日期
        public string Note { get; set; } = "";
    }
}
