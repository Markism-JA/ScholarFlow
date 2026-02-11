using System;
using System.IO;
using System.Text.Json;
using ScholarFlow.Models.Entities;
using ScholarFlow.Models.Settings;

namespace ScholarFlow.Services.Configuration
{
    public class ConfigManager
    {
        private static readonly JsonSerializerOptions _options = new() { WriteIndented = true };
        private static string ConfigPath =>
#if DEBUG
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
#else
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ScholarFlow",
                "config.json"
            );
#endif

        public static void Save(AppConfig config)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);

            string json = JsonSerializer.Serialize(config, _options);

            File.WriteAllText(ConfigPath, json);
        }

        public static AppConfig Load()
        {
            if (!File.Exists(ConfigPath))
                return new AppConfig();

            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
    }
}
