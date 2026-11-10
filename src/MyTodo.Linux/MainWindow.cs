using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Styling;
using MyTodo;

namespace MyTodo.Linux;

public sealed class MainWindow : Window
{
    private readonly DataService _data;
    private readonly Preferences _prefs;
    private readonly Grid _layout = new();
    private readonly StackPanel _navigation = new() { Spacing = 8 };
    private readonly StackPanel _page = new() { Spacing = 18 };
    private readonly StackPanel _details = new() { Spacing = 14 };
    private readonly Border _drawer = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _search = new() { Watermark = "⌕  搜索", Margin = new Thickness(0, 16, 0, 16) };
    private string _view = "我的一天";
    private DateTime _day = DateTime.Today;
    private int _historyPage;
    private Bitmap? _background;
    private Action? _flush;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private bool _dirty;
    private readonly HashSet<string> _completing = new();
    private string? _draggingNode;
    private string _loadedBackground = "";

    public MainWindow()
    {
        Title = "MyTodo";
        Width = 1180; Height = 780; MinWidth = 820; MinHeight = 560;
        _data = new DataService(Environment.GetEnvironmentVariable("MYTODO_DATA_DIR") ?? LinuxPaths.DataDirectory);
        _prefs = Preferences.Load(_data.DataDirectory);
        _saveTimer.Tick += (_, _) => Flush();
        _layout.ColumnDefinitions = new ColumnDefinitions("212,*,0");
        var nav = new DockPanel { Margin = new Thickness(16, 24) };
        var settings = Button("⚙", () => Navigate("设置"));
        settings.FontSize = 26;
        DockPanel.SetDock(settings, Dock.Bottom); nav.Children.Add(settings);
        var history = Button("◷  历史记录", () => Navigate("历史记录"));
        DockPanel.SetDock(history, Dock.Bottom); nav.Children.Add(history);
        _navigation.Children.Add(new TextBlock { Text = "MyTodo", FontSize = 26, FontWeight = FontWeight.Bold });
        _navigation.Children.Add(_search);
        foreach (var title in new[] { "我的一天", "计划", "全部任务" })
        {
            var entry = Button(title, () => Navigate(title)); entry.HorizontalAlignment = HorizontalAlignment.Stretch;
            _navigation.Children.Add(entry);
        }
        nav.Children.Add(_navigation);
        var navBorder = new Border { Child = nav, Background = new SolidColorBrush(Color.Parse("#DEE5ED")) };
        _layout.Children.Add(navBorder);
        var main = new Grid();
        var image = new Image { Stretch = Stretch.UniformToFill, IsHitTestVisible = false, Name = "Wallpaper" };
        main.Children.Add(image);
        var mask = new Border { Background = Brushes.White, Opacity = _prefs.MaskOpacity, IsHitTestVisible = false, Name = "Mask" };
        main.Children.Add(mask);
        var content = new DockPanel { Margin = new Thickness(32, 24) };
        DockPanel.SetDock(_status, Dock.Bottom); content.Children.Add(_status);
        content.Children.Add(new ScrollViewer { Content = _page, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        main.Children.Add(content);
        Grid.SetColumn(main, 1); _layout.Children.Add(main);
        _drawer.Child = new ScrollViewer { Content = _details };
        _drawer.Padding = new Thickness(22, 24); Grid.SetColumn(_drawer, 2); _layout.Children.Add(_drawer);
        Content = _layout;
        _search.TextChanged += (_, _) => Refresh();
        Closing += (_, e) => { if (!Flush()) e.Cancel = true; };
        Closed += (_, _) => { _saveTimer.Stop(); _background?.Dispose(); };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) CloseDetails();
            if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.F) { _search.Focus(); e.Handled = true; }
        };
        ApplyTheme(); Refresh();
    }
    private Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 9), HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (_, e) => { Guard(action); e.Handled = true; };
        return button;
    }
    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception e) { _status.Text = "操作失败，内容未清空：" + e.Message; Console.Error.WriteLine(e); }
    }
    private void Change(Action change)
    {
        change(); _dirty = true; _saveTimer.Stop(); _saveTimer.Start();
    }
    private bool Flush()
    {
        _saveTimer.Stop();
        if (!_dirty) return true;
        try { _flush?.Invoke(); _dirty = false; _status.Text = ""; return true; }
        catch (Exception e) { _status.Text = "保存失败，请勿关闭：" + e.Message; return false; }
    }
    private void Navigate(string view)
    {
        if (!CloseDetails()) return;
        _view = view; Refresh();
    }
    private bool CloseDetails()
    {
        if (!Flush()) return false;
        _flush = null; _details.Children.Clear(); _layout.ColumnDefinitions[2].Width = new GridLength(0); Refresh(); return true;
    }
    private bool BeginDetails(string title, Action save)
    {
        if (!Flush()) return false;
        _details.Children.Clear(); _flush = save;
        _layout.ColumnDefinitions[2].Width = new GridLength(Math.Clamp(_prefs.DetailPanelWidth, 310, 540));
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeight.Bold });
        var close = Button("×", () => CloseDetails()); close.Width = 36; close.Height = 36;
        Grid.SetColumn(close, 1); head.Children.Add(close); _details.Children.Add(head); return true;
    }
    private void Field(string label, Control input)
    {
        _details.Children.Add(new TextBlock { Text = label, Opacity = .7 }); _details.Children.Add(input);
    }
    private TextBox TextField(string value, Action<string> change)
    {
        var input = new TextBox { Text = value };
        input.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Change(() => change(input.Text ?? "")); }; return input;
    }
    private DatePicker DateField(DateTime? value, Action<DateTime?> change)
    {
        var input = new DatePicker { SelectedDate = value.HasValue ? new DateTimeOffset(value.Value) : null };
        input.SelectedDateChanged += (_, _) => Change(() => change(input.SelectedDate?.Date)); return input;
    }
    private ContextMenu Menu(params (string label, Action action)[] entries) => new()
    {
        ItemsSource = entries.Select(x => { var item = new MenuItem { Header = x.label }; item.Click += (_, e) => { Guard(x.action); e.Handled = true; }; return item; }).ToArray()
    };
    private Border Card(Control child)
    {
        var radius = _prefs.ThemeStyle == "mist" ? new CornerRadius(0) : _prefs.ThemeStyle == "fresh" ? new CornerRadius(0, 14, 14, 0) : new CornerRadius(14);
        return new Border
        {
            Child = child, Padding = new Thickness(16), CornerRadius = radius,
            BorderBrush = _prefs.ThemeStyle == "fresh" ? _prefs.Accent : new SolidColorBrush(Color.Parse("#CCD5DF")),
            BorderThickness = _prefs.ThemeStyle == "fresh" ? new Thickness(4, 0, 0, 0) : new Thickness(1),
            Background = new SolidColorBrush(Color.Parse(_prefs.ThemeStyle == "fresh" ? "#F3FAF5" : "#F8FAFC"))
        };
    }
    private bool Matches(params string[] text) => string.IsNullOrWhiteSpace(_search.Text) || text.Any(t => t.Contains(_search.Text, StringComparison.CurrentCultureIgnoreCase));
    private void Refresh()
    {
        _page.Children.Clear();
        _page.Children.Add(new TextBlock { Text = _view, FontSize = 30, FontWeight = FontWeight.Bold });
        switch (_view)
        {
            case "我的一天": Tasks(true); break;
            case "全部任务": Tasks(false); break;
            case "计划": Plans(); break;
            case "历史记录": History(); break;
            case "设置": Settings(); break;
            case "回收站": Recycle(); break;
        }
    }
    private void Tasks(bool daily)
    {
        if (daily)
        {
            var dates = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var picker = new DatePicker { SelectedDate = new DateTimeOffset(_day) };
            picker.SelectedDateChanged += (_, _) => { if (Flush()) { _day = picker.SelectedDate?.Date ?? DateTime.Today; Refresh(); } };
            dates.Children.Add(Button("今天", () => { if (Flush()) { _day = DateTime.Today; Refresh(); } }));
            dates.Children.Add(Button("明天", () => { if (Flush()) { _day = DateTime.Today.AddDays(1); Refresh(); } }));
            dates.Children.Add(picker); _page.Children.Add(dates);
        }
        var input = new TextBox { Watermark = "添加任务", MinWidth = 150 };
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        void Add()
        {
            if (!Flush()) return;
            if (string.IsNullOrWhiteSpace(input.Text)) { EditTask(new TaskItem { DueDate = daily ? _day : null }, daily, true); return; }
            var task = new TaskItem { Title = input.Text.Trim(), DueDate = daily ? _day : null };
            _data.SaveTask(task); if (daily) _data.AddToDay(task, _day); input.Text = ""; Refresh();
        }
        bar.Children.Add(Button("+", Add)); Grid.SetColumn(input, 1); bar.Children.Add(input);
        var add = Button("添加任务", Add); Grid.SetColumn(add, 2); bar.Children.Add(add);
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(input.Text)) { Guard(Add); e.Handled = true; } };
        _page.Children.Add(bar);
        var ids = daily ? _data.GetTaskIdsForDay(_day) : null;
        var tasks = (daily ? _data.GetAllTasks().Where(t => ids!.Contains(t.Id)) : _data.GetAllActionItems()).Where(t => Matches(t.Title, t.OriginalRequirement, t.PlanTitle)).OrderBy(t => t.IsCompleted).ThenByDescending(t => t.IsPinned).ThenBy(t => t.CreatedAt).ToList();
        bool separator = false;
        foreach (var task in tasks)
        {
            if (task.IsCompleted && !separator) { _page.Children.Add(new TextBlock { Text = "已完成", Opacity = .55 }); separator = true; }
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), MinHeight = Math.Clamp(_prefs.TaskRowHeight, 44, 110) };
            var check = new CheckBox { IsChecked = task.IsCompleted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            check.Click += async (_, e) =>
            {
                e.Handled = true;
                if (!_completing.Add(task.Id)) return;
                check.IsEnabled = false;
                try
                {
                    if (!Flush()) { check.IsChecked = task.IsCompleted; return; }
                    await System.Threading.Tasks.Task.Delay(180);
                    Guard(() => { if (task.IsPlanNode) _data.SetPlanNodeCompletion(task.Id, check.IsChecked == true); else _data.SetCompletion(task.Id, check.IsChecked == true, _day); Refresh(); });
                }
                finally { _completing.Remove(task.Id); check.IsEnabled = true; }
            };
            row.Children.Add(check);
            var texts = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = (task.IsPinned ? "↑ " : "") + task.Title, TextWrapping = TextWrapping.Wrap });
            texts.Children.Add(new TextBlock { Text = task.DetailText + (task.SummaryText.Length > 0 ? "  ·  " + task.SummaryText : ""), FontSize = FontSize - 1, Opacity = .6, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(texts, 1); row.Children.Add(texts);
            var card = Card(row); if (task.IsCompleted) card.Opacity = .55;
            texts.DoubleTapped += (_, e) => { Guard(() => { if (task.IsPlanNode) EditNode(_data.GetPlans().Single(p => p.Id == task.PlanId), _data.GetPlans().Single(p => p.Id == task.PlanId).Nodes.Single(n => n.Id == task.Id)); else EditTask(task, daily); }); e.Handled = true; };
            card.ContextMenu = Menu((task.IsPinned ? "取消置顶" : "置顶", () => { if (!Flush()) return; if (task.IsPlanNode) _data.SetPlanPinned(task.PlanId, !task.IsPinned); else _data.SetTaskPinned(task.Id, !task.IsPinned); Refresh(); }), ("删除", () => { if (!Flush()) return; if (task.IsPlanNode) _data.DeletePlanNode(task.Id); else _data.DeleteTask(task.Id); CloseDetails(); }));
            _page.Children.Add(card);
        }
    }
    private void EditTask(TaskItem task, bool daily, bool draft = false)
    {
        bool persisted = !draft;
        void Save()
        {
            if (string.IsNullOrWhiteSpace(task.Title)) { if (persisted) throw new InvalidOperationException("任务名称不能为空"); return; }
            _data.SaveTask(task); if (daily) _data.AddToDay(task, _day); persisted = true;
        }
        if (!BeginDetails("任务详情", Save)) return;
        Field("名称", TextField(task.Title, value => task.Title = value));
        if (!daily) Field("截止日期", DateField(task.DueDate, value => task.DueDate = value));
        else { task.DueDate = _day; Field("截止日期", new TextBlock { Text = _day.ToString("yyyy-MM-dd") }); }
        Priority(task.Priority, value => task.Priority = value);
        var note = new NoteEditor(task.OriginalRequirement, task.NoteXaml);
        note.Changed += value => Change(() => { task.OriginalRequirement = value; task.NoteXaml = ""; }); Field("备注", note);
        if (persisted) Attachments(task);
        _details.Children.Add(Button("完成", () => { _dirty = true; CloseDetails(); }));
    }
    private void Priority(int value, Action<int> changed)
    {
        var input = new ComboBox { ItemsSource = new[] { "低", "普通", "重要" }, SelectedIndex = Math.Clamp(value, 0, 2), HorizontalAlignment = HorizontalAlignment.Stretch };
        input.SelectionChanged += (_, _) => Change(() => changed(input.SelectedIndex)); Field("优先级", input);
    }
    private void Attachments(TaskItem task)
    {
        var items = new StackPanel { Spacing = 8 };
        void Reload()
        {
            items.Children.Clear();
            foreach (var attachment in _data.GetAttachments(task.Id))
            {
                var link = new TextBlock { Text = attachment.OriginalName, Foreground = Brushes.DodgerBlue, TextDecorations = TextDecorations.Underline, TextWrapping = TextWrapping.Wrap };
                ToolTip.SetTip(link, attachment.StoredPath); link.DoubleTapped += (_, e) => { NoteEditor.Open(attachment.StoredPath); e.Handled = true; };
                link.ContextMenu = Menu(("移除地址", () => { _data.RemoveAttachment(attachment); Reload(); })); items.Children.Add(link);
            }
        }
        Reload(); Field("附件", items);
        var add = Button("选择文件", () => { });
        add.Click += async (_, _) =>
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true });
                foreach (var file in files) { var path = file.TryGetLocalPath(); if (path != null) _data.AddAttachment(task.Id, path); }
                Reload();
            }
            catch (Exception e) { _status.Text = e.Message; }
        };
        _details.Children.Add(add);
    }
    private void Plans()
    {
        _page.Children.Add(Button("+  添加计划", () => EditPlan(new PlanItem(), true)));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        var columns = Enumerable.Range(0, 3).Select(_ => new StackPanel { Spacing = 16, Margin = new Thickness(0, 0, 12, 0) }).ToArray();
        for (int i = 0; i < 3; i++) { Grid.SetColumn(columns[i], i); grid.Children.Add(columns[i]); }
        var heights = new double[3];
        foreach (var plan in _data.GetPlans().Where(p => Matches(p.Title, p.Goal, string.Join(" ", p.Nodes.Select(n => n.Title)))))
        {
            var body = new StackPanel { Spacing = 10 };
            body.Children.Add(new TextBlock { Text = (plan.IsPinned ? "↑ " : "") + plan.Title, FontSize = 19, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrWhiteSpace(plan.Goal)) body.Children.Add(new TextBlock { Text = plan.Goal, Opacity = .65, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new TextBlock { Text = plan.DueDateText, Foreground = _prefs.Accent });
            foreach (var node in plan.PreviewNodes)
            {
                var text = new TextBlock { Text = (node.IsCompleted ? "✓  " : "○  ") + node.Title, Opacity = node.IsCompleted ? .5 : 1, TextWrapping = TextWrapping.Wrap };
                text.DoubleTapped += (_, e) => { Guard(() => EditNode(plan, node)); e.Handled = true; }; body.Children.Add(text);
            }
            body.Children.Add(new TextBlock { Text = plan.ProgressText, Opacity = .6 });
            body.Children.Add(new ProgressBar { Value = plan.ProgressValue, Height = 4, Foreground = _prefs.Accent });
            var card = Card(body);
            card.DoubleTapped += (_, e) => { Guard(() => EditPlan(plan)); e.Handled = true; };
            card.ContextMenu = Menu((plan.IsPinned ? "取消置顶" : "置顶计划", () => { if (!Flush()) return; _data.SetPlanPinned(plan.Id, !plan.IsPinned); Refresh(); }), ("删除计划", () => { if (!Flush()) return; _data.DeletePlan(plan.Id); CloseDetails(); }));
            int column = Array.IndexOf(heights, heights.Min()); columns[column].Children.Add(card);
            card.Measure(new Size(Math.Max(140, (Width - _prefs.NavigationWidth - 100 - _layout.ColumnDefinitions[2].ActualWidth) / 3), double.PositiveInfinity));
            heights[column] += card.DesiredSize.Height + 16;
        }
        _page.Children.Add(grid);
    }
    private void EditPlan(PlanItem plan, bool draft = false)
    {
        bool persisted = !draft;
        void Save() { if (string.IsNullOrWhiteSpace(plan.Title)) { if (persisted) throw new InvalidOperationException("计划名称不能为空"); return; } _data.SavePlan(plan); persisted = true; }
        if (!BeginDetails("计划详情", Save)) return;
        Field("名称", TextField(plan.Title, v => plan.Title = v));
        Field("目标", TextField(plan.Goal, v => plan.Goal = v));
        Field("截止日期", DateField(plan.DueDate, v => plan.DueDate = v));
        _details.Children.Add(new TextBlock { Text = "任务节点", FontWeight = FontWeight.Bold, FontSize = 18 });
        var input = new TextBox { Watermark = "节点名称" };
        void AddNode()
        {
            if (string.IsNullOrWhiteSpace(plan.Title)) { _status.Text = "请先填写计划名称"; return; }
            _dirty = true; if (!Flush()) return;
            if (string.IsNullOrWhiteSpace(input.Text)) return;
            _data.AddPlanNode(plan.Id, input.Text.Trim()); EditPlan(_data.GetPlans().Single(p => p.Id == plan.Id)); Refresh();
        }
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Guard(AddNode); e.Handled = true; } };
        _details.Children.Add(input); _details.Children.Add(Button("添加节点", AddNode));
        foreach (var node in plan.PreviewNodes)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 5) };
            var check = new CheckBox { IsChecked = node.IsCompleted, Margin = new Thickness(0, 0, 8, 0) };
            check.Click += (_, e) => { Guard(() => { if (Flush()) _data.SetPlanNodeCompletion(node.Id, check.IsChecked == true); }); e.Handled = true; };
            row.Children.Add(check);
            var text = new TextBlock { Text = node.Title, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            text.DoubleTapped += (_, e) => { Guard(() => EditNode(plan, node)); e.Handled = true; };
            Grid.SetColumn(text, 1); row.Children.Add(text);
            var handle = new TextBlock { Text = "☰", Margin = new Thickness(10), Cursor = new Cursor(StandardCursorType.SizeAll) };
            handle.PointerPressed += (_, e) => { if (e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) { _draggingNode = node.Id; e.Pointer.Capture(null); e.Handled = true; } };
            Grid.SetColumn(handle, 2); row.Children.Add(handle);
            row.PointerReleased += (_, e) =>
            {
                if (_draggingNode == null || _draggingNode == node.Id) { _draggingNode = null; return; }
                var dragging = _draggingNode; _draggingNode = null;
                Guard(() => { if (!Flush()) return; var order = plan.PreviewNodes.Select(n => n.Id).Where(id => id != dragging).ToList(); order.Insert(order.IndexOf(node.Id), dragging); _data.ReorderPlanNodes(plan.Id, order); EditPlan(_data.GetPlans().Single(p => p.Id == plan.Id)); Refresh(); }); e.Handled = true;
            };
            row.ContextMenu = Menu(("编辑节点", () => EditNode(plan, node)), ("插入节点", () => { if (!Flush()) return; var inserted = _data.InsertPlanNodeBefore(plan.Id, node.Id, "新节点"); EditNode(plan, inserted); Refresh(); }), ("删除节点", () => { if (!Flush()) return; _data.DeletePlanNode(node.Id); EditPlan(_data.GetPlans().Single(p => p.Id == plan.Id)); Refresh(); }));
            _details.Children.Add(row);
        }
        _details.Children.Add(Button("完成", () => { _dirty = true; CloseDetails(); }));
    }
    private void EditNode(PlanItem plan, PlanNode node)
    {
        if (!BeginDetails("节点详情", () => { if (string.IsNullOrWhiteSpace(node.Title)) throw new InvalidOperationException("节点名称不能为空"); if (!string.IsNullOrWhiteSpace(node.AttachmentPath) && !node.Note.Contains(node.AttachmentPath)) node.Note += "\n" + node.AttachmentPath; _data.UpdatePlanNodeDetails(node.Id, node.Title, node.Note, node.NoteXaml, node.DueDate, node.Priority); })) return;
        _details.Children.Add(Button("‹  返回计划", () => { if (Flush()) EditPlan(_data.GetPlans().Single(p => p.Id == plan.Id)); }));
        Field("名称", TextField(node.Title, v => node.Title = v));
        Field("截止日期", DateField(node.DueDate, v => node.DueDate = v)); Priority(node.Priority, v => node.Priority = v);
        var note = new NoteEditor(node.Note, node.NoteXaml);
        note.Changed += v => Change(() => { node.Note = v; node.NoteXaml = ""; }); Field("备注", note);
        if (!string.IsNullOrWhiteSpace(node.AttachmentPath)) Field("附件", new TextBlock { Text = node.AttachmentPath });
        _details.Children.Add(Button("完成", () => CloseDetails()));
    }
    private void History()
    {
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        controls.Children.Add(Button("较早", () => { _historyPage++; Refresh(); }));
        controls.Children.Add(Button("较新", () => { _historyPage = Math.Max(0, _historyPage - 1); Refresh(); })); _page.Children.Add(controls);
        var end = DateTime.Today.AddDays(-25 * _historyPage);
        var summaries = _data.GetDailySummaries(end.AddDays(-24), end);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto") };
        for (int i = 0; i < 25; i++)
        {
            var date = end.AddDays(-i);
            summaries.TryGetValue(date, out var summary);
            var button = Button($"{date:MM月dd日}\n{summary?.Completed ?? 0}/{summary?.Total ?? 0}", () => HistoryDay(date));
            button.Margin = new Thickness(4); button.MinHeight = 86; Grid.SetColumn(button, i % 5); Grid.SetRow(button, i / 5); grid.Children.Add(button);
        }
        _page.Children.Add(grid);
    }
    private void HistoryDay(DateTime date)
    {
        var value = _data.GetDailyNote(date);
        if (!BeginDetails(date.ToString("yyyy-MM-dd"), () => _data.SaveDailyNote(date, value))) return;
        foreach (var row in _data.GetDailyPlan(date)) _details.Children.Add(new TextBlock { Text = (row.CompletedOnDay ? "✓ " : "○ ") + row.Title, TextWrapping = TextWrapping.Wrap });
        var input = TextField(value, v => value = v); input.AcceptsReturn = true; input.MinHeight = 120; Field("备注", input);
    }
    private void Recycle()
    {
        foreach (var item in _data.GetRecycleItems())
        {
            var stack = new StackPanel { Spacing = 8 };
            stack.Children.Add(new TextBlock { Text = item.TypeText + " · " + item.Title, TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(Button("恢复", () => { _data.RestoreRecycleItem(item.ItemType, item.Id); Refresh(); }));
            stack.Children.Add(Button("永久删除", () => Confirm("永久删除“" + item.Title + "”？", () => { _data.PermanentlyDeleteRecycleItem(item.ItemType, item.Id); Refresh(); })));
            _page.Children.Add(Card(stack));
        }
        _page.Children.Add(Button("清空回收站", () => Confirm("永久删除回收站内全部记录？", () => { _data.EmptyRecycleBin(); Refresh(); })));
    }
    private void Confirm(string message, Action action)
    {
        var dialog = new Window { Title = "MyTodo", Width = 360, Height = 180, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = _drawer.Background };
        var body = new StackPanel { Margin = new Thickness(24), Spacing = 14 };
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(Button("确定", () => { action(); dialog.Close(); })); body.Children.Add(Button("取消", () => dialog.Close()));
        dialog.Content = body; _ = dialog.ShowDialog(this);
    }
    private void Settings()
    {
        void SavePrefs() { _prefs.Save(_data.DataDirectory); ApplyTheme(); }
        var theme = new ComboBox { ItemsSource = new[] { "雾灰", "Mac", "清新" }, SelectedIndex = _prefs.ThemeStyle == "mac" ? 1 : _prefs.ThemeStyle == "fresh" ? 2 : 0 };
        theme.SelectionChanged += (_, _) => Guard(() => { _prefs.ThemeStyle = new[] { "mist", "mac", "fresh" }[Math.Max(0, theme.SelectedIndex)]; SavePrefs(); Refresh(); });
        _page.Children.Add(theme);
        var colors = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var color in new[] { "#2563EB", "#0891B2", "#0D9488", "#16A34A", "#007AFF", "#5856D6" })
        {
            var button = Button(" ", () => { _prefs.ThemeColor = color; SavePrefs(); Refresh(); }); button.Background = Brush.Parse(color); button.Width = 38; colors.Children.Add(button);
        }
        _page.Children.Add(colors);
        void Slider(string label, double min, double max, double value, Action<double> set)
        {
            _page.Children.Add(new TextBlock { Text = label });
            var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), MaxWidth = 380, HorizontalAlignment = HorizontalAlignment.Left, Width = 330 };
            slider.PropertyChanged += (_, e) => { if (e.Property == RangeBaseValue) Guard(() => { set(slider.Value); SavePrefs(); }); }; _page.Children.Add(slider);
        }
        Slider("字体大小", 12, 22, _prefs.FontSize, v => _prefs.FontSize = v);
        Slider("导航栏宽度", 180, 280, _prefs.NavigationWidth, v => _prefs.NavigationWidth = v);
        Slider("详情栏宽度", 310, 540, _prefs.DetailPanelWidth, v => _prefs.DetailPanelWidth = v);
        Slider("任务行高度", 44, 110, _prefs.TaskRowHeight, v => _prefs.TaskRowHeight = v);
        Slider("背景蒙版", 0, 1, _prefs.MaskOpacity, v => _prefs.MaskOpacity = v);
        var font = new ComboBox { ItemsSource = new[] { "sans-serif", "Noto Sans CJK SC", "WenQuanYi Micro Hei" }, SelectedItem = _prefs.FontFamilyName };
        font.SelectionChanged += (_, _) => Guard(() => { _prefs.FontFamilyName = font.SelectedItem?.ToString() ?? "sans-serif"; SavePrefs(); }); _page.Children.Add(font);
        var background = Button("选择背景", () => { });
        background.Click += async (_, _) =>
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { FileTypeFilter = new[] { FilePickerFileTypes.ImageAll } });
                var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path == null) return;
                // Decode to the current screen width instead of retaining the full 8K image.
                using var stream = File.OpenRead(path); using var bitmap = Bitmap.DecodeToWidth(stream, 1920);
                var target = Path.Combine(_data.DataDirectory, "background.png"); bitmap.Save(target + ".tmp"); File.Move(target + ".tmp", target, true);
                _prefs.BackgroundImagePath = target; _loadedBackground = ""; SavePrefs();
            }
            catch (Exception e) { _status.Text = "背景加载失败：" + e.Message; }
        };
        _page.Children.Add(background); _page.Children.Add(Button("移除背景", () => { _prefs.BackgroundImagePath = ""; SavePrefs(); }));
        _page.Children.Add(new TextBlock { Text = "备份目录" });
        var backupPath = new TextBox { Text = _prefs.BackupDirectory, Watermark = _data.BackupDirectory };
        backupPath.LostFocus += (_, _) => Guard(() => { _prefs.BackupDirectory = backupPath.Text ?? ""; SavePrefs(); }); _page.Children.Add(backupPath);
        _page.Children.Add(Button("备份数据", () => { var path = _data.CreateBackup(_prefs.BackupDirectory); _status.Text = "已备份：" + path; }));
        var import = Button("导入 Windows / Linux 数据库", () => { });
        import.Click += async (_, _) =>
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择 mytodo.db 或数据库备份", AllowMultiple = false });
                var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path == null) return;
                Confirm("导入将替换当前任务数据。当前数据会先备份，附件原文件不会复制。", () => { DatabaseTransfer.Import(path, _data.DataDirectory); Refresh(); _status.Text = "导入完成。Windows 文件地址需重新定位。"; });
            }
            catch (Exception e) { _status.Text = "导入失败，当前数据未替换：" + e.Message; }
        };
        _page.Children.Add(import);
        _page.Children.Add(Button("打开数据目录", () => NoteEditor.Open(_data.DataDirectory)));
        _page.Children.Add(Button("回收站", () => Navigate("回收站")));
        _page.Children.Add(new TextBlock { Text = "1.1.0-preview.2 · Linux", Opacity = .5 });
    }
    private static readonly AvaloniaProperty RangeBaseValue = Avalonia.Controls.Primitives.RangeBase.ValueProperty;
    private void ApplyTheme()
    {
        FontSize = Math.Clamp(_prefs.FontSize, 12, 22); FontFamily = new FontFamily(_prefs.FontFamilyName);
        Background = Brush.Parse(_prefs.ThemeStyle == "fresh" ? "#E3EFE6" : _prefs.ThemeStyle == "mac" ? "#ECEEF2" : "#E9EEF4");
        _drawer.Background = Brush.Parse(_prefs.ThemeStyle == "fresh" ? "#F2F8F3" : "#F7F9FB");
        _layout.ColumnDefinitions[0].Width = new GridLength(Math.Clamp(_prefs.NavigationWidth, 180, 280));
        var main = (Grid)_layout.Children[1]; var image = (Image)main.Children[0]; var mask = (Border)main.Children[1];
        mask.Opacity = Math.Clamp(_prefs.MaskOpacity, 0, 1);
        ((Border)_layout.Children[0]).Background = new SolidColorBrush(Color.FromRgb(
            (byte)((Color.Parse(_prefs.ThemeColor).R + 3 * 220) / 4),
            (byte)((Color.Parse(_prefs.ThemeColor).G + 3 * 230) / 4),
            (byte)((Color.Parse(_prefs.ThemeColor).B + 3 * 235) / 4)));
        if (_loadedBackground != _prefs.BackgroundImagePath)
        {
            image.Source = null; _background?.Dispose(); _background = null; _loadedBackground = _prefs.BackgroundImagePath;
            if (File.Exists(_prefs.BackgroundImagePath))
            {
                try { using var stream = File.OpenRead(_prefs.BackgroundImagePath); _background = Bitmap.DecodeToWidth(stream, 1920); image.Source = _background; }
                catch (Exception e) { _status.Text = "背景不可用：" + e.Message; }
            }
        }
        Resources["SystemAccentColor"] = Color.Parse(_prefs.ThemeColor);
        Styles.Clear();
        var buttons = new Style(x => x.OfType<Button>());
        buttons.Setters.Add(new Setter(Avalonia.Controls.Button.CornerRadiusProperty, new CornerRadius(_prefs.ThemeStyle == "mist" ? 0 : _prefs.ThemeStyle == "mac" ? 9 : 18)));
        Styles.Add(buttons);
    }
    public void RunSmokeTest()
    {
        var task = new TaskItem { Title = "Linux UI test", DueDate = _day };
        _data.SaveTask(task); _data.AddToDay(task, _day);
        EditTask(task, true);
        _details.Children.OfType<TextBox>().First().Text = "Autosaved task";
        if (!Flush() || _data.GetAllTasks().Single().Title != "Autosaved task") throw new InvalidOperationException("Task editor did not autosave");
        CloseDetails();
        var plan = new PlanItem { Title = "Plan UI test" }; _data.SavePlan(plan);
        var node = _data.AddPlanNode(plan.Id, "Node UI test");
        EditPlan(_data.GetPlans().Single());
        _details.Children.OfType<TextBox>().First().Text = "Autosaved plan";
        if (!Flush() || _data.GetPlans().Single().Title != "Autosaved plan") throw new InvalidOperationException("Plan editor did not autosave");
        EditNode(plan, node);
        _details.Children.OfType<TextBox>().First().Text = "Autosaved node";
        if (!Flush() || _data.GetPlans().Single().Nodes.Single().Title != "Autosaved node") throw new InvalidOperationException("Node editor did not autosave");
        CloseDetails();
        foreach (var view in new[] { "我的一天", "全部任务", "计划", "历史记录", "设置", "回收站" }) Navigate(view);
        Navigate("计划");
    }
}
