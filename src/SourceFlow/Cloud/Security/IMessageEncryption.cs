using System;
using System.Threading;
using System.Threading.Tasks;

namespace SourceFlow.Cloud.Security;

/// <summary>
/// Provides message encryption and decryption capabilities
/// </summary>
public interface IMessageEncryption
{
    /// <summary>
    /// Encrypts plaintext message
    /// </summary>
    Task<string> EncryptAsync(string plaintext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decrypts ciphertext message
    /// </summary>
    Task<string> DecryptAsync(string ciphertext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the encryption algorithm name
    /// </summary>
    string AlgorithmName { get; }

    /// <summary>
    /// Gets the key identifier used for encryption
    /// </summary>
    string KeyIdentifier { get; }
}
