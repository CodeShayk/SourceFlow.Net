using SourceFlow.Messaging;

namespace SourceFlow.Core.Tests.Messaging
{
    [TestFixture]
    public class MetadataTests
    {
        [Test]
        public void Constructor_InitializesProperties()
        {
            var metadata = new Metadata();
            Assert.That(metadata.EventId, Is.Not.EqualTo(Guid.Empty));
            Assert.That(metadata.OccurredOn, Is.Not.EqualTo(default(DateTime)));
            Assert.IsFalse(metadata.IsReplay);
            Assert.IsNotNull(metadata.Properties);
            Assert.IsInstanceOf<Dictionary<string, object>>(metadata.Properties);
        }

        [Test]
        public void Properties_CanBeSetAndGet()
        {
            var metadata = new Metadata();
            var guid = Guid.NewGuid();
            var now = DateTime.UtcNow;
            metadata.EventId = guid;
            metadata.IsReplay = true;
            metadata.OccurredOn = now;
            metadata.SequenceNo = 42;
            metadata.Properties["foo"] = 123;
            Assert.That(metadata.EventId, Is.EqualTo(guid));
            Assert.IsTrue(metadata.IsReplay);
            Assert.That(metadata.OccurredOn, Is.EqualTo(now));
            Assert.That(metadata.SequenceNo, Is.EqualTo(42));
            Assert.That(metadata.Properties["foo"], Is.EqualTo(123));
        }
    }
}
