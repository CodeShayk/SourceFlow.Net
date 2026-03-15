using System.Text.Json;
using SourceFlow.Cloud.AWS.Messaging.Serialization;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using SourceFlow.Messaging;

namespace SourceFlow.Cloud.AWS.Tests.Unit;

[Trait("Category", "Unit")]
public class AwsJsonConverterTests
{
    // ── CommandPayloadConverter ───────────────────────────────────────────────

    [Fact]
    public void CommandPayloadConverter_RoundTrip_PreservesConcreteType()
    {
        // Arrange
        var options = new JsonSerializerOptions();
        options.Converters.Add(new CommandPayloadConverter());

        var payload = new TestCommandData { Message = "hello", Value = 42 };

        // Act
        var json = JsonSerializer.Serialize<IPayload>(payload, options);
        var result = JsonSerializer.Deserialize<IPayload>(json, options);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<TestCommandData>(result);
        var deserialized = (TestCommandData)result;
        Assert.Equal("hello", deserialized.Message);
        Assert.Equal(42, deserialized.Value);
    }

    [Fact]
    public void CommandPayloadConverter_Write_IncludesTypePropAndValueProp()
    {
        // Arrange
        var options = new JsonSerializerOptions();
        options.Converters.Add(new CommandPayloadConverter());
        var payload = new TestCommandData { Message = "test", Value = 1 };

        // Act
        var json = JsonSerializer.Serialize<IPayload>(payload, options);
        using var doc = JsonDocument.Parse(json);

        // Assert: envelope contains $type and $value
        Assert.True(doc.RootElement.TryGetProperty("$type", out _),
            "Serialized payload should contain $type");
        Assert.True(doc.RootElement.TryGetProperty("$value", out _),
            "Serialized payload should contain $value");
    }

    [Fact]
    public void CommandPayloadConverter_Read_MissingTypeProperty_ThrowsJsonException()
    {
        // Arrange
        var options = new JsonSerializerOptions();
        options.Converters.Add(new CommandPayloadConverter());

        const string json = "{\"$value\":{\"message\":\"x\",\"value\":0}}";

        // Act & Assert
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<IPayload>(json, options));
    }

    [Fact]
    public void CommandPayloadConverter_Read_UnknownTypeName_ThrowsJsonException()
    {
        // Arrange
        var options = new JsonSerializerOptions();
        options.Converters.Add(new CommandPayloadConverter());

        const string json = "{\"$type\":\"NonExistent.Type, FakeAssembly\",\"$value\":{}}";

        // Act & Assert
        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<IPayload>(json, options));
        Assert.Contains("NonExistent.Type", ex.Message);
    }

    // ── MetadataConverter ─────────────────────────────────────────────────────

    [Fact]
    public void MetadataConverter_RoundTrip_PreservesAllFields()
    {
        // Arrange
        var options = new JsonSerializerOptions();
        options.Converters.Add(new MetadataConverter());

        var eventId = Guid.NewGuid();
        var occurredOn = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        var metadata = new Metadata
        {
            EventId = eventId,
            IsReplay = false,
            OccurredOn = occurredOn,
            SequenceNo = 42,
            Properties = new Dictionary<string, object> { ["key"] = "value" }
        };

        // Act
        var json = JsonSerializer.Serialize(metadata, options);
        var result = JsonSerializer.Deserialize<Metadata>(json, options);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(eventId, result!.EventId);
        Assert.Equal(42, result.SequenceNo);
        Assert.False(result.IsReplay);
    }

    [Fact]
    public void MetadataConverter_RoundTrip_PreservesPropertiesDictionary()
    {
        // Arrange
        var options = new JsonSerializerOptions();
        options.Converters.Add(new MetadataConverter());

        var metadata = new Metadata
        {
            EventId = Guid.NewGuid(),
            IsReplay = true,
            OccurredOn = DateTime.UtcNow,
            SequenceNo = 7,
            Properties = new Dictionary<string, object>
            {
                ["correlationId"] = "abc-123",
                ["source"] = "test"
            }
        };

        // Act
        var json = JsonSerializer.Serialize(metadata, options);
        var result = JsonSerializer.Deserialize<Metadata>(json, options);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result!.Properties);
        Assert.True(result.Properties.ContainsKey("correlationId"), "Properties should contain 'correlationId'");
    }

    [Fact]
    public void MetadataConverter_Write_NullValue_ProducesNullToken()
    {
        // Arrange
        var options = new JsonSerializerOptions();
        options.Converters.Add(new MetadataConverter());

        // Act – serialise a null Metadata
        var json = JsonSerializer.Serialize<Metadata>(null!, options);

        // Assert
        Assert.Equal("null", json);
    }

    [Fact]
    public void MetadataConverter_Read_NullToken_ReturnsNull()
    {
        // Arrange
        var options = new JsonSerializerOptions();
        options.Converters.Add(new MetadataConverter());

        // Act
        var result = JsonSerializer.Deserialize<Metadata>("null", options);

        // Assert
        Assert.Null(result);
    }
}
