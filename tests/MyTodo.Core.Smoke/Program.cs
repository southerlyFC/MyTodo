using MyTodo;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

// Never point tests at a user's database.
var directory = Path.Combine(Path.GetTempPath(), "mytodo-core-test-" + Guid.NewGuid().ToString("N"));
try
{
    var data = new DataService(directory);
    var date = DateTime.Today.AddDays(1);
    var task = new TaskItem { Title = "跨平台任务", OriginalRequirement = "文档 https://example.org", DueDate = date };
    data.SaveTask(task);
    data.AddToDay(task, date);
    Check(data.GetTaskIdsForDay(date).Contains(task.Id), "Task missing from tomorrow");
    data.SetCompletion(task.Id, true, date);
    Check(data.GetDailyPlan(date).Single().CompletedOnDay, "Completion history missing");
    data.SetTaskPinned(task.Id, true);
    Check(data.GetAllTasks().Single().IsPinned, "Pin did not persist");
    var plan = new PlanItem { Title = "计划", Goal = "目标", DueDate = date };
    data.SavePlan(plan);
    var first = data.AddPlanNode(plan.Id, "节点一");
    var second = data.InsertPlanNodeBefore(plan.Id, first.Id, "插入节点");
    data.UpdatePlanNodeDetails(second.Id, "修改后的节点", "说明及 https://example.org", "", date, 2);
    data.ReorderPlanNodes(plan.Id, new[] { first.Id, second.Id });
    var nodes = data.GetPlans().Single().PreviewNodes.ToList();
    Check(nodes[0].Id == first.Id && nodes[1].Id == second.Id, "Node order did not persist");
    Check(nodes[1].Note.Contains("https://"), "Node properties lost");
    data.SetPlanNodeCompletion(first.Id, true);
    Check(data.GetPlans().Single().CompletedCount == 1, "Node completion lost");
    data.SaveDailyNote(date, "每日记录");
    Check(data.GetDailyNote(date) == "每日记录", "Daily note lost");
    data.DeleteTask(task.Id);
    Check(!data.GetAllTasks().Any(), "Deleted task still visible");
    Check(data.GetRecycleItems().Any(x => x.Id == task.Id), "Recycle item missing");
    data.RestoreRecycleItem("task", task.Id);
    Check(data.GetAllTasks().Any(x => x.Id == task.Id), "Restore failed");
    data.DeletePlan(plan.Id);
    data.RestoreRecycleItem("plan", plan.Id);
    Check(data.GetPlans().Single().Nodes.Count == 2, "Restored plan lost its nodes");
    var backup = data.CreateBackup();
    var importedDirectory = Path.Combine(directory, "imported");
    Directory.CreateDirectory(importedDirectory);
    File.Copy(backup, Path.Combine(importedDirectory, "mytodo.db"));
    var imported = new DataService(importedDirectory);
    Check(imported.GetAllTasks().Single().Title == task.Title, "Backup round trip lost task");
    Check(imported.GetPlans().Single().Nodes.Count == 2, "Backup round trip lost nodes");
    DatabaseTransfer.Import(backup, importedDirectory);
    Check(new DataService(importedDirectory).GetAllTasks().Single().Title == task.Title, "Validated import failed");
    var invalid = Path.Combine(directory, "invalid.db");
    File.WriteAllText(invalid, "not a sqlite database");
    try { DatabaseTransfer.Import(invalid, importedDirectory); throw new InvalidOperationException("Invalid import accepted"); }
    catch (Microsoft.Data.Sqlite.SqliteException) { }
    Check(new DataService(importedDirectory).GetPlans().Single().Nodes.Count == 2, "Failed import damaged data");
    data.DeleteTask(task.Id);
    data.PermanentlyDeleteRecycleItem("task", task.Id);
    Check(!data.GetRecycleItems().Any(x => x.Id == task.Id), "Permanent deletion failed");
    Check(!data.GetDailyPlan(date).Any(), "Permanent deletion left history");
    Console.WriteLine("PASS: tasks, tomorrow, history, pin, plans, node properties, insertion, ordering, recycle and backup round trip.");
}
finally
{
    Directory.Delete(directory, recursive: true);
}
