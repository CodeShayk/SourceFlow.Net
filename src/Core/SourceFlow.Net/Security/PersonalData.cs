using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using SourceFlow.Runtime;
using SourceFlow.Storage;

namespace SourceFlow.Security;

/// <summary>
/// A subject's data key (doc 10 §4): wrapped by the key provider; destroyed when the subject is erased. Keys belong to a
/// tenant: the same subject id in two tenants has two keys, and erasing it in one tenant leaves the other untouched.
/// </summary>
/// <param name="TenantId">The tenant (ADR-022); <see cref="Messaging.MessageEnvelope.DefaultTenant"/> when single-tenant.</param>
/// <param name="SubjectId">The subject, for example a customer id.</param>
/// <param name="KeyReference">The master key that wrapped the DEK.</param>
/// <param name="WrappedKey">The wrapped DEK; empty once shredded.</param>
/// <param name="CreatedAt">When the key was created.</param>
/// <param name="ShreddedAt">When the subject was erased; null while the key exists.</param>
public sealed record SubjectKey(string TenantId, string SubjectId, string KeyReference, ReadOnlyMemory<byte> WrappedKey, DateTimeOffset CreatedAt, DateTimeOffset? ShreddedAt)
{
    /// <summary>True once the subject was erased: its personal data can never be decrypted again.</summary>
    public bool IsShredded => ShreddedAt is not null;
}

/// <summary>
/// Stores subjects' data keys and which streams hold their personal data (doc 10 §4; tables <c>data_keys</c> and
/// <c>subject_streams</c>). Implemented by the storage adapters.
/// </summary>
public interface IPersonalDataKeyStore
{
    /// <summary>The tenant's key for the subject, shredded or not; null when the subject has none in that tenant.</summary>
    ValueTask<SubjectKey?> GetKeyAsync(string tenantId, string subjectId, CancellationToken cancellationToken);

    /// <summary>Stores a new key unless the subject already has one in its tenant; returns the stored key (the first writer wins).</summary>
    ValueTask<SubjectKey> AddKeyAsync(SubjectKey key, CancellationToken cancellationToken);

    /// <summary>
    /// Destroys the tenant's wrapped key for the subject and marks the subject erased in that tenant; a subject without a
    /// key gets a shredded record. Other tenants' keys for the same subject id are untouched.
    /// </summary>
    ValueTask ShredKeyAsync(string tenantId, string subjectId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Records that <paramref name="streams"/> hold personal data of the subject (idempotent).</summary>
    ValueTask RecordSubjectStreamsAsync(string tenantId, string subjectId, IReadOnlyCollection<StreamId> streams, CancellationToken cancellationToken);

    /// <summary>The streams that hold personal data of the subject.</summary>
    ValueTask<IReadOnlyList<StreamId>> GetSubjectStreamsAsync(string tenantId, string subjectId, CancellationToken cancellationToken);
}

/// <summary>Erases a subject's personal data by destroying its key (crypto-shredding, doc 10 §4; GDPR Art. 17).</summary>
public interface IPersonalDataEraser
{
    /// <summary>
    /// Erases the subject: (1) destroys its data key, so every <c>[PersonalData]</c> member encrypted for it reads as
    /// erased; (2) deletes snapshots of the streams that hold its data and evicts them from the aggregate cache; (3) scrubs
    /// the <c>[PersonalData]</c> members of documents built from those streams; (4) appends
    /// <see cref="PersonalDataErased"/>, so custom projections can scrub their own read models.
    /// </summary>
    ValueTask<ErasureResult> EraseSubjectAsync(string subjectId, string? tenantId = null, CancellationToken cancellationToken = default);
}

/// <summary>What an erasure did.</summary>
public sealed record ErasureResult(string SubjectId, int Streams, int SnapshotsDeleted, int DocumentsScrubbed);

/// <summary>
/// The system event appended when a subject is erased (doc 10 §4), on stream <c>personal-data-{subject}</c>. Projections
/// that keep personal data outside the built-in document projections handle it to scrub their read models.
/// </summary>
/// <param name="Subject">The erased subject.</param>
public sealed record PersonalDataErased(string Subject) : IEvent
{
    /// <summary>The CloudEvents type.</summary>
    public const string TypeName = "sourceflow.personal-data.erased";
}

/// <summary>Registers <see cref="PersonalDataErased"/> when personal data encryption is on.</summary>
internal sealed class PersonalDataModule : ISourceFlowModule
{
    public static string ModuleName => "SourceFlow.PersonalData";

    public static void Register(ISourceFlowModuleBuilder builder)
    {
        builder.AddMessage<PersonalDataErased>(PersonalDataErased.TypeName, 1);
        builder.AddJsonTypeInfoResolver(PersonalDataJsonContext.Default);
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(PersonalDataErased))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class PersonalDataJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

/// <summary>The <c>[PersonalData]</c> members of a type, by JSON path, with the JSON name of each member's subject.</summary>
internal sealed record PersonalDataPlan(IReadOnlyList<PersonalDataMember> Members)
{
    public static readonly PersonalDataPlan None = new([]);

    public bool IsEmpty => Members.Count == 0;
}

/// <summary>A <c>[PersonalData]</c> member: its JSON path and the JSON path of its subject (null: the stream is the subject).</summary>
internal sealed record PersonalDataMember(IReadOnlyList<string> Path, IReadOnlyList<string>? SubjectPath);

/// <summary>
/// Crypto-shredding of <c>[PersonalData]</c> members (doc 10 §4, WP-5.2). On write, each member's JSON value is encrypted
/// with its tenant's DEK for its subject (AES-256-GCM, associated data <c>tenant|subject|path</c>) and replaced by
/// <c>{"$pd":"v2:{tenant}:{subject}:{kind}:{nonce|tag|ciphertext}"}</c>; on read, markers are decrypted again. Markers of
/// the earlier <c>v1</c> format (no tenant, associated data <c>subject|path</c>) read with the default tenant's key. Once the subject is
/// erased its key is gone, and each marker reads as erased: <c>"[erased]"</c> for strings, 0, false or null otherwise.
/// </summary>
/// <remarks>
/// The subject of a member is the member named by <see cref="PersonalDataAttribute.Subject"/>, or the event's stream when
/// it names none. Keys are created on first use, wrapped by the <see cref="IKeyProvider"/>, and cached unwrapped (zeroed
/// on eviction). Writing personal data of an erased subject stores the erased value: nothing new is kept.
/// </remarks>
public sealed class PersonalDataProtector : IDisposable
{
    /// <summary>The value an erased string member reads as.</summary>
    public const string Erased = "[erased]";

    private const string Marker = "$pd";
    private const int CacheSize = 10_000;
    private static readonly byte[] MarkerBytes = Encoding.UTF8.GetBytes("\"$pd\"");

    private readonly IKeyProvider _provider;
    private readonly IPersonalDataKeyStore _store;
    private readonly RuntimeModel _model;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<Type, PersonalDataPlan> _plans = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Tenant, string Subject), CachedKey> _keys = [];

    /// <summary>Creates the protector.</summary>
    public PersonalDataProtector(IKeyProvider provider, IPersonalDataKeyStore store, RuntimeModel model, TimeProvider? time = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>True when <paramref name="type"/> has <c>[PersonalData]</c> members.</summary>
    public bool Protects(Type type)
    {
        return !Plan(type).IsEmpty;
    }

    /// <summary>
    /// Encrypts the <c>[PersonalData]</c> members of a serialized <paramref name="type"/> with <paramref name="tenantId"/>'s
    /// keys; returns the body and the subjects it holds personal data of. A type without such members is returned unchanged.
    /// </summary>
    public async ValueTask<(ReadOnlyMemory<byte> Body, IReadOnlyCollection<string> Subjects)> ProtectAsync(Type type, string tenantId, string stream, ReadOnlyMemory<byte> json, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        PersonalDataPlan plan = Plan(type);
        if (plan.IsEmpty)
        {
            return (json, []);
        }

        JsonNode? root = JsonNode.Parse(json.Span);
        if (root is not JsonObject document)
        {
            return (json, []);
        }

        var subjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (PersonalDataMember member in plan.Members)
        {
            (JsonObject? parent, string name) = Locate(document, member.Path);
            if (parent is null || !parent.TryGetPropertyValue(name, out JsonNode? value) || value is null || IsMarker(value))
            {
                continue;
            }

            string subject = member.SubjectPath is { } subjectPath && Read(document, subjectPath) is { Length: > 0 } named ? named : stream;
            subjects.Add(subject);
            KeyState key = await KeyAsync(tenantId, subject, create: true, cancellationToken).ConfigureAwait(false);
            char kind = KindOf(value);
            if (key.Shredded)
            {
                parent[name] = ErasedValue(kind);   // an erased subject: keep nothing new
                continue;
            }

            byte[] plaintext = Encoding.UTF8.GetBytes(value.ToJsonString());
            byte[] payload = new byte[12 + 16 + plaintext.Length];
            RandomNumberGenerator.Fill(payload.AsSpan(0, 12));
            using (AesGcm aes = key.Aes!)
            {
                aes.Encrypt(payload.AsSpan(0, 12), plaintext, payload.AsSpan(28), payload.AsSpan(12, 16), AssociatedData(tenantId, subject, member.Path));
            }

            CryptographicOperations.ZeroMemory(plaintext);
            parent[name] = new JsonObject { [Marker] = $"v2:{Encode(tenantId)}:{Encode(subject)}:{kind}:{Convert.ToBase64String(payload)}" };
        }

        return (Encoding.UTF8.GetBytes(document.ToJsonString()), subjects);
    }

    /// <summary>Decrypts every personal data marker in a JSON body; erased subjects' members read as erased values.</summary>
    /// <exception cref="DecryptionFailedException">A marker is malformed or was altered.</exception>
    /// <exception cref="JsonException">The body is not valid JSON (or not valid UTF-8).</exception>
    public async ValueTask<ReadOnlyMemory<byte>> UnprotectAsync(ReadOnlyMemory<byte> json, CancellationToken cancellationToken)
    {
        if (json.Span.IndexOf(MarkerBytes) < 0)
        {
            return json;   // the common case: no personal data, no parse
        }

        // JsonNode transcodes strings lazily, so invalid UTF-8 would surface later as InvalidOperationException (WP-5.5).
        if (!System.Text.Unicode.Utf8.IsValid(json.Span))
        {
            throw new JsonException("The body holding personal data is not valid UTF-8.");
        }

        try
        {
            JsonNode? root = JsonNode.Parse(json.Span);
            if (root is null)
            {
                return json;
            }

            await RestoreAsync(root, [], cancellationToken).ConfigureAwait(false);
            return Encoding.UTF8.GetBytes(root.ToJsonString());
        }
        catch (InvalidOperationException e)
        {
            // JsonNode reads strings lazily: an escape that isn't valid UTF-16 (a lone surrogate) surfaces here (WP-5.5).
            throw new JsonException("The body holding personal data is not valid JSON: " + e.Message, e);
        }
    }

    /// <summary>Unprotects a stored event's data.</summary>
    public async ValueTask<StoredEvent> UnprotectAsync(StoredEvent stored, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ReadOnlyMemory<byte> data = await UnprotectAsync(stored.Data, cancellationToken).ConfigureAwait(false);
        return data.Equals(stored.Data) ? stored : stored with { Data = data };
    }

    /// <summary>Replaces the <c>[PersonalData]</c> members of a serialized <paramref name="type"/> with erased values (document scrubbing).</summary>
    public ReadOnlyMemory<byte> Scrub(Type type, ReadOnlyMemory<byte> json)
    {
        PersonalDataPlan plan = Plan(type);
        if (plan.IsEmpty || JsonNode.Parse(json.Span) is not JsonObject document)
        {
            return json;
        }

        foreach (PersonalDataMember member in plan.Members)
        {
            (JsonObject? parent, string name) = Locate(document, member.Path);
            if (parent is not null && parent.TryGetPropertyValue(name, out JsonNode? value) && value is not null)
            {
                parent[name] = ErasedValue(KindOf(value));
            }
        }

        return Encoding.UTF8.GetBytes(document.ToJsonString());
    }

    /// <summary>Forgets the tenant's cached key for a subject (after erasure).</summary>
    public void Evict(string tenantId, string subjectId)
    {
        lock (_gate)
        {
            if (_keys.Remove((tenantId, subjectId), out CachedKey? key) && key.Key is { } bytes)
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    /// <summary>Zeroes every cached key.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (CachedKey key in _keys.Values)
            {
                if (key.Key is { } bytes)
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }

            _keys.Clear();
        }
    }

    internal PersonalDataPlan Plan(Type type)
    {
        return _plans.GetOrAdd(type, t =>
        {
            if (!_model.TryGetJsonTypeInfo(t, out JsonTypeInfo? info))
            {
                return PersonalDataPlan.None;
            }

            var members = new List<PersonalDataMember>();
            Collect(info, [], [], members, 0);
            return members.Count == 0 ? PersonalDataPlan.None : new PersonalDataPlan(members);
        });
    }

    // [PersonalData] members of an object and its nested objects; a member's subject is resolved in its own object first,
    // then in the enclosing objects; none means the stream is the subject.
    private void Collect(JsonTypeInfo info, IReadOnlyList<string> prefix, IReadOnlyList<(IReadOnlyList<string> Prefix, JsonTypeInfo Info)> enclosing, List<PersonalDataMember> members, int depth)
    {
        if (info.Kind != JsonTypeInfoKind.Object || depth > 4)
        {
            return;
        }

        foreach (JsonPropertyInfo property in info.Properties)
        {
            IReadOnlyList<string> path = [.. prefix, property.Name];
            if (property.AttributeProvider?.GetCustomAttributes(typeof(PersonalDataAttribute), inherit: true).OfType<PersonalDataAttribute>().FirstOrDefault() is { } personal)
            {
                IReadOnlyList<string>? subject = null;
                if (personal.Subject is { } subjectMember)
                {
                    foreach ((IReadOnlyList<string> scopePrefix, JsonTypeInfo scope) in enclosing.Prepend((prefix, info)))
                    {
                        if (scope.Properties.FirstOrDefault(p => MemberName(p) == subjectMember) is { } found)
                        {
                            subject = [.. scopePrefix, found.Name];
                            break;
                        }
                    }

                    if (subject is null)
                    {
                        throw new InvalidOperationException($"[PersonalData(Subject = \"{subjectMember}\")] on {info.Type.Name}.{MemberName(property)}: there is no member '{subjectMember}' (SF0011).");
                    }
                }

                members.Add(new PersonalDataMember(path, subject));
                continue;
            }

            Type nested = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (!nested.IsPrimitive && nested != typeof(string) && nested != typeof(decimal) && !typeof(IStronglyTypedId).IsAssignableFrom(nested)
                && _model.TryGetJsonTypeInfo(nested, out JsonTypeInfo? child) && child.Kind == JsonTypeInfoKind.Object)
            {
                Collect(child, path, [(prefix, info), .. enclosing], members, depth + 1);
            }
        }
    }

    private static string MemberName(JsonPropertyInfo property)
    {
        return property.AttributeProvider is MemberInfo member ? member.Name : property.Name;
    }

    private async ValueTask RestoreAsync(JsonNode node, List<string> path, CancellationToken cancellationToken)
    {
        if (node is JsonObject obj)
        {
            foreach (KeyValuePair<string, JsonNode?> property in obj.ToArray())
            {
                if (property.Value is null)
                {
                    continue;
                }

                path.Add(property.Key);
                if (IsMarker(property.Value))
                {
                    obj[property.Key] = await DecryptAsync((string)property.Value[Marker]!, path, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await RestoreAsync(property.Value, path, cancellationToken).ConfigureAwait(false);
                }

                path.RemoveAt(path.Count - 1);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                if (item is not null)
                {
                    await RestoreAsync(item, path, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async ValueTask<JsonNode?> DecryptAsync(string marker, IReadOnlyList<string> path, CancellationToken cancellationToken)
    {
        // v2:{tenant}:{subject}:{kind}:{payload}; v1:{subject}:{kind}:{payload} (before keys were per tenant: the default tenant's).
        string[] parts = marker.Split(':');
        bool v2 = parts.Length == 5 && parts[0] == "v2";
        int offset = v2 ? 1 : 0;
        if ((!v2 && (parts.Length != 4 || parts[0] != "v1")) || parts[2 + offset].Length != 1)
        {
            throw new DecryptionFailedException("A personal data marker is malformed.");
        }

        string tenant = v2 ? Decode(parts[1]) : Messaging.MessageEnvelope.DefaultTenant;
        string subject = Decode(parts[1 + offset]);
        char kind = parts[2 + offset][0];
        KeyState key = await KeyAsync(tenant, subject, create: false, cancellationToken).ConfigureAwait(false);
        if (key.Shredded)
        {
            return ErasedValue(kind);
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(parts[3 + offset]);
        }
        catch (FormatException e)
        {
            throw new DecryptionFailedException("A personal data marker is malformed.", e);
        }

        if (payload.Length < 28)
        {
            throw new DecryptionFailedException("A personal data marker is truncated.");
        }

        byte[] plaintext = new byte[payload.Length - 28];
        try
        {
            using AesGcm aes = key.Aes!;
            aes.Decrypt(payload.AsSpan(0, 12), payload.AsSpan(28), payload.AsSpan(12, 16), plaintext, v2 ? AssociatedData(tenant, subject, path) : AssociatedData(subject, path));
            return JsonNode.Parse(plaintext);
        }
        catch (CryptographicException e)
        {
            throw new DecryptionFailedException($"Personal data at '{string.Join('.', path)}' was altered or moved (authentication failed).", e);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    // The tenant's key for the subject as an AesGcm instance of its own (so eviction never races an operation), or Shredded.
    private async ValueTask<KeyState> KeyAsync(string tenant, string subject, bool create, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_keys.TryGetValue((tenant, subject), out CachedKey? cached))
            {
                return cached.Key is { } bytes ? new KeyState(new AesGcm(bytes, 16), false) : new KeyState(null, true);
            }
        }

        SubjectKey? stored = await _store.GetKeyAsync(tenant, subject, cancellationToken).ConfigureAwait(false);
        if (stored is null && !create)
        {
            return new KeyState(null, true);   // no key ever made for this subject: nothing can be decrypted
        }

        if (stored is null)
        {
            using DataKey fresh = await _provider.GenerateDataKeyAsync(cancellationToken).ConfigureAwait(false);
            stored = await _store.AddKeyAsync(new SubjectKey(tenant, subject, fresh.KeyReference, fresh.Wrapped, _time.GetUtcNow(), null), cancellationToken).ConfigureAwait(false);
        }

        byte[]? key = null;
        if (!stored.IsShredded)
        {
            byte[] unwrapped = await _provider.UnwrapDataKeyAsync(stored.KeyReference, stored.WrappedKey, cancellationToken).ConfigureAwait(false);
            key = GC.AllocateArray<byte>(32, pinned: true);
            unwrapped.AsSpan(0, 32).CopyTo(key);
            CryptographicOperations.ZeroMemory(unwrapped);
        }

        lock (_gate)
        {
            while (_keys.Count >= CacheSize)
            {
                (string oldestTenant, string oldestSubject) = _keys.MinBy(k => k.Value.CachedAt).Key;
                Evict(oldestTenant, oldestSubject);
            }

            _keys[(tenant, subject)] = new CachedKey(key, _time.GetUtcNow());
            return key is null ? new KeyState(null, true) : new KeyState(new AesGcm(key, 16), false);
        }
    }

    private static (JsonObject? Parent, string Name) Locate(JsonObject root, IReadOnlyList<string> path)
    {
        JsonObject current = root;
        for (int i = 0; i < path.Count - 1; i++)
        {
            if (!current.TryGetPropertyValue(path[i], out JsonNode? next) || next is not JsonObject nested)
            {
                return (null, path[^1]);
            }

            current = nested;
        }

        return (current, path[^1]);
    }

    private static string? Read(JsonObject root, IReadOnlyList<string> path)
    {
        (JsonObject? parent, string name) = Locate(root, path);
        return parent is not null && parent.TryGetPropertyValue(name, out JsonNode? value) && value is JsonValue scalar
            ? scalar.GetValueKind() == JsonValueKind.String ? scalar.GetValue<string>() : scalar.ToJsonString()
            : null;
    }

    private static bool IsMarker(JsonNode node)
    {
        return node is JsonObject { Count: 1 } obj && obj.TryGetPropertyValue(Marker, out JsonNode? value) && value is JsonValue;
    }

    private static char KindOf(JsonNode value)
    {
        return value.GetValueKind() switch
        {
            JsonValueKind.String => 's',
            JsonValueKind.Number => 'n',
            JsonValueKind.True or JsonValueKind.False => 'b',
            _ => 'o',
        };
    }

    private static JsonNode? ErasedValue(char kind)
    {
        return kind switch
        {
            's' => JsonValue.Create(Erased),
            'n' => JsonValue.Create(0),
            'b' => JsonValue.Create(false),
            _ => null,
        };
    }

    private static byte[] AssociatedData(string tenant, string subject, IReadOnlyList<string> path)
    {
        return Encoding.UTF8.GetBytes(tenant + "|" + subject + "|" + string.Join('.', path));
    }

    // The associated data of the v1 format, which had no tenant.
    private static byte[] AssociatedData(string subject, IReadOnlyList<string> path)
    {
        return Encoding.UTF8.GetBytes(subject + "|" + string.Join('.', path));
    }

    private static string Encode(string subject)
    {
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(subject));
    }

    private static string Decode(string encoded)
    {
        try
        {
            return Encoding.UTF8.GetString(Base64Url.DecodeFromChars(encoded));
        }
        catch (FormatException e)
        {
            throw new DecryptionFailedException("A personal data marker names no valid subject.", e);
        }
    }

    private readonly record struct KeyState(AesGcm? Aes, bool Shredded);

    private sealed record CachedKey(byte[]? Key, DateTimeOffset CachedAt);
}
