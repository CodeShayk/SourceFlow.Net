using System;

namespace SourceFlow.Cloud.Security;

/// <summary>
/// Marks a property as containing sensitive data that should be masked in logs
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class SensitiveDataAttribute : Attribute
{
    /// <summary>
    /// Type of sensitive data
    /// </summary>
    public SensitiveDataType Type { get; set; } = SensitiveDataType.Custom;

    /// <summary>
    /// Custom masking pattern (if Type is Custom)
    /// </summary>
    public string? MaskingPattern { get; set; }

    public SensitiveDataAttribute()
    {
    }

    public SensitiveDataAttribute(SensitiveDataType type)
    {
        Type = type;
    }
}

/// <summary>
/// Types of sensitive data
/// </summary>
public enum SensitiveDataType
{
    /// <summary>
    /// Credit card number
    /// </summary>
    CreditCard,

    /// <summary>
    /// Email address
    /// </summary>
    Email,

    /// <summary>
    /// Phone number
    /// </summary>
    PhoneNumber,

    /// <summary>
    /// Social Security Number
    /// </summary>
    SSN,

    /// <summary>
    /// Personal name
    /// </summary>
    PersonalName,

    /// <summary>
    /// IP Address
    /// </summary>
    IPAddress,

    /// <summary>
    /// Password or secret
    /// </summary>
    Password,

    /// <summary>
    /// API Key or token
    /// </summary>
    ApiKey,

    /// <summary>
    /// Custom masking
    /// </summary>
    Custom
}
