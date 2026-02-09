using System;
using System.IO;
using System.Text.Json;

namespace ScholarFlow.Models.Entities
{
    public class ConfigManager
    {
        private static String ConfigPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ScholarFlow",
                "config.json"
            );

        public static void Save(AppConfig config)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);

            var json = JsonSerializer.Serialize(
                config,
                new JsonSerializerOptions { WriteIndented = true }
            );
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
