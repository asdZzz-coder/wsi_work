namespace schedule.Services
{
    /// <summary>
    /// Code 39 條碼（流程卡上印工令，讓掃描器可以讀）。只支援 0-9、A-Z、- . 空白 $ / + %；
    /// 小寫字母自動轉大寫，有其他字元（例如中文）時無法編碼。
    /// </summary>
    public static class Code39
    {
        // 每個字元 9 個元素（條、空交錯，從條開始），1 = 寬、0 = 窄
        private static readonly Dictionary<char, string> Patterns = new()
        {
            ['0'] = "000110100", ['1'] = "100100001", ['2'] = "001100001", ['3'] = "101100000",
            ['4'] = "000110001", ['5'] = "100110000", ['6'] = "001110000", ['7'] = "000100101",
            ['8'] = "100100100", ['9'] = "001100100",
            ['A'] = "100001001", ['B'] = "001001001", ['C'] = "101001000", ['D'] = "000011001",
            ['E'] = "100011000", ['F'] = "001011000", ['G'] = "000001101", ['H'] = "100001100",
            ['I'] = "001001100", ['J'] = "000011100", ['K'] = "100000011", ['L'] = "001000011",
            ['M'] = "101000010", ['N'] = "000010011", ['O'] = "100010010", ['P'] = "001010010",
            ['Q'] = "000000111", ['R'] = "100000110", ['S'] = "001000110", ['T'] = "000010110",
            ['U'] = "110000001", ['V'] = "011000001", ['W'] = "111000000", ['X'] = "010010001",
            ['Y'] = "110010000", ['Z'] = "011010000",
            ['-'] = "010000101", ['.'] = "110000100", [' '] = "011000100", ['$'] = "010101000",
            ['/'] = "010100010", ['+'] = "010001010", ['%'] = "000101010",
            ['*'] = "010010100", // 起始 / 結束
        };

        public static bool CanEncode(string text) =>
            text.Trim().Length > 0 && text.Trim().ToUpperInvariant().All(c => c != '*' && Patterns.ContainsKey(c));

        /// <summary>
        /// 編碼成一串元素：(是否為條, 是否為寬)。前後自動加上起始 / 結束字元 *，字元之間是窄空白。
        /// 無法編碼時回傳 null。
        /// </summary>
        public static List<(bool Bar, bool Wide)>? Encode(string text)
        {
            if (!CanEncode(text)) return null;
            var result = new List<(bool, bool)>();
            var chars = "*" + text.Trim().ToUpperInvariant() + "*";
            for (int i = 0; i < chars.Length; i++)
            {
                if (i > 0) result.Add((false, false)); // 字元間隔
                var pattern = Patterns[chars[i]];
                for (int k = 0; k < pattern.Length; k++)
                    result.Add((k % 2 == 0, pattern[k] == '1'));
            }
            return result;
        }
    }
}
