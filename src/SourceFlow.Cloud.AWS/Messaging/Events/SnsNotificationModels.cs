namespace SourceFlow.Cloud.AWS.Messaging.Events;

internal sealed class SnsNotification
{
    public string Type { get; set; } = string.Empty;
    public string MessageId { get; set; } = string.Empty;
    public string TopicArn { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public Dictionary<string, SnsMessageAttribute> MessageAttributes { get; set; } = new();
}

internal sealed class SnsMessageAttribute
{
    public string Type { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
