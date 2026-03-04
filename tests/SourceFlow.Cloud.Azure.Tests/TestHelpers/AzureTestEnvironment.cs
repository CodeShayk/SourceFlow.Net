using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Logging;

namespace SourceFlow.Cloud.Azure.Tests.TestHelpers;

public class AzureTestEnvironment : IAzureTestEnvironment
{
    private readonly AzureTestConfiguration _config;
    private readonly ILogger<AzureTestEnvironment> _logger;
    private readonly DefaultAzureCredential? _credential;

    public bool IsAzuriteEmulator => _config.UseAzurite;

    public AzureTestEnvironment(AzureTestConfiguration config, ILoggerFactory loggerFactory)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = loggerFactory.CreateLogger<AzureTestEnvironment>();

        if (!_config.UseAzurite && _config.UseManagedIdentity)
        {
            _credential = new DefaultAzureCredential();
        }
    }

    public AzureTestEnvironment(ILogger<AzureTestEnvironment> logger)
    {
        _config = AzureTestConfiguration.CreateDefault();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (!_config.UseAzurite && _config.UseManagedIdentity)
        {
            _credential = new DefaultAzureCredential();
        }
    }

    public AzureTestEnvironment(
        AzureTestConfiguration config,
        ILogger<AzureTestEnvironment> logger,
        IAzuriteManager? azuriteManager = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (!_config.UseAzurite && _config.UseManagedIdentity)
        {
            _credential = new DefaultAzureCredential();
        }
    }

    public async Task InitializeAsync()
    {
        _logger.LogInformation("Initializing Azure test environment (Azurite: {UseAzurite})", IsAzuriteEmulator);
        if (!IsAzuriteEmulator && _config.UseManagedIdentity && _credential != null)
        {
            try
            {
                var token = await _credential.GetTokenAsync(
                    new TokenRequestContext(new[] { "https://servicebus.azure.net/.default" }));
                _logger.LogInformation("Managed identity authentication successful");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Managed identity authentication failed");
            }
        }
        await Task.CompletedTask;
    }

    public async Task CleanupAsync()
    {
        _logger.LogInformation("Cleaning up Azure test environment");
        await Task.CompletedTask;
    }

    public string GetServiceBusConnectionString() => _config.ServiceBusConnectionString;
    public string GetServiceBusFullyQualifiedNamespace() => _config.FullyQualifiedNamespace;
    public string GetKeyVaultUrl() => _config.KeyVaultUrl;

    public async Task<bool> IsServiceBusAvailableAsync()
    {
        await Task.CompletedTask;
        return true;
    }

    public async Task<bool> IsKeyVaultAvailableAsync()
    {
        await Task.CompletedTask;
        return true;
    }

    public async Task<bool> IsManagedIdentityConfiguredAsync()
    {
        if (!_config.UseManagedIdentity || _credential == null) return false;
        try
        {
            var token = await _credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://vault.azure.net/.default" }));
            return !string.IsNullOrEmpty(token.Token);
        }
        catch { return false; }
    }

    public async Task<TokenCredential> GetAzureCredentialAsync()
    {
        await Task.CompletedTask;
        return _credential ?? new DefaultAzureCredential();
    }

    public async Task<Dictionary<string, string>> GetEnvironmentMetadataAsync()
    {
        await Task.CompletedTask;
        return new Dictionary<string, string>
        {
            ["Environment"] = IsAzuriteEmulator ? "Azurite" : "Azure",
            ["ServiceBusNamespace"] = _config.FullyQualifiedNamespace,
            ["KeyVaultUrl"] = _config.KeyVaultUrl,
            ["UseManagedIdentity"] = _config.UseManagedIdentity.ToString(),
            ["Timestamp"] = DateTimeOffset.UtcNow.ToString("O")
        };
    }

    public ServiceBusClient CreateServiceBusClient() => 
        new ServiceBusClient(GetServiceBusConnectionString());

    public ServiceBusAdministrationClient CreateServiceBusAdministrationClient() => 
        new ServiceBusAdministrationClient(GetServiceBusConnectionString());

    public KeyClient CreateKeyClient() => 
        new KeyClient(new Uri(GetKeyVaultUrl()), GetAzureCredential());

    public SecretClient CreateSecretClient() => 
        new SecretClient(new Uri(GetKeyVaultUrl()), GetAzureCredential());

    public TokenCredential GetAzureCredential() => 
        _credential ?? new DefaultAzureCredential();

    public bool HasServiceBusPermissions() => 
        !string.IsNullOrEmpty(_config.ServiceBusConnectionString) || _config.UseManagedIdentity;

    public bool HasKeyVaultPermissions() => 
        !string.IsNullOrEmpty(_config.KeyVaultUrl);
}
