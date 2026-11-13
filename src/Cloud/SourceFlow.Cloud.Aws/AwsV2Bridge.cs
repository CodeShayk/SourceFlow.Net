using System.Globalization;
using System.Text.Json;
using SourceFlow.Migration;

namespace SourceFlow.Cloud.Aws;

/// <summary>
/// Reads the v2 SourceFlow.Cloud.AWS wire format (doc 07 §8, doc 12 §4). v2 commands went to SQS with the String
/// attributes <c>CommandType</c> (assembly-qualified), <c>EntityId</c> and <c>SequenceNo</c> and a camelCase
/// <c>Command&lt;TPayload&gt;</c> body; v2 events went to SNS with <c>EventType</c> and <c>EventName</c>, and reach a
/// queue either as raw deliveries (attributes on the SQS message) or wrapped in an SNS notification.
/// </summary>
public static class AwsV2Bridge
{
    /// <summary>The v2 command type attribute.</summary>
    public const string CommandTypeAttribute = "CommandType";

    /// <summary>The v2 entity id attribute.</summary>
    public const string EntityIdAttribute = "EntityId";

    /// <summary>The v2 sequence number attribute.</summary>
    public const string SequenceNoAttribute = "SequenceNo";

    /// <summary>The v2 event type attribute.</summary>
    public const string EventTypeAttribute = "EventType";

    /// <summary>The v2 event name attribute.</summary>
    public const string EventNameAttribute = "EventName";

    /// <summary>Reads a received SQS message as a v2 message; false when it is not in the v2 format.</summary>
    /// <param name="attributes">The SQS message's String attributes.</param>
    /// <param name="body">The SQS message body.</param>
    /// <param name="sqsMessageId">The SQS message id (the v2 message id of a command or a raw event delivery).</param>
    /// <param name="message">The v2 message.</param>
    public static bool TryRead(IReadOnlyDictionary<string, string> attributes, string body, string sqsMessageId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out V2Message? message)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrEmpty(sqsMessageId);
        message = null;
        if (attributes.TryGetValue(CommandTypeAttribute, out string? command) && command.Length > 0)
        {
            message = new V2Message
            {
                V2Type = command,
                MessageId = sqsMessageId,
                Body = body,
                EntityId = Int(attributes.GetValueOrDefault(EntityIdAttribute)),
                SequenceNo = Int(attributes.GetValueOrDefault(SequenceNoAttribute)),
            };
            return true;
        }

        if (attributes.TryGetValue(EventTypeAttribute, out string? @event) && @event.Length > 0)
        {
            message = new V2Message { V2Type = @event, MessageId = sqsMessageId, Body = body, IsEvent = true, EventName = attributes.GetValueOrDefault(EventNameAttribute) };
            return true;
        }

        return TryReadNotification(body, out message);
    }

    // An SNS notification: {"Type":"Notification","MessageId":…,"Message":"<v2 JSON>","MessageAttributes":{"EventType":{"Type":"String","Value":…}}}.
    private static bool TryReadNotification(string body, out V2Message? message)
    {
        message = null;
        if (body.Length == 0 || body[0] != '{')
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("Type", out JsonElement type) || type.ValueKind != JsonValueKind.String || type.GetString() != "Notification"
                || !root.TryGetProperty("MessageId", out JsonElement id) || id.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("Message", out JsonElement inner) || inner.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("MessageAttributes", out JsonElement attributes) || attributes.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            string? eventType = Attribute(attributes, EventTypeAttribute);
            string? commandType = Attribute(attributes, CommandTypeAttribute);
            string? v2Type = eventType ?? commandType;
            if (string.IsNullOrEmpty(v2Type) || string.IsNullOrEmpty(id.GetString()))
            {
                return false;
            }

            message = new V2Message
            {
                V2Type = v2Type,
                MessageId = id.GetString()!,
                Body = inner.GetString()!,
                IsEvent = eventType is not null,
                EventName = Attribute(attributes, EventNameAttribute),
                EntityId = Int(Attribute(attributes, EntityIdAttribute)),
                SequenceNo = Int(Attribute(attributes, SequenceNoAttribute)),
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Attribute(JsonElement attributes, string name)
    {
        return attributes.TryGetProperty(name, out JsonElement attribute) && attribute.ValueKind == JsonValueKind.Object
            && attribute.TryGetProperty("Value", out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int? Int(string? value)
    {
        return int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
    }
}
