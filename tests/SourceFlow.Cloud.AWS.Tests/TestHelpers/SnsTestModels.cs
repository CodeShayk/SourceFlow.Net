using System.Text.Json.Serialization;

namespace SourceFlow.Cloud.AWS.Tests.TestHelpers;

/// <summary>
/// Wrapper for SNS messages received via SQS
/// </summary>
public class SnsMessageWrapper
{
    [JsonPropertyName("Message")]
    public string? Message { get; set; }
    
    [JsonPropertyName("MessageAttributes")]
    public Dictionary<string, SnsMessageAttribute>? MessageAttributes { get; set; }
}

/// <summary>
/// SNS message attribute structure
/// </summary>
public class SnsMessageAttribute
{
    [JsonPropertyName("Type")]
    public string? Type { get; set; }
    
    [JsonPropertyName("Value")]
    public string? Value { get; set; }
}
