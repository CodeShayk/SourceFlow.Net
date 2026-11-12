using Microsoft.Extensions.Logging;
using SourceFlow.Messaging;
using SourceFlow.Runtime;
using SourceFlow.Storage;

namespace SourceFlow.Security;

/// <summary>The <see cref="IPersonalDataEraser"/> of the runtime (doc 10 §4, WP-5.2).</summary>
internal sealed partial class PersonalDataEraser(SourceFlowRuntime runtime, PersonalDataProtector protector, IPersonalDataKeyStore keys) : IPersonalDataEraser
{
    private readonly ILogger _logger = runtime.Logger<PersonalDataEraser>();

    public async ValueTask<ErasureResult> EraseSubjectAsync(string subjectId, string? tenantId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        string tenant = tenantId ?? runtime.Options.DefaultTenant;

        // (1) Destroy the key: from now on every member encrypted for the subject reads as erased.
        await keys.ShredKeyAsync(subjectId, runtime.Time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        protector.Evict(subjectId);

        // (2) Snapshots and cached states of the subject's streams were built from decrypted data.
        IReadOnlyList<StreamId> streams = await keys.GetSubjectStreamsAsync(tenant, subjectId, cancellationToken).ConfigureAwait(false);
        foreach (StreamId stream in streams)
        {
            await runtime.Storage.Snapshots.DeleteAsync(tenant, stream, cancellationToken).ConfigureAwait(false);
            runtime.AggregateCache.Remove(tenant, stream);
        }

        // (3) Scrub the [PersonalData] members of documents keyed by those streams' aggregates.
        int scrubbed = await ScrubDocumentsAsync(tenant, streams, cancellationToken).ConfigureAwait(false);

        // (4) Tell custom projections.
        await using (IStorageSession session = await runtime.Storage.Sessions.BeginAsync(tenant, cancellationToken).ConfigureAwait(false))
        {
            MessageEnvelope envelope = runtime.CreateEnvelope(new PersonalDataErased(subjectId), tenant: tenant);
            var stream = new StreamId("personal-data-" + subjectId);
            session.Append(stream, "personal-data", ExpectedVersion.Any,
                [new EventToAppend(envelope.MessageId, envelope.Type, envelope.TypeVersion, envelope.Body, new EventMetadata { Source = envelope.Source, CorrelationId = envelope.CorrelationId }, envelope.Time, [])]);
            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        SubjectErased(_logger, streams.Count, scrubbed);
        return new ErasureResult(subjectId, streams.Count, streams.Count, scrubbed);
    }

    private async ValueTask<int> ScrubDocumentsAsync(string tenant, IReadOnlyList<StreamId> streams, CancellationToken cancellationToken)
    {
        if (streams.Count == 0)
        {
            return 0;
        }

        // A document projection keyed by an aggregate id stores its document under the stream's id segment.
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (StreamId stream in streams)
        {
            foreach (AggregateDescriptor aggregate in runtime.Model.Aggregates.Values)
            {
                if (stream.Value.StartsWith(aggregate.Name + "-", StringComparison.Ordinal))
                {
                    ids.Add(stream.Value[(aggregate.Name.Length + 1)..]);
                }
            }
        }

        int scrubbed = 0;
        await using IStorageSession session = await runtime.Storage.Sessions.BeginAsync(tenant, cancellationToken).ConfigureAwait(false);
        foreach (DocumentProjectionDescriptor projection in runtime.Model.Projections.OfType<DocumentProjectionDescriptor>())
        {
            if (!protector.Protects(projection.DocumentType))
            {
                continue;
            }

            foreach (string collection in runtime.Generations.CollectionsOf(projection))
            {
                IReadOnlyList<StoredDocument> documents = await session.Documents.GetManyAsync(collection, ids, cancellationToken).ConfigureAwait(false);
                foreach (StoredDocument document in documents)
                {
                    session.Documents.Upsert(collection, document.Id, document.Version, protector.Scrub(projection.DocumentType, document.Data));
                    scrubbed++;
                }
            }
        }

        await session.CommitAsync(cancellationToken).ConfigureAwait(false);
        return scrubbed;
    }

    [LoggerMessage(EventId = 1501, Level = LogLevel.Information, Message = "Erased a subject: key destroyed, {Streams} stream(s), {Documents} document(s) scrubbed.")]
    private static partial void SubjectErased(ILogger logger, int streams, int documents);
}
