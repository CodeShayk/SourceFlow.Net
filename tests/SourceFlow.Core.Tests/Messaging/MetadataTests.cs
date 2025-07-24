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
            Assert.AreNotEqual(Guid.Empty, metadata.EventId);
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
            Assert.AreEqual(guid, metadata.EventId);
            Assert.IsTrue(metadata.IsReplay);
            Assert.AreEqual(now, metadata.OccurredOn);
            Assert.AreEqual(42, metadata.SequenceNo);
            Assert.AreEqual(123, metadata.Properties["foo"]);
        }
    }
}