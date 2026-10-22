using schedule.Models;

namespace schedule.Services
{
    /// <summary>大倉庫（物料清單）的篩選、新增 / 更新、貼上、轉成出料紀錄。</summary>
    public static class MaterialRules
    {
        private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

        /// <summary>
        /// 篩選：關鍵字用空格分開時每個都要符合（料號、名稱、規格、分類、備註任一個包含就算）；
        /// category 空白 = 全部分類。
        /// </summary>
        public static bool Matches(Material m, string? keyword, string? category = null)
        {
            if (!string.IsNullOrEmpty(category) && !Comparer.Equals(m.Category.Trim(), category.Trim())) return false;
            var words = (keyword ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var fields = new[] { m.PartNo, m.Name, m.Spec, m.Category, m.Unit, m.Note };
            return words.All(w => fields.Any(f => f.Contains(w, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>依分類、料號排序（料號裡的數字依數值比較）。</summary>
        public static IEnumerable<Material> Sort(IEnumerable<Material> items) =>
            items.OrderBy(m => m.Category.Trim().Length == 0)
                 .ThenBy(m => m.Category.Trim(), StringComparer.CurrentCultureIgnoreCase)
                 .ThenBy(m => m.PartNo.Trim().Length == 0)
                 .ThenBy(m => m.PartNo.Trim(), NaturalComparer.Instance)
                 .ThenBy(m => m.Name.Trim(), StringComparer.CurrentCultureIgnoreCase);

        public static List<string> Categories(IEnumerable<Material> items) =>
            items.Select(m => m.Category.Trim()).Where(c => c.Length > 0).Distinct(Comparer)
                 .OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase).ToList();

        /// <summary>同一項物料：有料號就比料號；兩邊都沒有料號時比名稱 + 規格。</summary>
        public static Material? Find(IEnumerable<Material> items, Material probe, Material? except = null)
        {
            var no = probe.PartNo.Trim();
            return items.FirstOrDefault(m => !ReferenceEquals(m, except) && (no.Length > 0
                ? Comparer.Equals(m.PartNo.Trim(), no)
                : m.PartNo.Trim().Length == 0 && Comparer.Equals(m.Name.Trim(), probe.Name.Trim()) && Comparer.Equals(m.Spec.Trim(), probe.Spec.Trim())));
        }

        public static Material? FindPartNo(IEnumerable<Material> items, string partNo)
        {
            partNo = partNo.Trim();
            return partNo.Length == 0 ? null : items.FirstOrDefault(m => Comparer.Equals(m.PartNo.Trim(), partNo));
        }

        /// <summary>
        /// 把一批物料併進大倉庫：已經有的（同料號）更新名稱、規格等（新的值空白時保留原本的），沒有的新增。
        /// </summary>
        public static (int Added, int Updated) Merge(List<Material> warehouse, IEnumerable<Material> incoming)
        {
            int added = 0, updated = 0;
            foreach (var item in incoming)
            {
                Clean(item);
                if (item.PartNo.Length == 0 && item.Name.Length == 0) continue;
                var same = Find(warehouse, item);
                if (same == null)
                {
                    warehouse.Add(item);
                    added++;
                    continue;
                }
                bool changed = false;
                void Take(Func<Material, string> get, Action<Material, string> set)
                {
                    var v = get(item);
                    if (v.Length > 0 && v != get(same)) { set(same, v); changed = true; }
                }
                Take(m => m.PartNo, (m, v) => m.PartNo = v);
                Take(m => m.Name, (m, v) => m.Name = v);
                Take(m => m.Spec, (m, v) => m.Spec = v);
                Take(m => m.Unit, (m, v) => m.Unit = v);
                Take(m => m.Category, (m, v) => m.Category = v);
                Take(m => m.Note, (m, v) => m.Note = v);
                if (changed) updated++;
            }
            return (added, updated);
        }

        public static void Clean(Material m)
        {
            m.PartNo = (m.PartNo ?? "").Trim();
            m.Name = (m.Name ?? "").Trim();
            m.Spec = (m.Spec ?? "").Trim();
            m.Unit = (m.Unit ?? "").Trim();
            m.Category = (m.Category ?? "").Trim();
            m.Note = (m.Note ?? "").Trim();
            if (string.IsNullOrEmpty(m.Id)) m.Id = Guid.NewGuid().ToString("N");
        }

        /// <summary>出料紀錄裡出過、但大倉庫還沒有的料（同料號只算一次，用最近一次的名稱 / 規格）。</summary>
        public static List<Material> FromDispatches(IEnumerable<Job> jobs, IEnumerable<Material> warehouse)
        {
            var have = warehouse.ToList();
            var result = new List<Material>();
            foreach (var info in DispatchSync.Catalog(jobs).Values)
            {
                if (FindPartNo(have, info.PartNo) != null) continue;
                result.Add(new Material { PartNo = info.PartNo, Name = info.Name, Spec = info.Spec, Unit = info.Unit });
            }
            return Sort(result).ToList();
        }

        public static DispatchItem ToDispatch(Material m, string quantity, DateTime? date) => new()
        {
            PartNo = m.PartNo.Trim(),
            Name = m.Name.Trim(),
            Spec = m.Spec.Trim(),
            Unit = m.Unit.Trim(),
            Quantity = quantity.Trim(),
            Date = date,
        };

        // ---------- 從 Excel 複製貼上 ----------

        /// <summary>標題的各種寫法 → 欄位。</summary>
        internal static readonly (string Field, string[] Names)[] HeaderNames =
        {
            ("PartNo", new[] { "料號", "品號", "料件編號", "物料編號", "編號", "PartNo", "Part No", "P/N" }),
            ("Name", new[] { "名稱", "品名", "物料名稱", "料件名稱", "Name" }),
            ("Spec", new[] { "規格", "型號", "規格型號", "Spec" }),
            ("Unit", new[] { "單位", "Unit" }),
            ("Category", new[] { "分類", "類別", "種類", "Category" }),
            ("Note", new[] { "備註", "說明", "Note" }),
        };

        internal static string? FieldOf(string header)
        {
            header = header.Trim();
            foreach (var (field, names) in HeaderNames)
                if (names.Any(n => Comparer.Equals(n, header))) return field;
            return null;
        }

        internal static void Set(Material m, string field, string value)
        {
            value = value.Trim();
            switch (field)
            {
                case "PartNo": m.PartNo = value; break;
                case "Name": m.Name = value; break;
                case "Spec": m.Spec = value; break;
                case "Unit": m.Unit = value; break;
                case "Category": m.Category = value; break;
                case "Note": m.Note = value; break;
            }
        }

        /// <summary>
        /// 解析從 Excel 複製的表格（Tab 分隔）。第一列是標題（料號、名稱、規格…）就依標題對應；
        /// 沒有標題時依序當成 料號、名稱、規格、單位、分類、備註。沒有料號也沒有名稱的列略過。
        /// </summary>
        public static List<Material> ParseTable(string text)
        {
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                .Where(l => l.Trim().Length > 0).Select(l => l.Split('\t')).ToList();
            if (lines.Count == 0) return new();

            string?[] map = lines[0].Select(FieldOf).ToArray();
            bool hasHeader = map.Any(f => f != null);
            if (!hasHeader) map = new string?[] { "PartNo", "Name", "Spec", "Unit", "Category", "Note" };

            var result = new List<Material>();
            foreach (var cells in lines.Skip(hasHeader ? 1 : 0))
            {
                var m = new Material();
                for (int i = 0; i < cells.Length && i < map.Length; i++)
                    if (map[i] is { } field) Set(m, field, cells[i]);
                if (m.PartNo.Length == 0 && m.Name.Length == 0) continue;
                result.Add(m);
            }
            return result;
        }
    }
}
