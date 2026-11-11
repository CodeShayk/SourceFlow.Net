using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Compliance.Classification;
using Microsoft.Extensions.Compliance.Redaction;
using SourceFlow.Messaging;

namespace SourceFlow.Runtime;

/// <summary>
/// Renders message payloads for logs (doc 08 §4, doc 10 §5): omitted unless <see cref="SourceFlowOptions.LogPayloads"/>
/// is on, and then with every classified member (<c>[PersonalData]</c>, <c>[Secret]</c>) passed through its redactor.
/// </summary>
internal sealed class PayloadRedaction(SourceFlowRuntime runtime, IRedactorProvider redactors)
{
    public const string Omitted = "[payload omitted]";

    public string ForLog(MessageEnvelope envelope)
    {
        if (!runtime.Options.LogPayloads)
        {
            return Omitted;
        }

        try
        {
            JsonNode? body = JsonNode.Parse(envelope.Body.Span);
            if (body is not null && runtime.Model.Registry.TryGet(envelope.Type, envelope.TypeVersion, out MessageTypeInfo? type))
            {
                Redact(body, type.JsonTypeInfo, 0);
            }

            return body?.ToJsonString() ?? "null";
        }
        catch (JsonException)
        {
            return "[payload is not JSON: " + envelope.Body.Length + " bytes]";
        }
    }

    private void Redact(JsonNode node, JsonTypeInfo info, int depth)
    {
        if (depth > 8 || node is not JsonObject obj || info.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        foreach (JsonPropertyInfo property in info.Properties)
        {
            if (!obj.TryGetPropertyValue(property.Name, out JsonNode? value) || value is null)
            {
                continue;
            }

            DataClassification? classification = (property.AttributeProvider?.GetCustomAttributes(typeof(DataClassificationAttribute), inherit: true) ?? [])
                .OfType<DataClassificationAttribute>().Select(a => (DataClassification?)a.Classification).FirstOrDefault();
            if (classification is { } c)
            {
                Redactor redactor = redactors.GetRedactor(new DataClassificationSet(c));
                string text = value is JsonValue v && v.TryGetValue(out string? s) ? s : value.ToJsonString();
                obj[property.Name] = JsonValue.Create(redactor.Redact(text));
            }
            else if (value is JsonObject && info.Options.TryGetTypeInfo(property.PropertyType, out JsonTypeInfo? nested))
            {
                Redact(value, nested, depth + 1);
            }
        }
    }
}
