namespace SectionCalc.Profile;

/// <summary>Editable input, not cached verification. Zones are depths below the pile head.</summary>
public sealed class SavedPileType
{
    public List<string> Piles { get; set; } = [];
    public bool Locked { get; set; }
    public List<TypeZone> Zones { get; set; } = [];

    public static List<SavedPileType> Capture(TypingResult result) => result.Types.Select(t => new SavedPileType
    {
        Piles = t.Members.Select(m => m.Info.Name).ToList(), Locked = t.Locked,
        Zones = t.Zones.Select(z => z with { Top = t.Head - z.Top, Bottom = t.Head - z.Bottom, K = null, Governing = null }).ToList(),
    }).ToList();

    /// <summary>
    /// The saved partition into types. Checking (check=true) takes the saved cages as they are; design (check=false)
    /// keeps only the piles of each type and its lock: the cages follow the current demands, as after «Собрать».
    /// </summary>
    public static TypingResult Restore(PileDemands demands, IReadOnlyList<SavedPileType> saved, bool check)
    {
        var byName = demands.Piles.ToDictionary(p => p.Info.Name, p => p.Pile);
        var members = saved.SelectMany(t => t.Piles).ToList();
        if (members.Count != byName.Count || members.Distinct().Count() != members.Count || members.Any(n => !byName.ContainsKey(n)))
            throw new InvalidOperationException("Состав сохранённых типов не совпадает с выгрузкой: каждая свая должна входить ровно в один тип");
        if (saved.Any(t => t.Piles.Count == 0)) throw new InvalidOperationException("Пустой тип свай");
        if (!check)
        {
            var design = PileTyping.Build(demands,
                saved.Select(t => (IReadOnlyList<int>)t.Piles.Select(n => byName[n]).ToList()).ToList(), saved.Select(t => t.Locked).ToList());
            return design;
        }
        var types = new List<PileType>();
        foreach (var entry in saved)
        {
            // The same order as the design: the first member (its head) is the origin of the zone depths.
            var piles = PileTyping.Ordered(entry.Piles.Select(n => demands.Piles[byName[n]]));
            var first = piles[0];
            var zones = entry.Zones.Select(z => z with { Top = first.Info.Head - z.Top, Bottom = first.Info.Head - z.Bottom, K = null, Governing = null }).ToList();
            ValidateZones(zones, first.Info.Head, first.Info.Tip);
            types.Add(new PileType(types.Count + 1, piles, first.Stations.Select(s => s.Level).ToArray(), first.As, first.Asw, zones, 0, 0)
                { Locked = entry.Locked });
        }
        return new TypingResult(demands, types, "Заданное армирование; автоматическое изменение отключено");
    }

    public static void ValidateZones(IReadOnlyList<TypeZone> zones, double head, double tip)
    {
        double next = head;
        foreach (var z in zones)
        {
            if (!double.IsFinite(z.Top) || !double.IsFinite(z.Bottom) || Math.Abs(z.Top - next) > 1e-6
                || z.Bottom >= z.Top || z.Bottom < tip - 1e-6 || z.Bars.Count < 1
                || !double.IsFinite(z.Bars.DiameterMm) || z.Bars.DiameterMm <= 0
                || z.SpiralMm is double sd && (!double.IsFinite(sd) || sd <= 0)
                || z.PitchMm is double pitch && (!double.IsFinite(pitch) || pitch <= 0))
                throw new InvalidOperationException("Зоны каркаса должны покрывать сваю без разрывов и перекрытий; размеры и шаг — положительные");
            next = z.Bottom;
        }
        if (zones.Count == 0 || Math.Abs(next - tip) > 1e-6)
            throw new InvalidOperationException("Зоны каркаса должны покрывать всю длину самой длинной сваи типа");
    }
}
