using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SourceFlow.Studio.Dsl.Manifests;
using SourceFlow.Studio.Dsl.Model;

namespace SourceFlow.Studio.Dsl.Validation
{
    /// <summary>
    /// What a workflow is validated against: the manifests of the business domain (doc 15 §4), the other workflows of
    /// the owner (WF-V15), and the compiling service when the generator validates.
    /// </summary>
    public sealed class WorkflowEnvironment
    {
        private readonly List<ServiceManifest> _manifests;

        /// <summary>Creates an environment.</summary>
        /// <param name="manifests">The domain's service manifests (the merged ones from <c>manifest export</c>, or static ones).</param>
        /// <param name="siblings">The other workflows the owner compiles, for duplicate <c>name@version</c>.</param>
        /// <param name="compilingService">The service compiling the workflow (generator), or null in the workspace.</param>
        /// <param name="partial">
        /// True at compile time: only some services' manifests are known (the owner's and its contract packages'), so
        /// rules about a command whose owner is unknown are skipped instead of reported.
        /// </param>
        public WorkflowEnvironment(IEnumerable<ServiceManifest> manifests, IEnumerable<WorkflowDefinition>? siblings = null, string? compilingService = null, bool partial = false)
        {
            _manifests = (manifests ?? throw new ArgumentNullException(nameof(manifests))).ToList();
            Siblings = siblings?.ToList() ?? new List<WorkflowDefinition>();
            CompilingService = compilingService;
            Partial = partial;
        }

        /// <summary>The manifests.</summary>
        public IReadOnlyList<ServiceManifest> Manifests => _manifests;

        /// <summary>The other workflows of the owner.</summary>
        public IReadOnlyList<WorkflowDefinition> Siblings { get; }

        /// <summary>The compiling service, or null.</summary>
        public string? CompilingService { get; }

        /// <summary>Whether manifests may be missing (compile time).</summary>
        public bool Partial { get; }

        /// <summary>The manifest of <paramref name="service"/> (a static manifest counts as the compiling service's).</summary>
        public ServiceManifest? Service(string service)
        {
            foreach (ServiceManifest manifest in _manifests)
            {
                if (manifest.Service == service)
                {
                    return manifest;
                }
            }

            return null;
        }

        /// <summary>The message type <paramref name="reference"/> from any manifest.</summary>
        public ManifestMessage? Message(string reference)
        {
            foreach (ServiceManifest manifest in _manifests)
            {
                if (manifest.FindMessage(reference) is { } message)
                {
                    return message;
                }
            }

            return null;
        }

        /// <summary>Every service that owns <paramref name="command"/>, with its aggregate and command entry.</summary>
        public IReadOnlyList<CommandOwner> Owners(string command)
        {
            var owners = new List<CommandOwner>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ServiceManifest manifest in _manifests)
            {
                if (manifest.TryGetCommand(command, out ManifestAggregate? aggregate, out ManifestCommand? entry)
                    && seen.Add((manifest.Service ?? manifest.Assembly ?? string.Empty) + "/" + aggregate!.Name))
                {
                    owners.Add(new CommandOwner(manifest, aggregate, entry!));
                }
            }

            return owners;
        }

        /// <summary>The publications of <paramref name="reference"/> (as the published type), by any service.</summary>
        public IEnumerable<(ServiceManifest Service, ManifestPublication Publication)> Publications(string reference)
        {
            foreach (ServiceManifest manifest in _manifests)
            {
                foreach (ManifestPublication publication in manifest.Publishes)
                {
                    if (publication.As == reference)
                    {
                        yield return (manifest, publication);
                    }
                }
            }
        }
    }

    /// <summary>A command's owning service and aggregate.</summary>
    public sealed record CommandOwner(ServiceManifest Service, ManifestAggregate Aggregate, ManifestCommand Command)
    {
        /// <summary>The owning service's name (a static manifest has none).</summary>
        public string? Name => Service.Service;
    }

    /// <summary>
    /// The structural fingerprint of a workflow version (WF-V15): a hash of everything that affects behaviour, so
    /// comments, layout, ordering of map keys and the description don't change it.
    /// </summary>
    public static class WorkflowFingerprint
    {
        /// <summary>Computes the fingerprint, <c>sha256:</c> and 16 hex digits.</summary>
        public static string Compute(WorkflowDefinition workflow)
        {
            if (workflow is null)
            {
                throw new ArgumentNullException(nameof(workflow));
            }

            var text = new StringBuilder();
            text.Append("start ").Append(workflow.Start.On).Append(' ').Append(workflow.Start.Correlate).Append(' ').Append(workflow.Start.When).Append('\n');
            foreach (DataField field in workflow.Data.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                text.Append("data ").Append(field.Name).Append(' ').Append(field.From).Append(' ').Append(field.Type).Append(' ').Append(field.Default).Append(' ').Append(field.Personal).Append('\n');
            }

            foreach (WorkflowStep step in workflow.Steps)
            {
                text.Append("step ").Append(step.Id).Append(" pivot=").Append(step.Pivot).Append(" when=").Append(step.When).Append(" next=").Append(step.Next).Append('\n');
                Send(text, "send", step.Send);
                if (step.Await is { } wait)
                {
                    Await(text, wait);
                }

                Timeout(text, "timeout", step.Timeout);
                if (step.Compensate is { } compensate)
                {
                    text.Append("compensate none=").Append(compensate.None).Append('\n');
                    Send(text, "compensate.send", compensate.Send);
                    if (compensate.Await is { } compensation)
                    {
                        Await(text, compensation);
                    }

                    Retry(text, "compensate.retry", compensate.Retry);
                    Timeout(text, "compensate.timeout", compensate.Timeout);
                }
            }

            Send(text, "on.completed", workflow.On.Completed);
            Send(text, "on.compensated", workflow.On.Compensated);
            text.Append("on.manual ").Append(workflow.On.ManualAlert).Append('\n');

            using SHA256 sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()));
            var hex = new StringBuilder("sha256:");
            for (int i = 0; i < 8; i++)
            {
                hex.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
            }

            return hex.ToString();
        }

        private static void Send(StringBuilder text, string label, SendSpec? send)
        {
            if (send is null)
            {
                return;
            }

            text.Append(label).Append(' ').Append(send.Command);
            foreach (MapEntry entry in send.Map.OrderBy(e => e.Field, StringComparer.Ordinal))
            {
                text.Append(' ').Append(entry.Field).Append('=').Append(entry.Value.Kind).Append(':').Append(entry.Value.Text).Append(entry.Value.Literal);
            }

            text.Append('\n');
        }

        private static void Await(StringBuilder text, AwaitSpec wait)
        {
            text.Append("success ").Append(wait.Success.On).Append(" when=").Append(wait.Success.When);
            foreach (CaptureSpec capture in wait.Success.Capture.OrderBy(c => c.Field, StringComparer.Ordinal))
            {
                text.Append(' ').Append(capture.Field).Append('=').Append(capture.Path);
            }

            text.Append('\n');
            foreach (FailureSpec failure in wait.Failures)
            {
                text.Append("failure ").Append(failure.Rejected).Append('|').Append(failure.On).Append(" then=").Append(failure.Then).Append('\n');
                Retry(text, "failure.retry", failure.Retry);
            }
        }

        private static void Timeout(StringBuilder text, string label, TimeoutSpec? timeout)
        {
            if (timeout is null)
            {
                return;
            }

            text.Append(label).Append(' ').Append(timeout.None ? "none" : timeout.After?.Ticks.ToString(CultureInfo.InvariantCulture)).Append(" then=").Append(timeout.Then).Append('\n');
            Retry(text, label + ".retry", timeout.Retry);
        }

        private static void Retry(StringBuilder text, string label, RetrySpec? retry)
        {
            if (retry is null)
            {
                return;
            }

            text.Append(label).Append(' ').Append(retry.Max).Append(' ').Append(retry.Backoff).Append(' ').Append(retry.Initial.Ticks)
                .Append(' ').Append(retry.Cap?.Ticks).Append(' ').Append(retry.Jitter).Append('\n');
        }
    }
}
