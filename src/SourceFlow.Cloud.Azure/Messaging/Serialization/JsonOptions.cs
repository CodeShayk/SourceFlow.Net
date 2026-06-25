using System.Text.Json;

namespace SourceFlow.Cloud.Azure.Messaging.Serialization;

public static class JsonOptions
{
    public static JsonSerializerOptions Default { get; } = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}
