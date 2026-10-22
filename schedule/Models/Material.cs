namespace schedule.Models
{
    /// <summary>大倉庫裡的一項物料。出料時從這裡勾選，帶入料號、名稱、規格、單位。</summary>
    public class Material
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string PartNo { get; set; } = "";      // 料號
        public string Name { get; set; } = "";        // 名稱
        public string Spec { get; set; } = "";        // 規格
        public string Unit { get; set; } = "";        // 單位
        public string Category { get; set; } = "";    // 分類（例如 斷路器、端子、線材）
        public string Note { get; set; } = "";

        public override string ToString() => PartNo.Length > 0 ? PartNo : Name;
    }
}
