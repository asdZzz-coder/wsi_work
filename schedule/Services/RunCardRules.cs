using System.Text.Json;
using schedule.Models;

namespace schedule.Services
{
    /// <summary>流程站別的狀態。</summary>
    public enum StepState { Pending, Soon, Overdue, InProgress, Done, Late, Skipped, Passed }

    /// <summary>
    /// 流程卡（Run Card）的規則：預設流程與檢驗項目、站別日期與工令時間點的對應、進度、目前站別、暫停與異常。
    /// 不依賴畫面，方便自動測試。
    /// </summary>
    public static class RunCardRules
    {
        /// <summary>新工令的預設流程（配電盤）。有對應時間點的站別不能刪，只能略過。</summary>
        public static List<RunStep> DefaultSteps() => new()
        {
            new() { Name = "圖面確認" },
            new() { Name = "材料入場", Link = StepLink.Material },
            new() { Name = "箱體組立" },
            new() { Name = "配電", Link = StepLink.Wiring },
            new() { Name = "自主檢查" },
            new() { Name = "品管檢驗" },
            new() { Name = "出料", Link = StepLink.Dispatch },
            new() { Name = "交貨 / 驗收", Link = StepLink.Delivery },
        };

        /// <summary>預設的品管檢驗項目；有 CE / TS 的工令另外加文件檢查。</summary>
        public static List<CheckItem> DefaultChecks(Job job)
        {
            var list = new List<CheckItem>
            {
                new() { Name = "外觀 / 塗裝", Standard = "無刮傷、掉漆、變形" },
                new() { Name = "銘牌 / 標示", Standard = "銘牌、警告標示、線號正確" },
                new() { Name = "配線檢查", Standard = "依圖面，線徑與顏色正確" },
                new() { Name = "端子鎖緊", Standard = "依規定扭力，做記號" },
                new() { Name = "接地", Standard = "接地線確實連接" },
                new() { Name = "絕緣電阻", Standard = "≥ 1 MΩ（DC 500 V）" },
                new() { Name = "耐壓測試", Standard = "AC 2000 V / 1 分鐘無異常" },
                new() { Name = "通電 / 動作測試", Standard = "指示燈、儀表、控制迴路動作正常" },
                new() { Name = "保護設定", Standard = "斷路器、保護電驛設定值正確" },
                new() { Name = "出貨文件", Standard = "圖面、測試報告、鑰匙、備品" },
            };
            AddCertChecks(job, list);
            return list;
        }

        private static void AddCertChecks(Job job, List<CheckItem> list)
        {
            if (job.HasCE && !list.Any(c => c.Name == CeCheck))
                list.Add(new CheckItem { Name = CeCheck, Standard = "CE 符合性文件、技術文件" });
            if (job.HasTS && !list.Any(c => c.Name == TsCheck))
                list.Add(new CheckItem { Name = TsCheck, Standard = "TS 認證要求的文件與標示" });
        }

        private const string CeCheck = "CE 文件";
        private const string TsCheck = "TS 文件";

        /// <summary>
        /// 補齊流程卡：舊資料沒有流程或檢驗項目時放預設內容；四個時間點的站別一定要有（被刪掉就補回去）；
        /// 勾了 CE / TS 就加上文件檢查。
        /// </summary>
        public static void Ensure(Job job)
        {
            job.Steps ??= new();
            job.Checks ??= new();
            job.Issues ??= new();
            job.Dispatches ??= new();
            job.DispatchGroup ??= "";
            job.Steps.RemoveAll(s => s == null);
            job.Checks.RemoveAll(c => c == null);
            job.Issues.RemoveAll(i => i == null);
            job.Dispatches.RemoveAll(d => d == null);

            if (job.Steps.Count == 0) job.Steps = DefaultSteps();
            var defaults = DefaultSteps();
            foreach (var link in new[] { StepLink.Material, StepLink.Wiring, StepLink.Dispatch, StepLink.Delivery })
            {
                if (job.Steps.Any(s => s.Link == link)) continue;
                // 依時間點的先後插在後面那個時間點之前，沒有就放最後
                var later = job.Steps.FindIndex(s => s.Link > link);
                var step = defaults.First(s => s.Link == link);
                if (later < 0) job.Steps.Add(step); else job.Steps.Insert(later, step);
            }
            // 同一個時間點只能對應一個站別
            foreach (var group in job.Steps.Where(s => s.Link != StepLink.None).GroupBy(s => s.Link))
                foreach (var extra in group.Skip(1)) extra.Link = StepLink.None;

            if (job.Checks.Count == 0) job.Checks = DefaultChecks(job);
            else AddCertChecks(job, job.Checks);
        }

        // ---------- 站別日期：有對應時間點的讀寫工令上的日期 ----------

        public static DateTime? PlanOf(Job job, RunStep step) => step.Link switch
        {
            StepLink.Material => job.MaterialPlan,
            StepLink.Wiring => job.WiringPlan,
            StepLink.Dispatch => job.DispatchPlan,
            StepLink.Delivery => job.DeliveryPlan,
            _ => step.Plan,
        };

        public static DateTime? DoneOf(Job job, RunStep step) => step.Link switch
        {
            StepLink.Material => job.MaterialActual,
            StepLink.Wiring => job.WiringActual,
            StepLink.Dispatch => job.DispatchActual,
            StepLink.Delivery => job.DeliveryActual,
            _ => step.Done,
        };

        public static void SetPlan(Job job, RunStep step, DateTime? value)
        {
            switch (step.Link)
            {
                case StepLink.Material: job.MaterialPlan = value; break;
                case StepLink.Wiring: job.WiringPlan = value; break;
                case StepLink.Dispatch: job.DispatchPlan = value; break;
                case StepLink.Delivery: job.DeliveryPlan = value; break;
                default: step.Plan = value; break;
            }
        }

        public static void SetDone(Job job, RunStep step, DateTime? value)
        {
            switch (step.Link)
            {
                case StepLink.Material: job.MaterialActual = value; break;
                case StepLink.Wiring: job.WiringActual = value; break;
                case StepLink.Dispatch: job.DispatchActual = value; break;
                case StepLink.Delivery: job.DeliveryActual = value; break;
                default: step.Done = value; break;
            }
        }

        /// <summary>對應的時間點名稱（顯示在站別旁邊）；沒有對應回傳空字串。</summary>
        public static string LinkName(StepLink link) => link switch
        {
            StepLink.Material => "材料入場",
            StepLink.Wiring => "配電",
            StepLink.Dispatch => "出料",
            StepLink.Delivery => "交期",
            _ => "",
        };

        // ---------- 狀態與進度 ----------

        public static StepState State(Job job, RunStep step, DateTime today) =>
            State(PlanOf(job, step), step.Started, DoneOf(job, step), step.Skipped, today);

        public static StepState State(DateTime? plan, DateTime? started, DateTime? done, bool skipped, DateTime today)
        {
            if (skipped) return StepState.Skipped;
            switch (ScheduleRules.State(plan, done, today))
            {
                case MilestoneState.Done: return StepState.Done;
                case MilestoneState.Late: return StepState.Late;
                case MilestoneState.Overdue: return StepState.Overdue;
            }
            if (started != null) return StepState.InProgress;
            return ScheduleRules.State(plan, done, today) == MilestoneState.Soon ? StepState.Soon : StepState.Pending;
        }

        public static string StateText(StepState state) => state switch
        {
            StepState.Done => "完成",
            StepState.Late => "延遲完成",
            StepState.Overdue => "逾期",
            StepState.InProgress => "進行中",
            StepState.Soon => "快到期",
            StepState.Skipped => "略過",
            StepState.Passed => "已過站",
            _ => "未開始",
        };

        private static bool IsFinished(Job job, RunStep step) => step.Skipped || DoneOf(job, step) != null;

        /// <summary>
        /// 每一站是否已經過了：自己完成或略過，或是後面有站已經完成（前面沒填日期的站當成已經過站）。
        /// 已交貨的工令每一站都算過了。
        /// </summary>
        public static bool[] Passed(Job job)
        {
            var steps = job.Steps ?? new();
            if (job.DeliveryActual != null) return steps.Select(_ => true).ToArray();
            return Passed(steps.Select(s => IsFinished(job, s)).ToList());
        }

        /// <summary>同上，只看每一站自己有沒有完成（流程卡視窗編輯中用）。</summary>
        public static bool[] Passed(IReadOnlyList<bool> finished)
        {
            int last = -1;
            for (int i = 0; i < finished.Count; i++) if (finished[i]) last = i;
            return finished.Select((f, i) => f || i < last).ToArray();
        }

        /// <summary>過了幾站、總共幾站。</summary>
        public static (int Done, int Total) Progress(Job job)
        {
            var passed = Passed(job);
            return (passed.Count(p => p), passed.Length);
        }

        /// <summary>目前做到的站：第一個還沒過的站。全部完成時為 null。</summary>
        public static RunStep? CurrentStep(Job job)
        {
            var passed = Passed(job);
            int i = Array.IndexOf(passed, false);
            return i < 0 ? null : job.Steps[i];
        }

        /// <summary>清單上的進度文字，例如「配電 · 3/8」、「全部完成」。</summary>
        public static string ProgressText(Job job)
        {
            var (done, total) = Progress(job);
            if (total == 0) return "";
            return CurrentStep(job) is { } step ? $"{step.Name} · {done}/{total}" : "全部完成";
        }

        public static bool IsOnHold(Job job) => job.HoldReason.Trim().Length > 0 && job.DeliveryActual == null;

        public static int OpenIssues(Job job) => (job.Issues ?? new()).Count(i => i.Closed == null);

        public static int FailedChecks(Job job) => (job.Checks ?? new()).Count(c => c.Result == CheckResult.Fail);

        /// <summary>需要注意：暫停中、有還沒結案的異常、或有檢驗不合格。</summary>
        public static bool NeedsAttention(Job job) => IsOnHold(job) || OpenIssues(job) > 0 || FailedChecks(job) > 0;

        /// <summary>需要注意的原因（看板與提示用），例如「暫停：等客戶確認圖面」「1 筆異常未結案」。</summary>
        public static List<string> AttentionReasons(Job job)
        {
            var list = new List<string>();
            if (IsOnHold(job)) list.Add($"暫停：{job.HoldReason.Trim()}");
            if (OpenIssues(job) is var open and > 0) list.Add($"{open} 筆異常未結案");
            if (FailedChecks(job) is var failed and > 0) list.Add($"{failed} 項檢驗不合格");
            return list;
        }

        public static string ResultText(CheckResult result) => result switch
        {
            CheckResult.Pass => "合格",
            CheckResult.Fail => "不合格",
            CheckResult.NotApplicable => "不適用",
            _ => "未檢",
        };

        public static string PriorityText(JobPriority priority) => priority switch
        {
            JobPriority.Urgent => "急件",
            JobPriority.Rush => "特急",
            _ => "一般",
        };

        /// <summary>品管檢驗摘要，例如「8 項合格、1 項不合格、2 項未檢」。</summary>
        public static string CheckSummary(Job job)
        {
            var checks = job.Checks ?? new();
            if (checks.Count == 0) return "沒有檢驗項目";
            var parts = new List<string>();
            void Add(CheckResult r, string text)
            {
                int n = checks.Count(c => c.Result == r);
                if (n > 0) parts.Add($"{n} 項{text}");
            }
            Add(CheckResult.Pass, "合格");
            Add(CheckResult.Fail, "不合格");
            Add(CheckResult.NotApplicable, "不適用");
            Add(CheckResult.Pending, "未檢");
            return string.Join("、", parts);
        }

        /// <summary>
        /// 各站別目前有幾筆工令（還沒交貨、依流程順序）：每筆工令算在它目前做到的那一站。
        /// 站名相同的合併；順序依第一次出現的位置。
        /// </summary>
        public static List<CountItem> WorkInProcess(IEnumerable<Job> jobs)
        {
            var order = new List<string>();
            var counts = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var job in jobs.Where(j => j.DeliveryActual == null))
            {
                foreach (var s in job.Steps ?? new())
                    if (!counts.ContainsKey(s.Name)) { counts[s.Name] = 0; order.Add(s.Name); }
                if (CurrentStep(job) is { } step) counts[step.Name]++;
            }
            return order.Where(n => counts[n] > 0).Select(n => new CountItem(n, counts[n])).ToList();
        }

        // ---------- 複製 ----------

        private static readonly JsonSerializerOptions CloneOptions = new();

        /// <summary>完整複製一筆工令（流程卡編輯時用複本，按「儲存」才寫回去）。</summary>
        public static Job Clone(Job job) =>
            JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job, CloneOptions), CloneOptions)!;

        /// <summary>
        /// 「複製工令」：基本資料、人員、流程站別、檢驗項目與出料料件留著，日期、完成狀態、檢驗結果、異常紀錄、出料同步都清掉，
        /// 方便開同一機種的下一張工令。
        /// </summary>
        public static Job CopyAsNew(Job job)
        {
            var copy = Clone(job);
            copy.Id = Guid.NewGuid().ToString("N");
            copy.MaterialPlan = copy.MaterialActual = null;
            copy.WiringPlan = copy.WiringActual = null;
            copy.DispatchPlan = copy.DispatchActual = null;
            copy.DeliveryPlan = copy.DeliveryActual = null;
            copy.HoldReason = "";
            foreach (var s in copy.Steps)
            {
                s.Plan = s.Started = s.Done = null;
                s.Signer = "";
                s.Skipped = false;
                s.Note = "";
            }
            foreach (var c in copy.Checks)
            {
                c.Measured = c.Inspector = c.Note = "";
                c.Result = CheckResult.Pending;
                c.Date = null;
            }
            copy.Issues = new();
            // 出料的料件留著（同機種通常出一樣的料），出料日期清掉；同步要自己在新工令上勾
            foreach (var d in copy.Dispatches) d.Date = null;
            copy.DispatchGroup = "";
            return copy;
        }

        /// <summary>
        /// 預計日期前後顛倒的提醒：站別的預計完成日期應該依流程順序。只比較有填、沒略過的站；沒問題回傳 null。
        /// </summary>
        public static string? StepOrderWarning(Job job)
        {
            var plans = (job.Steps ?? new())
                .Where(s => !s.Skipped && PlanOf(job, s) != null)
                .Select(s => (s.Name, Date: PlanOf(job, s)!.Value.Date))
                .ToList();
            for (int i = 0; i + 1 < plans.Count; i++)
                if (plans[i].Date > plans[i + 1].Date)
                    return $"「{plans[i].Name}」的預計日期比下一站「{plans[i + 1].Name}」晚。";
            return null;
        }
    }
}
