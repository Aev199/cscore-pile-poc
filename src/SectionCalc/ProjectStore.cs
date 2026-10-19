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
        if (p.Version != 1) throw new InvalidDataException("Неподдерживаемая версия файла проекта");
        return p;
    }

    public static void Save(SectionProject p, string path) => File.WriteAllText(path, Serialize(p));

    public static SectionProject Load(string path) => Deserialize(File.ReadAllText(path));
}
