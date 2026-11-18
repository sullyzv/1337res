using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace Res1337;

public sealed record Hotkey(uint Modifiers, uint VirtualKey)
{
    public override string ToString()
    {
        List<string> parts = [];
        if ((Modifiers & 2) != 0) parts.Add("Ctrl");
        if ((Modifiers & 1) != 0) parts.Add("Alt");
        if ((Modifiers & 4) != 0) parts.Add("Shift");
        if ((Modifiers & 8) != 0) parts.Add("Win");
        string key = VirtualKey is >= 0x30 and <= 0x39
            ? ((char)VirtualKey).ToString()
            : KeyInterop.KeyFromVirtualKey((int)VirtualKey).ToString();
        parts.Add(key);
        return string.Join(" + ", parts);
    }
}

public sealed record ResolutionProfile(Guid Id, string Name, string DeviceName, DisplayMode Mode, Hotkey? Hotkey)
{
    public string? MonitorId { get; init; }
}

internal sealed record Settings(bool CloseToTray, List<ResolutionProfile> Profiles);

internal static class SettingsStore
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "1337res");
    private static readonly string FileName = Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 12 };

    public static Settings Load(out string? warning)
    {
        warning = null;
        if (File.Exists(FileName))
        {
            try
            {
                var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FileName), JsonOptions);
                if (settings?.Profiles is not null && settings.Profiles.All(profile =>
                        profile is not null && !string.IsNullOrWhiteSpace(profile.Name)
                        && !string.IsNullOrWhiteSpace(profile.DeviceName) && profile.Mode is not null)
                    && settings.Profiles.Select(profile => profile.Id).Distinct().Count() == settings.Profiles.Count)
                {
                    return settings;
                }
                throw new JsonException("Invalid profile data.");
            }
            catch (JsonException)
            {
                string backup = Path.Combine(Folder, $"settings.backup-{Guid.NewGuid():N}.json");
                File.Move(FileName, backup);
                warning = "The settings file could not be read. A copy was saved in the 1337res settings folder.";
            }
        }

        return new Settings(true, []);
    }

    public static void Save(Settings settings)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string temporary = Path.Combine(Folder, "settings.tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, FileName, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("1337res could not save its settings. Check folder permissions and try again.", error);
        }
    }
}
