using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MyTodo;

public sealed class TaskItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string OriginalRequirement { get; set; } = string.Empty;
    public string NoteXaml { get; set; } = string.Empty;
    public DateTime? DueDate { get; set; }
    public int Priority { get; set; } = 1;
    public string WebsiteUrl { get; set; } = string.Empty;
    public bool IsPinned { get; set; }
    public bool IsCompleted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    // “全部任务”中的计划节点会打开所属计划及节点详情。
    public bool IsPlanNode { get; set; }
    public string PlanId { get; set; } = string.Empty;
    public string PlanTitle { get; set; } = string.Empty;
    public bool IsSeparator { get; set; }

    public string SummaryText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(OriginalRequirement)) return string.Empty;
            return OriginalRequirement
                .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.Length > 0) ?? string.Empty;
        }
    }

    public string DueDateText => DueDate?.ToString("yyyy-MM-dd") ?? string.Empty;
    public string DetailText
    {
        get
        {
            var parts = new List<string>();
            if (IsPlanNode && !string.IsNullOrWhiteSpace(PlanTitle))
            {
                parts.Add($"计划：{PlanTitle}");
            }
            if (DueDate.HasValue)
            {
                parts.Add($"截止 {DueDate:MM月dd日}");
            }
            return string.Join("  ·  ", parts);
        }
    }

    public string ImportantGlyph => Priority == 2 ? "★" : string.Empty;
    public string PinnedGlyph => IsPinned ? "置顶" : string.Empty;
    public string PinMenuText => IsPlanNode
        ? (IsPinned ? "取消置顶所属计划" : "置顶所属计划")
        : (IsPinned ? "取消置顶" : "置顶任务");
    public string CompletionGlyph => IsCompleted ? "✓" : string.Empty;
}

public sealed class PlanItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string Goal { get; set; } = string.Empty;
    public DateTime? DueDate { get; set; }
    public bool IsPinned { get; set; }
    public bool IsCompleted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<PlanNode> Nodes { get; set; } = new List<PlanNode>();

    public string DueDateText => DueDate.HasValue ? $"截止 {DueDate:yyyy-MM-dd}" : "未设置截止日期";
    public int CompletedCount => Nodes.Count(node => node.IsCompleted);
    public string ProgressText => Nodes.Count == 0
        ? "尚未添加任务节点"
        : $"已完成 {CompletedCount}/{Nodes.Count}";
    public double ProgressValue => Nodes.Count == 0 ? 0 : (double)CompletedCount / Nodes.Count * 100;
    public IEnumerable<PlanNode> PreviewNodes => Nodes.OrderBy(node => node.SortOrder).ThenBy(node => node.CreatedAt);
    public string PinnedGlyph => IsPinned ? "置顶" : string.Empty;
    public string PinMenuText => IsPinned ? "取消置顶" : "置顶计划";
}

public sealed class PlanNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PlanId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string NoteXaml { get; set; } = string.Empty;
    public string AttachmentPath { get; set; } = string.Empty;
    public DateTime? DueDate { get; set; }
    public int Priority { get; set; } = 1;
    public bool IsCompleted { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }

    public string CompletionGlyph => IsCompleted ? "✓" : string.Empty;
    public string ImportantGlyph => Priority == 2 ? "★" : string.Empty;
    public string DueDateText => DueDate.HasValue ? $"截止 {DueDate:MM月dd日}" : string.Empty;
    public string NoteSummary => string.IsNullOrWhiteSpace(Note)
        ? string.Empty
        : Note.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0) ?? string.Empty;
    public string AttachmentName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(AttachmentPath)) return string.Empty;
            var trimmed = AttachmentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(name) ? AttachmentPath : name;
        }
    }
}

public sealed class AttachmentItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TaskId { get; set; } = string.Empty;
    public string OriginalName { get; set; } = string.Empty;
    public string StoredPath { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; } = DateTime.Now;

    public override string ToString() => OriginalName;
}

public sealed class DailyPlanRow
{
    public string TaskId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Requirement { get; set; } = string.Empty;
    public string DueDateText { get; set; } = string.Empty;
    public bool CompletedOnDay { get; set; }
    public string Outcome { get; set; } = "planned";
    public string OutcomeText => CompletedOnDay ? "当天完成" : "当天未完成";
}

public sealed class DailySummary
{
    public DateTime Date { get; set; }
    public int Total { get; set; }
    public int Completed { get; set; }
    public string NoteSummary { get; set; } = string.Empty;
}

public sealed class CalendarDay
{
    public DateTime Date { get; set; }
    public bool IsCurrentMonth { get; set; }
    public bool IsToday => Date.Date == DateTime.Today;
    public int Total { get; set; }
    public int Completed { get; set; }
    public string DayText => Date.Day.ToString();
    public string SummaryText => Total == 0 ? string.Empty : $"{Completed}/{Total} 完成";
    public string NoteSummary { get; set; } = string.Empty;
}

public sealed class RecycleItem
{
    public string Id { get; set; } = string.Empty;
    public string ItemType { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ParentTitle { get; set; } = string.Empty;
    public DateTime DeletedAt { get; set; }

    public string TypeText => ItemType == "task" ? "任务" : ItemType == "plan" ? "计划" : "计划节点";
    public string DetailText => string.IsNullOrWhiteSpace(ParentTitle)
        ? $"删除于 {DeletedAt:yyyy-MM-dd HH:mm}"
        : $"所属计划：{ParentTitle}  ·  删除于 {DeletedAt:yyyy-MM-dd HH:mm}";
}
