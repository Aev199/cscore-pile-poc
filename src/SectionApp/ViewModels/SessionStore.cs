using System.Text.Json;
using SectionCalc;

namespace SectionApp.ViewModels;

/// <summary>What the application remembers between runs: the last section, the recent files, the last folder.</summary>
public sealed class SessionStore(string directory)
{
    public static SessionStore Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CScore", "SectionApp"));

    sealed class Settings
    {
        public List<string> RecentFiles { get; set; } = [];
        public string? LastFolder { get; set; }
    }

    public const int RecentCount = 8;
    string SettingsPath => Path.Combine(directory, "settings.json");
    string LastPath => Path.Combine(directory, "last.section.json");

    Settings Read()
    {
        try { return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new() : new(); }
        catch { return new(); }
    }

    void Write(Settings s)
    {
        try { Directory.CreateDirectory(directory); File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s)); }
        catch { /* remembering is a convenience: never fails the work */ }
    }

    public IReadOnlyList<string> RecentFiles => Read().RecentFiles.Where(File.Exists).ToList();
    public string? LastFolder => Read().LastFolder;

    /// <summary>A file opened or saved: first in the recent list; its folder becomes the last folder.</summary>
    public void Remember(string path)
    {
        var s = Read();
        s.RecentFiles.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
        s.RecentFiles.Insert(0, path);
        if (s.RecentFiles.Count > RecentCount) s.RecentFiles.RemoveRange(RecentCount, s.RecentFiles.Count - RecentCount);
        s.LastFolder = Path.GetDirectoryName(path);
        Write(s);
    }

    public void RememberFolder(string path)
    {
        var s = Read();
        s.LastFolder = Path.GetDirectoryName(path);
        Write(s);
    }

    /// <summary>The section on screen when the window closed.</summary>
    public SectionProject? LastSection()
    {
        try { return File.Exists(LastPath) ? ProjectStore.Deserialize(File.ReadAllText(LastPath)) : null; }
        catch { return null; }
    }

    public void SaveLastSection(SectionProject p)
    {
        try { Directory.CreateDirectory(directory); File.WriteAllText(LastPath, ProjectStore.Serialize(p)); }
        catch { }
    }
}
