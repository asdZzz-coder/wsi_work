using System.Globalization;
using System.Text;

namespace schedule.Services
{
    /// <summary>
    /// 數字與日期的輸入解析、畫面顯示格式。
    /// 輸入時容許前後空白，以及中文輸入法打出來的全形數字（２０２６／１０／０６）。
    /// </summary>
    public static class TextFormat
    {
        public const string DateFormat = "yyyy/MM/dd";

        private static readonly string[] DateFormats =
        {
            "yyyy/M/d", "yyyy-M-d", "yyyy.M.d", "yyyyMMdd", "yyyy/M/d H:mm", "yyyy/M/d H:mm:ss",
        };

        /// <summary>全形轉半形、去掉千分位與空白。</summary>
        public static string Normalize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var sb = new StringBuilder(text.Length);
            foreach (var c in text.Trim())
            {
                var h = c is >= '！' and <= '～' ? (char)(c - 0xFEE0) : c; // 全形 ASCII → 半形
                if (h is ',' or ' ' or '　') continue;
                if (h == '。') h = '.';
                sb.Append(h);
            }
            return sb.ToString();
        }

        public static bool TryParseInt(string? text, out int value) =>
            int.TryParse(Normalize(text), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

        public static bool TryParseDate(string? text, out DateTime value) => TryParseDate(text, DateTime.Today, out value);

        internal static bool TryParseDate(string? text, DateTime today, out DateTime value)
        {
            var t = Normalize(text);
            if (DateTime.TryParseExact(t, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
                return true;
            // 自己拆月、日：交給 DateTime 解析的話，沒有年份時會用「現在」的年份檢查 2/29
            var parts = t.Split('/', '-', '.');
            if (parts.Length == 2 && parts.All(p => p.Length is 1 or 2 && p.All(char.IsAsciiDigit)))
            {
                int month = int.Parse(parts[0], CultureInfo.InvariantCulture), day = int.Parse(parts[1], CultureInfo.InvariantCulture);
                if (month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(today.Year, month))
                {
                    value = new DateTime(today.Year, month, day);
                    return true;
                }
            }
            value = default;
            return false;
        }

        /// <summary>
        /// 選填日期欄：空白 → null（成功）；看得懂 → 日期（成功）；看不懂 → 失敗。
        /// </summary>
        public static bool TryParseOptionalDate(string? text, out DateTime? value)
        {
            value = null;
            if (Normalize(text).Length == 0) return true;
            if (!TryParseDate(text, out var d)) return false;
            value = d;
            return true;
        }

        public static string Date(DateTime d) => d.ToString(DateFormat, CultureInfo.InvariantCulture);

        public static string Date(DateTime? d) => d is { } v ? Date(v) : "";

        /// <summary>清單用的短日期：今年只顯示 10/06，其他年份顯示 25/10/06。</summary>
        public static string ShortDate(DateTime? d, DateTime today) => d is not { } v ? "—"
            : v.Year == today.Year ? v.ToString("MM/dd", CultureInfo.InvariantCulture)
            : v.ToString("yy/MM/dd", CultureInfo.InvariantCulture);
    }
}
