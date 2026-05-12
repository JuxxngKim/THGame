namespace TH.Common.Config;

public interface IConfigManager
{
    string Env { get; }
    string? Service { get; }
    string Id { get; }
    IniFile Profile { get; }
    IniFile Config { get; }
    string? Get(string section, string key);
    string GetRequired(string section, string key);
}
