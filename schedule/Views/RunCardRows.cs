using System.ComponentModel;
using System.Runtime.CompilerServices;
using schedule.Models;
using schedule.Services;

namespace schedule.Views
{
    /// <summary>流程卡上可以編輯的一列：屬性改了就通知畫面，並讓視窗知道有修改。</summary>
    public abstract class EditRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            Raise(name!);
            OnChanged(name!);
        }

        protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>某個欄位改了之後要重算的東西（例如狀態標籤）。</summary>
        protected virtual void OnChanged(string name) { }

        protected static DateTime? ParseOrNull(string text) => TextFormat.TryParseOptionalDate(text, out var d) ? d : null;

        /// <summary>狀態標籤的顏色：Overdue / Soon / Late / Done 用 StateChip 的顏色，Muted 是灰色。</summary>
        protected static string ChipTag(StepState state) => state switch
        {
            StepState.Overdue => "Overdue",
            StepState.Soon => "Soon",
            StepState.Late => "Late",
            StepState.Done => "Done",
            StepState.InProgress => "",
            StepState.Passed => "Done",
            _ => "Muted",
        };
    }

    /// <summary>流程站別的一列。有對應時間點的站別，預計 / 實際完成就是工令上那個時間點的日期。</summary>
    public sealed class StepRow : EditRow
    {
        private int _number;
        private string _name, _owner, _plan, _started, _done, _signer, _note;
        private bool _skipped;

        public StepRow(RunStep step, Job job)
        {
            Link = step.Link;
            _name = step.Name;
            _owner = step.Owner;
            _plan = TextFormat.Date(RunCardRules.PlanOf(job, step));
            _started = TextFormat.Date(step.Started);
            _done = TextFormat.Date(RunCardRules.DoneOf(job, step));
            _signer = step.Signer;
            _skipped = step.Skipped;
            _note = step.Note;
        }

        public StepLink Link { get; }
        public bool IsLinked => Link != StepLink.None;
        public bool CanDelete => !IsLinked;
        public string LinkToolTip => IsLinked
            ? $"這站和工令的「{RunCardRules.LinkName(Link)}」日期連動：在這裡改，清單、甘特圖、時間軸也會跟著改。這站不能刪除，不需要時可以按「略過」。"
            : "";

        public int Number { get => _number; set => Set(ref _number, value); }
        public string Name { get => _name; set => Set(ref _name, value); }
        public string Owner { get => _owner; set => Set(ref _owner, value); }
        public string PlanText { get => _plan; set => Set(ref _plan, value); }
        public string StartedText { get => _started; set => Set(ref _started, value); }
        public string DoneText { get => _done; set => Set(ref _done, value); }
        public string Signer { get => _signer; set => Set(ref _signer, value); }
        public bool Skipped { get => _skipped; set => Set(ref _skipped, value); }
        public string Note { get => _note; set => Set(ref _note, value); }

        /// <summary>自己沒填完成日期，但後面的站已經完成（由視窗設定）。</summary>
        public bool ImplicitlyPassed
        {
            get => _implicit;
            set { Set(ref _implicit, value); }
        }
        private bool _implicit;

        /// <summary>自己完成或略過。</summary>
        public bool IsFinished => Skipped || ParseOrNull(DoneText) != null;

        public StepState State
        {
            get
            {
                var state = RunCardRules.State(ParseOrNull(PlanText), ParseOrNull(StartedText), ParseOrNull(DoneText), Skipped, DateTime.Today);
                return ImplicitlyPassed && state is not (StepState.Done or StepState.Late or StepState.Skipped) ? StepState.Passed : state;
            }
        }
        public string StateText => RunCardRules.StateText(State);
        public string StateTag => ChipTag(State);

        protected override void OnChanged(string name)
        {
            if (name is nameof(PlanText) or nameof(StartedText) or nameof(DoneText) or nameof(Skipped) or nameof(ImplicitlyPassed))
            {
                Raise(nameof(State));
                Raise(nameof(StateText));
                Raise(nameof(StateTag));
            }
        }

        public RunStep ToStep() => new()
        {
            Name = Name.Trim(),
            Link = Link,
            Owner = Owner.Trim(),
            Started = ParseOrNull(StartedText),
            Signer = Signer.Trim(),
            Skipped = Skipped,
            Note = Note.TrimEnd(),
        };
    }

    /// <summary>品管檢驗的一列。結果用下拉選單的位置（0 未檢、1 合格、2 不合格、3 不適用）。</summary>
    public sealed class CheckRow : EditRow
    {
        private int _number, _result;
        private string _name, _standard, _measured, _inspector, _date, _note;

        public CheckRow(CheckItem item)
        {
            _name = item.Name;
            _standard = item.Standard;
            _measured = item.Measured;
            _result = (int)item.Result;
            _inspector = item.Inspector;
            _date = TextFormat.Date(item.Date);
            _note = item.Note;
        }

        public int Number { get => _number; set => Set(ref _number, value); }
        public string Name { get => _name; set => Set(ref _name, value); }
        public string Standard { get => _standard; set => Set(ref _standard, value); }
        public string Measured { get => _measured; set => Set(ref _measured, value); }
        public int ResultIndex { get => _result; set => Set(ref _result, value); }
        public string Inspector { get => _inspector; set => Set(ref _inspector, value); }
        public string DateText { get => _date; set => Set(ref _date, value); }
        public string Note { get => _note; set => Set(ref _note, value); }

        public CheckResult Result => (CheckResult)Math.Clamp(ResultIndex, 0, 3);
        public bool IsFail => Result == CheckResult.Fail;

        protected override void OnChanged(string name)
        {
            if (name == nameof(ResultIndex)) Raise(nameof(IsFail));
        }

        public CheckItem ToItem() => new()
        {
            Name = Name.Trim(),
            Standard = Standard.Trim(),
            Measured = Measured.Trim(),
            Result = Result,
            Inspector = Inspector.Trim(),
            Date = ParseOrNull(DateText),
            Note = Note.TrimEnd(),
        };
    }

    /// <summary>異常 / 變更紀錄的一列。</summary>
    public sealed class IssueRow : EditRow
    {
        private string _date, _type, _description, _owner, _action, _closed;

        public IssueRow(IssueRecord issue)
        {
            _date = TextFormat.Date(issue.Date);
            _type = issue.Type;
            _description = issue.Description;
            _owner = issue.Owner;
            _action = issue.Action;
            _closed = TextFormat.Date(issue.Closed);
        }

        public string DateText { get => _date; set => Set(ref _date, value); }
        public string Type { get => _type; set => Set(ref _type, value); }
        public string Description { get => _description; set => Set(ref _description, value); }
        public string Owner { get => _owner; set => Set(ref _owner, value); }
        public string Action { get => _action; set => Set(ref _action, value); }
        public string ClosedText { get => _closed; set => Set(ref _closed, value); }

        public bool IsOpen => ClosedText.Trim().Length == 0;
        public string StateText => IsOpen ? "未結案" : "已結案";
        public string StateTag => IsOpen ? "Soon" : "Done";

        protected override void OnChanged(string name)
        {
            if (name != nameof(ClosedText)) return;
            Raise(nameof(IsOpen));
            Raise(nameof(StateText));
            Raise(nameof(StateTag));
        }

        public bool IsEmpty => Type.Trim().Length == 0 && Description.Trim().Length == 0 && Action.Trim().Length == 0;

        public IssueRecord ToRecord() => new()
        {
            Date = ParseOrNull(DateText),
            Type = Type.Trim(),
            Description = Description.Trim(),
            Owner = Owner.Trim(),
            Action = Action.Trim(),
            Closed = ParseOrNull(ClosedText),
        };
    }
}
