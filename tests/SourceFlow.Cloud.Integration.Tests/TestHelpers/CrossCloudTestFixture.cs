using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;

namespace SourceFlow.Cloud.Integration.Tests.TestHelpers;

/// <summary>
/// Test fixture for cross-cloud integration testing
/// Manages both AWS and Azure test environments
/// </summary>
public class CrossCloudTestFixture : IAsyncLifetime
{
    private readonly CloudIntegrationTestConfiguration _configuration;
    private LocalStackTestFixture? _awsFixture;
    private IServiceProvider? _serviceProvider;
    
    public CrossCloudTestFixture()
    {
        // Load configuration from appsettings.json
        var configBuilder = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables();
        
        var config = configBuilder.Build();
        _configuration = new CloudIntegrationTestConfiguration();
        config.GetSection("CloudIntegrationTests").Bind(_configuration);
    }
    
    /// <summary>
    /// Test configuration
    /// </summary>
    public CloudIntegrationTestConfiguration Configuration => _configuration;
    
    /// <summary>
    /// AWS test fixture
    /// </summary>
    public LocalStackTestFixture? AwsFixture => _awsFixture;
    
    /// <summary>
    /// Service provider with both AWS and Azure services configured
    /// </summary>
    public IServiceProvider ServiceProvider => _serviceProvider ?? throw new InvalidOperationException("Fixture not initialized");
    
    /// <summary>
    /// Initialize both AWS and Azure test environments
    /// </summary>
    public async Task InitializeAsync()
    {
        var tasks = new List<Task>();
        
        // Initialize AWS environment if enabled
        if (_configuration.Aws.RunIntegrationTests)
        {
            _awsFixture = new LocalStackTestFixture();
            tasks.Add(_awsFixture.InitializeAsync());
        }
        
        // Wait for environments to initialize
        await Task.WhenAll(tasks);
        
        // Create service provider with cloud providers configured
        _serviceProvider = CreateServiceProvider();
    }
    
    /// <summary>
    /// Clean up test environments
    /// </summary>
    public async Task DisposeAsync()
    {
        if (_awsFixture != null)
        {
            await _awsFixture.DisposeAsync();
        }
        
        if (_serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
    
    /// <summary>
    /// Check if test environments are available
    /// </summary>
    public async Task<bool> AreEnvironmentsAvailableAsync()
    {
        if (_awsFixture != null)
        {
            return await _awsFixture.IsAvailableAsync();
        }
        
        return false;
    }
    
    /// <summary>
    /// Create service provider with cloud providers configured
    /// </summary>
    private IServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        
        // Add logging
        services.AddLogging(builder => 
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
        });
        
        // Add configuration
        services.AddSingleton(_configuration);
        
        // Add AWS services if available
        if (_awsFixture != null)
        {
            var awsServices = _awsFixture.CreateTestServices();
            foreach (var service in awsServices)
            {
                services.Add(service);
            }
        }
        
        // Add cross-cloud test utilities
        services.AddSingleton<PerformanceMeasurement>();
        services.AddSingleton<SecurityTestHelpers>();
        
        return services.BuildServiceProvider();
    }
}