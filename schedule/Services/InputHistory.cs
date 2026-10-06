using schedule.Models;

namespace schedule.Services
{
    /// <summary>有下拉選單的欄位。盤內、盤外配電共用同一份人員清單。</summary>
    public enum HistoryField { Model, Customer, People, Consumables }

    /// <summary>每個欄位之前輸入過的值，最近用過的排在最前面。</summary>
    public class InputHistoryData
    {
        public List<string> Models { get; set; } = new();
        public List<string> Customers { get; set; } = new();
        public List<string> People { get; set; } = new();
        public List<string> Consumables { get; set; } = new();
    }

    /// <summary>
    /// 記住表單輸入過的機種、客戶、人員、耗材提供，給下拉選單用。
    /// 從下拉選單移除的值不會再自動回來，除非又存了一筆用到它的工令。
    /// </summary>
    public static class InputHistory
    {
        /// <summary>每個欄位最多記幾筆，太舊的會被擠掉。</summary>
        public const int MaxItems = 200;

        private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

        /// <summary>舊版資料沒有記錄：從現有工令建立一份（清單後面的工令比較新，排在前面）。</summary>
        public static InputHistoryData Ensure(ScheduleData data)
        {
            if (data.History != null) return data.History;
            data.History = new InputHistoryData();
            foreach (var job in data.Jobs) Remember(data.History, job);
            return data.History;
        }

        public static List<string> Items(InputHistoryData history, HistoryField field) => field switch
        {
            HistoryField.Model => history.Models,
            HistoryField.Customer => history.Customers,
            HistoryField.People => history.People,
            _ => history.Consumables,
        };

        /// <summary>存檔時呼叫：把這筆工令的值移到各清單最前面。</summary>
        public static void Remember(InputHistoryData history, Job job)
        {
            Remember(history.Models, job.Model);
            Remember(history.Customers, job.Customer);
            Remember(history.People, job.OuterWiring);
            Remember(history.People, job.InnerWiring);
            Remember(history.Consumables, job.Consumables);
        }

        public static void Remember(List<string> items, string? value)
        {
            value = value?.Trim();
            if (string.IsNullOrEmpty(value)) return;
            items.RemoveAll(v => Comparer.Equals(v, value));
            items.Insert(0, value);
            if (items.Count > MaxItems) items.RemoveRange(MaxItems, items.Count - MaxItems);
        }

        /// <summary>從下拉選單移除一筆。</summary>
        public static bool Forget(InputHistoryData history, HistoryField field, string value) =>
            Items(history, field).RemoveAll(v => Comparer.Equals(v, value.Trim())) > 0;

        /// <summary>
        /// 輸入時的建議：包含輸入文字的值（不分大小寫），開頭就符合的排前面，其餘照原本（最近用過）的順序。
        /// 沒有輸入時回傳全部。
        /// </summary>
        public static List<string> Filter(IEnumerable<string> items, string? text)
        {
            text = text?.Trim() ?? "";
            if (text.Length == 0) return items.ToList();
            return items
                .Where(v => v.Contains(text, StringComparison.OrdinalIgnoreCase))
                .OrderBy(v => v.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToList();
        }
    }
}
