using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Q4Sender;

public sealed class AppConfig
{
    public QrSettings QrSettings { get; set; } = new();
    public int? TimerInterval { get; set; }

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return CreateDefault();
        }

        using var reader = File.OpenText(path);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var config = deserializer.Deserialize<AppConfig?>(reader) ?? CreateDefault();

        config.QrSettings ??= new QrSettings();
        config.QrSettings.ErrorCorrectionLevel = string.IsNullOrWhiteSpace(config.QrSettings.ErrorCorrectionLevel)
            ? "Q"
            : config.QrSettings.ErrorCorrectionLevel.Trim();

        if (config.QrSettings.Version is int version && (version < 1 || version > 40))
        {
            config.QrSettings.Version = null;
        }

        if (config.TimerInterval is int timerInterval && timerInterval < 1)
        {
            config.TimerInterval = null;
        }

        return config;
    }

    public static AppConfig CreateDefault() => new()
    {
        QrSettings = new QrSettings
        {
            ErrorCorrectionLevel = "Q",
            Version = null
        },
        TimerInterval = null
    };
}

public sealed class QrSettings
{
    public string? ErrorCorrectionLevel { get; set; }
    public int? Version { get; set; }
}
