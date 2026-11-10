using System.Text.Json;
using Avalonia.Media;

namespace MyTodo.Linux;

public sealed class Preferences
{
    public string ThemeStyle { get; set; } = "mist";
    public string ThemeColor { get; set; } = "#2563EB";
    public string BackgroundImagePath { get; set; } = "";
    public double MaskOpacity { get; set; } = 0.58;
    public double FontSize { get; set; } = 14;
    public string FontFamilyName { get; set; } = "sans-serif";
    public string BackupDirectory { get; set; } = "";
    public double NavigationWidth { get; set; } = 212;
    public double DetailPanelWidth { get; set; } = 410;
    public double TaskRowHeight { get; set; } = 58;

    public static Preferences Load(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "linux-settings.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
    }

    public void Save(string directory)
    {
        var path = Path.Combine(directory, "linux-settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this));
        File.Move(path + ".tmp", path, true);
    }
    public IBrush Accent => new SolidColorBrush(Color.TryParse(ThemeColor, out var color) ? color : Colors.RoyalBlue);
}
