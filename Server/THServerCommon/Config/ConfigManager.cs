namespace TH.Common.Config;

public sealed class ConfigManager : IConfigManager
{
    public string Env { get; }
    public string? Service { get; }
    public string Id { get; }
    public IniFile Profile { get; }
    public IniFile Config { get; }

    public ConfigManager(string configDirectory)
    {
        var profilePath = Path.Combine(configDirectory, "profile.ini");
        Profile = IniFile.Load(profilePath);

        Env     = Profile.GetRequired("Profile", "Env");
        Service = Profile.Get("Profile", "Service") is { Length: > 0 } s ? s : null;
        Id      = Profile.Get("Profile", "Id") ?? "1";

        var configPath = Path.Combine(configDirectory, $"config.{Env}.ini");
        Config = IniFile.Load(configPath);
    }

    public string? Get(string section, string key) => Config.Get(section, key);

    public string GetRequired(string section, string key) => Config.GetRequired(section, key);
}
