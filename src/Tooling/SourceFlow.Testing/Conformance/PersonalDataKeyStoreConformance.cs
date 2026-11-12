using SourceFlow.Security;

namespace SourceFlow.Testing.Conformance;

/// <summary>
/// The <see cref="IPersonalDataKeyStore"/> contract (doc 10 §4, WP-5.2), for every storage adapter: keys are added once
/// (the first writer wins), shredding destroys the wrapped key and is permanent, a subject without a key can be shredded
/// ahead of any data, and subject streams are recorded idempotently per tenant.
/// </summary>
public static class PersonalDataKeyStoreConformance
{
    /// <summary>Runs every check against <paramref name="store"/>; subject ids are made unique per run.</summary>
    public static async Task RunAsync(IPersonalDataKeyStore store, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        string run = Guid.CreateVersion7(now).ToString("N");
        string subject = "subject-" + run;
        string other = "other-" + run;

        // Add: the first writer wins.
        Check.Null(await store.GetKeyAsync(subject, cancellationToken).ConfigureAwait(false), "no key before the first add");
        SubjectKey first = await store.AddKeyAsync(new SubjectKey(subject, "kms/v1", new byte[] { 1, 2, 3 }, now, null), cancellationToken).ConfigureAwait(false);
        SubjectKey second = await store.AddKeyAsync(new SubjectKey(subject, "kms/v2", new byte[] { 9 }, now, null), cancellationToken).ConfigureAwait(false);
        Check.Equal("kms/v1", first.KeyReference, "the first key is stored");
        Check.Equal("kms/v1", second.KeyReference, "a second add returns the stored key");
        Check.SequenceEqual(new byte[] { 1, 2, 3 }, (await store.GetKeyAsync(subject, cancellationToken).ConfigureAwait(false))!.WrappedKey.ToArray(), "the wrapped key round-trips");

        // Shred: the wrapped key is destroyed, the subject stays erased.
        await store.ShredKeyAsync(subject, now, cancellationToken).ConfigureAwait(false);
        SubjectKey shredded = (await store.GetKeyAsync(subject, cancellationToken).ConfigureAwait(false))!;
        Check.True(shredded.IsShredded, "shredded");
        Check.Equal(0, shredded.WrappedKey.Length, "the wrapped key is gone");
        await store.ShredKeyAsync(subject, now.AddDays(1), cancellationToken).ConfigureAwait(false);
        Check.True((await store.GetKeyAsync(subject, cancellationToken).ConfigureAwait(false))!.IsShredded, "shredding twice is harmless");
        SubjectKey afterShred = await store.AddKeyAsync(new SubjectKey(subject, "kms/v3", new byte[] { 7 }, now, null), cancellationToken).ConfigureAwait(false);
        Check.True(afterShred.IsShredded, "an erased subject never gets a new key");

        // A subject erased before any data has a shredded record.
        await store.ShredKeyAsync(other, now, cancellationToken).ConfigureAwait(false);
        Check.True((await store.GetKeyAsync(other, cancellationToken).ConfigureAwait(false))!.IsShredded, "shredded ahead of any data");

        // Subject streams: idempotent, per tenant.
        await store.RecordSubjectStreamsAsync("tenant-a", subject, [new StreamId("s-2"), new StreamId("s-1")], cancellationToken).ConfigureAwait(false);
        await store.RecordSubjectStreamsAsync("tenant-a", subject, [new StreamId("s-1")], cancellationToken).ConfigureAwait(false);
        await store.RecordSubjectStreamsAsync("tenant-b", subject, [new StreamId("s-9")], cancellationToken).ConfigureAwait(false);
        Check.SequenceEqual(["s-1", "s-2"], (await store.GetSubjectStreamsAsync("tenant-a", subject, cancellationToken).ConfigureAwait(false)).Select(s => s.Value), "recorded once, per tenant");
        Check.SequenceEqual(["s-9"], (await store.GetSubjectStreamsAsync("tenant-b", subject, cancellationToken).ConfigureAwait(false)).Select(s => s.Value), "another tenant's streams");
        Check.Equal(0, (await store.GetSubjectStreamsAsync("tenant-a", other, cancellationToken).ConfigureAwait(false)).Count, "no streams for another subject");
    }
}
