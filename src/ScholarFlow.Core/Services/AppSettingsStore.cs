using System.Text.Json;
using ScholarFlow.Core.Models;

namespace ScholarFlow.Core.Services;

public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string GetDefaultSettingsPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ScholarFlow",
            "config.json"
        );

    public static async Task<AppSettings> LoadAsync(
        string? path = null,
        CancellationToken ct = default
    )
    {
        var target = path ?? GetDefaultSettingsPath();
        if (!File.Exists(target))
            return new AppSettings();

        await using var stream = File.OpenRead(target);
        return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, ct)
            ?? new AppSettings();
    }

    public static async Task SaveAsync(
        AppSettings settings,
        string? path = null,
        CancellationToken ct = default
    )
    {
        var target = path ?? GetDefaultSettingsPath();
        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var temp = $"{target}.tmp.{Guid.NewGuid():N}";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, ct);
        }

        File.Move(temp, target, overwrite: true);
    }
}
