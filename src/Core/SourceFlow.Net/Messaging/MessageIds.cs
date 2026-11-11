using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SourceFlow.Messaging;

/// <summary>Message id factories: UUIDv7 for new messages, RFC 9562 UUIDv5 for idempotency keys and derived ids.</summary>
public static class MessageIds
{
    /// <summary>The namespace UUID for SourceFlow name-based ids.</summary>
    public static readonly Guid SourceFlowNamespace = new("5f1c8e2a-6d3b-4c71-9a0e-7b2d4f6a8c13");

    /// <summary>Creates a time-ordered UUIDv7 message id.</summary>
    public static MessageId New(TimeProvider time)
    {
        return MessageId.New(time);
    }

    /// <summary>
    /// The deterministic id of an idempotency key: UUIDv5 of <c>{scope}\n{key}</c> in <see cref="SourceFlowNamespace"/>.
    /// The same key in the same scope always yields the same id; a different scope yields a different id (UT-MSG-03).
    /// </summary>
    /// <param name="scope">Usually the service name.</param>
    /// <param name="key">The caller's idempotency key.</param>
    public static MessageId FromKey(string scope, string key)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrEmpty(key);
        return new MessageId(CreateVersion5(SourceFlowNamespace, scope + "\n" + key));
    }

    /// <summary>A deterministic id derived from another id and a discriminator, for example a re-send attempt.</summary>
    public static MessageId Derive(MessageId source, string discriminator)
    {
        ArgumentException.ThrowIfNullOrEmpty(discriminator);
        return new MessageId(CreateVersion5(SourceFlowNamespace, source.ToString() + "/" + discriminator));
    }

    /// <summary>Creates an RFC 9562 version 5 (SHA-1, name-based) UUID.</summary>
    public static Guid CreateVersion5(Guid namespaceId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Span<byte> namespaceBytes = stackalloc byte[16];
        if (!namespaceId.TryWriteBytes(namespaceBytes, bigEndian: true, out _))
        {
            throw new InvalidOperationException("Could not write the namespace UUID.");
        }

        int nameLength = Encoding.UTF8.GetByteCount(name);
        byte[] input = new byte[16 + nameLength];
        namespaceBytes.CopyTo(input);
        Encoding.UTF8.GetBytes(name, input.AsSpan(16));

        Span<byte> hash = stackalloc byte[20];
#pragma warning disable CA5350 // RFC 9562 version 5 is defined with SHA-1; it is not used for security here.
        SHA1.HashData(input, hash);
#pragma warning restore CA5350

        Span<byte> uuid = hash[..16];
        uuid[6] = (byte)((uuid[6] & 0x0F) | 0x50); // version 5
        uuid[8] = (byte)((uuid[8] & 0x3F) | 0x80); // RFC variant
        return new Guid(uuid, bigEndian: true);
    }

    /// <summary>Reads the version nibble of a UUID.</summary>
    public static int VersionOf(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);
        return bytes[6] >> 4;
    }

    /// <summary>Reads the Unix-epoch milliseconds of a UUIDv7.</summary>
    public static long UnixTimeMillisecondsOf(Guid version7)
    {
        Span<byte> bytes = stackalloc byte[16];
        version7.TryWriteBytes(bytes, bigEndian: true, out _);
        Span<byte> padded = stackalloc byte[8];
        bytes[..6].CopyTo(padded[2..]);
        return BinaryPrimitives.ReadInt64BigEndian(padded);
    }
}
