using System.Text.Json;
using SourceFlow.Cloud.Core.Security;

namespace SourceFlow.Cloud.Integration.Tests.TestHelpers;

/// <summary>
/// Helper utilities for security testing across cloud providers
/// </summary>
public class SecurityTestHelpers
{
    /// <summary>
    /// Validate that sensitive data is properly masked in logs
    /// </summary>
    public bool ValidateSensitiveDataMasking(string logOutput, string[] sensitiveValues)
    {
        foreach (var sensitiveValue in sensitiveValues)
        {
            if (logOutput.Contains(sensitiveValue))
            {
                return false; // Sensitive data found in logs
            }
        }
        return true;
    }
    
    /// <summary>
    /// Create test message with sensitive data
    /// </summary>
    public TestMessageWithSensitiveData CreateTestMessageWithSensitiveData()
    {
        return new TestMessageWithSensitiveData
        {
            Id = Guid.NewGuid(),
            PublicData = "This is public information",
            SensitiveData = "This is sensitive information that should be encrypted",
            CreditCardNumber = "4111-1111-1111-1111",
            SocialSecurityNumber = "123-45-6789"
        };
    }
    
    /// <summary>
    /// Validate encryption round-trip consistency
    /// </summary>
    public async Task<bool> ValidateEncryptionRoundTripAsync(
        IMessageEncryption encryption, 
        string originalMessage)
    {
        try
        {
            var encrypted = await encryption.EncryptAsync(originalMessage);
            var decrypted = await encryption.DecryptAsync(encrypted);
            
            return originalMessage == decrypted;
        }
        catch
        {
            return false;
        }
    }
    
    /// <summary>
    /// Validate that encrypted data is different from original
    /// </summary>
    public async Task<bool> ValidateDataIsEncryptedAsync(
        IMessageEncryption encryption,
        string originalMessage)
    {
        try
        {
            var encrypted = await encryption.EncryptAsync(originalMessage);
            return encrypted != originalMessage && !string.IsNullOrEmpty(encrypted);
        }
        catch
        {
            return false;
        }
    }
    
    /// <summary>
    /// Create security test result
    /// </summary>
    public SecurityTestResult CreateSecurityTestResult(
        string testName,
        bool encryptionWorking,
        bool sensitiveDataMasked,
        bool accessControlValid,
        List<SecurityViolation>? violations = null)
    {
        return new SecurityTestResult
        {
            TestName = testName,
            EncryptionWorking = encryptionWorking,
            SensitiveDataMasked = sensitiveDataMasked,
            AccessControlValid = accessControlValid,
            Violations = violations ?? new List<SecurityViolation>()
        };
    }
    
    /// <summary>
    /// Validate access control by attempting unauthorized operations
    /// </summary>
    public async Task<bool> ValidateAccessControlAsync(Func<Task> unauthorizedOperation)
    {
        try
        {
            await unauthorizedOperation();
            return false; // Should have thrown an exception
        }
        catch (UnauthorizedAccessException)
        {
            return true; // Expected exception
        }
        catch (Exception ex) when (ex.Message.Contains("Unauthorized") || 
                                   ex.Message.Contains("Forbidden") ||
                                   ex.Message.Contains("Access denied"))
        {
            return true; // Expected authorization failure
        }
        catch
        {
            return false; // Unexpected exception
        }
    }
}

/// <summary>
/// Test message with sensitive data for security testing
/// </summary>
public class TestMessageWithSensitiveData
{
    /// <summary>
    /// Message ID
    /// </summary>
    public Guid Id { get; set; }
    
    /// <summary>
    /// Public data that doesn't need encryption
    /// </summary>
    public string PublicData { get; set; } = "";
    
    /// <summary>
    /// Sensitive data that should be encrypted
    /// </summary>
    [SensitiveData]
    public string SensitiveData { get; set; } = "";
    
    /// <summary>
    /// Credit card number that should be encrypted
    /// </summary>
    [SensitiveData]
    public string CreditCardNumber { get; set; } = "";
    
    /// <summary>
    /// Social security number that should be encrypted
    /// </summary>
    [SensitiveData]
    public string SocialSecurityNumber { get; set; } = "";
}

/// <summary>
/// Security test result
/// </summary>
public class SecurityTestResult
{
    /// <summary>
    /// Test name
    /// </summary>
    public string TestName { get; set; } = "";
    
    /// <summary>
    /// Whether encryption is working correctly
    /// </summary>
    public bool EncryptionWorking { get; set; }
    
    /// <summary>
    /// Whether sensitive data is properly masked
    /// </summary>
    public bool SensitiveDataMasked { get; set; }
    
    /// <summary>
    /// Whether access control is working correctly
    /// </summary>
    public bool AccessControlValid { get; set; }
    
    /// <summary>
    /// List of security violations found
    /// </summary>
    public List<SecurityViolation> Violations { get; set; } = new();
}

/// <summary>
/// Security violation details
/// </summary>
public class SecurityViolation
{
    /// <summary>
    /// Type of violation
    /// </summary>
    public string Type { get; set; } = "";
    
    /// <summary>
    /// Description of the violation
    /// </summary>
    public string Description { get; set; } = "";
    
    /// <summary>
    /// Severity level
    /// </summary>
    public string Severity { get; set; } = "";
    
    /// <summary>
    /// Recommendation for fixing the violation
    /// </summary>
    public string Recommendation { get; set; } = "";
}