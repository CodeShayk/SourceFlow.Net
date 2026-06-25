using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SourceFlow.Cloud.Azure.Tests.TestHelpers;

/// <summary>
/// Helper for working with Azure Resource Manager (ARM) templates in tests.
/// Provides utilities for generating and deploying ARM templates for test resources.
/// </summary>
public class ArmTemplateHelper
{
    private readonly ILogger<ArmTemplateHelper> _logger;

    public ArmTemplateHelper(ILogger<ArmTemplateHelper> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Generates an ARM template for a Service Bus namespace with queues and topics.
    /// </summary>
    public string GenerateServiceBusTemplate(ServiceBusTemplateParameters parameters)
    {
        _logger.LogInformation("Generating Service Bus ARM template for namespace: {Namespace}",
            parameters.NamespaceName);

        var template = new
        {
            schema = "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
            contentVersion = "1.0.0.0",
            parameters = new
            {
                namespaceName = new
                {
                    type = "string",
                    defaultValue = parameters.NamespaceName
                },
                location = new
                {
                    type = "string",
                    defaultValue = parameters.Location
                },
                skuName = new
                {
                    type = "string",
                    defaultValue = parameters.SkuName,
                    allowedValues = new[] { "Basic", "Standard", "Premium" }
                }
            },
            resources = new[]
            {
                new
                {
                    type = "Microsoft.ServiceBus/namespaces",
                    apiVersion = "2021-11-01",
                    name = "[parameters('namespaceName')]",
                    location = "[parameters('location')]",
                    sku = new
                    {
                        name = "[parameters('skuName')]",
                        tier = "[parameters('skuName')]"
                    },
                    properties = new { }
                }
            }
        };

        var json = JsonSerializer.Serialize(template, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        _logger.LogDebug("Generated ARM template: {Template}", json);
        return json;
    }

    /// <summary>
    /// Generates an ARM template for a Key Vault.
    /// </summary>
    public string GenerateKeyVaultTemplate(KeyVaultTemplateParameters parameters)
    {
        _logger.LogInformation("Generating Key Vault ARM template for vault: {VaultName}",
            parameters.VaultName);

        var template = new
        {
            schema = "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
            contentVersion = "1.0.0.0",
            parameters = new
            {
                vaultName = new
                {
                    type = "string",
                    defaultValue = parameters.VaultName
                },
                location = new
                {
                    type = "string",
                    defaultValue = parameters.Location
                },
                skuName = new
                {
                    type = "string",
                    defaultValue = parameters.SkuName,
                    allowedValues = new[] { "standard", "premium" }
                },
                tenantId = new
                {
                    type = "string",
                    defaultValue = parameters.TenantId
                }
            },
            resources = new[]
            {
                new
                {
                    type = "Microsoft.KeyVault/vaults",
                    apiVersion = "2021-11-01-preview",
                    name = "[parameters('vaultName')]",
                    location = "[parameters('location')]",
                    properties = new
                    {
                        tenantId = "[parameters('tenantId')]",
                        sku = new
                        {
                            family = "A",
                            name = "[parameters('skuName')]"
                        },
                        accessPolicies = Array.Empty<object>(),
                        enableRbacAuthorization = true,
                        enableSoftDelete = true,
                        softDeleteRetentionInDays = 7
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(template, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        _logger.LogDebug("Generated ARM template: {Template}", json);
        return json;
    }

    /// <summary>
    /// Generates a combined ARM template for Service Bus and Key Vault resources.
    /// </summary>
    public string GenerateCombinedTemplate(
        ServiceBusTemplateParameters serviceBusParams,
        KeyVaultTemplateParameters keyVaultParams)
    {
        _logger.LogInformation("Generating combined ARM template for Service Bus and Key Vault");

        var template = new
        {
            schema = "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
            contentVersion = "1.0.0.0",
            parameters = new
            {
                namespaceName = new
                {
                    type = "string",
                    defaultValue = serviceBusParams.NamespaceName
                },
                vaultName = new
                {
                    type = "string",
                    defaultValue = keyVaultParams.VaultName
                },
                location = new
                {
                    type = "string",
                    defaultValue = serviceBusParams.Location
                },
                serviceBusSku = new
                {
                    type = "string",
                    defaultValue = serviceBusParams.SkuName
                },
                keyVaultSku = new
                {
                    type = "string",
                    defaultValue = keyVaultParams.SkuName
                },
                tenantId = new
                {
                    type = "string",
                    defaultValue = keyVaultParams.TenantId
                }
            },
            resources = new object[]
            {
                new
                {
                    type = "Microsoft.ServiceBus/namespaces",
                    apiVersion = "2021-11-01",
                    name = "[parameters('namespaceName')]",
                    location = "[parameters('location')]",
                    sku = new
                    {
                        name = "[parameters('serviceBusSku')]",
                        tier = "[parameters('serviceBusSku')]"
                    },
                    properties = new { }
                },
                new
                {
                    type = "Microsoft.KeyVault/vaults",
                    apiVersion = "2021-11-01-preview",
                    name = "[parameters('vaultName')]",
                    location = "[parameters('location')]",
                    properties = new
                    {
                        tenantId = "[parameters('tenantId')]",
                        sku = new
                        {
                            family = "A",
                            name = "[parameters('keyVaultSku')]"
                        },
                        accessPolicies = Array.Empty<object>(),
                        enableRbacAuthorization = true,
                        enableSoftDelete = true,
                        softDeleteRetentionInDays = 7
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(template, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        _logger.LogDebug("Generated combined ARM template");
        return json;
    }

    /// <summary>
    /// Saves an ARM template to a file.
    /// </summary>
    public async Task SaveTemplateAsync(string template, string filePath)
    {
        _logger.LogInformation("Saving ARM template to: {FilePath}", filePath);

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(filePath, template);

        _logger.LogInformation("ARM template saved successfully");
    }

    /// <summary>
    /// Loads an ARM template from a file.
    /// </summary>
    public async Task<string> LoadTemplateAsync(string filePath)
    {
        _logger.LogInformation("Loading ARM template from: {FilePath}", filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"ARM template file not found: {filePath}");
        }

        var template = await File.ReadAllTextAsync(filePath);

        _logger.LogInformation("ARM template loaded successfully");
        return template;
    }
}

/// <summary>
/// Parameters for Service Bus ARM template generation.
/// </summary>
public class ServiceBusTemplateParameters
{
    /// <summary>
    /// Name of the Service Bus namespace.
    /// </summary>
    public string NamespaceName { get; set; } = string.Empty;

    /// <summary>
    /// Azure region for the namespace.
    /// </summary>
    public string Location { get; set; } = "eastus";

    /// <summary>
    /// SKU name (Basic, Standard, Premium).
    /// </summary>
    public string SkuName { get; set; } = "Standard";

    /// <summary>
    /// Queue names to create.
    /// </summary>
    public List<string> QueueNames { get; set; } = new();

    /// <summary>
    /// Topic names to create.
    /// </summary>
    public List<string> TopicNames { get; set; } = new();
}

/// <summary>
/// Parameters for Key Vault ARM template generation.
/// </summary>
public class KeyVaultTemplateParameters
{
    /// <summary>
    /// Name of the Key Vault.
    /// </summary>
    public string VaultName { get; set; } = string.Empty;

    /// <summary>
    /// Azure region for the vault.
    /// </summary>
    public string Location { get; set; } = "eastus";

    /// <summary>
    /// SKU name (standard, premium).
    /// </summary>
    public string SkuName { get; set; } = "standard";

    /// <summary>
    /// Azure AD tenant ID.
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Key names to create.
    /// </summary>
    public List<string> KeyNames { get; set; } = new();
}
