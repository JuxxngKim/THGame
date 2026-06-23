using Serilog;

namespace TH.Common.Config;

public sealed class ConfigManager : Singleton<ConfigManager>
{
    private bool _initialized;

    private ConfigManager() { }

    public string Env { get; private set; } = "";
    public string? Service { get; private set; }
    public string ID { get; private set; } = "1";
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
            ID      = Profile.Get("Profile", "ID") ?? "1";

            var configPath = Path.Combine(configDirectory, $"config.{Env}.ini");
            Config = IniFile.Load(configPath);

            _initialized = true;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ConfigManager init failed (configDirectory={Dir})", configDirectory);
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

    public string? Get(string section, string key)
    {
        if (!_initialized || Config is null)
            throw new InvalidOperationException("ConfigManager가 초기화되지 않았습니다. Init()을 먼저 호출하세요.");
        return Config.Get(section, key);
    }

    public string GetRequired(string section, string key)
    {
        if (!_initialized || Config is null)
            throw new InvalidOperationException("ConfigManager가 초기화되지 않았습니다. Init()을 먼저 호출하세요.");
        return Config.GetRequired(section, key);
    }
}
