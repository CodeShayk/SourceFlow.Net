using System;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SourceFlow.Cloud.Security;

/// <summary>
/// Masks sensitive data in objects for logging
/// </summary>
public class SensitiveDataMasker
{
    private readonly JsonSerializerOptions _jsonOptions;

    public SensitiveDataMasker(JsonSerializerOptions? jsonOptions = null)
    {
        _jsonOptions = jsonOptions ?? new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <summary>
    /// Masks sensitive data in an object
    /// </summary>
    public string Mask(object? obj)
    {
        if (obj == null) return "null";

        // Serialize to JSON
        var json = JsonSerializer.Serialize(obj, _jsonOptions);

        // Parse JSON
        using var doc = JsonDocument.Parse(json);

        // Mask sensitive fields
        var masked = MaskJsonElement(doc.RootElement, obj.GetType());

        return masked;
    }

    private string MaskJsonElement(JsonElement element, Type objectType)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var sb = new StringBuilder();
            sb.Append('{');

            bool first = true;
            foreach (var property in element.EnumerateObject())
            {
                if (!first) sb.Append(',');
                first = false;

                sb.Append('"').Append(property.Name).Append("\":");

                // Find corresponding property in type
                var propInfo = FindProperty(objectType, property.Name);
                var sensitiveAttr = propInfo?.GetCustomAttribute<SensitiveDataAttribute>();

                if (sensitiveAttr != null)
                {
                    // Mask based on type
                    var maskedValue = MaskValue(property.Value.ToString(), sensitiveAttr.Type);
                    sb.Append('"').Append(maskedValue).Append('"');
                }
                else if (property.Value.ValueKind == JsonValueKind.Object && propInfo != null)
                {
                    // Recursively mask nested objects
                    sb.Append(MaskJsonElement(property.Value, propInfo.PropertyType));
                }
                else if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    sb.Append(property.Value.GetRawText());
                }
                else
                {
                    sb.Append(property.Value.GetRawText());
                }
            }

            sb.Append('}');
            return sb.ToString();
        }

        return element.GetRawText();
    }

    private PropertyInfo? FindProperty(Type type, string jsonPropertyName)
    {
        // Try direct match first
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        // Try case-insensitive match
        return props.FirstOrDefault(p =>
            string.Equals(p.Name, jsonPropertyName, StringComparison.OrdinalIgnoreCase));
    }

    private string MaskValue(string value, SensitiveDataType type)
    {
        return type switch
        {
            SensitiveDataType.CreditCard => MaskCreditCard(value),
            SensitiveDataType.Email => MaskEmail(value),
            SensitiveDataType.PhoneNumber => MaskPhoneNumber(value),
            SensitiveDataType.SSN => MaskSSN(value),
            SensitiveDataType.PersonalName => MaskPersonalName(value),
            SensitiveDataType.IPAddress => MaskIPAddress(value),
            SensitiveDataType.Password => "********",
            SensitiveDataType.ApiKey => MaskApiKey(value),
            _ => "***REDACTED***"
        };
    }

    private string MaskCreditCard(string value)
    {
        // Show last 4 digits: ************1234
        var digits = Regex.Replace(value, @"\D", "");
        if (digits.Length >= 4)
        {
            return new string('*', digits.Length - 4) + digits.Substring(digits.Length - 4);
        }
        return new string('*', value.Length);
    }

    private string MaskEmail(string value)
    {
        // Show domain only: ***@example.com
        var parts = value.Split('@');
        if (parts.Length == 2)
        {
            return "***@" + parts[1];
        }
        return "***@***.***";
    }

    private string MaskPhoneNumber(string value)
    {
        // Show last 4 digits: ***-***-1234
        var digits = Regex.Replace(value, @"\D", "");
        if (digits.Length >= 4)
        {
            return "***-***-" + digits.Substring(digits.Length - 4);
        }
        return "***-***-****";
    }

    private string MaskSSN(string value)
    {
        // Show last 4 digits: ***-**-1234
        var digits = Regex.Replace(value, @"\D", "");
        if (digits.Length >= 4)
        {
            return "***-**-" + digits.Substring(digits.Length - 4);
        }
        return "***-**-****";
    }

    private string MaskPersonalName(string value)
    {
        // Show first letter only: J*** D***
        var parts = value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts.Select(p => p.Length > 0 ? p[0] + new string('*', Math.Max(0, p.Length - 1)) : "*"));
    }

    private string MaskIPAddress(string value)
    {
        // Show first octet: 192.*.*.*
        var parts = value.Split('.');
        if (parts.Length == 4)
        {
            return $"{parts[0]}.*.*.*";
        }
        return "*.*.*.*";
    }

    private string MaskApiKey(string value)
    {
        // Show first 4 and last 4 characters: abcd...xyz9
        if (value.Length > 8)
        {
            return value.Substring(0, 4) + "..." + value.Substring(value.Length - 4);
        }
        return "********";
    }
}
