using System.Text.Json;
using System.Text.Json.Serialization;

namespace SourceFlow.Cloud.AWS.Messaging.Serialization;

public static class JsonMessageSerializer
{
    public static JsonSerializerOptions CreateDefaultOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters =
            {
                new JsonStringEnumConverter(),
                // Add custom converters as needed
            }
        };
    }

    public static string Serialize<T>(T value, JsonSerializerOptions options = null)
    {
        options ??= CreateDefaultOptions();
        return JsonSerializer.Serialize(value, options);
    }

    public static T Deserialize<T>(string json, JsonSerializerOptions options = null)
    {
        options ??= CreateDefaultOptions();
        return JsonSerializer.Deserialize<T>(json, options);
    }
}