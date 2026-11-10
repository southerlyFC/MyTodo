using Microsoft.Data.Sqlite;

namespace MyTodo;

public static class DatabaseTransfer
{
    public static void Import(string source, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        var target = Path.Combine(destinationDirectory, "mytodo.db");
        if (Path.GetFullPath(source) == Path.GetFullPath(target)) throw new IOException("不能导入当前正在使用的数据库。");
        var staging = Path.Combine(destinationDirectory, "import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using (var input = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                input.Open();
                using var check = input.CreateCommand();
                check.CommandText = "PRAGMA integrity_check;";
                if (!string.Equals(Convert.ToString(check.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase)) throw new IOException("数据库完整性检查失败。");
                check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('tasks','plans','plan_nodes','daily_plan');";
                if (Convert.ToInt32(check.ExecuteScalar()) != 4) throw new IOException("文件不是 MyTodo 数据库。");
                using var output = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(staging, "mytodo.db"), Pooling = false }.ToString());
                output.Open(); input.BackupDatabase(output);
            }
            var imported = new DataService(staging);
            _ = imported.GetAllTasks(); _ = imported.GetPlans(); _ = imported.GetRecycleItems();
            if (File.Exists(target)) new DataService(destinationDirectory).CreateBackup();
            File.Move(Path.Combine(staging, "mytodo.db"), target, true);
        }
        finally { Directory.Delete(staging, true); }
    }
}
