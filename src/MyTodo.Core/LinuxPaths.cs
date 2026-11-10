namespace MyTodo;

public static class LinuxPaths
{
    public static string DataDirectory
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var root = !string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured)
                ? configured
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(root, "mytodo");
        }
    }
}
