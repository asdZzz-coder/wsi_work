using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using schedule.Models;
using schedule.Services;

namespace schedule.Views
{
    /// <summary>
    /// 大倉庫：先把物料（料號、名稱、規格…）放進來，出料時再篩選、勾選多項一次加入。
    /// 從主視窗打開是「管理」：新增、修改、刪除、貼上、匯入匯出；從流程卡打開是「選料」：多了出料數量、出料日期和「加入出料」。
    /// 倉庫有修改時馬上呼叫 save 存檔（兩種模式都可以修改倉庫）。
    /// </summary>
    public partial class MaterialWindow : Window
    {
        private static readonly string[] DefaultUnits = { "個", "只", "支", "組", "套", "台", "條", "片", "包", "盒", "米", "捲", "式" };
        private const string AllCategories = "全部分類";

        private readonly List<Material> _warehouse;
        private readonly IReadOnlyList<Job> _jobs;
        private readonly Action _save;
        private readonly bool _pick;
        private readonly HashSet<string> _inDispatch;
        private List<MaterialRow> _rows = new();
        private ICollectionView? _view;
        private Material? _editing;
        private bool _loading = true;

        /// <summary>選料模式按「加入出料」後選到的料件（照清單順序）。</summary>
        public List<DispatchItem> Picked { get; private set; } = new();

        /// <summary>倉庫有沒有被修改過（流程卡用來更新料號下拉選單）。</summary>
        public bool WarehouseChanged { get; private set; }

        /// <param name="warehouse">大倉庫（直接修改這份清單）。</param>
        /// <param name="jobs">所有工令：「加入出過的料」用。</param>
        /// <param name="save">倉庫改了之後存檔。</param>
        /// <param name="pick">true = 從流程卡打開選料。</param>
        /// <param name="inDispatch">選料時，這張工令已經出過的料號（標示「已出料」）。</param>
        public MaterialWindow(List<Material> warehouse, IReadOnlyList<Job> jobs, Action save, bool pick, IEnumerable<string>? inDispatch = null)
        {
            InitializeComponent();
            Height = Math.Min(Height, SystemParameters.WorkArea.Height - 20);
            Width = Math.Min(Width, SystemParameters.WorkArea.Width - 20);

            _warehouse = warehouse;
            _jobs = jobs;
            _save = save;
            _pick = pick;
            _inDispatch = new HashSet<string>((inDispatch ?? Array.Empty<string>()).Select(p => p.Trim()).Where(p => p.Length > 0),
                StringComparer.OrdinalIgnoreCase);

            if (pick)
            {
                Title = "從大倉庫選料";
                HeadingText.Text = "從大倉庫選料";
                PickFooter.Visibility = Visibility.Visible;
                ManageFooter.Visibility = Visibility.Collapsed;
                QuantityHeader.Visibility = Visibility.Visible;
                PickDateBox.Text = TextFormat.Date(DateTime.Today);
            }

            Reload();
            ClearForm();
            _loading = false;
            UpdateSelection();

            Loaded += (_, _) => SearchBox.Focus();
            ThemeService.ThemeChanged += ApplyTitleBar;
            Closed += (_, _) => ThemeService.ThemeChanged -= ApplyTitleBar;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ApplyTitleBar();
        }

        private void ApplyTitleBar() => WindowTheme.ApplyTitleBar(this);

        // ---------- 清單 ----------

        /// <summary>倉庫改過之後重建清單（勾選和數量照舊）。</summary>
        private void Reload(Material? select = null)
        {
            var old = _rows.ToDictionary(r => r.Material.Id);
            foreach (var row in _rows) row.PropertyChanged -= Row_Changed;
            _rows = MaterialRules.Sort(_warehouse).Select(m =>
            {
                var row = new MaterialRow(m, _pick, _inDispatch.Contains(m.PartNo.Trim()));
                if (old.TryGetValue(m.Id, out var before)) row.CopyStateFrom(before);
                row.PropertyChanged += Row_Changed;
                return row;
            }).ToList();

            _view = CollectionViewSource.GetDefaultView(_rows);
            _view.Filter = o => Visible((MaterialRow)o);
            MaterialList.ItemsSource = _view;

            // 分類下拉選單
            var current = CategoryBox.SelectedItem as string;
            var categories = MaterialRules.Categories(_warehouse);
            _loading = true;
            CategoryBox.ItemsSource = new[] { AllCategories }.Concat(categories).ToList();
            CategoryBox.SelectedItem = current != null && categories.Contains(current) ? current : AllCategories;
            _loading = false;

            UnitBox.Items = _warehouse.Select(m => m.Unit.Trim()).Where(u => u.Length > 0).Concat(DefaultUnits)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            CategoryEditBox.Items = categories;

            CountText.Text = _warehouse.Count == 0 ? "還沒有物料" : $"共 {_warehouse.Count:#,0} 項物料，{categories.Count} 個分類";
            if (select != null && _rows.FirstOrDefault(r => r.Material == select) is { } selected)
            {
                if (!Visible(selected)) { SearchBox.Text = ""; CategoryBox.SelectedItem = AllCategories; _view.Refresh(); }
                MaterialList.SelectedItem = selected;
                MaterialList.ScrollIntoView(selected);
            }
            UpdateCounts();
        }

        private bool Visible(MaterialRow row)
        {
            if (CheckedOnlyBox.IsChecked == true && !row.IsChecked) return false;
            var category = CategoryBox.SelectedItem as string;
            return MaterialRules.Matches(row.Material, SearchBox.Text, category == AllCategories ? null : category);
        }

        private IEnumerable<MaterialRow> VisibleRows => _view?.Cast<MaterialRow>() ?? Enumerable.Empty<MaterialRow>();

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            if (_view == null || _loading) return;
            _view.Refresh();
            UpdateCounts();
        }

        private void Category_Changed(object sender, SelectionChangedEventArgs e) => Filter_Changed(sender, e);

        private void UpdateCounts()
        {
            int shown = VisibleRows.Count();
            bool filtered = shown != _rows.Count;
            ResultText.Text = _rows.Count == 0 ? "" : filtered ? $"篩選出 {shown:#,0} 項" : $"{shown:#,0} 項";
            EmptyText.Text = _warehouse.Count == 0
                ? "大倉庫還沒有物料。\n在右邊填料號、名稱、規格後按「新增」，\n或用上方的「從剪貼簿貼上」「匯入 Excel」一次加入很多項。"
                : shown == 0 ? "沒有符合篩選條件的物料" : "";
            EmptyText.Visibility = EmptyText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateSelection();
        }

        private void Row_Changed(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(MaterialRow.IsChecked)) return;
            UpdateSelection();
        }

        /// <summary>勾選數、全選框、底部按鈕。</summary>
        private void UpdateSelection()
        {
            if (_loading) return;
            int count = _rows.Count(r => r.IsChecked);
            var visible = VisibleRows.ToList();
            AllBox.IsChecked = visible.Count == 0 || visible.All(r => !r.IsChecked) ? false
                : visible.All(r => r.IsChecked) ? true : null;
            if (_pick)
            {
                PickButtonText.Text = count > 0 ? $"加入出料（{count} 項）" : "加入出料";
                PickButton.IsEnabled = count > 0;
            }
            else
            {
                DeleteCheckedText.Text = count > 0 ? $"刪除勾選的 {count} 項" : "刪除勾選的物料";
                DeleteCheckedButton.IsEnabled = count > 0;
            }
        }

        private void AllBox_Click(object sender, RoutedEventArgs e)
        {
            var visible = VisibleRows.ToList();
            bool check = visible.Any(r => !r.IsChecked);
            foreach (var row in visible) row.IsChecked = check;
            AllBox.IsChecked = check && visible.Count > 0;
            StatusText.Text = check ? $"勾選了篩選出來的 {visible.Count} 項" : "取消勾選篩選出來的項目";
        }

        private void MaterialList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MaterialList.SelectedItem is MaterialRow row) FillForm(row.Material);
        }

        private void MaterialList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // 選料時按兩下 = 勾選 / 取消
            if (!_pick) return;
            var item = PopupDismisser.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (item?.DataContext is MaterialRow row && e.OriginalSource is not TextBox) row.IsChecked = !row.IsChecked;
        }

        private void MaterialList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // 空白鍵勾選目前這項
            if (e.Key == Key.Space && e.OriginalSource is not TextBox && MaterialList.SelectedItem is MaterialRow row)
            {
                row.IsChecked = !row.IsChecked;
                e.Handled = true;
            }
        }

        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // ↓ 或 Enter 跳到清單第一項
            if (e.Key is Key.Down or Key.Enter && VisibleRows.FirstOrDefault() is { } first)
            {
                MaterialList.SelectedItem = first;
                MaterialList.UpdateLayout();
                (MaterialList.ItemContainerGenerator.ContainerFromItem(first) as ListBoxItem)?.Focus();
                e.Handled = true;
            }
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
            base.OnPreviewKeyDown(e);
        }

        // ---------- 表單：新增 / 修改 / 刪除 ----------

        private void FillForm(Material m)
        {
            _editing = m;
            FormTitle.Text = "修改物料";
            FormHint.Text = "改好按「儲存修改」；要另外新增一項請先按「清空」。";
            PartNoBox.Text = m.PartNo;
            NameBox.Text = m.Name;
            SpecBox.Text = m.Spec;
            UnitBox.Text = m.Unit;
            CategoryEditBox.Text = m.Category;
            NoteBox.Text = m.Note;
        }

        private void ClearForm()
        {
            _editing = null;
            FormTitle.Text = "新增物料";
            FormHint.Text = "在左邊點一項可以修改。";
            PartNoBox.Clear();
            NameBox.Clear();
            SpecBox.Clear();
            UnitBox.Clear();
            NoteBox.Clear();
            // 分類留著，連續新增同一類的物料比較方便
        }

        private Material? ReadForm()
        {
            var m = new Material
            {
                PartNo = PartNoBox.Text, Name = NameBox.Text, Spec = SpecBox.Text,
                Unit = UnitBox.Text, Category = CategoryEditBox.Text, Note = NoteBox.Text,
            };
            MaterialRules.Clean(m);
            if (m.PartNo.Length == 0 && m.Name.Length == 0)
            {
                MessageBox.Show(this, "請至少填料號或名稱。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                PartNoBox.Focus();
                return null;
            }
            return m;
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var m = ReadForm();
            if (m == null) return;
            if (MaterialRules.Find(_warehouse, m) is { } same)
            {
                MessageBox.Show(this, $"「{same}」已經在倉庫裡了，已幫你選到那一項，可以直接修改。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                Reload(same);
                return;
            }
            _warehouse.Add(m);
            Changed($"已新增「{m}」");
            ClearForm();
            Reload(m);
            PartNoBox.Focus();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_editing == null)
            {
                MessageBox.Show(this, "請先在左邊點一項要修改的物料；新的物料請按「新增」。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var m = ReadForm();
            if (m == null) return;
            if (MaterialRules.Find(_warehouse, m, except: _editing) is { } same)
            {
                MessageBox.Show(this, $"料號「{same}」已經有另一項物料了。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                PartNoBox.Focus();
                return;
            }
            var target = _editing;
            (target.PartNo, target.Name, target.Spec, target.Unit, target.Category, target.Note) = (m.PartNo, m.Name, m.Spec, m.Unit, m.Category, m.Note);
            Changed($"已儲存「{target}」");
            Reload(target);
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_editing is not { } target)
            {
                MessageBox.Show(this, "請先在左邊點一項要刪除的物料。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(this, $"確定從倉庫刪除「{target}」{(target.Name.Length > 0 && target.PartNo.Length > 0 ? $"（{target.Name}）" : "")}？\n\n已經出料的紀錄不受影響。",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            _warehouse.Remove(target);
            Changed($"已刪除「{target}」");
            ClearForm();
            Reload();
        }

        private void DeleteChecked_Click(object sender, RoutedEventArgs e)
        {
            var targets = _rows.Where(r => r.IsChecked).Select(r => r.Material).ToList();
            if (targets.Count == 0) return;
            if (MessageBox.Show(this, $"確定從倉庫刪除勾選的 {targets.Count} 項物料？\n\n已經出料的紀錄不受影響。", Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            foreach (var m in targets) _warehouse.Remove(m);
            if (_editing != null && targets.Contains(_editing)) ClearForm();
            Changed($"已刪除 {targets.Count} 項物料");
            Reload();
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            MaterialList.SelectedItem = null;
            ClearForm();
            PartNoBox.Focus();
        }

        /// <summary>倉庫改了：存檔並顯示狀態。</summary>
        private void Changed(string status)
        {
            WarehouseChanged = true;
            _save();
            StatusText.Text = status;
        }

        // ---------- 一次加入很多項 ----------

        private void Paste_Click(object sender, RoutedEventArgs e)
        {
            string text;
            try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
            catch (System.Runtime.InteropServices.COMException) { text = ""; } // 剪貼簿被別的程式佔用
            var items = MaterialRules.ParseTable(text);
            if (items.Count == 0)
            {
                MessageBox.Show(this,
                    "剪貼簿裡沒有看得懂的物料。\n\n請在 Excel 選好要加入的範圍按 Ctrl+C，再按「從剪貼簿貼上」。\n" +
                    "第一列可以是標題（料號、名稱、規格、單位、分類、備註，順序不拘）；\n沒有標題時依序當成 料號、名稱、規格、單位、分類、備註。",
                    "從剪貼簿貼上", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            AddMany(items, "從剪貼簿貼上");
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Excel 檔案 (*.xlsx)|*.xlsx" };
            if (dlg.ShowDialog(this) != true) return;
            List<Material>? items;
            try { items = ExcelService.ImportMaterials(dlg.FileName); }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"匯入失敗：{ex.Message}", "匯入 Excel", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (items == null || items.Count == 0)
            {
                MessageBox.Show(this, "這個檔案裡沒有可匯入的物料。\n\n第一列需為標題列，至少要有「料號」或「名稱」欄（也可以寫品號、品名），其他欄位：規格、單位、分類、備註。",
                    "匯入 Excel", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            AddMany(items, "匯入 Excel");
        }

        private void FromHistory_Click(object sender, RoutedEventArgs e)
        {
            var items = MaterialRules.FromDispatches(_jobs, _warehouse);
            if (items.Count == 0)
            {
                MessageBox.Show(this, "各工令出料紀錄裡的料號都已經在倉庫裡了。", "加入出過的料", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            AddMany(items, "加入出過的料");
        }

        private void AddMany(List<Material> items, string title)
        {
            int existing = items.Count(m => MaterialRules.Find(_warehouse, m) != null);
            var detail = existing > 0 ? $"\n其中 {existing} 項倉庫已經有了（同料號），會用新的名稱、規格更新。" : "";
            if (MessageBox.Show(this, $"讀到 {items.Count} 項物料，要加入大倉庫嗎？{detail}", title,
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes) return;
            var (added, updated) = MaterialRules.Merge(_warehouse, items);
            Changed($"{title}：新增 {added} 項、更新 {updated} 項");
            Reload();
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog { Filter = "Excel 檔案 (*.xlsx)|*.xlsx", FileName = $"大倉庫_{DateTime.Now:yyyyMMdd}.xlsx" };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                ExcelService.ExportMaterials(_warehouse, dlg.FileName);
                StatusText.Text = $"已匯出 {_warehouse.Count} 項物料";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"匯出失敗：{ex.Message}", "匯出 Excel", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ---------- 選料 ----------

        private void Pick_Click(object sender, RoutedEventArgs e)
        {
            var chosen = _rows.Where(r => r.IsChecked).ToList();
            if (chosen.Count == 0) return;
            if (!TextFormat.TryParseOptionalDate(PickDateBox.Text, out var date))
            {
                MessageBox.Show(this, "出料日期看不懂，請用 2026/10/06 或 10/6 這種寫法；還沒出料就留空白。", Title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                PickDateBox.Focus();
                return;
            }
            var again = chosen.Where(r => r.InDispatch).Select(r => r.Material.ToString()).ToList();
            if (again.Count > 0 && MessageBox.Show(this,
                    $"這些料號這張工令已經出過了：\n{string.Join("、", again.Take(10))}{(again.Count > 10 ? $" 等 {again.Count} 項" : "")}\n\n還是要再加一筆嗎？",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes) return;

            Picked = chosen.Select(r => MaterialRules.ToDispatch(r.Material, r.Quantity, date)).ToList();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    }

    /// <summary>大倉庫清單的一列：物料、勾選、出料數量。</summary>
    public sealed class MaterialRow : EditRow
    {
        private bool _isChecked;
        private string _quantity = "";

        public MaterialRow(Material material, bool isPick, bool inDispatch)
        {
            Material = material;
            IsPick = isPick;
            InDispatch = inDispatch;
        }

        public Material Material { get; }
        public bool IsPick { get; }
        public bool InDispatch { get; }
        public string CheckName => $"勾選 {Material}";

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                Set(ref _isChecked, value);
                // 勾選時數量預設 1
                if (value && IsPick && Quantity.Trim().Length == 0) Quantity = "1";
            }
        }

        public string Quantity
        {
            get => _quantity;
            set
            {
                Set(ref _quantity, value);
                if (value.Trim().Length > 0 && !IsChecked) IsChecked = true;
            }
        }

        public void CopyStateFrom(MaterialRow other)
        {
            _isChecked = other._isChecked;
            _quantity = other._quantity;
        }

        public override string ToString() => Material.ToString();
    }
}
