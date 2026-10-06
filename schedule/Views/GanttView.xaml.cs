using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using schedule.Models;
using schedule.Services;

namespace schedule.Views
{
    /// <summary>
    /// 甘特圖：每筆工令一列。上面的粗條是預計（材料入場 → 配電 → 交期），下面的細條是實際完成，
    /// 菱形是預計日期、圓點是實際日期，紅線是今天。點一列就會在右邊表單打開那筆工令。
    /// </summary>
    public partial class GanttView : UserControl
    {
        private const double RowHeight = 48;
        private const double NameWidth = 200;
        private const double PlanBarY = 10, PlanBarHeight = 12;   // 預計橫條在列內的位置
        private const double ActualBarY = 31, ActualBarHeight = 5; // 實際橫條

        /// <summary>使用者點了某一列。</summary>
        public event Action<Job>? JobClicked;

        private IReadOnlyList<Job> _jobs = Array.Empty<Job>();
        private Job? _selected;
        private DateTime _today = DateTime.Today;
        private GanttZoom _zoom = GanttZoom.Day;
        private DateTime _start;
        private double _dayWidth;
        private bool _scrollToToday = true; // 第一次顯示、換縮放時捲到今天
        private Rectangle? _bodyHighlight, _nameHighlight;

        public GanttView()
        {
            InitializeComponent();
            // 圖表高度至少填滿可見範圍，週末底色與今天的紅線才會畫到底
            BodyScroll.SizeChanged += (_, e) => { if (e.HeightChanged && IsVisible) Draw(); };
        }

        /// <summary>沒有工令時顯示的文字。</summary>
        public string EmptyMessage { get; set; } = "沒有符合條件的工令";

        public void Show(IReadOnlyList<Job> jobs, Job? selected, DateTime today)
        {
            _jobs = jobs;
            _selected = selected;
            _today = today.Date;
            Draw();
        }

        /// <summary>只換選取的列（不重畫），並捲到看得見的地方。</summary>
        public void Select(Job? job)
        {
            _selected = job;
            UpdateHighlight(scrollIntoView: true);
        }

        // ---------- 繪製 ----------

        private void Draw()
        {
            if (BodyCanvas == null) return; // InitializeComponent 期間
            HeaderCanvas.Children.Clear();
            NameCanvas.Children.Clear();
            BodyCanvas.Children.Clear();

            EmptyText.Text = EmptyMessage;
            EmptyText.Visibility = _jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            _dayWidth = GanttMath.DayWidth(_zoom);
            (_start, var end) = GanttMath.Range(_jobs, _today, _zoom);
            int days = (int)(end - _start).TotalDays + 1;
            double width = days * _dayWidth;
            double rowsHeight = _jobs.Count * RowHeight;
            double height = Math.Max(rowsHeight, BodyScroll.ActualHeight - 14);

            BodyCanvas.Width = width;
            BodyCanvas.Height = height;
            // 標題列與工令欄沒有捲軸，多留一點空間，捲到最右 / 最下時才對得齊
            HeaderCanvas.Width = width + 20;
            NameCanvas.Width = NameWidth - 1;
            NameCanvas.Height = height + 20;

            DrawGrid(days, width, height);

            _bodyHighlight = AddRect(BodyCanvas, 0, 0, width, RowHeight, "AccentSoftBrush");
            _nameHighlight = AddRect(NameCanvas, 0, 0, NameWidth, RowHeight, "AccentSoftBrush");

            var hitAreas = new List<UIElement>();
            for (int i = 0; i < _jobs.Count; i++)
                DrawRow(i, _jobs[i], width, hitAreas);

            // 今天的紅線：畫在橫條上面，但不擋滑鼠
            var todayX = GanttMath.CenterX(_today, _start, _dayWidth);
            AddRect(BodyCanvas, todayX - 1, 0, 2, height, "DangerBrush").Opacity = 0.75;

            // 透明的感應區放最上層：滑鼠移上去看完整資料
            foreach (var hit in hitAreas)
                Panel.SetZIndex(hit, 10);

            DrawHeader(days);
            UpdateHighlight(scrollIntoView: false);

            if (_scrollToToday)
            {
                _scrollToToday = false;
                Dispatcher.BeginInvoke(ScrollToToday, DispatcherPriority.Loaded);
            }
        }

        private void DrawGrid(int days, double width, double height)
        {
            for (int d = 0; d < days; d++)
            {
                var date = _start.AddDays(d);
                double x = d * _dayWidth;
                if (_zoom != GanttZoom.Month && date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                    AddRect(BodyCanvas, x, 0, _dayWidth, height, "HoverBrush");

                bool line = _zoom == GanttZoom.Month ? date.Day == 1 : date.DayOfWeek == DayOfWeek.Monday;
                if (line && d > 0)
                    AddRect(BodyCanvas, x, 0, 1, height, "LineBrush");
            }

            for (int i = 1; i <= _jobs.Count; i++)
            {
                AddRect(BodyCanvas, 0, i * RowHeight - 1, width, 1, "LineBrush");
                AddRect(NameCanvas, 0, i * RowHeight - 1, NameWidth, 1, "LineBrush");
            }
        }

        /// <summary>畫一列；這列的感應區加進 hitAreas（之後統一放到最上層）。</summary>
        private void DrawRow(int index, Job job, double width, List<UIElement> hitAreas)
        {
            double y = index * RowHeight;
            var status = ScheduleRules.Status(job, _today);

            // ---- 左邊工令欄 ----
            var dot = new Ellipse { Width = 8, Height = 8, IsHitTestVisible = false };
            dot.SetResourceReference(Shape.FillProperty, status switch
            {
                JobStatus.Overdue => "DangerBrush",
                JobStatus.Done => "SuccessBrush",
                _ => "AccentBrush",
            });
            Place(NameCanvas, dot, 14, y + 14);

            var title = Text(job.WorkOrder, 13, "TextBrush");
            title.FontWeight = FontWeights.SemiBold;
            title.Width = NameWidth - 44;
            Place(NameCanvas, title, 30, y + 7);

            var subText = string.Join(" · ", new[] { job.Model.Trim(), job.Customer.Trim() }.Where(s => s.Length > 0));
            var sub = Text(subText.Length > 0 ? subText : "—", 11.5, "MutedBrush");
            sub.Width = NameWidth - 44;
            Place(NameCanvas, sub, 30, y + 26);

            // ---- 預計橫條 ----
            foreach (var seg in GanttMath.PlanSegments(job))
            {
                double x1 = GanttMath.CenterX(seg.From, _start, _dayWidth);
                double x2 = GanttMath.CenterX(seg.To, _start, _dayWidth);
                var bar = AddRect(BodyCanvas, x1, y + PlanBarY, Math.Max(x2 - x1, 2), PlanBarHeight,
                    seg.StartsAt == MilestoneKind.Material ? "AccentSoftBrush" : "AccentBrush");
                bar.RadiusX = bar.RadiusY = 4;
                if (seg.StartsAt == MilestoneKind.Material)
                {
                    bar.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
                    bar.StrokeThickness = 1;
                }
            }

            // ---- 實際橫條 + 進行中的虛線 ----
            foreach (var seg in GanttMath.ActualSegments(job))
            {
                double x1 = GanttMath.CenterX(seg.From, _start, _dayWidth);
                double x2 = GanttMath.CenterX(seg.To, _start, _dayWidth);
                var bar = AddRect(BodyCanvas, x1, y + ActualBarY, Math.Max(x2 - x1, 2), ActualBarHeight, "SuccessBrush");
                bar.RadiusX = bar.RadiusY = ActualBarHeight / 2;
            }
            if (GanttMath.Progress(job, _today) is { } progress)
            {
                var line = new Line
                {
                    X1 = GanttMath.CenterX(progress.From, _start, _dayWidth),
                    X2 = GanttMath.CenterX(progress.To, _start, _dayWidth),
                    Y1 = y + ActualBarY + ActualBarHeight / 2,
                    Y2 = y + ActualBarY + ActualBarHeight / 2,
                    StrokeThickness = 1.5,
                    StrokeDashArray = new DoubleCollection { 2, 2 },
                    IsHitTestVisible = false,
                };
                line.SetResourceReference(Shape.StrokeProperty, "SubtleBrush");
                BodyCanvas.Children.Add(line);
            }

            // ---- 時間點記號 ----
            foreach (var m in GanttMath.Markers(job, _today))
            {
                double cx = GanttMath.CenterX(m.Date, _start, _dayWidth);
                if (m.IsActual)
                {
                    var c = new Ellipse { Width = 9, Height = 9, StrokeThickness = 1.5, IsHitTestVisible = false };
                    c.SetResourceReference(Shape.FillProperty, m.State == MilestoneState.Late ? "WarningBrush" : "SuccessBrush");
                    c.SetResourceReference(Shape.StrokeProperty, "CardBrush");
                    Place(BodyCanvas, c, cx - 4.5, y + ActualBarY + ActualBarHeight / 2 - 4.5);
                }
                else
                {
                    var diamond = new Rectangle
                    {
                        Width = 10, Height = 10, StrokeThickness = 1.5, IsHitTestVisible = false,
                        RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(45),
                    };
                    var (fill, stroke) = m.State switch
                    {
                        MilestoneState.Overdue => ("DangerBrush", "CardBrush"),
                        MilestoneState.Soon or MilestoneState.Late => ("WarningBrush", "CardBrush"),
                        MilestoneState.Done => ("SuccessBrush", "CardBrush"),
                        _ => ("CardBrush", "AccentBrush"),
                    };
                    diamond.SetResourceReference(Shape.FillProperty, fill);
                    diamond.SetResourceReference(Shape.StrokeProperty, stroke);
                    Place(BodyCanvas, diamond, cx - 5, y + PlanBarY + PlanBarHeight / 2 - 5);
                }
            }

            // ---- 交期文字：放在最右邊的記號後面 ----
            var dates = GanttMath.AllDates(job).ToList();
            if (_zoom != GanttZoom.Month && job.DeliveryPlan is { } due && dates.Count > 0)
            {
                var label = Text($"交期 {TextFormat.ShortDate(due, _today)}", 11, "MutedBrush");
                Place(BodyCanvas, label, GanttMath.CenterX(dates.Max(), _start, _dayWidth) + 10, y + PlanBarY - 1);
            }

            // ---- 感應區：滑鼠游標、提示 ----
            var tip = ToolTipText(job, status);
            foreach (var (canvas, w) in new[] { (BodyCanvas, width), (NameCanvas, NameWidth) })
            {
                var hit = new Rectangle { Width = w, Height = RowHeight, Fill = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = tip };
                ToolTipService.SetInitialShowDelay(hit, 300);
                Place(canvas, hit, 0, y);
                hitAreas.Add(hit);
            }
        }

        private string ToolTipText(Job j, JobStatus status)
        {
            string Line(string name, DateTime? plan, DateTime? actual) =>
                $"{name}　預計 {(plan == null ? "未排定" : TextFormat.Date(plan))}　實際 {(actual == null ? "未完成" : TextFormat.Date(actual))}";

            var head = string.Join(" · ", new[] { j.WorkOrder, j.Model.Trim(), j.Customer.Trim() }.Where(s => s.Length > 0));
            return $"{head}\n" +
                   $"{Line("材料入場", j.MaterialPlan, j.MaterialActual)}\n" +
                   $"{Line("配　　電", j.WiringPlan, j.WiringActual)}\n" +
                   $"{Line("交　　期", j.DeliveryPlan, j.DeliveryActual)}\n" +
                   $"狀態：{ScheduleRules.StatusText(status)}（點一下編輯）";
        }

        private void DrawHeader(int days)
        {
            const double split = 24; // 上排：月份（或年份），下排：日 / 週 / 月
            for (int d = 0; d < days; d++)
            {
                var date = _start.AddDays(d);
                double x = d * _dayWidth;

                // 上排
                bool topStart = _zoom == GanttZoom.Month ? date.DayOfYear == 1 : date.Day == 1;
                if (topStart || d == 0)
                {
                    if (d > 0) AddRect(HeaderCanvas, x, 0, 1, 49, "LineBrush");
                    var text = _zoom == GanttZoom.Month ? $"{date.Year} 年" : $"{date.Year} 年 {date.Month} 月";
                    var top = Text(text, 12, "TextBrush");
                    top.FontWeight = FontWeights.SemiBold;
                    Place(HeaderCanvas, top, x + 8, 5);
                }

                // 下排
                switch (_zoom)
                {
                    case GanttZoom.Day:
                        bool isToday = date == _today;
                        bool weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                        if (isToday)
                        {
                            var pill = AddRect(HeaderCanvas, x + 2, split + 2, _dayWidth - 4, 20, "DangerBrush");
                            pill.RadiusX = pill.RadiusY = 6;
                        }
                        var num = Text(date.Day.ToString(), 11.5, isToday ? "CardBrush" : weekend ? "SubtleBrush" : "MutedBrush");
                        num.Width = _dayWidth;
                        num.TextAlignment = TextAlignment.Center;
                        if (isToday) num.FontWeight = FontWeights.Bold;
                        num.ToolTip = $"{TextFormat.Date(date)}（{Timeline.Weekday(date)}）";
                        num.IsHitTestVisible = true;
                        Place(HeaderCanvas, num, x, split + 4);
                        break;
                    case GanttZoom.Week when date.DayOfWeek == DayOfWeek.Monday:
                        Place(HeaderCanvas, Text($"{date.Month}/{date.Day}", 11.5, "MutedBrush"), x + 4, split + 4);
                        break;
                    case GanttZoom.Month when date.Day == 1:
                        Place(HeaderCanvas, Text($"{date.Month} 月", 11.5, "MutedBrush"), x + 6, split + 4);
                        break;
                }
            }

            if (_zoom != GanttZoom.Day)
                AddRect(HeaderCanvas, GanttMath.CenterX(_today, _start, _dayWidth) - 1, split, 2, 25, "DangerBrush");
        }

        // ---------- 選取 ----------

        private void UpdateHighlight(bool scrollIntoView)
        {
            if (_bodyHighlight == null || _nameHighlight == null) return;
            int index = _selected == null ? -1 : IndexOf(_selected);
            var visibility = index < 0 ? Visibility.Collapsed : Visibility.Visible;
            _bodyHighlight.Visibility = _nameHighlight.Visibility = visibility;
            if (index < 0) return;

            Canvas.SetTop(_bodyHighlight, index * RowHeight);
            Canvas.SetTop(_nameHighlight, index * RowHeight);

            if (!scrollIntoView) return;
            double top = index * RowHeight;
            if (top < BodyScroll.VerticalOffset)
                BodyScroll.ScrollToVerticalOffset(top);
            else if (top + RowHeight > BodyScroll.VerticalOffset + BodyScroll.ViewportHeight)
                BodyScroll.ScrollToVerticalOffset(top + RowHeight - BodyScroll.ViewportHeight);
        }

        private int IndexOf(Job job)
        {
            for (int i = 0; i < _jobs.Count; i++)
                if (ReferenceEquals(_jobs[i], job)) return i;
            return -1;
        }

        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            int index = (int)(e.GetPosition((IInputElement)sender).Y / RowHeight);
            if (index < 0 || index >= _jobs.Count) return;
            _selected = _jobs[index];
            UpdateHighlight(scrollIntoView: false);
            JobClicked?.Invoke(_selected);
        }

        // ---------- 捲動 / 縮放 ----------

        private void ScrollToToday()
        {
            double x = GanttMath.X(_today, _start, _dayWidth);
            BodyScroll.ScrollToHorizontalOffset(Math.Max(0, x - BodyScroll.ViewportWidth * 0.25));
        }

        private void Today_Click(object sender, RoutedEventArgs e) => ScrollToToday();

        private void Zoom_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton { Tag: string tag } || !Enum.TryParse<GanttZoom>(tag, out var zoom)) return;
            _zoom = zoom;
            _scrollToToday = true;
            Draw();
        }

        // 標題列與工令欄跟著圖表一起捲
        private void BodyScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            HeaderScroll.ScrollToHorizontalOffset(BodyScroll.HorizontalOffset);
            NameScroll.ScrollToVerticalOffset(BodyScroll.VerticalOffset);
        }

        // 按住 Shift 滾滾輪（或資料不夠上下捲時）改成左右捲動
        private void BodyScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Shift && BodyScroll.ScrollableHeight > 0) return;
            BodyScroll.ScrollToHorizontalOffset(BodyScroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        }

        private void NameScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            BodyScroll.ScrollToVerticalOffset(BodyScroll.VerticalOffset - e.Delta);
            e.Handled = true;
        }

        // ---------- 小工具 ----------

        private static Rectangle AddRect(Canvas canvas, double x, double y, double w, double h, string brushKey)
        {
            var r = new Rectangle { Width = Math.Max(w, 0), Height = Math.Max(h, 0), IsHitTestVisible = false };
            r.SetResourceReference(Shape.FillProperty, brushKey);
            Place(canvas, r, x, y);
            return r;
        }

        private static TextBlock Text(string text, double size, string brushKey)
        {
            var t = new TextBlock { Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false };
            t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            return t;
        }

        private static void Place(Canvas canvas, UIElement element, double x, double y)
        {
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
            canvas.Children.Add(element);
        }
    }
}
