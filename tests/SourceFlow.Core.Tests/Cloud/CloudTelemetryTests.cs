using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SourceFlow.Cloud.Observability;

namespace SourceFlow.Core.Tests.Cloud
{
    [TestFixture]
    [Category("Unit")]
    public class CloudTelemetryTests
    {
        private CloudTelemetry _telemetry = null!;
        private ActivityListener _listener = null!;

        [SetUp]
        public void SetUp()
        {
            _telemetry = new CloudTelemetry(NullLogger<CloudTelemetry>.Instance);

            // Register an activity listener so that activities are actually started
            _listener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.AllDataAndRecorded
            };
            ActivitySource.AddActivityListener(_listener);
        }

        [TearDown]
        public void TearDown()
        {
            _listener.Dispose();
        }

        // ── StartCommandDispatch ──────────────────────────────────────────────────

        [Test]
        public void StartCommandDispatch_WithListener_ReturnsNonNullActivity()
        {
            using var activity = _telemetry.StartCommandDispatch(
                commandType: "CreateOrder",
                destination: "https://sqs.us-east-1.amazonaws.com/123/orders",
                cloudProvider: "aws");

            Assert.That(activity, Is.Not.Null);
        }

        [Test]
        public void StartCommandDispatch_ActivityHasCorrectOperationName()
        {
            using var activity = _telemetry.StartCommandDispatch(
                commandType: "CreateOrder",
                destination: "queue-url",
                cloudProvider: "aws");

            Assert.That(activity, Is.Not.Null);
            Assert.That(activity!.OperationName, Does.Contain("CreateOrder"));
        }

        // ── InjectTraceContext ────────────────────────────────────────────────────

        [Test]
        public void InjectTraceContext_WritesTraceparentToAttributes()
        {
            using var activity = _telemetry.StartCommandDispatch(
                commandType: "TestCommand",
                destination: "queue",
                cloudProvider: "aws");

            var attributes = new Dictionary<string, string>();
            _telemetry.InjectTraceContext(activity, attributes);

            Assert.That(attributes.ContainsKey("traceparent"), Is.True,
                "InjectTraceContext should write 'traceparent' to the attributes dictionary");
            Assert.That(attributes["traceparent"], Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void InjectTraceContext_NullActivity_DoesNotThrow()
        {
            var attributes = new Dictionary<string, string>();

            Assert.DoesNotThrow(() => _telemetry.InjectTraceContext(null, attributes));
            Assert.That(attributes, Is.Empty);
        }

        // ── ExtractTraceParent ────────────────────────────────────────────────────

        [Test]
        public void ExtractTraceParent_AttributeAbsent_ReturnsNull()
        {
            var attributes = new Dictionary<string, string> { ["other"] = "value" };

            var result = _telemetry.ExtractTraceParent(attributes);

            Assert.That(result, Is.Null);
        }

        [Test]
        public void ExtractTraceParent_AttributePresent_ReturnsValue()
        {
            const string traceId = "00-abc123-def456-01";
            var attributes = new Dictionary<string, string> { ["traceparent"] = traceId };

            var result = _telemetry.ExtractTraceParent(attributes);

            Assert.That(result, Is.EqualTo(traceId));
        }

        [Test]
        public void ExtractTraceParent_NullDictionary_ReturnsNull()
        {
            var result = _telemetry.ExtractTraceParent(null);

            Assert.That(result, Is.Null);
        }

        // ── RecordError ───────────────────────────────────────────────────────────

        [Test]
        public void RecordError_SetsActivityStatusCodeToError()
        {
            using var activity = _telemetry.StartCommandDispatch(
                commandType: "FailingCommand",
                destination: "queue",
                cloudProvider: "aws");

            Assert.That(activity, Is.Not.Null);

            var exception = new InvalidOperationException("something went wrong");
            _telemetry.RecordError(activity, exception);

            Assert.That(activity!.Status, Is.EqualTo(ActivityStatusCode.Error));
        }

        [Test]
        public void RecordError_NullActivity_DoesNotThrow()
        {
            var ex = new Exception("boom");
            Assert.DoesNotThrow(() => _telemetry.RecordError(null, ex));
        }

        // ── RecordSuccess ─────────────────────────────────────────────────────────

        [Test]
        public void RecordSuccess_SetsActivityStatusCodeToOk()
        {
            using var activity = _telemetry.StartCommandDispatch(
                commandType: "SuccessCommand",
                destination: "queue",
                cloudProvider: "aws");

            Assert.That(activity, Is.Not.Null);

            _telemetry.RecordSuccess(activity);

            Assert.That(activity!.Status, Is.EqualTo(ActivityStatusCode.Ok));
        }
    }
}
