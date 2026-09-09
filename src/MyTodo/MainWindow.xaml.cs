using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace MyTodo;

public partial class MainWindow : Window
{
    private readonly DataService _dataService = new DataService();
    private readonly SettingsService _settingsService;
    private readonly QuadraticEase _drawerEase = new QuadraticEase { EasingMode = EasingMode.EaseOut };
    private readonly DispatcherTimer _editorAutoSaveTimer;
    private readonly DispatcherTimer _memoryTrimTimer;
    private static readonly Regex WebAddressPattern = new Regex(
        @"(?i)\b(?:https?://|www\.)[^\s<>{}\[\]]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> RemovedThemeColors = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "#C14B36", "#DB2777", "#D97706", "#6D5BD0"
    };

    private AppSettings _settings;
    private List<TaskItem> _allTasks = new List<TaskItem>();
    private List<TaskItem> _visibleTasks = new List<TaskItem>();
    private List<PlanItem> _plans = new List<PlanItem>();
    private HashSet<string> _selectedDayIds = new HashSet<string>();
    private TaskItem? _editingTask;
    private PlanItem? _editingPlan;
    private PlanNode? _editingPlanNode;
    private Button? _activeNavigation;
    private string _currentMode = "today";
    private bool _loadingSettings;
    private DateTime _historyPageStart = DateTime.Today.AddDays(-24);
    private DateTime _selectedHistoryDate = DateTime.Today;
    private DateTime _selectedDay = DateTime.Today;
    private bool _changingSelectedDay;
    private bool _suppressTaskAutoSave;
    private bool _loadingEditorFields;
    private Point _planNodeDragStart;
    private PlanNode? _pendingPlanNodeDrag;
    private FrameworkElement? _capturedPlanNodeDragHandle;
    private DateTime _lastWorkingSetTrimUtc = DateTime.MinValue;

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr processHandle);

    public MainWindow()
    {
        InitializeComponent();
        _editorAutoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _editorAutoSaveTimer.Tick += EditorAutoSaveTimer_Tick;
        _memoryTrimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _memoryTrimTimer.Tick += MemoryTrimTimer_Tick;
        _settingsService = new SettingsService(_dataService.DataDirectory);
        _settings = _settingsService.Load();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _loadingSettings = true;
        if (RemovedThemeColors.Contains(_settings.ThemeColor))
        {
            _settings.ThemeColor = "#2563EB";
            SaveSettings();
        }
        ThemeColorBox.Text = _settings.ThemeColor;
        SelectThemeStyle(_settings.ThemeStyle);
        SelectFontFamily(_settings.FontFamilyName);
        MaskSlider.Value = Clamp(_settings.MaskOpacity, 0.10, 0.90);
        FontSizeSlider.Value = Clamp(_settings.FontSize, 12, 19);
        NavigationWidthSlider.Value = Clamp(_settings.NavigationWidth, 180, 280);
        TaskRowHeightSlider.Value = Clamp(_settings.TaskRowHeight, 46, 80);
        DetailWidthSlider.Value = Clamp(_settings.DetailPanelWidth, 350, 560);
        BackupDirectoryBox.Text = GetBackupDirectory();
        ApplyAppearance();
        DataObject.AddPastingHandler(RequirementBox, RequirementBox_Pasting);
        DataObject.AddPastingHandler(PlanNodeNoteBox, PlanNodeNoteBox_Pasting);
        _changingSelectedDay = true;
        DayPicker.DisplayDateStart = DateTime.Today;
        DayPicker.SelectedDate = _selectedDay;
        _changingSelectedDay = false;
        _loadingSettings = false;

        ReloadCoreData();
        ShowMode("today", TodayNav);
        ScheduleMemoryTrim(TimeSpan.FromSeconds(5));
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _editorAutoSaveTimer.Stop();
        _memoryTrimTimer.Stop();
        if (TaskDrawer.Visibility == Visibility.Visible) SaveTaskFromEditor(false);
        if (PlanDrawer.Visibility == Visibility.Visible)
        {
            if (PlanNodeEditor.Visibility == Visibility.Visible) SaveCurrentPlanNode(false);
            SaveCurrentPlan(false);
        }
        if (_currentMode == "today") SaveDailyTip();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
        => ScheduleMemoryTrim(TimeSpan.FromMilliseconds(700));

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            ScheduleMemoryTrim(TimeSpan.FromMilliseconds(250));
    }

    private void ScheduleMemoryTrim(TimeSpan delay)
    {
        if (!IsLoaded) return;
        _memoryTrimTimer.Stop();
        _memoryTrimTimer.Interval = delay;
        _memoryTrimTimer.Start();
    }

    private void MemoryTrimTimer_Tick(object? sender, EventArgs e)
    {
        _memoryTrimTimer.Stop();
        if (DateTime.UtcNow - _lastWorkingSetTrimUtc < TimeSpan.FromSeconds(4)) return;
        _lastWorkingSetTrimUtc = DateTime.UtcNow;

        // WPF会保留已经访问过的布局、字体及渲染页。窗口进入后台或启动稳定后，
        // 回收不可达对象并归还空闲工作集；数据仍在内存中，不会重新读取附件或背景原图。
        try
        {
            GC.Collect(2, GCCollectionMode.Optimized, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Optimized, false);
            using var process = Process.GetCurrentProcess();
            EmptyWorkingSet(process.Handle);
        }
        catch
        {
            // 内存整理只是优化，失败不能影响任务编辑。
        }
    }

    private void ReloadCoreData(string? selectId = null)
    {
        _selectedDayIds = _dataService.GetTaskIdsForDay(_selectedDay);
        LoadDailyTip();
        if (_currentMode == "plans")
        {
            _plans = _dataService.GetPlans();
            ApplyPlanFilter();
        }
        else
        {
            _allTasks = _currentMode == "all"
                ? _dataService.GetAllActionItems()
                : _dataService.GetAllTasks();
            ApplyTaskFilter();
        }

        if (!string.IsNullOrWhiteSpace(selectId))
        {
            var selected = _visibleTasks.FirstOrDefault(item => item.Id == selectId);
            if (selected != null)
            {
                TaskList.SelectedItem = selected;
                TaskList.ScrollIntoView(selected);
            }
        }
    }

    private void ApplyTaskFilter()
    {
        IEnumerable<TaskItem> query = _allTasks;
        if (_currentMode == "today")
        {
            query = query.Where(item => !item.IsPlanNode && _selectedDayIds.Contains(item.Id));
        }

        var keyword = SearchBox.Text.Trim();
        if (keyword.Length > 0)
        {
            query = query.Where(item =>
                item.Title.IndexOf(keyword, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                item.OriginalRequirement.IndexOf(keyword, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                item.PlanTitle.IndexOf(keyword, StringComparison.CurrentCultureIgnoreCase) >= 0);
        }

        var rows = query
            .OrderByDescending(item => item.IsPinned)
            .ThenBy(item => item.DueDate ?? DateTime.MaxValue)
            .ThenByDescending(item => item.Priority)
            .ToList();
        var unfinished = rows.Where(item => !item.IsCompleted).ToList();
        var completed = rows.Where(item => item.IsCompleted)
            .OrderByDescending(item => item.IsPinned)
            .ThenBy(item => item.DueDate ?? DateTime.MaxValue)
            .ToList();
        _visibleTasks = unfinished;
        if (completed.Count > 0)
        {
            _visibleTasks.Add(new TaskItem { IsSeparator = true, Title = $"已完成 · {completed.Count}" });
            _visibleTasks.AddRange(completed);
        }
        TaskList.ItemsSource = _visibleTasks;
        var completedCount = completed.Count;
        var unfinishedCount = unfinished.Count;
        StatusText.Text = rows.Count == 0
            ? "这里还没有任务"
            : $"未完成 {unfinishedCount} 项  ·  已完成 {completedCount} 项";
    }

    private void ApplyPlanFilter()
    {
        IEnumerable<PlanItem> query = _plans;
        var keyword = SearchBox.Text.Trim();
        if (keyword.Length > 0)
        {
            query = query.Where(plan =>
                plan.Title.IndexOf(keyword, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                plan.Goal.IndexOf(keyword, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                plan.Nodes.Any(node =>
                    node.Title.IndexOf(keyword, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                    node.Note.IndexOf(keyword, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                    node.AttachmentPath.IndexOf(keyword, StringComparison.CurrentCultureIgnoreCase) >= 0));
        }

        // 三列彼此独立。每次按创建顺序取出计划，并放入当前估算高度最短的列，
        // 因此后一张卡片会紧接在上一张卡片下方，不再被其他列的高卡片撑出空白。
        var columns = new[] { new List<PlanItem>(), new List<PlanItem>(), new List<PlanItem>() };
        var columnHeights = new double[3];
        foreach (var plan in query.OrderByDescending(item => item.IsPinned)
                                  .ThenBy(item => item.CreatedAt)
                                  .ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            var targetColumn = 0;
            if (columnHeights[1] < columnHeights[targetColumn]) targetColumn = 1;
            if (columnHeights[2] < columnHeights[targetColumn]) targetColumn = 2;
            columns[targetColumn].Add(plan);
            columnHeights[targetColumn] += EstimatePlanCardHeight(plan);
        }

        PlanColumnOne.ItemsSource = columns[0];
        PlanColumnTwo.ItemsSource = columns[1];
        PlanColumnThree.ItemsSource = columns[2];
    }

    private static double EstimatePlanCardHeight(PlanItem plan)
    {
        var goalLines = 0;
        foreach (var line in (plan.Goal ?? string.Empty).Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
        {
            if (line.Length > 0) goalLines += Math.Max(1, (int)Math.Ceiling(line.Length / 22.0));
        }
        return 112 + goalLines * 19 + plan.Nodes.Count * 24;
    }

    private void ClearPlanColumns()
    {
        PlanColumnOne.ItemsSource = null;
        PlanColumnTwo.ItemsSource = null;
        PlanColumnThree.ItemsSource = null;
    }

    private void Navigation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string mode)
        {
            ShowMode(mode, button);
        }
    }

    private void ShowMode(string mode, Button? navigation)
    {
        if (_currentMode == "today" && mode != "today") SaveDailyTip();
        CloseAllPanels(true);
        _currentMode = mode;
        TaskView.Visibility = mode == "today" || mode == "all" ? Visibility.Visible : Visibility.Collapsed;
        DailyTipPanel.Visibility = mode == "today" ? Visibility.Visible : Visibility.Collapsed;
        PlanView.Visibility = mode == "plans" ? Visibility.Visible : Visibility.Collapsed;
        HistoryView.Visibility = mode == "history" ? Visibility.Visible : Visibility.Collapsed;
        RecycleView.Visibility = mode == "recycle" ? Visibility.Visible : Visibility.Collapsed;
        DaySelector.Visibility = mode == "today" ? Visibility.Visible : Visibility.Collapsed;

        if (_activeNavigation != null) _activeNavigation.Background = Brushes.Transparent;
        _activeNavigation = navigation;
        if (_activeNavigation != null) RefreshActiveNavigationBrush();

        if (mode == "history")
        {
            TaskList.ItemsSource = null;
            ClearPlanColumns();
            RecycleList.ItemsSource = null;
            _allTasks.Clear();
            _plans.Clear();
            BuildHistoryCalendar();
            return;
        }

        if (mode == "plans")
        {
            TaskList.ItemsSource = null;
            HistoryCalendar.ItemsSource = null;
            HistoryTaskList.ItemsSource = null;
            RecycleList.ItemsSource = null;
            _allTasks.Clear();
            _plans = _dataService.GetPlans();
            ApplyPlanFilter();
            return;
        }

        if (mode == "recycle")
        {
            TaskList.ItemsSource = null;
            ClearPlanColumns();
            HistoryCalendar.ItemsSource = null;
            HistoryTaskList.ItemsSource = null;
            _allTasks.Clear();
            _plans.Clear();
            LoadRecycleBin();
            return;
        }

        ClearPlanColumns();
        HistoryCalendar.ItemsSource = null;
        HistoryTaskList.ItemsSource = null;
        RecycleList.ItemsSource = null;
        _plans.Clear();
        _selectedDayIds = _dataService.GetTaskIdsForDay(_selectedDay);
        _allTasks = mode == "all" ? _dataService.GetAllActionItems() : _dataService.GetAllTasks();
        ViewTitle.Text = mode == "today" ? "我的一天" : "全部任务";
        UpdateSelectedDayHeader();
        ViewSubtitle.Visibility = mode == "today" ? Visibility.Visible : Visibility.Collapsed;
        ApplyTaskFilter();
    }

    private void DayPicker_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _changingSelectedDay) return;
        var selected = (DayPicker.SelectedDate ?? DateTime.Today).Date;
        if (selected < DateTime.Today)
        {
            selected = DateTime.Today;
            _changingSelectedDay = true;
            DayPicker.SelectedDate = selected;
            _changingSelectedDay = false;
        }
        if (_currentMode == "today") SaveDailyTip();
        _selectedDay = selected;
        _selectedDayIds = _dataService.GetTaskIdsForDay(_selectedDay);
        _allTasks = _dataService.GetAllTasks();
        LoadDailyTip();
        UpdateSelectedDayHeader();
        ApplyTaskFilter();
    }

    private void SelectToday_Click(object sender, RoutedEventArgs e)
    {
        DayPicker.SelectedDate = DateTime.Today;
    }

    private void SelectTomorrow_Click(object sender, RoutedEventArgs e)
    {
        DayPicker.SelectedDate = DateTime.Today.AddDays(1);
    }

    private void LoadDailyTip() => DailyTipBox.Text = _dataService.GetDailyNote(_selectedDay);

    private void SaveDailyTip() => _dataService.SaveDailyNote(_selectedDay, DailyTipBox.Text);

    private void DailyTipBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => SaveDailyTip();

    private void UpdateSelectedDayHeader()
    {
        if (_currentMode != "today")
        {
            ViewSubtitle.Text = string.Empty;
            return;
        }
        var marker = _selectedDay == DateTime.Today ? "今天" : _selectedDay == DateTime.Today.AddDays(1) ? "明天" : string.Empty;
        ViewSubtitle.Text = $"{_selectedDay:yyyy年M月d日 · dddd}" + (marker.Length == 0 ? string.Empty : $" · {marker}");
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (_currentMode == "plans") ApplyPlanFilter();
        else if (_currentMode == "today" || _currentMode == "all") ApplyTaskFilter();
    }

    private void EditorTextChanged(object sender, TextChangedEventArgs e) => ScheduleEditorAutoSave();

    private void EditorSelectionChanged(object sender, SelectionChangedEventArgs e) => ScheduleEditorAutoSave();

    private void ScheduleEditorAutoSave()
    {
        if (!IsLoaded || _loadingSettings || _loadingEditorFields) return;
        _editorAutoSaveTimer.Stop();
        _editorAutoSaveTimer.Start();
    }

    private void EditorAutoSaveTimer_Tick(object? sender, EventArgs e)
    {
        _editorAutoSaveTimer.Stop();
        if (PlanDrawer.Visibility == Visibility.Visible)
        {
            if (PlanNodeEditor.Visibility == Visibility.Visible) SaveCurrentPlanNode(false);
            else SaveCurrentPlan(false);
        }
        else if (TaskDrawer.Visibility == Visibility.Visible && !_suppressTaskAutoSave)
        {
            SaveTaskFromEditor(false);
        }
    }

    private void AddTask_Click(object sender, RoutedEventArgs e)
    {
        var title = QuickAddBox.Text.Trim();
        if (title.Length > 0)
        {
            CreateQuickTask(title);
            return;
        }

        OpenNewTaskEditor(string.Empty);
    }

    private void OpenNewTask_Click(object sender, RoutedEventArgs e)
    {
        OpenNewTaskEditor(QuickAddBox.Text.Trim());
    }

    private void OpenNewTaskEditor(string title)
    {
        if (TaskDrawer.Visibility == Visibility.Visible) SaveTaskFromEditor(false);
        ClearTaskEditor();
        TitleBox.Text = title;
        OpenTaskDrawer();
        TitleBox.Focus();
        TitleBox.CaretIndex = TitleBox.Text.Length;
    }

    private void CreateQuickTask(string title)
    {
        var task = new TaskItem
        {
            Title = title,
            DueDate = _currentMode == "today" ? _selectedDay : (DateTime?)null
        };
        _dataService.SaveTask(task);
        if (_currentMode == "today") _dataService.AddToDay(task, _selectedDay);
        QuickAddBox.Clear();
        ReloadCoreData(task.Id);
        QuickAddBox.Focus();
        StatusText.Text = $"已添加：{task.Title}";
    }

    private void QuickAddBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var title = QuickAddBox.Text.Trim();
            if (title.Length > 0) CreateQuickTask(title);
            e.Handled = true;
        }
    }

    private void TaskRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount != 2) return;
        if (!(sender is FrameworkElement row) || !(row.DataContext is TaskItem item) || item.IsSeparator) return;

        var current = e.OriginalSource as DependencyObject;
        while (current != null && !ReferenceEquals(current, row))
        {
            if (current is Button) return;
            current = current is FrameworkContentElement contentElement
                ? contentElement.Parent
                : VisualTreeHelper.GetParent(current);
        }

        TaskList.SelectedItem = item;
        OpenTaskItem(item);
        e.Handled = true;
    }

    private void OpenTaskItem(TaskItem item)
    {
        if (TaskDrawer.Visibility == Visibility.Visible && (_editingTask == null || _editingTask.Id != item.Id))
            SaveTaskFromEditor(false);
        if (item.IsPlanNode)
        {
            var plan = _plans.FirstOrDefault(candidate => candidate.Id == item.PlanId)
                ?? _dataService.GetPlans().FirstOrDefault(candidate => candidate.Id == item.PlanId);
            if (plan != null)
            {
                PopulatePlanEditor(plan);
                OpenPlanDrawer();
            }
            return;
        }

        PopulateTaskEditor(item);
        OpenTaskDrawer();
    }

    private void PopulateTaskEditor(TaskItem task)
    {
        _loadingEditorFields = true;
        try
        {
            _editingTask = task;
            TitleBox.Text = task.Title;
            var activeDay = _dataService.GetActiveDayForTask(task.Id);
            DueDatePicker.SelectedDate = activeDay ?? task.DueDate;
            DueDatePicker.IsEnabled = !activeDay.HasValue;
            PriorityBox.SelectedIndex = Clamp(task.Priority, 0, 2);
            if (!TryLoadRichTextXaml(RequirementBox, task.NoteXaml))
            {
                var legacyLines = new List<string>();
                if (!string.IsNullOrWhiteSpace(task.OriginalRequirement)) legacyLines.Add(task.OriginalRequirement);
                if (!string.IsNullOrWhiteSpace(task.WebsiteUrl)) legacyLines.Add(task.WebsiteUrl);
                legacyLines.AddRange(_dataService.GetAttachments(task.Id).Select(item => item.StoredPath));
                SetRichText(RequirementBox, string.Join(Environment.NewLine, legacyLines), true);
            }
            CompleteButton.ToolTip = task.IsCompleted ? "恢复为未完成" : "标记完成";
            TodayActionButton.Content = _selectedDayIds.Contains(task.Id) ? "移出所选日期" : "加入所选日期";
        }
        finally { _loadingEditorFields = false; }
    }

    private void ClearTaskEditor()
    {
        _loadingEditorFields = true;
        try
        {
            _editingTask = null;
            TaskList.SelectedItem = null;
            TitleBox.Clear();
            DueDatePicker.SelectedDate = _currentMode == "today" ? _selectedDay : (DateTime?)null;
            DueDatePicker.IsEnabled = _currentMode != "today";
            PriorityBox.SelectedIndex = 1;
            SetRichText(RequirementBox, string.Empty, false);
            CompleteButton.ToolTip = "标记完成";
            TodayActionButton.Content = "加入所选日期";
        }
        finally { _loadingEditorFields = false; }
    }

    private bool SaveTaskFromEditor(bool showValidation)
    {
        var title = TitleBox.Text.Trim();
        if (title.Length == 0 && !showValidation && _editingTask != null)
            title = _editingTask.Title;
        if (title.Length == 0)
        {
            if (showValidation)
            {
                AppDialog.Notify(this, "任务名称", "请填写任务名称。");
                TitleBox.Focus();
            }
            return false;
        }

        var isNew = _editingTask == null;
        var task = _editingTask ?? new TaskItem();
        task.Title = title;
        var activeDay = isNew ? null : _dataService.GetActiveDayForTask(task.Id);
        task.DueDate = _currentMode == "today" ? _selectedDay : activeDay ?? DueDatePicker.SelectedDate;
        task.Priority = PriorityBox.SelectedIndex < 0 ? 1 : PriorityBox.SelectedIndex;
        task.OriginalRequirement = GetPlainText(RequirementBox);
        task.NoteXaml = GetRichTextXaml(RequirementBox);
        task.WebsiteUrl = string.Empty;
        _dataService.SaveTask(task);

        if (_currentMode == "today") _dataService.AddToDay(task, _selectedDay);
        _editingTask = task;
        if (isNew) _allTasks.Add(task);
        if (_currentMode == "today") _selectedDayIds.Add(task.Id);
        QuickAddBox.Clear();
        return true;
    }

    private void SaveTask_Click(object sender, RoutedEventArgs e)
    {
        _editorAutoSaveTimer.Stop();
        if (SaveTaskFromEditor(true)) HidePanel(TaskDrawer, TaskDrawerTransform, false);
    }

    private void DeleteTask_Click(object sender, RoutedEventArgs e)
    {
        if (_editingTask != null) DeleteTaskWithConfirmation(_editingTask);
    }

    private void TaskContextDelete_Click(object sender, RoutedEventArgs e)
    {
        if (!(sender is MenuItem menuItem) || !(menuItem.DataContext is TaskItem item) || item.IsSeparator) return;
        if (item.IsPlanNode)
        {
            if (AppDialog.Confirm(this, "删除节点", $"将计划节点“{item.Title}”移入回收站？", "移入回收站"))
            {
                _dataService.DeletePlanNode(item.Id);
                ReloadCoreData();
            }
            return;
        }
        DeleteTaskWithConfirmation(item);
    }

    private void TaskContextPin_Click(object sender, RoutedEventArgs e)
    {
        if (!(sender is MenuItem menuItem) || !(menuItem.DataContext is TaskItem item) || item.IsSeparator) return;
        if (item.IsPlanNode)
        {
            var pinned = !item.IsPinned;
            _dataService.SetPlanPinned(item.PlanId, pinned);
            item.IsPinned = pinned;
            if (_editingPlan?.Id == item.PlanId) _editingPlan.IsPinned = pinned;
        }
        else
        {
            var pinned = !item.IsPinned;
            _dataService.SetTaskPinned(item.Id, pinned);
            item.IsPinned = pinned;
            if (_editingTask?.Id == item.Id) _editingTask.IsPinned = pinned;
        }
        ReloadCoreData();
    }

    private void DeleteTaskWithConfirmation(TaskItem task)
    {
        if (!AppDialog.Confirm(this, "删除任务",
                $"将“{task.Title}”移入回收站？\n可在回收站恢复，原文件不受影响。",
                "移入回收站")) return;

        _dataService.DeleteTask(task.Id);
        _editingTask = null;
        TitleBox.Clear();
        _suppressTaskAutoSave = true;
        try { CloseAllPanels(true); }
        finally { _suppressTaskAutoSave = false; }
        ReloadCoreData();
        StatusText.Text = "任务已移入回收站";
    }

    private void ToggleComplete_Click(object sender, RoutedEventArgs e)
    {
        if (_editingTask != null) SetTaskCompletion(_editingTask, !_editingTask.IsCompleted);
    }

    private async void TaskComplete_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!(sender is Button button) || !(button.DataContext is TaskItem item) || item.IsSeparator) return;
        button.IsEnabled = false;
        var completed = !item.IsCompleted;
        await AnimateTaskCompletionAsync(button, completed);
        if (item.IsPlanNode)
        {
            _dataService.SetPlanNodeCompletion(item.Id, completed);
            ReloadCoreData();
        }
        else
        {
            SetTaskCompletion(item, completed);
        }
    }

    private static async System.Threading.Tasks.Task AnimateTaskCompletionAsync(Button button, bool completed)
    {
        if (!(button.Content is Grid content)) return;
        var glyph = content.Children.OfType<TextBlock>().FirstOrDefault();
        var checkPath = content.Children.OfType<System.Windows.Shapes.Path>().FirstOrDefault();
        if (completed && checkPath != null)
        {
            if (glyph != null) glyph.Opacity = 0;
            checkPath.Opacity = 1;
            checkPath.BeginAnimation(
                System.Windows.Shapes.Shape.StrokeDashOffsetProperty,
                new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(380))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.HoldEnd
                });
            await System.Threading.Tasks.Task.Delay(440);
            return;
        }

        if (glyph != null)
        {
            glyph.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180)));
            await System.Threading.Tasks.Task.Delay(210);
        }
    }

    private void SetTaskCompletion(TaskItem task, bool completed)
    {
        if (TaskDrawer.Visibility == Visibility.Visible && _editingTask?.Id == task.Id)
            SaveTaskFromEditor(false);
        var completionPlanDate = _currentMode == "today"
            ? _selectedDay
            : _dataService.GetActiveDayForTask(task.Id) ?? DateTime.Today;
        _dataService.SetCompletion(task.Id, completed, completionPlanDate);
        task.IsCompleted = completed;
        task.CompletedAt = completed ? DateTime.Now : (DateTime?)null;
        _suppressTaskAutoSave = true;
        try { CloseAllPanels(true); }
        finally { _suppressTaskAutoSave = false; }
        ReloadCoreData();
        StatusText.Text = completed ? "任务已完成" : "已恢复为未完成";
    }

    private void TodayAction_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveTaskFromEditor(true) || _editingTask == null) return;

        if (_selectedDayIds.Contains(_editingTask.Id))
            _dataService.RemoveFromDay(_editingTask.Id, _selectedDay);
        else
        {
            _editingTask.DueDate = _selectedDay;
            _dataService.SaveTask(_editingTask);
            _dataService.AddToDay(_editingTask, _selectedDay);
        }

        var id = _editingTask.Id;
        ReloadCoreData(id);
        TodayActionButton.Content = _selectedDayIds.Contains(id) ? "移出所选日期" : "加入所选日期";
    }

    private void NewPlan_Click(object sender, RoutedEventArgs e)
    {
        ClearPlanEditor();
        OpenPlanDrawer();
        PlanTitleBox.Focus();
    }

    private void PlanCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount != 2) return;
        if (!(sender is FrameworkElement card) || !(card.DataContext is PlanItem plan)) return;

        // 节点完成按钮自行处理点击，不能顺带打开计划详情。
        var current = e.OriginalSource as DependencyObject;
        while (current != null && !ReferenceEquals(current, card))
        {
            if (current is Button) return;
            current = current is FrameworkContentElement contentElement
                ? contentElement.Parent
                : VisualTreeHelper.GetParent(current);
        }

        PopulatePlanEditor(plan);
        OpenPlanDrawer();
        e.Handled = true;
    }

    private void PlanPreviewNode_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount != 2 ||
            !(sender is FrameworkElement row) || !(row.DataContext is PlanNode previewNode)) return;

        var current = e.OriginalSource as DependencyObject;
        while (current != null && !ReferenceEquals(current, row))
        {
            if (current is Button) return;
            current = current is FrameworkContentElement contentElement
                ? contentElement.Parent
                : VisualTreeHelper.GetParent(current);
        }

        var plan = _plans.FirstOrDefault(candidate => candidate.Id == previewNode.PlanId)
            ?? _dataService.GetPlans().FirstOrDefault(candidate => candidate.Id == previewNode.PlanId);
        if (plan == null) return;
        PopulatePlanEditor(plan);
        OpenPlanDrawer();
        var node = _editingPlan?.Nodes.FirstOrDefault(candidate => candidate.Id == previewNode.Id);
        if (node != null)
        {
            PlanNodeList.SelectedItem = node;
            OpenPlanNodeEditor(node);
        }
        e.Handled = true;
    }

    private void PlanContextPin_Click(object sender, RoutedEventArgs e)
    {
        if (!(sender is MenuItem menuItem) || !(menuItem.DataContext is PlanItem plan)) return;
        var pinned = !plan.IsPinned;
        _dataService.SetPlanPinned(plan.Id, pinned);
        plan.IsPinned = pinned;
        if (_editingPlan?.Id == plan.Id) _editingPlan.IsPinned = pinned;
        _plans = _dataService.GetPlans();
        ApplyPlanFilter();
    }

    private void PlanContextDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is PlanItem plan)
            MovePlanToRecycleBin(plan);
    }

    private void PopulatePlanEditor(PlanItem plan)
    {
        _loadingEditorFields = true;
        try
        {
            _editingPlan = plan;
            PlanTitleBox.Text = plan.Title;
            PlanGoalBox.Text = plan.Goal;
            PlanDueDatePicker.SelectedDate = plan.DueDate;
            PlanNodeList.ItemsSource = plan.Nodes;
            ClearPlanNodeEditor();
        }
        finally { _loadingEditorFields = false; }
    }

    private void ClearPlanEditor()
    {
        _loadingEditorFields = true;
        try
        {
            _editingPlan = null;
            PlanTitleBox.Clear();
            PlanGoalBox.Clear();
            PlanDueDatePicker.SelectedDate = null;
            NewPlanNodeBox.Clear();
            PlanNodeList.ItemsSource = null;
            ClearPlanNodeEditor();
        }
        finally { _loadingEditorFields = false; }
    }

    private bool SaveCurrentPlan(bool showValidation)
    {
        var title = PlanTitleBox.Text.Trim();
        if (title.Length == 0 && !showValidation && _editingPlan != null)
            title = _editingPlan.Title;
        if (title.Length == 0)
        {
            if (showValidation)
            {
                AppDialog.Notify(this, "计划名称", "请填写计划名称。");
                PlanTitleBox.Focus();
            }
            return false;
        }

        var isNew = _editingPlan == null;
        var plan = _editingPlan ?? new PlanItem();
        plan.Title = title;
        plan.Goal = PlanGoalBox.Text;
        plan.DueDate = PlanDueDatePicker.SelectedDate;
        _dataService.SavePlan(plan);
        _editingPlan = plan;
        if (isNew && _currentMode == "plans") _plans.Add(plan);
        return true;
    }

    private void SavePlan_Click(object sender, RoutedEventArgs e)
    {
        _editorAutoSaveTimer.Stop();
        if (!SaveCurrentPlan(true)) return;
        StatusText.Text = "计划已保存";
        HidePanel(PlanDrawer, PlanDrawerTransform, false);
    }

    private void AddPlanNode_Click(object sender, RoutedEventArgs e)
    {
        var title = NewPlanNodeBox.Text.Trim();
        if (title.Length == 0) return;
        _editorAutoSaveTimer.Stop();
        if (!SaveCurrentPlan(true) || _editingPlan == null) return;
        var node = _dataService.AddPlanNode(_editingPlan!.Id, title);
        NewPlanNodeBox.Clear();
        ReloadPlansAndKeepEditor(_editingPlan.Id);
        var selected = _editingPlan?.Nodes.FirstOrDefault(item => item.Id == node.Id);
        if (selected != null)
        {
            PlanNodeList.SelectedItem = selected;
            OpenPlanNodeEditor(selected);
        }
    }

    private void NewPlanNodeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddPlanNode_Click(sender, e);
            e.Handled = true;
        }
    }

    private async void PlanNodeComplete_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!(sender is Button button) || !(button.DataContext is PlanNode node)) return;
        button.IsEnabled = false;
        var completed = !node.IsCompleted;
        await AnimateTaskCompletionAsync(button, completed);
        _dataService.SetPlanNodeCompletion(node.Id, completed);
        if (PlanDrawer.Visibility == Visibility.Visible && _editingPlan?.Id == node.PlanId)
            ReloadPlansAndKeepEditor(node.PlanId);
        else
        {
            _plans = _dataService.GetPlans();
            ApplyPlanFilter();
        }
    }

    private void PlanNodeContextDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is PlanNode node) MovePlanNodeToRecycleBin(node);
    }

    private void InsertPlanNodeBefore_Click(object sender, RoutedEventArgs e)
    {
        if (!(sender is MenuItem menuItem) || !(menuItem.DataContext is PlanNode beforeNode)) return;
        _editorAutoSaveTimer.Stop();
        if (!SaveCurrentPlan(false) || _editingPlan == null) return;

        var inserted = _dataService.InsertPlanNodeBefore(beforeNode.PlanId, beforeNode.Id, "新节点");
        ReloadPlansAndKeepEditor(beforeNode.PlanId);
        var selected = _editingPlan?.Nodes.FirstOrDefault(node => node.Id == inserted.Id);
        if (selected == null) return;
        PlanNodeList.SelectedItem = selected;
        OpenPlanNodeEditor(selected);
        PlanNodeEditorTitle.Focus();
        PlanNodeEditorTitle.SelectAll();
    }

    private void PlanNodeRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount != 2) return;
        if (!(sender is FrameworkElement row) || !(row.DataContext is PlanNode node)) return;
        var current = e.OriginalSource as DependencyObject;
        while (current != null && !ReferenceEquals(current, row))
        {
            if (current is Button) return;
            current = current is FrameworkContentElement contentElement
                ? contentElement.Parent
                : VisualTreeHelper.GetParent(current);
        }
        _editorAutoSaveTimer.Stop();
        if (!SaveCurrentPlan(false)) return;
        PlanNodeList.SelectedItem = node;
        OpenPlanNodeEditor(node);
        e.Handled = true;
    }

    private void PlanNodeDragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            !(sender is FrameworkElement handle) ||
            !(handle.DataContext is PlanNode node)) return;

        _planNodeDragStart = e.GetPosition(PlanNodeList);
        _pendingPlanNodeDrag = node;
        _capturedPlanNodeDragHandle = handle;
        handle.CaptureMouse();
        e.Handled = true;
    }

    private void PlanNodeDragHandle_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pendingPlanNodeDrag == null || _capturedPlanNodeDragHandle == null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            ResetPlanNodeDrag();
            return;
        }

        var position = e.GetPosition(PlanNodeList);
        if (Math.Abs(position.X - _planNodeDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _planNodeDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var node = _pendingPlanNodeDrag;
        var handle = _capturedPlanNodeDragHandle;
        ResetPlanNodeDrag();
        DragDrop.DoDragDrop(handle, new DataObject(typeof(PlanNode), node), DragDropEffects.Move);
        e.Handled = true;
    }

    private void PlanNodeDragHandle_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ResetPlanNodeDrag();
        e.Handled = true;
    }

    private void PlanNodeList_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(PlanNode)) ||
            !(e.Data.GetData(typeof(PlanNode)) is PlanNode node) ||
            _editingPlan == null || node.PlanId != _editingPlan.Id)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void PlanNodeList_Drop(object sender, DragEventArgs e)
    {
        if (!(e.Data.GetData(typeof(PlanNode)) is PlanNode draggedNode) ||
            _editingPlan == null || draggedNode.PlanId != _editingPlan.Id) return;

        var original = e.OriginalSource as DependencyObject;
        var targetContainer = original == null
            ? null
            : ItemsControl.ContainerFromElement(PlanNodeList, original) as ListBoxItem;
        var targetNode = targetContainer?.DataContext as PlanNode;
        if (targetNode?.Id == draggedNode.Id)
        {
            e.Handled = true;
            return;
        }

        _editorAutoSaveTimer.Stop();
        if (!SaveCurrentPlan(false)) return;
        var orderedNodes = _editingPlan.Nodes
            .OrderBy(node => node.SortOrder)
            .ThenBy(node => node.CreatedAt)
            .ToList();
        orderedNodes.RemoveAll(node => node.Id == draggedNode.Id);

        var insertIndex = orderedNodes.Count;
        if (targetNode != null)
        {
            insertIndex = orderedNodes.FindIndex(node => node.Id == targetNode.Id);
            if (insertIndex < 0) insertIndex = orderedNodes.Count;
            else if (targetContainer != null && e.GetPosition(targetContainer).Y > targetContainer.ActualHeight / 2)
                insertIndex++;
        }
        orderedNodes.Insert(Math.Min(insertIndex, orderedNodes.Count), draggedNode);
        _dataService.ReorderPlanNodes(_editingPlan.Id, orderedNodes.Select(node => node.Id).ToList());
        ReloadPlansAndKeepEditor(_editingPlan.Id);
        StatusText.Text = "节点顺序已更新";
        e.Handled = true;
    }

    private void ResetPlanNodeDrag()
    {
        if (_capturedPlanNodeDragHandle?.IsMouseCaptured == true)
            _capturedPlanNodeDragHandle.ReleaseMouseCapture();
        _pendingPlanNodeDrag = null;
        _capturedPlanNodeDragHandle = null;
    }

    private void OpenPlanNodeEditor(PlanNode node)
    {
        _loadingEditorFields = true;
        try
        {
            _editingPlanNode = node;
            PlanNodeEditorTitle.Text = node.Title;
            PlanNodeDueDatePicker.SelectedDate = node.DueDate;
            PlanNodePriorityBox.SelectedIndex = Clamp(node.Priority, 0, 2);
            if (!TryLoadRichTextXaml(PlanNodeNoteBox, node.NoteXaml))
            {
                var legacyLines = new List<string>();
                if (!string.IsNullOrWhiteSpace(node.Note)) legacyLines.Add(node.Note);
                if (!string.IsNullOrWhiteSpace(node.AttachmentPath)) legacyLines.Add(node.AttachmentPath);
                SetRichText(PlanNodeNoteBox, string.Join(Environment.NewLine, legacyLines), true);
            }
            PlanNodeAutoSaveText.Text = "自动保存";
            PlanEditorRoot.Visibility = Visibility.Collapsed;
            PlanNodeEditor.Visibility = Visibility.Visible;
        }
        finally { _loadingEditorFields = false; }
    }

    private void ClearPlanNodeEditor()
    {
        _loadingEditorFields = true;
        try
        {
            _editingPlanNode = null;
            PlanNodeEditor.Visibility = Visibility.Collapsed;
            PlanEditorRoot.Visibility = Visibility.Visible;
            PlanNodeEditorTitle.Text = string.Empty;
            PlanNodeDueDatePicker.SelectedDate = null;
            PlanNodePriorityBox.SelectedIndex = 1;
            SetRichText(PlanNodeNoteBox, string.Empty, false);
            PlanNodeAutoSaveText.Text = "自动保存";
        }
        finally { _loadingEditorFields = false; }
    }

    private bool SaveCurrentPlanNode(bool showValidation)
    {
        if (_editingPlanNode == null) return false;
        var title = PlanNodeEditorTitle.Text.Trim();
        if (title.Length == 0 && !showValidation) title = _editingPlanNode.Title;
        if (title.Length == 0)
        {
            if (showValidation)
            {
                AppDialog.Notify(this, "节点名称", "请输入节点名称。");
                PlanNodeEditorTitle.Focus();
            }
            return false;
        }
        var node = _editingPlanNode;
        node.Title = title;
        node.Note = GetPlainText(PlanNodeNoteBox);
        node.NoteXaml = GetRichTextXaml(PlanNodeNoteBox);
        node.DueDate = PlanNodeDueDatePicker.SelectedDate;
        node.Priority = PlanNodePriorityBox.SelectedIndex < 0 ? 1 : PlanNodePriorityBox.SelectedIndex;
        node.AttachmentPath = string.Empty;
        _dataService.UpdatePlanNodeDetails(node.Id, node.Title, node.Note, node.NoteXaml, node.DueDate, node.Priority);
        PlanNodeList.Items.Refresh();
        PlanNodeAutoSaveText.Text = $"已保存 {DateTime.Now:HH:mm:ss}";
        return true;
    }

    private void ClosePlanNodeEditor_Click(object sender, RoutedEventArgs e)
    {
        _editorAutoSaveTimer.Stop();
        if (!SaveCurrentPlanNode(true)) return;
        ClearPlanNodeEditor();
    }

    private void MovePlanNodeToRecycleBin(PlanNode node)
    {
        if (!AppDialog.Confirm(this, "删除节点", $"将节点“{node.Title}”移入回收站？", "移入回收站")) return;
        _editorAutoSaveTimer.Stop();
        _dataService.DeletePlanNode(node.Id);
        var planId = node.PlanId;
        ClearPlanNodeEditor();
        ReloadPlansAndKeepEditor(planId);
    }

    private void DeletePlan_Click(object sender, RoutedEventArgs e)
    {
        if (_editingPlan != null) MovePlanToRecycleBin(_editingPlan);
    }

    private void MovePlanToRecycleBin(PlanItem plan)
    {
        if (!AppDialog.Confirm(this, "删除计划", $"将计划“{plan.Title}”及其节点移入回收站？", "移入回收站")) return;
        _editorAutoSaveTimer.Stop();
        _dataService.DeletePlan(plan.Id);
        ClearPlanEditor();
        CloseAllPanels(true);
        ReloadCoreData();
        StatusText.Text = "计划已移入回收站";
    }

    private void ReloadPlansAndKeepEditor(string planId)
    {
        _plans = _dataService.GetPlans();
        ApplyPlanFilter();
        var plan = _plans.FirstOrDefault(item => item.Id == planId);
        if (plan != null) PopulatePlanEditor(plan);
    }

    private void PreviousMonth_Click(object sender, RoutedEventArgs e)
    {
        _historyPageStart = _historyPageStart.AddDays(-25);
        BuildHistoryCalendar();
    }

    private void NextMonth_Click(object sender, RoutedEventArgs e)
    {
        var latestStart = DateTime.Today.AddDays(-24);
        _historyPageStart = _historyPageStart.AddDays(25);
        if (_historyPageStart > latestStart) _historyPageStart = latestStart;
        BuildHistoryCalendar();
    }

    private void BuildHistoryCalendar()
    {
        var calendarStart = _historyPageStart.Date;
        var calendarEnd = calendarStart.AddDays(24);
        var summaries = _dataService.GetDailySummaries(calendarStart, calendarEnd);
        var days = new List<CalendarDay>();
        for (var index = 0; index < 25; index++)
        {
            var date = calendarStart.AddDays(index);
            summaries.TryGetValue(date.Date, out var summary);
            days.Add(new CalendarDay
            {
                Date = date,
                IsCurrentMonth = true,
                Total = summary?.Total ?? 0,
                Completed = summary?.Completed ?? 0,
                NoteSummary = summary?.NoteSummary ?? string.Empty
            });
        }
        HistoryMonthText.Text = $"{calendarStart:yyyy.MM.dd} — {calendarEnd:yyyy.MM.dd}";
        NextHistoryPageButton.IsEnabled = calendarEnd < DateTime.Today;
        HistoryCalendar.ItemsSource = days;
        if (_selectedHistoryDate < calendarStart || _selectedHistoryDate > calendarEnd)
            _selectedHistoryDate = calendarEnd > DateTime.Today ? DateTime.Today : calendarEnd;
        LoadHistoryDate(_selectedHistoryDate);
    }

    private void CalendarDay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is CalendarDay day)
        {
            _selectedHistoryDate = day.Date;
            LoadHistoryDate(day.Date);
        }
    }

    private void LoadHistoryDate(DateTime date)
    {
        var rows = _dataService.GetDailyPlan(date);
        HistoryTaskList.ItemsSource = rows;
        HistoryTipText.Text = _dataService.GetDailyNote(date);
        SelectedHistoryDateText.Text = $"{date:yyyy年M月d日 dddd}";
        var completed = rows.Count(row => row.CompletedOnDay);
        HistorySummaryText.Text = rows.Count == 0 ? "当天没有安排" : $"{completed}/{rows.Count} 完成";
    }

    private void LoadRecycleBin()
    {
        var items = _dataService.GetRecycleItems();
        RecycleList.ItemsSource = items;
        RecycleEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RestoreRecycleItem_Click(object sender, RoutedEventArgs e)
    {
        if (!(sender is Button button) || !(button.DataContext is RecycleItem item)) return;
        _dataService.RestoreRecycleItem(item.ItemType, item.Id);
        LoadRecycleBin();
    }

    private void PermanentlyDeleteRecycleItem_Click(object sender, RoutedEventArgs e)
    {
        if (!(sender is Button button) || !(button.DataContext is RecycleItem item)) return;
        if (!AppDialog.Confirm(this, "永久删除",
                $"永久删除“{item.Title}”？\n此操作会清除关联历史且无法恢复。",
                "永久删除")) return;
        _dataService.PermanentlyDeleteRecycleItem(item.ItemType, item.Id);
        LoadRecycleBin();
    }

    private void EmptyRecycleBin_Click(object sender, RoutedEventArgs e)
    {
        if (!(RecycleList.ItemsSource is IEnumerable<RecycleItem> items) || !items.Any()) return;
        if (!AppDialog.Confirm(this, "清空回收站",
                "清空回收站？\n其中的任务、计划、节点及相关历史将永久删除。",
                "确认清空")) return;
        _dataService.EmptyRecycleBin();
        LoadRecycleBin();
    }

    private void OpenTaskDrawer()
    {
        HidePanel(PlanDrawer, PlanDrawerTransform, true);
        SettingsPanel.Visibility = Visibility.Collapsed;
        DrawerShade.Visibility = Visibility.Visible;
        TaskDrawer.Visibility = Visibility.Visible;
        AnimateIn(TaskDrawerTransform);
    }

    private void OpenPlanDrawer()
    {
        HidePanel(TaskDrawer, TaskDrawerTransform, true);
        SettingsPanel.Visibility = Visibility.Collapsed;
        DrawerShade.Visibility = Visibility.Visible;
        PlanDrawer.Visibility = Visibility.Visible;
        AnimateIn(PlanDrawerTransform);
    }

    private void AnimateIn(TranslateTransform transform)
    {
        var start = transform.X;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = 0;
        transform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(start, 0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = _drawerEase,
                FillBehavior = FillBehavior.Stop
            });
    }

    private void CloseTaskDrawer_Click(object sender, RoutedEventArgs e) => HidePanel(TaskDrawer, TaskDrawerTransform, false);
    private void ClosePlanDrawer_Click(object sender, RoutedEventArgs e) => HidePanel(PlanDrawer, PlanDrawerTransform, false);

    private void HidePanel(Border panel, TranslateTransform transform, bool immediate)
    {
        if (panel.Visibility != Visibility.Visible) return;
        _editorAutoSaveTimer.Stop();
        var reloadTasks = false;
        var reloadPlans = false;
        if (ReferenceEquals(panel, TaskDrawer))
        {
            reloadTasks = !_suppressTaskAutoSave && SaveTaskFromEditor(false);
        }
        else if (ReferenceEquals(panel, PlanDrawer))
        {
            if (PlanNodeEditor.Visibility == Visibility.Visible) SaveCurrentPlanNode(false);
            reloadPlans = SaveCurrentPlan(false);
        }
        var target = panel.Width + 20;
        if (immediate)
        {
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = target;
            panel.Visibility = Visibility.Collapsed;
            TaskList.SelectedItem = null;
            ReleasePanelContent(panel);
            if (reloadTasks && (_currentMode == "today" || _currentMode == "all")) ReloadCoreData();
            if (reloadPlans && _currentMode == "plans")
            {
                _plans = _dataService.GetPlans();
                ApplyPlanFilter();
            }
            else if (reloadPlans && _currentMode == "all") ReloadCoreData();
            return;
        }

        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(150)) { EasingFunction = _drawerEase };
        animation.Completed += delegate
        {
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = target;
            panel.Visibility = Visibility.Collapsed;
            TaskList.SelectedItem = null;
            ReleasePanelContent(panel);
            if (reloadTasks && (_currentMode == "today" || _currentMode == "all")) ReloadCoreData();
            if (reloadPlans && _currentMode == "plans")
            {
                _plans = _dataService.GetPlans();
                ApplyPlanFilter();
            }
            else if (reloadPlans && _currentMode == "all") ReloadCoreData();
            if (TaskDrawer.Visibility != Visibility.Visible && PlanDrawer.Visibility != Visibility.Visible &&
                SettingsPanel.Visibility != Visibility.Visible) DrawerShade.Visibility = Visibility.Collapsed;
        };
        transform.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private void ReleasePanelContent(Border panel)
    {
        if (ReferenceEquals(panel, TaskDrawer))
        {
            ClearTaskEditor();
        }
        else if (ReferenceEquals(panel, PlanDrawer))
        {
            ClearPlanEditor();
        }
    }

    private void CloseAllPanels(bool immediate)
    {
        HidePanel(TaskDrawer, TaskDrawerTransform, immediate);
        HidePanel(PlanDrawer, PlanDrawerTransform, immediate);
        SettingsPanel.Visibility = Visibility.Collapsed;
        DrawerShade.Visibility = Visibility.Collapsed;
    }

    private void DrawerShade_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseAllPanels(false);

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (PlanDrawer.Visibility == Visibility.Visible && PlanNodeEditor.Visibility == Visibility.Visible)
                ClosePlanNodeEditor_Click(sender, e);
            else
                CloseAllPanels(false);
            e.Handled = true;
        }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        HidePanel(TaskDrawer, TaskDrawerTransform, true);
        HidePanel(PlanDrawer, PlanDrawerTransform, true);
        ThemeColorBox.Text = _settings.ThemeColor;
        SelectThemeStyle(_settings.ThemeStyle);
        SelectFontFamily(_settings.FontFamilyName);
        BackupDirectoryBox.Text = GetBackupDirectory();
        DrawerShade.Visibility = Visibility.Visible;
        SettingsPanel.Visibility = Visibility.Visible;
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        DrawerShade.Visibility = Visibility.Collapsed;
        SaveSettings();
    }

    private void OpenRecycleBinFromSettings_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        ShowMode("recycle", null);
    }

    private void ChooseBackground_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择页面背景",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif|所有文件|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            _settings.BackgroundImagePath = _settingsService.ImportBackground(dialog.FileName);
            if (_settings.MaskOpacity > 0.70)
            {
                _settings.MaskOpacity = 0.50;
                MaskSlider.Value = _settings.MaskOpacity;
            }
            SaveSettings();
            LoadBackgroundImage(true);
        }
        catch (Exception exception)
        {
            AppDialog.Notify(this, "背景图片", $"背景图片无法设置：\n{exception.Message}");
        }
    }

    private void RemoveBackground_Click(object sender, RoutedEventArgs e)
    {
        _settings.BackgroundImagePath = string.Empty;
        BackgroundImage.Source = null;
        BackgroundStatusText.Text = "未设置背景";
        SaveSettings();
    }

    private bool LoadBackgroundImage(bool showError = false)
    {
        BackgroundImage.Source = null;
        var path = _settings.BackgroundImagePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            BackgroundStatusText.Text = string.IsNullOrWhiteSpace(path) ? "未设置背景" : "背景文件不存在";
            return false;
        }

        try
        {
            var optimizedPath = _settingsService.EnsureOptimizedBackground(path);
            if (!string.Equals(path, optimizedPath, StringComparison.OrdinalIgnoreCase))
            {
                path = optimizedPath;
                _settings.BackgroundImagePath = optimizedPath;
                _settingsService.Save(_settings);
            }
            // 背景仅用于视觉衬底，限制解码宽度可显著降低WPF像素缓存占用。
            var targetWidth = Clamp((int)Math.Ceiling(ActualWidth - NavigationColumn.ActualWidth), 800, 1100);
            using var stream = File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = targetWidth;
            // 不设置IgnoreImageCache：.NET Framework在StreamSource下会因空URI缓存键抛出异常。
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            BackgroundImage.Source = image;
            BackgroundStatusText.Text = $"已使用：{Path.GetFileName(path)}";
            return true;
        }
        catch (Exception exception)
        {
            BackgroundStatusText.Text = "图片无法载入";
            if (showError) AppDialog.Notify(this, "背景图片", $"背景图片无法载入：\n{exception.Message}");
            return false;
        }
    }

    private void ApplyTheme_Click(object sender, RoutedEventArgs e) => ApplyThemeFromInput();

    private void ThemePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string color)
        {
            ThemeColorBox.Text = color;
            ApplyThemeFromInput();
        }
    }

    private void ApplyThemeFromInput()
    {
        if (!TryParseColor(ThemeColorBox.Text.Trim(), out var color))
        {
            AppDialog.Notify(this, "主题色", "请输入有效颜色，例如 #2563EB。");
            return;
        }
        _settings.ThemeColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        ThemeColorBox.Text = _settings.ThemeColor;
        ApplyThemeResources(color);
        ApplyThemeStyleResources();
        SaveSettings();
    }

    private void ThemeStyleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _loadingSettings || !(ThemeStyleBox.SelectedItem is ComboBoxItem item) || !(item.Tag is string style)) return;
        _settings.ThemeStyle = style;
        var accentHex = style == "mac" ? "#0A84FF" : style == "fresh" ? "#2F855A" : "#2563EB";
        _settings.ThemeColor = accentHex;
        ThemeColorBox.Text = accentHex;
        TryParseColor(accentHex, out var accent);
        ApplyThemeResources(accent);
        ApplyThemeStyleResources();
        SaveSettings();
    }

    private void SelectThemeStyle(string style)
    {
        foreach (var candidate in ThemeStyleBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(candidate.Tag as string, style, StringComparison.OrdinalIgnoreCase))
            {
                ThemeStyleBox.SelectedItem = candidate;
                return;
            }
        }
        ThemeStyleBox.SelectedIndex = 0;
        _settings.ThemeStyle = "mist";
    }

    private void FontFamilyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _loadingSettings || !(FontFamilyBox.SelectedItem is ComboBoxItem item) || !(item.Tag is string fontName)) return;
        _settings.FontFamilyName = fontName;
        ApplyFontSetting();
        SaveSettings();
    }

    private void SelectFontFamily(string fontName)
    {
        foreach (var candidate in FontFamilyBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(candidate.Tag as string, fontName, StringComparison.OrdinalIgnoreCase))
            {
                FontFamilyBox.SelectedItem = candidate;
                return;
            }
        }
        FontFamilyBox.SelectedIndex = 0;
        _settings.FontFamilyName = "system";
    }

    private void ApplyFontSetting()
    {
        if (string.Equals(_settings.FontFamilyName, "yahei", StringComparison.OrdinalIgnoreCase))
        {
            FontFamily = new FontFamily("Microsoft YaHei UI");
            return;
        }
        if (string.Equals(_settings.FontFamilyName, "dengxian", StringComparison.OrdinalIgnoreCase))
        {
            FontFamily = new FontFamily("DengXian");
            return;
        }

        // 系统默认值就是原始界面使用的字体；清除本地值可避免主题切换偷偷改字体。
        ClearValue(FontFamilyProperty);
    }

    private void MaskSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        _settings.MaskOpacity = e.NewValue;
        ApplyMaskResource();
        if (!_loadingSettings) SaveSettings();
    }

    private void LayoutSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        _settings.FontSize = FontSizeSlider.Value;
        _settings.NavigationWidth = NavigationWidthSlider.Value;
        _settings.TaskRowHeight = TaskRowHeightSlider.Value;
        _settings.DetailPanelWidth = DetailWidthSlider.Value;
        ApplyLayoutSettings();
        if (!_loadingSettings) SaveSettings();
    }

    private void ApplyAppearance()
    {
        if (!TryParseColor(_settings.ThemeColor, out var accent)) accent = Color.FromRgb(37, 99, 235);
        ApplyThemeResources(accent);
        ApplyThemeStyleResources();
        ApplyFontSetting();
        ApplyMaskResource();
        ApplyLayoutSettings();
        LoadBackgroundImage();
    }

    private static bool TryParseColor(string value, out Color color)
    {
        try
        {
            color = (Color)ColorConverter.ConvertFromString(value);
            return true;
        }
        catch
        {
            color = default(Color);
            return false;
        }
    }

    private static void ApplyThemeResources(Color accent)
    {
        Application.Current.Resources["AccentBrush"] = CreateFrozenBrush(accent);
        Application.Current.Resources["AccentSoftBrush"] = CreateFrozenBrush(Color.FromArgb(42, accent.R, accent.G, accent.B));
    }

    private void ApplyMaskResource()
    {
        var opacity = Clamp(_settings.MaskOpacity, 0.10, 0.90);
        if (!TryParseColor(_settings.ThemeColor, out var accent)) accent = Color.FromRgb(37, 99, 235);
        var baseColor = GetThemeStyleColor(_settings.ThemeStyle, accent);
        Application.Current.Resources["ContentMaskBrush"] = CreateFrozenBrush(
            Color.FromArgb((byte)Math.Round(opacity * 255), baseColor.R, baseColor.G, baseColor.B));
        MaskValueText.Text = $"{opacity:P0}";
    }

    private void ApplyThemeStyleResources()
    {
        var style = (_settings.ThemeStyle ?? "mist").ToLowerInvariant();
        if (!TryParseColor(_settings.ThemeColor, out var accent)) accent = Color.FromRgb(37, 99, 235);
        string panel;
        string toolbar;
        string card;
        string completed;
        string text;
        string muted;
        string line;
        string sidebar;
        string sidebarText;
        string sidebarMuted;
        string sidebarSearch;
        string sidebarHover;
        string sidebarSelection;
        string sidebarDivider;
        string themeDecor;
        string inputSurface;
        string inputBorder;
        double buttonRadius;
        double navRadius;
        double taskCardRadius;
        CornerRadius planCardCornerRadius;
        double toolbarRadius;
        double searchRadius;
        double editorRadius;
        Thickness taskCardMargin;
        Thickness taskCardBorderThickness;
        double taskAccentWidth;
        double planAccentWidth;

        switch (style)
        {
            case "mac":
                panel = BlendThemeColor(Color.FromRgb(247, 247, 249), accent, 0.025);
                toolbar = BlendThemeColor(Color.FromRgb(245, 245, 247), accent, 0.055, 0xEA);
                card = BlendThemeColor(Colors.White, accent, 0.025, 0xF5);
                completed = BlendThemeColor(Color.FromRgb(232, 232, 237), accent, 0.05, 0xE6);
                text = "#FF1D1D1F";
                muted = "#FF6E6E73";
                line = ColorToHex(accent, 0x24);
                sidebar = BlendThemeColor(Color.FromRgb(233, 236, 241), accent, 0.08, 0xF2);
                sidebarText = "#FF1D1D1F";
                sidebarMuted = "#FF6E6E73";
                sidebarSearch = "#D9FFFFFF";
                sidebarHover = ColorToHex(accent, 0x10);
                sidebarSelection = ColorToHex(accent, 0x24);
                sidebarDivider = ColorToHex(accent, 0x18);
                themeDecor = "#00000000";
                inputSurface = BlendThemeColor(Colors.White, accent, 0.025, 0xF2);
                inputBorder = ColorToHex(accent, 0x32);
                buttonRadius = 8;
                navRadius = 7;
                taskCardRadius = 10;
                planCardCornerRadius = new CornerRadius(12);
                toolbarRadius = 11;
                searchRadius = 8;
                editorRadius = 10;
                taskCardMargin = new Thickness(0, 3, 0, 3);
                taskCardBorderThickness = new Thickness(1);
                taskAccentWidth = 0;
                planAccentWidth = 0;
                break;
            case "fresh":
                panel = BlendThemeColor(Color.FromRgb(243, 248, 244), accent, 0.10);
                toolbar = BlendThemeColor(Color.FromRgb(251, 255, 252), accent, 0.08, 0xF2);
                card = BlendThemeColor(Color.FromRgb(252, 255, 253), accent, 0.04, 0xFA);
                completed = BlendThemeColor(Color.FromRgb(231, 240, 233), accent, 0.10, 0xEA);
                text = "#FF163226";
                muted = "#FF5D7467";
                line = ColorToHex(accent, 0x28);
                sidebar = BlendThemeColor(Color.FromRgb(227, 241, 231), accent, 0.16, 0xF4);
                sidebarText = "#FF17372A";
                sidebarMuted = "#FF6B8073";
                sidebarSearch = "#D9FFFFFF";
                sidebarHover = ColorToHex(accent, 0x16);
                sidebarSelection = ColorToHex(accent, 0x28);
                sidebarDivider = ColorToHex(accent, 0x20);
                themeDecor = ColorToHex(accent);
                inputSurface = BlendThemeColor(Colors.White, accent, 0.035, 0xF5);
                inputBorder = ColorToHex(accent, 0x38);
                buttonRadius = 7;
                navRadius = 8;
                taskCardRadius = 8;
                planCardCornerRadius = new CornerRadius(0, 10, 10, 0);
                toolbarRadius = 8;
                searchRadius = 8;
                editorRadius = 7;
                taskCardMargin = new Thickness(0, 3, 0, 3);
                taskCardBorderThickness = new Thickness(1);
                taskAccentWidth = 3;
                planAccentWidth = 6;
                break;
            default:
                panel = "#FFF7F9FA";
                toolbar = "#F1F5F7F8";
                card = "#EEF8FAFB";
                completed = "#E5DDE3E7";
                text = "#FF172033";
                muted = "#FF667085";
                line = "#180F172A";
                if (!TryParseColor(_settings.ThemeColor, out var mistAccent)) mistAccent = Color.FromRgb(37, 99, 235);
                const double ratio = 0.42;
                var mistSidebar = Color.FromRgb(
                    (byte)(mistAccent.R * ratio + 15 * (1 - ratio)),
                    (byte)(mistAccent.G * ratio + 23 * (1 - ratio)),
                    (byte)(mistAccent.B * ratio + 42 * (1 - ratio)));
                sidebar = $"#{mistSidebar.A:X2}{mistSidebar.R:X2}{mistSidebar.G:X2}{mistSidebar.B:X2}";
                sidebarText = "#FFE5EAF2";
                sidebarMuted = "#FFAAB6C8";
                sidebarSearch = "#20FFFFFF";
                sidebarHover = "#22FFFFFF";
                sidebarSelection = "#30FFFFFF";
                sidebarDivider = "#24FFFFFF";
                themeDecor = "#00000000";
                inputSurface = "#F8FFFFFF";
                inputBorder = "#FFCBD5E1";
                buttonRadius = 3;
                navRadius = 2;
                taskCardRadius = 0;
                planCardCornerRadius = new CornerRadius(4);
                toolbarRadius = 0;
                searchRadius = 2;
                editorRadius = 0;
                taskCardMargin = new Thickness(0);
                taskCardBorderThickness = new Thickness(0, 0, 0, 1);
                taskAccentWidth = 0;
                planAccentWidth = 0;
                break;
        }

        Application.Current.Resources["PanelBrush"] = CreateFrozenBrush((Color)ColorConverter.ConvertFromString(panel));
        Application.Current.Resources["ToolbarBrush"] = CreateFrozenBrush((Color)ColorConverter.ConvertFromString(toolbar));
        Application.Current.Resources["CardBrush"] = CreateFrozenBrush((Color)ColorConverter.ConvertFromString(card));
        Application.Current.Resources["CompletedBrush"] = CreateFrozenBrush((Color)ColorConverter.ConvertFromString(completed));
        Application.Current.Resources["TextBrush"] = CreateFrozenBrush((Color)ColorConverter.ConvertFromString(text));
        Application.Current.Resources["MutedBrush"] = CreateFrozenBrush((Color)ColorConverter.ConvertFromString(muted));
        Application.Current.Resources["LineBrush"] = CreateFrozenBrush((Color)ColorConverter.ConvertFromString(line));
        Application.Current.Resources["SidebarBrush"] = CreateFrozenBrush((Color)ColorConverter.ConvertFromString(sidebar));
        Application.Current.Resources["SidebarTextBrush"] = CreateFrozenBrush((Color)ColorConverter.ConvertFromString(sidebarText));
        Application.Current.Resources["SidebarMutedBrush"] = CreateFrozenBrush((Color)ColorConverter.ConvertFromString(sidebarMuted));
        Application.Current.Resources["SidebarSearchBrush"] = CreateFrozenBrush((Color)ColorConverter.ConvertFromString(sidebarSearch));
        Application.Current.Resources["SidebarHoverBrush"] = CreateFrozenBrush((Color)ColorConverter.ConvertFromString(sidebarHover));
        Application.Current.Resources["SidebarSelectionBrush"] = CreateFrozenBrush((Color)ColorConverter.ConvertFromString(sidebarSelection));
        Application.Current.Resources["SidebarDividerBrush"] = CreateFrozenBrush((Color)ColorConverter.ConvertFromString(sidebarDivider));
        Application.Current.Resources["ThemeDecorBrush"] = CreateFrozenBrush((Color)ColorConverter.ConvertFromString(themeDecor));
        Application.Current.Resources["InputSurfaceBrush"] = CreateFrozenBrush((Color)ColorConverter.ConvertFromString(inputSurface));
        Application.Current.Resources["InputBorderBrush"] = CreateFrozenBrush((Color)ColorConverter.ConvertFromString(inputBorder));
        Application.Current.Resources["ButtonCornerRadius"] = new CornerRadius(buttonRadius);
        Application.Current.Resources["NavCornerRadius"] = new CornerRadius(navRadius);
        Application.Current.Resources["TaskCardCornerRadius"] = new CornerRadius(taskCardRadius);
        Application.Current.Resources["PlanCardCornerRadius"] = planCardCornerRadius;
        Application.Current.Resources["ToolbarCornerRadius"] = new CornerRadius(toolbarRadius);
        Application.Current.Resources["SearchCornerRadius"] = new CornerRadius(searchRadius);
        Application.Current.Resources["EditorCornerRadius"] = new CornerRadius(editorRadius);
        Application.Current.Resources["TaskCardMargin"] = taskCardMargin;
        Application.Current.Resources["TaskCardBorderThickness"] = taskCardBorderThickness;
        Application.Current.Resources["TaskAccentWidth"] = taskAccentWidth;
        Application.Current.Resources["PlanAccentWidth"] = planAccentWidth;
        MacColorPresetPanel.Visibility = style == "mac" ? Visibility.Visible : Visibility.Collapsed;
        RefreshActiveNavigationBrush();
        ApplyMaskResource();
    }

    private void RefreshActiveNavigationBrush()
    {
        if (_activeNavigation == null) return;
        _activeNavigation.Background = Application.Current.Resources["SidebarSelectionBrush"] as Brush ?? Brushes.Transparent;
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static string BlendThemeColor(Color baseColor, Color accent, double accentWeight, byte alpha = 0xFF)
    {
        var weight = Clamp(accentWeight, 0, 1);
        var mixed = Color.FromRgb(
            (byte)Math.Round(baseColor.R * (1 - weight) + accent.R * weight),
            (byte)Math.Round(baseColor.G * (1 - weight) + accent.G * weight),
            (byte)Math.Round(baseColor.B * (1 - weight) + accent.B * weight));
        return ColorToHex(mixed, alpha);
    }

    private static string ColorToHex(Color color, byte alpha = 0xFF) =>
        $"#{alpha:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Color GetThemeStyleColor(string style, Color accent)
    {
        switch ((style ?? "mist").ToLowerInvariant())
        {
            case "mist": return Color.FromRgb(239, 243, 246);
            case "mac": return MixThemeColor(Color.FromRgb(245, 245, 247), accent, 0.04);
            case "fresh": return MixThemeColor(Color.FromRgb(238, 248, 241), accent, 0.10);
            default: return Color.FromRgb(239, 243, 246);
        }
    }

    private static Color MixThemeColor(Color baseColor, Color accent, double accentWeight)
    {
        var weight = Clamp(accentWeight, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(baseColor.R * (1 - weight) + accent.R * weight),
            (byte)Math.Round(baseColor.G * (1 - weight) + accent.G * weight),
            (byte)Math.Round(baseColor.B * (1 - weight) + accent.B * weight));
    }

    private void ApplyLayoutSettings()
    {
        FontSize = Clamp(_settings.FontSize, 12, 19);
        NavigationColumn.Width = new GridLength(Clamp(_settings.NavigationWidth, 180, 280));
        TaskDrawer.Width = Clamp(_settings.DetailPanelWidth, 350, 560);
        PlanDrawer.Width = Clamp(_settings.DetailPanelWidth + 50, 400, 610);
        Application.Current.Resources["TaskRowHeight"] = Clamp(_settings.TaskRowHeight, 46, 80);
        FontSizeText.Text = $"{FontSize:F0} px";
        NavigationWidthText.Text = $"{NavigationColumn.Width.Value:F0} px";
        TaskRowHeightText.Text = $"{_settings.TaskRowHeight:F0} px";
        DetailWidthText.Text = $"{TaskDrawer.Width:F0} px";
    }

    private void ChooseAttachment_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "插入文件地址", Multiselect = false, Filter = "所有文件|*.*" };
        if (dialog.ShowDialog() != true) return;
        InsertNoteHyperlink(RequirementBox, dialog.FileName);
    }

    private void ChooseFolderAttachment_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "插入文件夹地址", ShowNewFolderButton = true };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) InsertNoteHyperlink(RequirementBox, dialog.SelectedPath);
    }

    private void ChoosePlanNodeFile_Click(object sender, RoutedEventArgs e)
    {
        if (_editingPlanNode == null) return;
        var dialog = new OpenFileDialog { Title = "插入文件地址", Multiselect = false, Filter = "所有文件|*.*" };
        if (dialog.ShowDialog() == true) InsertNoteHyperlink(PlanNodeNoteBox, dialog.FileName);
    }

    private void ChoosePlanNodeFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_editingPlanNode == null) return;
        using var dialog = new Forms.FolderBrowserDialog { Description = "插入文件夹地址", ShowNewFolderButton = true };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) InsertNoteHyperlink(PlanNodeNoteBox, dialog.SelectedPath);
    }

    private void AddSelectedNoteHyperlink_Click(object sender, RoutedEventArgs e)
        => AddSelectedHyperlink(RequirementBox);

    private void AddSelectedPlanNodeHyperlink_Click(object sender, RoutedEventArgs e)
        => AddSelectedHyperlink(PlanNodeNoteBox);

    private static void AddSelectedHyperlink(RichTextBox editor)
    {
        var selection = editor.Selection;
        var address = selection.Text.Trim().Trim('"');
        if (selection.IsEmpty || !TryGetLinkUri(address, out var uri))
        {
            AppDialog.Notify(Application.Current?.MainWindow, "添加超链接",
                "请先选中一个完整的网址、文件地址或文件夹地址。");
            return;
        }

        try
        {
            var hyperlink = new Hyperlink(selection.Start, selection.End) { NavigateUri = uri };
            StyleHyperlink(hyperlink);
        }
        catch
        {
            AppDialog.Notify(Application.Current?.MainWindow, "添加超链接",
                "超链接不能跨越多个段落，请只选中单行地址。");
        }
    }

    private void RequirementBox_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
        => OpenHyperlinkAtPointer(RequirementBox, e);

    private void PlanNodeNoteBox_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
        => OpenHyperlinkAtPointer(PlanNodeNoteBox, e);

    private static void OpenHyperlinkAtPointer(RichTextBox editor, MouseButtonEventArgs e)
    {
        var pointer = editor.GetPositionFromPoint(e.GetPosition(editor), true);
        DependencyObject? current = pointer?.Parent as DependencyObject;
        while (current != null && !(current is Hyperlink))
        {
            current = current is FrameworkContentElement contentElement
                ? contentElement.Parent as DependencyObject
                : VisualTreeHelper.GetParent(current);
        }
        if (!(current is Hyperlink hyperlink) || hyperlink.NavigateUri == null) return;
        OpenPath(hyperlink.NavigateUri.IsFile ? hyperlink.NavigateUri.LocalPath : hyperlink.NavigateUri.AbsoluteUri);
        e.Handled = true;
    }

    private static void InsertNoteHyperlink(RichTextBox editor, string address)
    {
        if (!TryGetLinkUri(address, out var uri)) return;
        editor.Focus();
        var selection = editor.Selection;
        selection.Text = address;
        try
        {
            var hyperlink = new Hyperlink(selection.Start, selection.End) { NavigateUri = uri };
            StyleHyperlink(hyperlink);
            editor.CaretPosition = hyperlink.ElementEnd.GetInsertionPosition(LogicalDirection.Forward);
        }
        catch
        {
            // selection.Text 已保留地址；这里只是不应用超链接格式。
        }
    }

    private void RequirementBox_Pasting(object sender, DataObjectPastingEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() => FormatRichTextLinks(RequirementBox)), DispatcherPriority.Background);

    private void PlanNodeNoteBox_Pasting(object sender, DataObjectPastingEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() => FormatRichTextLinks(PlanNodeNoteBox)), DispatcherPriority.Background);

    private static void FormatRichTextLinks(RichTextBox editor)
    {
        foreach (var paragraph in editor.Document.Blocks.OfType<Paragraph>())
            FormatInlineLinks(paragraph.Inlines);
    }

    private static void FormatInlineLinks(InlineCollection inlines)
    {
        foreach (var inline in inlines.Cast<Inline>().ToList())
        {
            if (inline is Hyperlink) continue;
            if (inline is Span span)
            {
                FormatInlineLinks(span.Inlines);
                continue;
            }
            if (!(inline is Run run) || !WebAddressPattern.IsMatch(run.Text)) continue;

            var position = 0;
            foreach (Match match in WebAddressPattern.Matches(run.Text))
            {
                if (match.Index > position)
                    inlines.InsertBefore(run, new Run(run.Text.Substring(position, match.Index - position)));
                var address = match.Value.TrimEnd('.', ',', ';', '，', '。', ')', '）');
                if (TryGetLinkUri(address, out var uri))
                {
                    var link = new Hyperlink(new Run(address)) { NavigateUri = uri };
                    StyleHyperlink(link);
                    inlines.InsertBefore(run, link);
                }
                else inlines.InsertBefore(run, new Run(address));
                var suffixLength = match.Value.Length - address.Length;
                if (suffixLength > 0)
                    inlines.InsertBefore(run, new Run(match.Value.Substring(address.Length)));
                position = match.Index + match.Length;
            }
            if (position < run.Text.Length) inlines.InsertBefore(run, new Run(run.Text.Substring(position)));
            inlines.Remove(run);
        }
    }

    private static string GetPlainText(RichTextBox box) =>
        new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text.TrimEnd('\r', '\n');

    private static string GetRichTextXaml(RichTextBox box)
    {
        try
        {
            using var stream = new MemoryStream();
            new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Save(stream, DataFormats.Xaml);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch { return string.Empty; }
    }

    private static bool TryLoadRichTextXaml(RichTextBox box, string xaml)
    {
        if (string.IsNullOrWhiteSpace(xaml)) return false;
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xaml));
            box.Document.Blocks.Clear();
            new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Load(stream, DataFormats.Xaml);
            AttachHyperlinkHandlers(box.Document);
            return true;
        }
        catch
        {
            box.Document.Blocks.Clear();
            return false;
        }
    }

    private static void AttachHyperlinkHandlers(FlowDocument document)
    {
        foreach (var paragraph in document.Blocks.OfType<Paragraph>())
            AttachHyperlinkHandlers(paragraph.Inlines);
    }

    private static void AttachHyperlinkHandlers(InlineCollection inlines)
    {
        foreach (var inline in inlines.Cast<Inline>())
        {
            if (inline is Hyperlink hyperlink) StyleHyperlink(hyperlink);
            else if (inline is Span span) AttachHyperlinkHandlers(span.Inlines);
        }
    }

    private static void SetRichText(RichTextBox box, string text, bool formatLinks)
    {
        box.Document.Blocks.Clear();
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        var lines = (text ?? string.Empty).Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        for (var index = 0; index < lines.Length; index++)
        {
            if (formatLinks) AppendTextWithLinks(paragraph, lines[index]);
            else paragraph.Inlines.Add(new Run(lines[index]));
            if (index < lines.Length - 1) paragraph.Inlines.Add(new LineBreak());
        }
        box.Document.Blocks.Add(paragraph);
    }

    private static void AppendTextWithLinks(Paragraph paragraph, string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length > 0 && TryGetLinkUri(trimmed.Trim('"'), out var wholeLineUri))
        {
            var leading = line.Substring(0, line.IndexOf(trimmed, StringComparison.Ordinal));
            if (leading.Length > 0) paragraph.Inlines.Add(new Run(leading));
            var wholeLink = new Hyperlink(new Run(trimmed)) { NavigateUri = wholeLineUri };
            StyleHyperlink(wholeLink);
            return;
        }

        var position = 0;
        foreach (Match match in WebAddressPattern.Matches(line))
        {
            if (match.Index > position) paragraph.Inlines.Add(new Run(line.Substring(position, match.Index - position)));
            var address = match.Value.TrimEnd('.', ',', ';', '，', '。', ')', '）');
            if (TryGetLinkUri(address, out var uri))
            {
                var link = new Hyperlink(new Run(address)) { NavigateUri = uri };
                StyleHyperlink(link);
            }
            else paragraph.Inlines.Add(new Run(address));
            var suffixLength = match.Value.Length - address.Length;
            if (suffixLength > 0) paragraph.Inlines.Add(new Run(match.Value.Substring(address.Length)));
            position = match.Index + match.Length;
        }
        if (position < line.Length) paragraph.Inlines.Add(new Run(line.Substring(position)));
    }

    private static bool TryGetLinkUri(string value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var address = value.Trim().Trim('"');
        if (address.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) address = "https://" + address;
        if (address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(address, UriKind.Absolute, out uri);
        if (!Path.IsPathRooted(address)) return false;
        try
        {
            uri = new Uri(Path.GetFullPath(address));
            return true;
        }
        catch { return false; }
    }

    private static void StyleHyperlink(Hyperlink hyperlink)
    {
        hyperlink.Foreground = Brushes.DodgerBlue;
        hyperlink.TextDecorations = TextDecorations.Underline;
        hyperlink.Cursor = Cursors.Hand;
        hyperlink.ToolTip = "双击打开";
        hyperlink.RequestNavigate -= Hyperlink_RequestNavigate;
    }

    private static void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        OpenPath(e.Uri.IsFile ? e.Uri.LocalPath : e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private static void OpenPathIfPresent(string path)
    {
        if (!string.IsNullOrWhiteSpace(path)) OpenPath(path.Trim().Trim('"'));
    }

    private void Backup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _dataService.CreateBackup(GetBackupDirectory());
            AppDialog.Notify(this, "备份完成", path);
        }
        catch (Exception exception)
        {
            AppDialog.Notify(this, "备份失败", exception.Message);
        }
    }

    private void ChooseBackupDirectory_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择 MyTodo 备份文件夹",
            SelectedPath = GetBackupDirectory(),
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        _settings.BackupDirectory = dialog.SelectedPath;
        BackupDirectoryBox.Text = dialog.SelectedPath;
        SaveSettings();
    }

    private void OpenDataDirectory_Click(object sender, RoutedEventArgs e) => OpenPath(_dataService.DataDirectory);

    private string GetBackupDirectory() => string.IsNullOrWhiteSpace(_settings.BackupDirectory)
        ? _dataService.BackupDirectory
        : _settings.BackupDirectory;

    private void SaveSettings()
    {
        try { _settingsService.Save(_settings); }
        catch { }
    }

    private static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception exception)
        {
            AppDialog.Notify(Application.Current?.MainWindow, "无法打开", exception.Message);
        }
    }

    private static double Clamp(double value, double minimum, double maximum) =>
        Math.Max(minimum, Math.Min(maximum, value));

    private static int Clamp(int value, int minimum, int maximum) =>
        Math.Max(minimum, Math.Min(maximum, value));
}
