using System.IO;

namespace QuickClip.Services;

/// <summary>应用数据目录与路径管理。</summary>
public sealed class AppPaths
{
    public string BaseDir { get; }
    public string PreviewDir { get; }
    public string UpdatesDir { get; }
    public string OcrDir { get; }
    public string DatabasePath { get; }
    public string SettingsPath { get; }

    public AppPaths()
    {
        BaseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuickClip");
        PreviewDir = Path.Combine(BaseDir, "previews");
        UpdatesDir = Path.Combine(BaseDir, "updates");
        OcrDir = Path.Combine(BaseDir, "ocr");
        DatabasePath = Path.Combine(BaseDir, "quickclip.db");
        SettingsPath = Path.Combine(BaseDir, "settings.json");
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(BaseDir);
        Directory.CreateDirectory(PreviewDir);
        Directory.CreateDirectory(UpdatesDir);
        Directory.CreateDirectory(OcrDir);
    }
}


