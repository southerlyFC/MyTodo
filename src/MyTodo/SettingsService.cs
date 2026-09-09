using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Windows.Media.Imaging;

namespace MyTodo;

public sealed class AppSettings
{
    public string ThemeColor { get; set; } = "#2563EB";
    public string ThemeStyle { get; set; } = "mist";
    public double MaskOpacity { get; set; } = 0.58;
    public string BackgroundImagePath { get; set; } = string.Empty;
    public double NavigationWidth { get; set; } = 212;
    public double TaskRowHeight { get; set; } = 58;
    public double DetailPanelWidth { get; set; } = 410;
    public double FontSize { get; set; } = 14;
    public string FontFamilyName { get; set; } = "system";
    public string BackupDirectory { get; set; } = string.Empty;
}

public sealed class SettingsService
{
    private readonly string _settingsPath;
    private readonly string _backgroundDirectory;

    public SettingsService(string dataDirectory)
    {
        _settingsPath = Path.Combine(dataDirectory, "settings.json");
        _backgroundDirectory = Path.Combine(dataDirectory, "Backgrounds");
        Directory.CreateDirectory(_backgroundDirectory);
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new AppSettings();
            }

            var serializer = new DataContractJsonSerializer(typeof(AppSettings));
            using var stream = File.OpenRead(_settingsPath);
            return serializer.ReadObject(stream) as AppSettings ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var temporaryPath = _settingsPath + ".tmp";
        var serializer = new DataContractJsonSerializer(typeof(AppSettings));
        using (var stream = File.Create(temporaryPath))
        {
            serializer.WriteObject(stream, settings);
        }
        if (File.Exists(_settingsPath))
        {
            File.Delete(_settingsPath);
        }
        File.Move(temporaryPath, _settingsPath);
    }

    public string ImportBackground(string sourcePath)
    {
        return CreateOptimizedBackground(sourcePath);
    }

    public string EnsureOptimizedBackground(string sourcePath)
    {
        if (string.Equals(
                Path.GetFileName(sourcePath),
                "background-optimized.jpg",
                StringComparison.OrdinalIgnoreCase))
        {
            return sourcePath;
        }
        return CreateOptimizedBackground(sourcePath);
    }

    private string CreateOptimizedBackground(string sourcePath)
    {
        var targetPath = Path.Combine(_backgroundDirectory, "background-optimized.jpg");
        var temporaryPath = targetPath + ".tmp";

        using (var input = File.OpenRead(sourcePath))
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 1600;
            image.StreamSource = input;
            image.EndInit();
            image.Freeze();

            var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = File.Create(temporaryPath);
            encoder.Save(output);
        }

        if (File.Exists(targetPath)) File.Delete(targetPath);
        File.Move(temporaryPath, targetPath);
        foreach (var existingFile in Directory.EnumerateFiles(_backgroundDirectory, "background.*"))
        {
            if (!string.Equals(existingFile, targetPath, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(existingFile, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(existingFile);
            }
        }
        if (!string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase) &&
            sourcePath.StartsWith(_backgroundDirectory, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(sourcePath);
        }
        return targetPath;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // 背景文件被查看器占用时保留，不影响当前设置。
        }
    }
}
