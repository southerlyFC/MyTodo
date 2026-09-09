using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MyTodo;

public sealed class DataService
{
    private readonly string _databasePath;

    public string DataDirectory { get; }
    public string AttachmentDirectory { get; }
    public string BackupDirectory { get; }

    public DataService()
    {
        DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "MyTodoData");
        AttachmentDirectory = Path.Combine(DataDirectory, "Attachments-1.0");
        BackupDirectory = Path.Combine(DataDirectory, "Backups");

        // 使用新数据库让首个正式版本从空白开始；旧todo.db保留为可恢复的测试数据。
        _databasePath = Path.Combine(DataDirectory, "mytodo.db");

        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(BackupDirectory);
        InitializeDatabase();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(
            $"Data Source={_databasePath};Mode=ReadWriteCreate;Cache=Private;Pooling=False;Foreign Keys=True");
        connection.Open();
        return connection;
    }

    private void InitializeDatabase()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS tasks (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                original_requirement TEXT NOT NULL DEFAULT '',
                due_date TEXT NULL,
                priority INTEGER NOT NULL DEFAULT 1,
                website_url TEXT NOT NULL DEFAULT '',
                note_xaml TEXT NOT NULL DEFAULT '',
                is_pinned INTEGER NOT NULL DEFAULT 0,
                is_deleted INTEGER NOT NULL DEFAULT 0,
                deleted_at TEXT NULL,
                is_completed INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                completed_at TEXT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS daily_plan (
                id TEXT PRIMARY KEY,
                task_id TEXT NOT NULL,
                plan_date TEXT NOT NULL,
                sort_order INTEGER NOT NULL DEFAULT 0,
                added_at TEXT NOT NULL,
                title_snapshot TEXT NOT NULL,
                requirement_snapshot TEXT NOT NULL DEFAULT '',
                due_date_snapshot TEXT NULL,
                completed_on_day INTEGER NOT NULL DEFAULT 0,
                outcome TEXT NOT NULL DEFAULT 'planned',
                UNIQUE(task_id, plan_date),
                FOREIGN KEY(task_id) REFERENCES tasks(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS daily_notes (
                plan_date TEXT PRIMARY KEY,
                note TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS attachments (
                id TEXT PRIMARY KEY,
                task_id TEXT NOT NULL,
                original_name TEXT NOT NULL,
                stored_path TEXT NOT NULL,
                added_at TEXT NOT NULL,
                FOREIGN KEY(task_id) REFERENCES tasks(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS plans (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                goal TEXT NOT NULL DEFAULT '',
                due_date TEXT NULL,
                is_pinned INTEGER NOT NULL DEFAULT 0,
                is_deleted INTEGER NOT NULL DEFAULT 0,
                deleted_at TEXT NULL,
                is_completed INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS plan_nodes (
                id TEXT PRIMARY KEY,
                plan_id TEXT NOT NULL,
                title TEXT NOT NULL,
                note TEXT NOT NULL DEFAULT '',
                note_xaml TEXT NOT NULL DEFAULT '',
                attachment_path TEXT NOT NULL DEFAULT '',
                due_date TEXT NULL,
                priority INTEGER NOT NULL DEFAULT 1,
                is_deleted INTEGER NOT NULL DEFAULT 0,
                deleted_at TEXT NULL,
                is_completed INTEGER NOT NULL DEFAULT 0,
                sort_order INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                completed_at TEXT NULL,
                FOREIGN KEY(plan_id) REFERENCES plans(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_daily_plan_date ON daily_plan(plan_date);
            CREATE INDEX IF NOT EXISTS idx_attachments_task ON attachments(task_id);
            CREATE INDEX IF NOT EXISTS idx_plan_nodes_plan ON plan_nodes(plan_id, sort_order);
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "plan_nodes", "note", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "plan_nodes", "note_xaml", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "plan_nodes", "attachment_path", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "plan_nodes", "due_date", "TEXT NULL");
        EnsureColumn(connection, "plan_nodes", "priority", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(connection, "tasks", "note_xaml", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "tasks", "is_pinned", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "plans", "is_pinned", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "tasks", "is_deleted", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "tasks", "deleted_at", "TEXT NULL");
        EnsureColumn(connection, "plans", "is_deleted", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "plans", "deleted_at", "TEXT NULL");
        EnsureColumn(connection, "plan_nodes", "is_deleted", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "plan_nodes", "deleted_at", "TEXT NULL");
    }

    public List<TaskItem> GetAllTasks()
    {
        var result = new List<TaskItem>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, original_requirement, due_date, priority,
                   website_url, is_completed, created_at, completed_at, updated_at, note_xaml, is_pinned
            FROM tasks WHERE is_deleted = 0
            ORDER BY is_completed, is_pinned DESC, COALESCE(due_date, '9999-12-31'), priority DESC, created_at DESC;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new TaskItem
            {
                Id = reader.GetString(0),
                Title = reader.GetString(1),
                OriginalRequirement = reader.GetString(2),
                DueDate = ReadDateTime(reader, 3),
                Priority = reader.GetInt32(4),
                WebsiteUrl = reader.GetString(5),
                IsCompleted = reader.GetInt32(6) == 1,
                CreatedAt = ReadRequiredDateTime(reader, 7),
                CompletedAt = ReadDateTime(reader, 8),
                UpdatedAt = ReadRequiredDateTime(reader, 9),
                NoteXaml = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
                IsPinned = reader.GetInt32(11) == 1
            });
        }
        return result;
    }

    public List<TaskItem> GetAllActionItems()
    {
        var result = GetAllTasks();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.id, n.title, n.note, n.is_completed, n.created_at, n.completed_at,
                   p.id, p.title, COALESCE(n.due_date, p.due_date), n.priority, p.is_pinned
            FROM plan_nodes n
            INNER JOIN plans p ON p.id = n.plan_id
            WHERE n.is_deleted = 0 AND p.is_deleted = 0
            ORDER BY n.is_completed, p.due_date, n.sort_order;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new TaskItem
            {
                Id = reader.GetString(0),
                Title = reader.GetString(1),
                OriginalRequirement = reader.GetString(2),
                IsCompleted = reader.GetInt32(3) == 1,
                CreatedAt = ReadRequiredDateTime(reader, 4),
                CompletedAt = ReadDateTime(reader, 5),
                IsPlanNode = true,
                PlanId = reader.GetString(6),
                PlanTitle = reader.GetString(7),
                DueDate = ReadDateTime(reader, 8),
                Priority = reader.GetInt32(9),
                IsPinned = reader.GetInt32(10) == 1
            });
        }
        return result;
    }

    public void SaveTask(TaskItem task)
    {
        task.UpdatedAt = DateTime.Now;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tasks (
                id, title, original_requirement, due_date, priority,
                website_url, is_completed, created_at, completed_at, updated_at, note_xaml, is_pinned)
            VALUES (
                $id, $title, $requirement, $dueDate, $priority,
                $websiteUrl, $isCompleted, $createdAt, $completedAt, $updatedAt, $noteXaml, $isPinned)
            ON CONFLICT(id) DO UPDATE SET
                title = excluded.title,
                original_requirement = excluded.original_requirement,
                due_date = excluded.due_date,
                priority = excluded.priority,
                website_url = excluded.website_url,
                note_xaml = excluded.note_xaml,
                is_pinned = excluded.is_pinned,
                is_completed = excluded.is_completed,
                completed_at = excluded.completed_at,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", task.Id);
        command.Parameters.AddWithValue("$title", task.Title.Trim());
        command.Parameters.AddWithValue("$requirement", task.OriginalRequirement ?? string.Empty);
        command.Parameters.AddWithValue("$dueDate", ToDbValue(task.DueDate));
        command.Parameters.AddWithValue("$priority", task.Priority);
        command.Parameters.AddWithValue("$websiteUrl", task.WebsiteUrl ?? string.Empty);
        command.Parameters.AddWithValue("$noteXaml", task.NoteXaml ?? string.Empty);
        command.Parameters.AddWithValue("$isPinned", task.IsPinned ? 1 : 0);
        command.Parameters.AddWithValue("$isCompleted", task.IsCompleted ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", task.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$completedAt", ToDbValue(task.CompletedAt));
        command.Parameters.AddWithValue("$updatedAt", task.UpdatedAt.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void DeleteTask(string taskId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE tasks SET is_deleted = 1, deleted_at = $deletedAt WHERE id = $id;";
        command.Parameters.AddWithValue("$deletedAt", DateTime.Now.ToString("O"));
        command.Parameters.AddWithValue("$id", taskId);
        command.ExecuteNonQuery();
    }

    public void SetTaskPinned(string taskId, bool pinned)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE tasks SET is_pinned = $pinned, updated_at = $updatedAt WHERE id = $id;";
        command.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", DateTime.Now.ToString("O"));
        command.Parameters.AddWithValue("$id", taskId);
        command.ExecuteNonQuery();
    }

    public void SetCompletion(string taskId, bool isCompleted, DateTime planDate)
    {
        var now = DateTime.Now;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE tasks SET is_completed = $completed, completed_at = $completedAt,
                    updated_at = $updatedAt WHERE id = $taskId;
                """;
            command.Parameters.AddWithValue("$completed", isCompleted ? 1 : 0);
            command.Parameters.AddWithValue("$completedAt", isCompleted ? now.ToString("O") : (object)DBNull.Value);
            command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
            command.Parameters.AddWithValue("$taskId", taskId);
            command.ExecuteNonQuery();
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE daily_plan SET completed_on_day = $completed,
                    outcome = CASE WHEN $completed = 1 THEN 'completed' ELSE 'planned' END
                WHERE task_id = $taskId AND plan_date = $planDate;
                """;
            command.Parameters.AddWithValue("$completed", isCompleted ? 1 : 0);
            command.Parameters.AddWithValue("$taskId", taskId);
            command.Parameters.AddWithValue("$planDate", planDate.Date.ToString("yyyy-MM-dd"));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void AddToDay(TaskItem task, DateTime date)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText = """
                DELETE FROM daily_plan
                WHERE task_id = $taskId AND plan_date >= $today AND plan_date <> $planDate;
                """;
            cleanup.Parameters.AddWithValue("$taskId", task.Id);
            cleanup.Parameters.AddWithValue("$today", DateTime.Today.ToString("yyyy-MM-dd"));
            cleanup.Parameters.AddWithValue("$planDate", date.Date.ToString("yyyy-MM-dd"));
            cleanup.ExecuteNonQuery();
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO daily_plan (
                id, task_id, plan_date, sort_order, added_at, title_snapshot,
                requirement_snapshot, due_date_snapshot, completed_on_day, outcome)
            VALUES ($id, $taskId, $planDate,
                COALESCE((SELECT MAX(sort_order) + 1 FROM daily_plan WHERE plan_date = $planDate), 0),
                $addedAt, $title, $requirement, $dueDate, $completed, $outcome)
            ON CONFLICT(task_id, plan_date) DO UPDATE SET
                title_snapshot = excluded.title_snapshot,
                requirement_snapshot = excluded.requirement_snapshot,
                due_date_snapshot = excluded.due_date_snapshot,
                completed_on_day = excluded.completed_on_day,
                outcome = excluded.outcome;
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$taskId", task.Id);
        command.Parameters.AddWithValue("$planDate", date.Date.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$addedAt", DateTime.Now.ToString("O"));
        command.Parameters.AddWithValue("$title", task.Title);
        command.Parameters.AddWithValue("$requirement", task.OriginalRequirement ?? string.Empty);
        command.Parameters.AddWithValue("$dueDate", ToDbValue(task.DueDate));
        command.Parameters.AddWithValue("$completed", task.IsCompleted ? 1 : 0);
        command.Parameters.AddWithValue("$outcome", task.IsCompleted ? "completed" : "planned");
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void RemoveFromDay(string taskId, DateTime date)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM daily_plan WHERE task_id = $taskId AND plan_date = $planDate;";
        command.Parameters.AddWithValue("$taskId", taskId);
        command.Parameters.AddWithValue("$planDate", date.Date.ToString("yyyy-MM-dd"));
        command.ExecuteNonQuery();
    }

    public HashSet<string> GetTaskIdsForDay(DateTime date)
    {
        var ids = new HashSet<string>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.task_id FROM daily_plan d
            INNER JOIN tasks t ON t.id = d.task_id
            WHERE d.plan_date = $date AND t.is_deleted = 0
            ORDER BY d.sort_order;
            """;
        command.Parameters.AddWithValue("$date", date.Date.ToString("yyyy-MM-dd"));
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    public DateTime? GetActiveDayForTask(string taskId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MIN(plan_date) FROM daily_plan
            WHERE task_id = $taskId AND plan_date >= $today;
            """;
        command.Parameters.AddWithValue("$taskId", taskId);
        command.Parameters.AddWithValue("$today", DateTime.Today.ToString("yyyy-MM-dd"));
        var value = command.ExecuteScalar();
        if (value == null || value == DBNull.Value) return null;
        return DateTime.TryParse(Convert.ToString(value), out var date) ? date.Date : (DateTime?)null;
    }

    public List<DailyPlanRow> GetDailyPlan(DateTime date)
    {
        var result = new List<DailyPlanRow>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT task_id, title_snapshot, requirement_snapshot,
                   due_date_snapshot, completed_on_day, outcome
            FROM daily_plan d INNER JOIN tasks t ON t.id = d.task_id
            WHERE d.plan_date = $date AND t.is_deleted = 0
            ORDER BY d.sort_order, d.added_at;
            """;
        command.Parameters.AddWithValue("$date", date.Date.ToString("yyyy-MM-dd"));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var dueDate = ReadDateTime(reader, 3);
            result.Add(new DailyPlanRow
            {
                TaskId = reader.GetString(0),
                Title = reader.GetString(1),
                Requirement = reader.GetString(2),
                DueDateText = dueDate?.ToString("yyyy-MM-dd") ?? string.Empty,
                CompletedOnDay = reader.GetInt32(4) == 1,
                Outcome = reader.GetString(5)
            });
        }
        return result;
    }

    public Dictionary<DateTime, DailySummary> GetDailySummaries(DateTime firstDate, DateTime lastDate)
    {
        var result = new Dictionary<DateTime, DailySummary>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.plan_date, COUNT(*), SUM(d.completed_on_day), COALESCE(MAX(n.note), '')
            FROM daily_plan d
            INNER JOIN tasks t ON t.id = d.task_id AND t.is_deleted = 0
            LEFT JOIN daily_notes n ON n.plan_date = d.plan_date
            WHERE d.plan_date BETWEEN $first AND $last GROUP BY d.plan_date;
            """;
        command.Parameters.AddWithValue("$first", firstDate.Date.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$last", lastDate.Date.ToString("yyyy-MM-dd"));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!DateTime.TryParse(reader.GetString(0), out var parsedDate)) continue;
            var date = parsedDate.Date;
            result[date] = new DailySummary
            {
                Date = date,
                Total = reader.GetInt32(1),
                Completed = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                NoteSummary = reader.IsDBNull(3) ? string.Empty : reader.GetString(3)
            };
        }
        using var noteConnection = OpenConnection();
        using var noteCommand = noteConnection.CreateCommand();
        noteCommand.CommandText = "SELECT plan_date, note FROM daily_notes WHERE plan_date BETWEEN $first AND $last;";
        noteCommand.Parameters.AddWithValue("$first", firstDate.Date.ToString("yyyy-MM-dd"));
        noteCommand.Parameters.AddWithValue("$last", lastDate.Date.ToString("yyyy-MM-dd"));
        using var noteReader = noteCommand.ExecuteReader();
        while (noteReader.Read())
        {
            if (!DateTime.TryParse(noteReader.GetString(0), out var noteDate)) continue;
            if (!result.TryGetValue(noteDate.Date, out var summary))
            {
                summary = new DailySummary { Date = noteDate.Date };
                result[noteDate.Date] = summary;
            }
            summary.NoteSummary = noteReader.GetString(1);
        }
        return result;
    }

    public List<PlanItem> GetPlans()
    {
        var plans = new List<PlanItem>();
        using var connection = OpenConnection();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, title, goal, due_date, is_completed, created_at, updated_at, is_pinned
                FROM plans WHERE is_deleted = 0
                ORDER BY is_pinned DESC, created_at ASC, rowid ASC;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                plans.Add(new PlanItem
                {
                    Id = reader.GetString(0),
                    Title = reader.GetString(1),
                    Goal = reader.GetString(2),
                    DueDate = ReadDateTime(reader, 3),
                    IsCompleted = reader.GetInt32(4) == 1,
                    CreatedAt = ReadRequiredDateTime(reader, 5),
                    UpdatedAt = ReadRequiredDateTime(reader, 6),
                    IsPinned = reader.GetInt32(7) == 1
                });
            }
        }

        var byId = plans.ToDictionary(plan => plan.Id);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, plan_id, title, note, note_xaml, attachment_path, due_date, priority,
                       is_completed, sort_order, created_at, completed_at
                FROM plan_nodes WHERE is_deleted = 0
                ORDER BY plan_id, sort_order, created_at;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var node = new PlanNode
                {
                    Id = reader.GetString(0),
                    PlanId = reader.GetString(1),
                    Title = reader.GetString(2),
                    Note = reader.GetString(3),
                    NoteXaml = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    AttachmentPath = reader.GetString(5),
                    DueDate = ReadDateTime(reader, 6),
                    Priority = reader.GetInt32(7),
                    IsCompleted = reader.GetInt32(8) == 1,
                    SortOrder = reader.GetInt32(9),
                    CreatedAt = ReadRequiredDateTime(reader, 10),
                    CompletedAt = ReadDateTime(reader, 11)
                };
                if (byId.TryGetValue(node.PlanId, out var plan)) plan.Nodes.Add(node);
            }
        }
        return plans;
    }

    public void SavePlan(PlanItem plan)
    {
        plan.UpdatedAt = DateTime.Now;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO plans (id, title, goal, due_date, is_pinned, is_completed, created_at, updated_at)
            VALUES ($id, $title, $goal, $dueDate, $pinned, $completed, $createdAt, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET title = excluded.title, goal = excluded.goal,
                due_date = excluded.due_date, is_pinned = excluded.is_pinned,
                is_completed = excluded.is_completed,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", plan.Id);
        command.Parameters.AddWithValue("$title", plan.Title.Trim());
        command.Parameters.AddWithValue("$goal", plan.Goal ?? string.Empty);
        command.Parameters.AddWithValue("$dueDate", ToDbValue(plan.DueDate));
        command.Parameters.AddWithValue("$pinned", plan.IsPinned ? 1 : 0);
        command.Parameters.AddWithValue("$completed", plan.IsCompleted ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", plan.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", plan.UpdatedAt.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void DeletePlan(string planId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE plans SET is_deleted = 1, deleted_at = $deletedAt WHERE id = $id;";
        command.Parameters.AddWithValue("$deletedAt", DateTime.Now.ToString("O"));
        command.Parameters.AddWithValue("$id", planId);
        command.ExecuteNonQuery();
    }

    public void SetPlanPinned(string planId, bool pinned)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE plans SET is_pinned = $pinned, updated_at = $updatedAt WHERE id = $id;";
        command.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", DateTime.Now.ToString("O"));
        command.Parameters.AddWithValue("$id", planId);
        command.ExecuteNonQuery();
    }

    public PlanNode AddPlanNode(string planId, string title)
    {
        var node = new PlanNode { PlanId = planId, Title = title.Trim() };
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO plan_nodes (id, plan_id, title, is_completed, sort_order, created_at, completed_at)
            VALUES ($id, $planId, $title, 0,
                COALESCE((SELECT MAX(sort_order) + 1 FROM plan_nodes
                          WHERE plan_id = $planId AND is_deleted = 0), 0),
                $createdAt, NULL);
            """;
        command.Parameters.AddWithValue("$id", node.Id);
        command.Parameters.AddWithValue("$planId", planId);
        command.Parameters.AddWithValue("$title", node.Title);
        command.Parameters.AddWithValue("$createdAt", node.CreatedAt.ToString("O"));
        command.ExecuteNonQuery();
        return node;
    }

    public PlanNode InsertPlanNodeBefore(string planId, string beforeNodeId, string title)
    {
        var node = new PlanNode { PlanId = planId, Title = title.Trim() };
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        int? targetOrder = null;
        using (var findCommand = connection.CreateCommand())
        {
            findCommand.Transaction = transaction;
            findCommand.CommandText = """
                SELECT sort_order FROM plan_nodes
                WHERE id = $id AND plan_id = $planId AND is_deleted = 0;
                """;
            findCommand.Parameters.AddWithValue("$id", beforeNodeId);
            findCommand.Parameters.AddWithValue("$planId", planId);
            var value = findCommand.ExecuteScalar();
            if (value != null && value != DBNull.Value) targetOrder = Convert.ToInt32(value);
        }

        if (targetOrder.HasValue)
        {
            using var shiftCommand = connection.CreateCommand();
            shiftCommand.Transaction = transaction;
            shiftCommand.CommandText = """
                UPDATE plan_nodes SET sort_order = sort_order + 1
                WHERE plan_id = $planId AND is_deleted = 0 AND sort_order >= $targetOrder;
                """;
            shiftCommand.Parameters.AddWithValue("$planId", planId);
            shiftCommand.Parameters.AddWithValue("$targetOrder", targetOrder.Value);
            shiftCommand.ExecuteNonQuery();
        }
        else
        {
            using var orderCommand = connection.CreateCommand();
            orderCommand.Transaction = transaction;
            orderCommand.CommandText = """
                SELECT COALESCE(MAX(sort_order) + 1, 0) FROM plan_nodes
                WHERE plan_id = $planId AND is_deleted = 0;
                """;
            orderCommand.Parameters.AddWithValue("$planId", planId);
            targetOrder = Convert.ToInt32(orderCommand.ExecuteScalar());
        }

        node.SortOrder = targetOrder.Value;
        using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO plan_nodes (id, plan_id, title, is_completed, sort_order, created_at, completed_at)
                VALUES ($id, $planId, $title, 0, $sortOrder, $createdAt, NULL);
                """;
            insertCommand.Parameters.AddWithValue("$id", node.Id);
            insertCommand.Parameters.AddWithValue("$planId", planId);
            insertCommand.Parameters.AddWithValue("$title", node.Title);
            insertCommand.Parameters.AddWithValue("$sortOrder", node.SortOrder);
            insertCommand.Parameters.AddWithValue("$createdAt", node.CreatedAt.ToString("O"));
            insertCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        return node;
    }

    public void ReorderPlanNodes(string planId, IReadOnlyList<string> orderedNodeIds)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE plan_nodes SET sort_order = $sortOrder
            WHERE id = $id AND plan_id = $planId AND is_deleted = 0;
            """;
        var orderParameter = command.Parameters.Add("$sortOrder", Microsoft.Data.Sqlite.SqliteType.Integer);
        var idParameter = command.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
        command.Parameters.AddWithValue("$planId", planId);
        for (var index = 0; index < orderedNodeIds.Count; index++)
        {
            orderParameter.Value = index;
            idParameter.Value = orderedNodeIds[index];
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void SetPlanNodeCompletion(string nodeId, bool completed)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE plan_nodes SET is_completed = $completed, completed_at = $completedAt WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$completed", completed ? 1 : 0);
        command.Parameters.AddWithValue("$completedAt", completed ? DateTime.Now.ToString("O") : (object)DBNull.Value);
        command.Parameters.AddWithValue("$id", nodeId);
        command.ExecuteNonQuery();
    }

    public void UpdatePlanNodeDetails(
        string nodeId,
        string title,
        string note,
        string noteXaml,
        DateTime? dueDate,
        int priority)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE plan_nodes
            SET title = $title, note = $note, note_xaml = $noteXaml,
                attachment_path = '', due_date = $dueDate, priority = $priority
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$title", title ?? string.Empty);
        command.Parameters.AddWithValue("$note", note ?? string.Empty);
        command.Parameters.AddWithValue("$noteXaml", noteXaml ?? string.Empty);
        command.Parameters.AddWithValue("$dueDate", ToDbValue(dueDate));
        command.Parameters.AddWithValue("$priority", priority);
        command.Parameters.AddWithValue("$id", nodeId);
        command.ExecuteNonQuery();
    }

    public string GetDailyNote(DateTime date)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT note FROM daily_notes WHERE plan_date = $date;";
        command.Parameters.AddWithValue("$date", date.Date.ToString("yyyy-MM-dd"));
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    public void SaveDailyNote(DateTime date, string note)
    {
        var value = note ?? string.Empty;
        if (value.Length > 200) value = value.Substring(0, 200);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO daily_notes (plan_date, note, updated_at)
            VALUES ($date, $note, $updatedAt)
            ON CONFLICT(plan_date) DO UPDATE SET note = excluded.note, updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$date", date.Date.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$note", value);
        command.Parameters.AddWithValue("$updatedAt", DateTime.Now.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void DeletePlanNode(string nodeId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE plan_nodes SET is_deleted = 1, deleted_at = $deletedAt WHERE id = $id;";
        command.Parameters.AddWithValue("$deletedAt", DateTime.Now.ToString("O"));
        command.Parameters.AddWithValue("$id", nodeId);
        command.ExecuteNonQuery();
    }

    public List<RecycleItem> GetRecycleItems()
    {
        var result = new List<RecycleItem>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 'task', id, title, '', deleted_at
            FROM tasks WHERE is_deleted = 1
            UNION ALL
            SELECT 'plan', id, title, '', deleted_at
            FROM plans WHERE is_deleted = 1
            UNION ALL
            SELECT 'node', n.id, n.title, p.title, n.deleted_at
            FROM plan_nodes n
            INNER JOIN plans p ON p.id = n.plan_id
            WHERE n.is_deleted = 1 AND p.is_deleted = 0
            ORDER BY deleted_at DESC;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new RecycleItem
            {
                ItemType = reader.GetString(0),
                Id = reader.GetString(1),
                Title = reader.GetString(2),
                ParentTitle = reader.GetString(3),
                DeletedAt = ReadRequiredDateTime(reader, 4)
            });
        }
        return result;
    }

    public void RestoreRecycleItem(string itemType, string id)
    {
        var table = itemType == "task" ? "tasks" : itemType == "plan" ? "plans" : "plan_nodes";
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = itemType == "node"
            ? """
                UPDATE plan_nodes
                SET is_deleted = 0,
                    deleted_at = NULL,
                    sort_order = COALESCE((
                        SELECT MAX(active.sort_order) + 1
                        FROM plan_nodes active
                        WHERE active.plan_id = plan_nodes.plan_id AND active.is_deleted = 0
                    ), 0)
                WHERE id = $id;
                """
            : $"UPDATE {table} SET is_deleted = 0, deleted_at = NULL WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void PermanentlyDeleteRecycleItem(string itemType, string id)
    {
        using (var connection = OpenConnection())
        using (var transaction = connection.BeginTransaction())
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            if (itemType == "task")
            {
                command.CommandText = """
                    DELETE FROM attachments WHERE task_id = $id;
                    DELETE FROM daily_plan WHERE task_id = $id;
                    DELETE FROM tasks WHERE id = $id AND is_deleted = 1;
                    """;
            }
            else if (itemType == "plan")
            {
                command.CommandText = """
                    DELETE FROM plan_nodes WHERE plan_id = $id;
                    DELETE FROM plans WHERE id = $id AND is_deleted = 1;
                    """;
            }
            else
            {
                command.CommandText = "DELETE FROM plan_nodes WHERE id = $id AND is_deleted = 1;";
            }
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
            transaction.Commit();
        }
        CompactDatabase();
    }

    public void EmptyRecycleBin()
    {
        using (var connection = OpenConnection())
        using (var transaction = connection.BeginTransaction())
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM attachments WHERE task_id IN (SELECT id FROM tasks WHERE is_deleted = 1);
                DELETE FROM daily_plan WHERE task_id IN (SELECT id FROM tasks WHERE is_deleted = 1);
                DELETE FROM tasks WHERE is_deleted = 1;
                DELETE FROM plan_nodes WHERE is_deleted = 1
                    OR plan_id IN (SELECT id FROM plans WHERE is_deleted = 1);
                DELETE FROM plans WHERE is_deleted = 1;
                """;
            command.ExecuteNonQuery();
            transaction.Commit();
        }
        CompactDatabase();
    }

    public List<AttachmentItem> GetAttachments(string taskId)
    {
        var result = new List<AttachmentItem>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, task_id, original_name, stored_path, added_at
            FROM attachments WHERE task_id = $taskId ORDER BY added_at;
            """;
        command.Parameters.AddWithValue("$taskId", taskId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new AttachmentItem
            {
                Id = reader.GetString(0),
                TaskId = reader.GetString(1),
                OriginalName = reader.GetString(2),
                StoredPath = reader.GetString(3),
                AddedAt = ReadRequiredDateTime(reader, 4)
            });
        }
        return result;
    }

    public AttachmentItem AddAttachment(string taskId, string sourcePath)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        var displayName = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var attachment = new AttachmentItem
        {
            TaskId = taskId,
            OriginalName = string.IsNullOrWhiteSpace(displayName) ? fullPath : displayName,
            StoredPath = fullPath
        };

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO attachments (id, task_id, original_name, stored_path, added_at)
            VALUES ($id, $taskId, $name, $path, $addedAt);
            """;
        command.Parameters.AddWithValue("$id", attachment.Id);
        command.Parameters.AddWithValue("$taskId", attachment.TaskId);
        command.Parameters.AddWithValue("$name", attachment.OriginalName);
        command.Parameters.AddWithValue("$path", attachment.StoredPath);
        command.Parameters.AddWithValue("$addedAt", attachment.AddedAt.ToString("O"));
        command.ExecuteNonQuery();
        return attachment;
    }

    public void RemoveAttachment(AttachmentItem attachment)
    {
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM attachments WHERE id = $id;";
            command.Parameters.AddWithValue("$id", attachment.Id);
            command.ExecuteNonQuery();
        }
        CompactDatabase();
    }

    private void CompactDatabase()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA optimize; VACUUM;";
        command.ExecuteNonQuery();
    }

    public string CreateBackup(string? customDirectory = null)
    {
        var directory = string.IsNullOrWhiteSpace(customDirectory) ? BackupDirectory : customDirectory;
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"mytodo_{DateTime.Now:yyyyMMdd_HHmmss_fff}.db");
        File.Copy(_databasePath, destination, false);
        return destination;
    }

    private static object ToDbValue(DateTime? value) =>
        value.HasValue ? (object)value.Value.ToString("O") : DBNull.Value;

    private static DateTime? ReadDateTime(SqliteDataReader reader, int index)
    {
        if (reader.IsDBNull(index)) return null;
        return DateTime.TryParse(reader.GetString(index), out var value) ? value : (DateTime?)null;
    }

    private static DateTime ReadRequiredDateTime(SqliteDataReader reader, int index)
    {
        if (reader.IsDBNull(index)) return DateTime.Now;
        return DateTime.TryParse(reader.GetString(index), out var value) ? value : DateTime.Now;
    }

    private static void EnsureColumn(SqliteConnection connection, string tableName, string columnName, string declaration)
    {
        using (var check = connection.CreateCommand())
        {
            check.CommandText = $"PRAGMA table_info({tableName});";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase)) return;
            }
        }
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {declaration};";
        alter.ExecuteNonQuery();
    }
}
