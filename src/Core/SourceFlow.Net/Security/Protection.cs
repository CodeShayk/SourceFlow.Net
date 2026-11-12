using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Messaging;
using SourceFlow.Pipeline;
using SourceFlow.Storage;

namespace SourceFlow.Security;

/// <summary>
/// Configures security (doc 10): the key provider and the payload protection policy.
/// <code>
/// sf.Security(s =&gt;
/// {
///     s.UseAwsKms(k =&gt; k.KeyId = "alias/accounts-messages");   // or UseAzureKeyVault / UseGoogleKms
///     s.Protect(p =&gt; p.Protect("accounts.events", "payments"));
/// });
/// </code>
/// </summary>
public sealed class SecurityBuilder
{
    internal SecurityBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>The service collection, for key provider packages.</summary>
    public IServiceCollection Services { get; }

    internal Func<IServiceProvider, IKeyProvider>? KeyProvider { get; private set; }

    internal PayloadProtectionOptions? Protection { get; private set; }

    internal bool PersonalDataEncryption { get; private set; }

    /// <summary>
    /// Encrypts <c>[PersonalData]</c> members per subject in the event store and snapshots, and makes them erasable with
    /// <see cref="IPersonalDataEraser"/> (crypto-shredding, doc 10 §4). Needs a key provider and a storage adapter that
    /// keeps data keys.
    /// </summary>
    public SecurityBuilder EncryptPersonalData()
    {
        PersonalDataEncryption = true;
        return this;
    }

    /// <summary>Uses a key provider (the cloud packages add <c>UseAwsKms</c>, <c>UseAzureKeyVault</c> and <c>UseGoogleKms</c>).</summary>
    public SecurityBuilder UseKeyProvider(Func<IServiceProvider, IKeyProvider> factory)
    {
        KeyProvider = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    /// <summary>Uses a key provider instance.</summary>
    public SecurityBuilder UseKeyProvider(IKeyProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return UseKeyProvider(_ => provider);
    }

    /// <summary>
    /// Protects message bodies sent or published to the chosen destinations (doc 10 §3), in the outbox and on the wire,
    /// and decrypts protected messages this service receives.
    /// </summary>
    public SecurityBuilder Protect(Action<PayloadProtectionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Protection ??= new PayloadProtectionOptions();
        configure(Protection);
        return this;
    }
}

/// <summary>
/// Protects outbox messages at commit (doc 10 §3, "outbox at rest"): rows for protected destinations are held until
/// <see cref="IStorageSession.FlushAsync"/> or <see cref="IStorageSession.CommitAsync"/>, encrypted (a key service call
/// only when a new DEK is due), size-checked again, and written with the session's other writes in one transaction.
/// </summary>
internal sealed class ProtectingSessionFactory(IStorageSessionFactory inner, PayloadProtector protector, Func<int> maxBodyBytes) : IStorageSessionFactory
{
    public async ValueTask<IStorageSession> BeginAsync(string tenantId, CancellationToken cancellationToken)
    {
        IStorageSession session = await inner.BeginAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return new ProtectingSession(session, protector, maxBodyBytes);
    }

    private sealed class ProtectingSession(IStorageSession inner, PayloadProtector protector, Func<int> maxBodyBytes) : IStorageSession
    {
        private readonly List<OutboxMessage> _pending = [];

        public string TenantId => inner.TenantId;

        public IDocumentSession Documents => inner.Documents;

        public DbConnection? Connection => inner.Connection;

        public DbTransaction? Transaction => inner.Transaction;

        public ValueTask<StreamSlice> ReadStreamAsync(StreamId stream, StreamVersion fromInclusive, int maxCount, CancellationToken cancellationToken)
        {
            return inner.ReadStreamAsync(stream, fromInclusive, maxCount, cancellationToken);
        }

        public void Append(StreamId stream, string streamType, ExpectedVersion expected, IReadOnlyList<EventToAppend> events)
        {
            inner.Append(stream, streamType, expected, events);
        }

        public void EnqueueOutbox(OutboxMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (message.Envelope.Protection is null && protector.Protects(message.Destination))
            {
                _pending.Add(message);
            }
            else
            {
                inner.EnqueueOutbox(message);
            }
        }

        public ValueTask<InboxEntry?> InboxSeenAsync(string consumer, MessageId messageId, CancellationToken cancellationToken)
        {
            return inner.InboxSeenAsync(consumer, messageId, cancellationToken);
        }

        public void RecordInbox(string consumer, MessageId messageId, InboxResult result)
        {
            inner.RecordInbox(consumer, messageId, result);
        }

        public void SaveCheckpoint(string subscription, int partition, GlobalPosition position, LeaseToken lease)
        {
            inner.SaveCheckpoint(subscription, partition, position, lease);
        }

        public ValueTask<ProcessState?> LoadProcessStateAsync(string processType, string processId, CancellationToken cancellationToken)
        {
            return inner.LoadProcessStateAsync(processType, processId, cancellationToken);
        }

        public void SaveProcessState(string processType, string processId, long expectedVersion, ReadOnlyMemory<byte> state, bool completed)
        {
            inner.SaveProcessState(processType, processId, expectedVersion, state, completed);
        }

        public async ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            await ProtectPendingAsync(cancellationToken).ConfigureAwait(false);
            await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<CommitResult> CommitAsync(CancellationToken cancellationToken)
        {
            await ProtectPendingAsync(cancellationToken).ConfigureAwait(false);
            return await inner.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            return inner.DisposeAsync();
        }

        private async ValueTask ProtectPendingAsync(CancellationToken cancellationToken)
        {
            foreach (OutboxMessage message in _pending)
            {
                MessageEnvelope protectedEnvelope = await protector.ProtectAsync(message.Envelope, cancellationToken).ConfigureAwait(false);
                EnvelopeLimits.ValidateBody(protectedEnvelope, maxBodyBytes());   // the ciphertext and wrapped key add bytes
                inner.EnqueueOutbox(message with { Envelope = protectedEnvelope });
            }

            _pending.Clear();
        }
    }
}

/// <summary>
/// Receive-stage middleware (doc 10 §3): decrypts a protected message before it is dispatched. A tampered or foreign
/// message fails with <see cref="DecryptionFailedException"/> (permanent: parked as <c>DecryptionFailed</c>); a key
/// service outage is transient and retried.
/// </summary>
internal sealed class PayloadProtectionMiddleware(PayloadProtector protector, string endpoint) : IMessageMiddleware
{
    public async ValueTask InvokeAsync(MessageContext context, MessageDelegate next)
    {
        if (context.Stage == PipelineStage.Receive)
        {
            if (context.Envelope.Protection is not null)
            {
                context.Envelope = await protector.UnprotectAsync(context.Envelope, context.CancellationToken).ConfigureAwait(false);
            }
            else if (protector.Options.RequireOnReceive && protector.Protects(endpoint))
            {
                throw new ProtectionRequiredException($"Message {context.Envelope.MessageId} arrived unprotected on protected endpoint '{endpoint}'.");
            }
        }

        await next(context).ConfigureAwait(false);
    }
}

/// <summary>An unprotected message arrived where protection is required (<see cref="PayloadProtectionOptions.RequireOnReceive"/>).</summary>
public sealed class ProtectionRequiredException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ProtectionRequiredException(string message)
        : base(message)
    {
    }
}
