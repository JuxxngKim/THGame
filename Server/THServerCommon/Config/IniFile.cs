namespace TH.Common.Config;

public sealed class IniFile
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections;
    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? _cachedSections;

    public string FilePath { get; }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Sections =>
        _cachedSections ??= _sections.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyDictionary<string, string>)kv.Value,
            StringComparer.OrdinalIgnoreCase);

    private IniFile(string filePath, Dictionary<string, Dictionary<string, string>> sections)
    {
        FilePath = filePath;
        _sections = sections;
    }

    public static IniFile Load(string path)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;

        using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        string? raw;
        while ((raw = reader.ReadLine()) != null)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                var name = line[1..^1].Trim();
                if (!sections.TryGetValue(name, out current))
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    sections[name] = current;
                }
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0 || current is null)
                continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            current[key] = value;
        }

        return new IniFile(path, sections);
    }

    public string? Get(string section, string key)
    {
        if (_sections.TryGetValue(section, out var kv) && kv.TryGetValue(key, out var val))
            return val;
        return null;
    }

    public string GetRequired(string section, string key)
    {
        var val = Get(section, key);
        if (val is null)
            throw new InvalidDataException($"필수 설정 누락: [{section}] {key} (파일: {FilePath})");
        return val;
    }

    public bool TryGetSection(string section, out IReadOnlyDictionary<string, string> values)
    {
        if (_sections.TryGetValue(section, out var kv))
        {
            values = kv;
            return true;
        }
        values = new Dictionary<string, string>();
        return false;
    }
}
