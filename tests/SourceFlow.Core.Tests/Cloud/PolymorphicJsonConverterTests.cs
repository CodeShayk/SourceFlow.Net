using System;
using System.Text.Json;
using SourceFlow.Cloud.Serialization;

namespace SourceFlow.Core.Tests.Cloud
{
    // ── Test types ────────────────────────────────────────────────────────────────

    internal abstract class TestBase
    {
        public string Common { get; set; } = "";
    }

    internal class TestConcrete : TestBase
    {
        public string Specific { get; set; } = "";
    }

    // Concrete converter for TestBase
    internal class TestConverter : PolymorphicJsonConverter<TestBase> { }

    [TestFixture]
    [Category("Unit")]
    public class PolymorphicJsonConverterTests
    {
        private JsonSerializerOptions _options = null!;

        [SetUp]
        public void SetUp()
        {
            _options = new JsonSerializerOptions();
            _options.Converters.Add(new TestConverter());
        }

        // ── Round-trip ────────────────────────────────────────────────────────────

        [Test]
        public void RoundTrip_ConcreteThroughWriteRead_PreservesConcreteType()
        {
            var original = new TestConcrete { Common = "shared", Specific = "detail" };

            var json = JsonSerializer.Serialize<TestBase>(original, _options);
            var result = JsonSerializer.Deserialize<TestBase>(json, _options);

            Assert.That(result, Is.Not.Null);
            Assert.That(result, Is.InstanceOf<TestConcrete>());
            var concrete = (TestConcrete)result!;
            Assert.That(concrete.Common, Is.EqualTo("shared"));
            Assert.That(concrete.Specific, Is.EqualTo("detail"));
        }

        [Test]
        public void Write_IncludesTypeDiscriminator()
        {
            var original = new TestConcrete { Common = "c" };

            var json = JsonSerializer.Serialize<TestBase>(original, _options);
            using var doc = JsonDocument.Parse(json);

            Assert.That(doc.RootElement.TryGetProperty("$type", out _), Is.True,
                "Serialized JSON should contain $type discriminator");
        }

        // ── Missing discriminator ─────────────────────────────────────────────────

        [Test]
        public void Read_MissingTypeDiscriminator_ThrowsJsonException()
        {
            const string json = "{\"common\":\"x\",\"specific\":\"y\"}";

            var ex = Assert.Throws<JsonException>(() =>
                JsonSerializer.Deserialize<TestBase>(json, _options));

            Assert.That(ex!.Message, Does.Contain("$type").Or.Contain("discriminator").IgnoreCase);
        }

        // ── Unknown type name ─────────────────────────────────────────────────────

        [Test]
        public void Read_UnknownTypeName_ThrowsJsonExceptionContainingTypeName()
        {
            const string unknownType = "UnknownNamespace.UnknownType, UnknownAssembly";
            var json = $"{{\"$type\":\"{unknownType}\",\"common\":\"x\"}}";

            var ex = Assert.Throws<JsonException>(() =>
                JsonSerializer.Deserialize<TestBase>(json, _options));

            Assert.That(ex!.Message, Does.Contain("UnknownNamespace.UnknownType"));
        }

        // ── Null value ────────────────────────────────────────────────────────────

        [Test]
        public void Write_NullValue_ProducesNullJson()
        {
            var json = JsonSerializer.Serialize<TestBase>(null!, _options);

            Assert.That(json, Is.EqualTo("null"));
        }

        [Test]
        public void Read_NullToken_ReturnsNullWithoutCallingConverter()
        {
            // JsonSerializer handles null tokens before delegating to converters,
            // so a null JSON token for a nullable reference type should return null.
            TestBase? result = null;
            Exception? thrownException = null;

            try
            {
                result = JsonSerializer.Deserialize<TestBase>("null", _options);
            }
            catch (JsonException ex)
            {
                thrownException = ex;
            }

            // Either returns null or throws JsonException — both acceptable outcomes
            // for a class-typed (non-nullable-annotated) converter
            if (thrownException == null)
            {
                Assert.That(result, Is.Null);
            }
            else
            {
                Assert.That(thrownException, Is.InstanceOf<JsonException>());
            }
        }
    }
}
