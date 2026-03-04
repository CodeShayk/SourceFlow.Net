using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SourceFlow.Cloud.Observability;

/// <summary>
/// Provides distributed tracing capabilities for cloud messaging
/// </summary>
public class CloudTelemetry
{
    private readonly ILogger<CloudTelemetry> _logger;

    public CloudTelemetry(ILogger<CloudTelemetry> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Start a command dispatch activity
    /// </summary>
    public Activity? StartCommandDispatch(
        string commandType,
        string destination,
        string cloudProvider,
        object? entityId = null,
        long? sequenceNo = null)
    {
        var activity = CloudActivitySource.Instance.StartActivity(
            $"{commandType}.Dispatch",
            ActivityKind.Producer);

        if (activity != null)
        {
            activity.SetTag(CloudActivitySource.SemanticConventions.MessagingSystem, cloudProvider);
            activity.SetTag(CloudActivitySource.SemanticConventions.MessagingDestination, destination);
            activity.SetTag(CloudActivitySource.SemanticConventions.MessagingDestinationKind,
                CloudActivitySource.DestinationKind.Queue);
            activity.SetTag(CloudActivitySource.SemanticConventions.MessagingOperation,
                CloudActivitySource.Operation.Send);
            activity.SetTag(CloudActivitySource.SemanticConventions.SourceFlowCommandType, commandType);
            activity.SetTag(CloudActivitySource.SemanticConventions.CloudProvider, cloudProvider);
            activity.SetTag(CloudActivitySource.SemanticConventions.CloudQueue, destination);

            if (entityId != null)
                activity.SetTag(CloudActivitySource.SemanticConventions.SourceFlowEntityId, entityId);

            if (sequenceNo.HasValue)
                activity.SetTag(CloudActivitySource.SemanticConventions.SourceFlowSequenceNo, sequenceNo.Value);

            _logger.LogTrace("Started command dispatch activity: {ActivityId}", activity.Id);
        }

        return activity;
    }

    /// <summary>
    /// Start a command processing activity
    /// </summary>
    public Activity? StartCommandProcess(
        string commandType,
        string source,
        string cloudProvider,
        string? parentTraceId = null,
        object? entityId = null,
        long? sequenceNo = null)
    {
        var activity = CloudActivitySource.Instance.StartActivity(
            $"{commandType}.Process",
            ActivityKind.Consumer,
            parentTraceId ?? string.Empty);

        if (activity != null)
        {
            activity.SetTag(CloudActivitySource.SemanticConventions.MessagingSystem, cloudProvider);
            activity.SetTag(CloudActivitySource.SemanticConventions.MessagingDestination, source);
            activity.SetTag(CloudActivitySource.SemanticConventions.MessagingOperation,
                CloudActivitySource.Operation.Process);
            activity.SetTag(CloudActivitySource.SemanticConventions.SourceFlowCommandType, commandType);
            activity.SetTag(CloudActivitySource.SemanticConventions.CloudProvider, cloudProvider);

            if (entityId != null)
                activity.SetTag(CloudActivitySource.SemanticConventions.SourceFlowEntityId, entityId);

            if (sequenceNo.HasValue)
                activity.SetTag(CloudActivitySource.SemanticConventions.SourceFlowSequenceNo, sequenceNo.Value);

            _logger.LogTrace("Started command process activity: {ActivityId}", activity.Id);
        }

        return activity;
    }

    /// <summary>
    /// Start an event publish activity
    /// </summary>
    public Activity? StartEventPublish(
        string eventType,
        string destination,
        string cloudProvider,
        long? sequenceNo = null)
    {
        var activity = CloudActivitySource.Instance.StartActivity(
            $"{eventType}.Publish",
            ActivityKind.Producer);

        if (activity != null)
        {
            activity.SetTag(CloudActivitySource.SemanticConventions.MessagingSystem, cloudProvider);
            activity.SetTag(CloudActivitySource.SemanticConventions.MessagingDestination, destination);
            activity.SetTag(CloudActivitySource.SemanticConventions.MessagingDestinationKind,
                CloudActivitySource.DestinationKind.Topic);
            activity.SetTag(CloudActivitySource.SemanticConventions.MessagingOperation,
                CloudActivitySource.Operation.Publish);
            activity.SetTag(CloudActivitySource.SemanticConventions.SourceFlowEventType, eventType);
            activity.SetTag(CloudActivitySource.SemanticConventions.CloudProvider, cloudProvider);
            activity.SetTag(CloudActivitySource.SemanticConventions.CloudTopic, destination);

            if (sequenceNo.HasValue)
                activity.SetTag(CloudActivitySource.SemanticConventions.SourceFlowSequenceNo, sequenceNo.Value);

            _logger.LogTrace("Started event publish activity: {ActivityId}", activity.Id);
        }

        return activity;
    }

    /// <summary>
    /// Start an event receive activity
    /// </summary>
    public Activity? StartEventReceive(
        string eventType,
        string source,
        string cloudProvider,
        string? parentTraceId = null,
        long? sequenceNo = null)
    {
        var activity = CloudActivitySource.Instance.StartActivity(
            $"{eventType}.Receive",
            ActivityKind.Consumer,
            parentTraceId ?? string.Empty);

        if (activity != null)
        {
            activity.SetTag(CloudActivitySource.SemanticConventions.MessagingSystem, cloudProvider);
            activity.SetTag(CloudActivitySource.SemanticConventions.MessagingDestination, source);
            activity.SetTag(CloudActivitySource.SemanticConventions.MessagingOperation,
                CloudActivitySource.Operation.Receive);
            activity.SetTag(CloudActivitySource.SemanticConventions.SourceFlowEventType, eventType);
            activity.SetTag(CloudActivitySource.SemanticConventions.CloudProvider, cloudProvider);

            if (sequenceNo.HasValue)
                activity.SetTag(CloudActivitySource.SemanticConventions.SourceFlowSequenceNo, sequenceNo.Value);

            _logger.LogTrace("Started event receive activity: {ActivityId}", activity.Id);
        }

        return activity;
    }

    /// <summary>
    /// Record successful completion
    /// </summary>
    public void RecordSuccess(Activity? activity, long? durationMs = null)
    {
        if (activity == null) return;

        activity.SetStatus(ActivityStatusCode.Ok);

        if (durationMs.HasValue)
        {
            activity.SetTag(CloudActivitySource.SemanticConventions.ProcessingDuration, durationMs.Value);
        }

        _logger.LogTrace("Recorded success for activity: {ActivityId}", activity.Id);
    }

    /// <summary>
    /// Record error
    /// </summary>
    public void RecordError(Activity? activity, Exception exception, long? durationMs = null)
    {
        if (activity == null) return;

        activity.SetStatus(ActivityStatusCode.Error, exception.Message);

        // Add exception details as tags
        activity.SetTag("exception.type", exception.GetType().FullName);
        activity.SetTag("exception.message", exception.Message);
        activity.SetTag("exception.stacktrace", exception.StackTrace);

        if (durationMs.HasValue)
        {
            activity.SetTag(CloudActivitySource.SemanticConventions.ProcessingDuration, durationMs.Value);
        }

        _logger.LogTrace("Recorded error for activity: {ActivityId}, Error: {Error}",
            activity.Id, exception.Message);
    }

    /// <summary>
    /// Extract trace context from message attributes
    /// </summary>
    public string? ExtractTraceParent(Dictionary<string, string>? messageAttributes)
    {
        if (messageAttributes == null) return null;

        messageAttributes.TryGetValue("traceparent", out var traceParent);
        return traceParent;
    }

    /// <summary>
    /// Inject trace context into message attributes
    /// </summary>
    public void InjectTraceContext(Activity? activity, Dictionary<string, string> messageAttributes)
    {
        if (activity == null || string.IsNullOrEmpty(activity.Id)) return;

        messageAttributes["traceparent"] = activity.Id;

        if (!string.IsNullOrEmpty(activity.TraceStateString))
        {
            messageAttributes["tracestate"] = activity.TraceStateString;
        }
    }
}
