using Serilog;

namespace TH.Common.Config;

public sealed class ConfigManager : Singleton<ConfigManager>
{
    private bool _initialized;

    public string Env { get; private set; } = "";
    public string? Service { get; private set; }
    public string Id { get; private set; } = "1";
    public IniFile? Profile { get; private set; }
    public IniFile? Config { get; private set; }

    public bool Init(string configDirectory)
    {
        if (_initialized)
            return true;

        try
        {
            var profilePath = Path.Combine(configDirectory, "profile.ini");
            Profile = IniFile.Load(profilePath);

            Env     = Profile.GetRequired("Profile", "Env");
            Service = Profile.Get("Profile", "Service") is { Length: > 0 } s ? s : null;
            Id      = Profile.Get("Profile", "Id") ?? "1";

            var configPath = Path.Combine(configDirectory, $"config.{Env}.ini");
            Config = IniFile.Load(configPath);

            _initialized = true;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ConfigManager 초기화 실패 (configDirectory={Dir})", configDirectory);
            return false;
        }
    }

    public void Shutdown()
    {
        if (!_initialized)
            return;

        Profile = null;
        Config = null;
        _initialized = false;
    }

    public string? Get(string section, string key) => Config!.Get(section, key);

    public string GetRequired(string section, string key) => Config!.GetRequired(section, key);
}
