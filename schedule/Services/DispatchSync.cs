using schedule.Models;

namespace schedule.Services
{
    /// <summary>料號曾經用過的名稱、規格、單位（輸入料號時自動帶入）。</summary>
    public record PartInfo(string PartNo, string Name, string Spec, string Unit);

    /// <summary>
    /// 出料紀錄與同步：DispatchGroup 相同（不是空白）的工令是同一組，共用同一份出料紀錄。
    /// 在流程卡勾選要同步的機台，存檔時把這台的出料紀錄複製到同組的每一台。
    /// </summary>
    public static class DispatchSync
    {
        /// <summary>和這筆工令同步出料的其他工令（照原本清單的順序）。</summary>
        public static List<Job> Partners(IEnumerable<Job> jobs, Job job) =>
            string.IsNullOrEmpty(job.DispatchGroup)
                ? new()
                : jobs.Where(j => !ReferenceEquals(j, job) && j.Id != job.Id && j.DispatchGroup == job.DispatchGroup).ToList();

        /// <summary>
        /// 流程卡存檔時呼叫：job 只和 partnerIds 這幾台同步（原本同組、但這次沒勾的退出），
        /// 然後把 job 的出料紀錄複製到同組的每一台。回傳出料紀錄被改到的其他工令。
        /// </summary>
        public static List<Job> Apply(List<Job> jobs, Job job, IEnumerable<string> partnerIds)
        {
            var ids = partnerIds.ToHashSet();
            var partners = jobs.Where(j => !ReferenceEquals(j, job) && j.Id != job.Id && ids.Contains(j.Id)).ToList();
            var oldGroup = job.DispatchGroup ?? "";

            if (partners.Count == 0)
            {
                job.DispatchGroup = "";
            }
            else
            {
                var group = oldGroup.Length > 0 ? oldGroup : Guid.NewGuid().ToString("N");
                job.DispatchGroup = group;
                // 原本同組、這次沒勾的退出
                foreach (var other in jobs)
                    if (!ReferenceEquals(other, job) && other.DispatchGroup == group && !partners.Contains(other))
                        other.DispatchGroup = "";
                foreach (var p in partners) p.DispatchGroup = group;
            }
            Normalize(jobs);

            var changed = new List<Job>();
            foreach (var p in Partners(jobs, job))
            {
                if (SameItems(p.Dispatches, job.Dispatches)) continue;
                p.Dispatches = CopyItems(job.Dispatches);
                changed.Add(p);
            }
            return changed;
        }

        /// <summary>只剩一台的組（其他台被刪掉、退出）不算同步，清掉。</summary>
        public static void Normalize(IEnumerable<Job> jobs)
        {
            var list = jobs.ToList();
            foreach (var group in list.Where(j => !string.IsNullOrEmpty(j.DispatchGroup)).GroupBy(j => j.DispatchGroup))
                if (group.Count() < 2)
                    foreach (var j in group) j.DispatchGroup = "";
        }

        /// <summary>
        /// 新勾選的機台裡，原本已經有不一樣的出料紀錄的（存檔後會被這台的取代），給存檔前提醒用。
        /// </summary>
        public static List<Job> WouldOverwrite(IEnumerable<Job> jobs, Job job, IEnumerable<string> partnerIds, IReadOnlyList<DispatchItem> items)
        {
            var ids = partnerIds.ToHashSet();
            return jobs.Where(j => j.Id != job.Id && ids.Contains(j.Id)
                    && (string.IsNullOrEmpty(job.DispatchGroup) || j.DispatchGroup != job.DispatchGroup)
                    && (j.Dispatches?.Count ?? 0) > 0 && !SameItems(j.Dispatches!, items))
                .ToList();
        }

        /// <summary>
        /// 匯入 Excel 時：names 是「出料同步」欄寫的其他工令。寫在同一列、或互相寫到的工令併成一組；
        /// 欄位空白的工令退出原本的組。
        /// </summary>
        public static void ApplyNames(List<Job> jobs, IReadOnlyDictionary<Job, List<string>> names)
        {
            foreach (var job in names.Keys) job.DispatchGroup = "";
            Normalize(jobs);
            foreach (var (job, list) in names)
            {
                if (!jobs.Contains(job)) continue;
                foreach (var name in list)
                {
                    var other = ScheduleRules.FindDuplicate(jobs, name, except: job);
                    if (other != null) Join(jobs, job, other);
                }
            }
        }

        /// <summary>把兩台（以及它們原本的組）併成一組。</summary>
        private static void Join(List<Job> jobs, Job a, Job b)
        {
            var ga = a.DispatchGroup ?? "";
            var gb = b.DispatchGroup ?? "";
            if (ga.Length > 0 && ga == gb) return;
            var group = ga.Length > 0 ? ga : gb.Length > 0 ? gb : Guid.NewGuid().ToString("N");
            foreach (var j in jobs)
                if (gb.Length > 0 && j.DispatchGroup == gb || ga.Length > 0 && j.DispatchGroup == ga)
                    j.DispatchGroup = group;
            a.DispatchGroup = group;
            b.DispatchGroup = group;
        }

        public static List<DispatchItem> CopyItems(IEnumerable<DispatchItem> items) => items.Select(d => new DispatchItem
        {
            PartNo = d.PartNo, Name = d.Name, Spec = d.Spec, Quantity = d.Quantity, Unit = d.Unit, Date = d.Date, Note = d.Note,
        }).ToList();

        public static bool SameItems(IReadOnlyList<DispatchItem> a, IReadOnlyList<DispatchItem> b) =>
            a.Count == b.Count && a.Zip(b).All(p =>
                p.First.PartNo == p.Second.PartNo && p.First.Name == p.Second.Name && p.First.Spec == p.Second.Spec &&
                p.First.Quantity == p.Second.Quantity && p.First.Unit == p.Second.Unit && p.First.Date == p.Second.Date &&
                p.First.Note == p.Second.Note);

        /// <summary>
        /// 所有工令出過的料號（最近存的工令優先），輸入料號時帶入名稱、規格、單位。
        /// </summary>
        public static Dictionary<string, PartInfo> Catalog(IEnumerable<Job> jobs)
        {
            var map = new Dictionary<string, PartInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var job in jobs)
                foreach (var d in job.Dispatches ?? new())
                {
                    var no = d.PartNo.Trim();
                    if (no.Length == 0 || d.Name.Trim().Length == 0 && d.Spec.Trim().Length == 0) continue;
                    map[no] = new PartInfo(no, d.Name.Trim(), d.Spec.Trim(), d.Unit.Trim());
                }
            return map;
        }

        /// <summary>某個欄位用過的值（不重複，最近的工令排前面），給下拉選單。</summary>
        public static List<string> Values(IEnumerable<Job> jobs, Func<DispatchItem, string> field) =>
            jobs.Reverse().SelectMany(j => j.Dispatches ?? new()).Select(d => field(d).Trim())
                .Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>清單、Excel 用的簡短說明，例如「12 項」。</summary>
        public static string Summary(Job job) => (job.Dispatches?.Count ?? 0) == 0 ? "" : $"{job.Dispatches!.Count} 項";
    }
}
