using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace SectionCalc;

/// <summary>Project files (*.section.json). Results are never stored: they are recalculated.</summary>
public static class ProjectStore
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public static string Serialize(SectionProject p) => JsonSerializer.Serialize(p, Options);

    public static SectionProject Deserialize(string json)
    {
        var p = JsonSerializer.Deserialize<SectionProject>(json, Options) ?? throw new InvalidDataException("Пустой файл проекта");
        if (p.Version != SectionProject.CurrentVersion)
            throw new InvalidDataException(p.Version < SectionProject.CurrentVersion
                ? $"Файл проекта версии {p.Version} создан предыдущей версией программы: усилия задавались в другом виде (ULS/SLS). Введите сечение заново."
                : "Файл проекта создан более новой версией программы");
        return p;
    }

    public static void Save(SectionProject p, string path) => File.WriteAllText(path, Serialize(p));

    public static SectionProject Load(string path) => Deserialize(File.ReadAllText(path));
}
