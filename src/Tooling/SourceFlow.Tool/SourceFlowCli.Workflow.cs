using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Messaging;
using SourceFlow.Runtime;
using SourceFlow.Storage;
using SourceFlow.Workflows;

namespace SourceFlow.Tool;

// `dotnet sourceflow workflow list|show|resume|retry-step|abort` (doc 15 §5.6, §7; WP-S.6). Instances are read from the
// workflow-instances read model; operator actions are WorkflowControl messages queued in the owner's outbox, which its
// relay delivers to the owner's endpoint (the scheduler), so they run in the instance's own transaction.
internal sealed partial class Cli
{
    private readonly Option<string> _tenant = new("--tenant") { Description = "The tenant of the instances.", DefaultValueFactory = _ => MessageEnvelope.DefaultTenant };
    private readonly Option<string?> _endpoint = new("--endpoint") { Description = "The owning service's endpoint (global tool only; inside the service its own)." };

    private Command WorkflowCommand()
    {
        var name = new Option<string?>("--name") { Description = "Only this workflow (without the version)." };
        var version = new Option<int?>("--version") { Description = "Only this version." };
        var status = new Option<string?>("--status") { Description = "Only this status (Running, Compensating, ManualIntervention, Completed, Compensated, Aborted)." };
        var active = new Option<bool>("--active") { Description = "Only unfinished instances (Running, Compensating, ManualIntervention)." };
        var limit = new Option<int>("--limit") { Description = "At most this many rows.", DefaultValueFactory = _ => 100 };
        var list = Store(new Command("list", "List workflow instances, newest first. With --name and --version, also says whether that version can be removed."), _tenant, name, version, status, active, limit);
        list.SetAction((r, ct) => Guard(async () =>
        {
            await using Ports ports = await PortsAsync(r, ct);
            IReadOnlyList<StoredDocument> documents = await ports.Storage.Documents.ListAsync(r.GetValue(_tenant)!, WorkflowInstanceSummary.Collection, int.MaxValue, ct);
            WorkflowInstanceSummary[] rows =
            [
                .. documents.Select(d => WorkflowRuntime.DeserializeSummary(d.Data.Span))
                    .Where(s => r.GetValue(name) is not { } n || s.Workflow == n)
                    .Where(s => r.GetValue(version) is not { } v || s.WorkflowVersion == v)
                    .Where(s => r.GetValue(status) is not { } st || string.Equals(s.Status, st, StringComparison.OrdinalIgnoreCase))
                    .Where(s => !r.GetValue(active) || s.IsActive)
                    .OrderByDescending(s => s.UpdatedAt).ThenBy(s => s.ProcessName, StringComparer.Ordinal).ThenBy(s => s.Instance, StringComparer.Ordinal)
                    .Take(Math.Max(0, r.GetValue(limit))),
            ];
            await Out.WriteLineAsync("workflow                    instance                              status              step                    attempt  updated");
            foreach (WorkflowInstanceSummary s in rows)
            {
                await Out.WriteLineAsync($"{s.ProcessName,-26}  {s.Instance,-36}  {s.Status,-18}  {s.Step ?? "-",-22}  {s.Attempt,7}  {s.UpdatedAt.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z");
            }

            await Out.WriteLineAsync($"{rows.Length} instance(s)");
            if (r.GetValue(name) is { } workflow && r.GetValue(version) is { } v)
            {
                await Out.WriteLineAsync(await RemovalVerdictAsync(ports.Storage, workflow + "@" + v.ToString(CultureInfo.InvariantCulture), ct));
            }

            return 0;
        }));

        var workflowArgument = new Argument<string>("workflow") { Description = "The workflow version, name@version (for example order-fulfilment@1)." };
        var instanceArgument = new Argument<string>("instance") { Description = "The instance: its correlation value (for example the order id)." };

        var show = Store(new Command("show", "Show a workflow instance: status, step, attempt and failure."), _tenant);
        show.Arguments.Add(workflowArgument);
        show.Arguments.Add(instanceArgument);
        show.SetAction((r, ct) => Guard(async () =>
        {
            await using Ports ports = await PortsAsync(r, ct);
            WorkflowInstanceSummary s = await InstanceAsync(ports.Storage, r.GetValue(_tenant)!, r.GetValue(workflowArgument)!, r.GetValue(instanceArgument)!, ct);
            var text = new System.Text.StringBuilder()
                .AppendLine(CultureInfo.InvariantCulture, $"workflow:      {s.ProcessName}")
                .AppendLine(CultureInfo.InvariantCulture, $"instance:      {s.Instance}")
                .AppendLine(CultureInfo.InvariantCulture, $"status:        {s.Status}")
                .AppendLine(CultureInfo.InvariantCulture, $"step:          {s.Step ?? "-"} (attempt {s.Attempt})")
                .AppendLine(CultureInfo.InvariantCulture, $"compensations: {s.PendingCompensations} pending")
                .AppendLine(CultureInfo.InvariantCulture, $"failure:       {(s.FailureCode is null ? "-" : $"{s.FailureCode} at {s.FailureStep}: {s.FailureMessage}")}")
                .AppendLine(CultureInfo.InvariantCulture, $"started:       {s.StartedAt:O}")
                .AppendLine(CultureInfo.InvariantCulture, $"updated:       {s.UpdatedAt:O}");
            await Out.WriteAsync(text.ToString());
            return 0;
        }));

        var note = new Option<string?>("--note") { Description = "An audit note." };
        var step = new Option<string?>("--step") { Description = "Resume at this step instead of the current one." };
        var outboxPartitions = new Option<int>("--outbox-partitions") { Description = "The service's OutboxPartitions (global tool only).", DefaultValueFactory = _ => 16 };
        var resume = Store(new Command("resume", "Resume an instance in manual intervention: the compensation continues, or the step (or --step) runs again."), _tenant, _endpoint, outboxPartitions, step, note);
        resume.Arguments.Add(workflowArgument);
        resume.Arguments.Add(instanceArgument);
        resume.SetAction((r, ct) => Guard(() => ControlAsync(r, workflowArgument, instanceArgument, outboxPartitions, WorkflowControl.ResumeAction, r.GetValue(step), r.GetValue(note), compensate: false, ct)));

        var retry = Store(new Command("retry-step", "Re-send the current step (or compensation) of an instance in manual intervention, as a new attempt."), _tenant, _endpoint, outboxPartitions, note);
        retry.Arguments.Add(workflowArgument);
        retry.Arguments.Add(instanceArgument);
        retry.SetAction((r, ct) => Guard(() => ControlAsync(r, workflowArgument, instanceArgument, outboxPartitions, WorkflowControl.RetryStepAction, null, r.GetValue(note), compensate: false, ct)));

        var reason = new Option<string>("--reason") { Description = "Why it is aborted (audited).", Required = true };
        var compensate = new Option<bool>("--compensate") { Description = "Compensate the completed steps first (the instance ends Aborted after the rollback)." };
        var abort = Store(new Command("abort", "Abort an unfinished instance, optionally compensating its completed steps."), _tenant, _endpoint, outboxPartitions, reason, compensate);
        abort.Arguments.Add(workflowArgument);
        abort.Arguments.Add(instanceArgument);
        abort.SetAction((r, ct) => Guard(() => ControlAsync(r, workflowArgument, instanceArgument, outboxPartitions, WorkflowControl.AbortAction, null, r.GetValue(reason), r.GetValue(compensate), ct)));

        var command = new Command("workflow", "DSL workflow instances (doc 15 §5.6, §7).");
        foreach (Command c in new[] { list, show, resume, retry, abort })
        {
            command.Subcommands.Add(c);
        }

        return command;
    }

    private async Task<int> ControlAsync(ParseResult r, Argument<string> workflowArgument, Argument<string> instanceArgument, Option<int> outboxPartitions,
        string action, string? step, string? note, bool compensate, CancellationToken ct)
    {
        string tenant = r.GetValue(_tenant)!;
        await using Ports ports = await PortsAsync(r, ct);
        WorkflowInstanceSummary s = await InstanceAsync(ports.Storage, tenant, r.GetValue(workflowArgument)!, r.GetValue(instanceArgument)!, ct);
        if (action == WorkflowControl.AbortAction ? !s.IsActive : s.Status != nameof(WorkflowStatus.ManualIntervention))
        {
            throw new CliException(action == WorkflowControl.AbortAction
                ? $"{s.ProcessName} {s.Instance} is {s.Status}: only an unfinished instance can be aborted."
                : $"{s.ProcessName} {s.Instance} is {s.Status}: '{action}' needs ManualIntervention.");
        }

        var control = new WorkflowControl(s.ProcessName, s.Instance, action, step, Audited(note), compensate);
        var correlation = new CorrelationId(new WorkflowInstanceId(s.Instance).ToStreamSegment());
        if (services?.GetService<SourceFlowRuntime>() is { } runtime)
        {
            await using IStorageSession session = await runtime.Storage.Sessions.BeginAsync(tenant, ct);
            runtime.EnqueueScheduled(session, control, runtime.Time.GetUtcNow(), cause: null, correlation);
            await session.CommitAsync(ct);
        }
        else
        {
            string endpoint = r.GetValue(_endpoint) ?? throw new CliException("Pass --endpoint: the owning service's endpoint (or run inside the service).");
            DateTimeOffset now = TimeProvider.System.GetUtcNow();
            var envelope = new MessageEnvelope
            {
                MessageId = MessageIds.New(TimeProvider.System),
                Type = WorkflowControl.TypeName,
                TypeVersion = 1,
                Source = CliOwner,
                Time = now,
                CorrelationId = correlation,
                TenantId = tenant,
                Actor = Environment.UserName + "@" + Environment.MachineName,
                Body = WorkflowRuntime.SerializeControl(control),
            };
            await using IStorageSession session = await ports.Storage.Sessions.BeginAsync(tenant, ct);
            session.EnqueueOutbox(new OutboxMessage(envelope, endpoint, OutboxKind.Scheduled, now, OutboxPartitioning.PartitionOf(envelope.EffectivePartitionKey, r.GetValue(outboxPartitions))));
            await session.CommitAsync(ct);
        }

        await Out.WriteLineAsync($"Sent {action} to {s.ProcessName} {s.Instance}");
        return 0;
    }

    private static async Task<WorkflowInstanceSummary> InstanceAsync(StoragePorts storage, string tenant, string workflow, string instance, CancellationToken ct)
    {
        if (!WorkflowRuntime.TryParseProcessName(workflow, out _, out _))
        {
            throw new CliException($"'{workflow}' is not a workflow version: use name@version, for example order-fulfilment@1.");
        }

        string value = WorkflowInstanceId.TryParse(instance, out WorkflowInstanceId id) ? id.Value : instance;
        StoredDocument document = await storage.Documents.GetAsync(tenant, WorkflowInstanceSummary.Collection, WorkflowInstanceSummary.DocumentId(workflow, value), ct)
            ?? throw new CliException($"No instance '{value}' of {workflow} in tenant '{tenant}'.");
        return WorkflowRuntime.DeserializeSummary(document.Data.Span);
    }

    // doc 15 §5.6: a version can be removed once no instance of it is active, in any tenant (the startup guard's check).
    private static async Task<string> RemovalVerdictAsync(StoragePorts storage, string processName, CancellationToken ct)
    {
        if (storage.Monitor is not { } monitor)
        {
            return $"{processName}: this storage can't count active instances across tenants.";
        }

        int active = (await monitor.CountActiveProcessesAsync(ct)).GetValueOrDefault(processName);
        return active == 0
            ? $"{processName}: no active instances in any tenant; it can be removed."
            : $"{processName}: {active} active instance(s) across tenants; keep it until they finish (or abort them).";
    }
}
