using System.Text.Json;
using System.Text.Json.Serialization;

namespace VpnUs.Core.Storage;

public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static T Load<T>(string path, Func<T> factory)
    {
        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var value = JsonSerializer.Deserialize<T>(text, Options);
                    if (value is not null)
                    {
                        return value;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return factory();
    }

    public static void Save<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(value, Options);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);

        if (File.Exists(path))
        {
            File.Replace(tmp, path, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? FromJson<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
