namespace SourceFlow.Cloud.AWS.Tests.TestHelpers;

/// <summary>
/// Configuration for LocalStack container and AWS service emulation
/// </summary>
public class LocalStackConfiguration
{
    /// <summary>
    /// LocalStack container image to use
    /// </summary>
    public string Image { get; set; } = "localstack/localstack:latest";
    
    /// <summary>
    /// LocalStack endpoint URL (typically http://localhost:4566)
    /// </summary>
    public string Endpoint { get; set; } = "http://localhost:4566";
    
    /// <summary>
    /// Port to bind LocalStack to (default 4566)
    /// </summary>
    public int Port { get; set; } = 4566;
    
    /// <summary>
    /// AWS services to enable in LocalStack
    /// </summary>
    public List<string> EnabledServices { get; set; } = new() { "sqs", "sns", "kms", "iam" };
    
    /// <summary>
    /// Enable debug logging in LocalStack
    /// </summary>
    public bool Debug { get; set; } = false;
    
    /// <summary>
    /// Persist LocalStack data between container restarts
    /// </summary>
    public bool PersistData { get; set; } = false;
    
    /// <summary>
    /// Data directory for persistent storage
    /// </summary>
    public string DataDirectory { get; set; } = "/tmp/localstack/data";
    
    /// <summary>
    /// Additional environment variables for LocalStack container
    /// </summary>
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
    
    /// <summary>
    /// Container startup timeout
    /// </summary>
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromMinutes(2);
    
    /// <summary>
    /// Health check timeout for individual services
    /// </summary>
    public TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromSeconds(30);
    
    /// <summary>
    /// Maximum number of health check retries
    /// </summary>
    public int MaxHealthCheckRetries { get; set; } = 10;
    
    /// <summary>
    /// Delay between health check retries
    /// </summary>
    public TimeSpan HealthCheckRetryDelay { get; set; } = TimeSpan.FromSeconds(2);
    
    /// <summary>
    /// Whether to automatically remove the container on disposal
    /// </summary>
    public bool AutoRemove { get; set; } = true;
    
    /// <summary>
    /// Container name (auto-generated if not specified)
    /// </summary>
    public string? ContainerName { get; set; }
    
    /// <summary>
    /// Network mode for the container
    /// </summary>
    public string NetworkMode { get; set; } = "bridge";
    
    /// <summary>
    /// Additional port bindings for the container
    /// </summary>
    public Dictionary<int, int> AdditionalPortBindings { get; set; } = new();
    
    /// <summary>
    /// Volume mounts for the container
    /// </summary>
    public Dictionary<string, string> VolumeMounts { get; set; } = new();
    
    /// <summary>
    /// Get all environment variables including defaults
    /// </summary>
    public Dictionary<string, string> GetAllEnvironmentVariables()
    {
        var env = new Dictionary<string, string>
        {
            ["SERVICES"] = string.Join(",", EnabledServices),
            ["DEBUG"] = Debug ? "1" : "0",
            ["DATA_DIR"] = DataDirectory
        };
        
        if (PersistData)
        {
            env["PERSISTENCE"] = "1";
        }
        
        // Add custom environment variables
        foreach (var kvp in EnvironmentVariables)
        {
            env[kvp.Key] = kvp.Value;
        }
        
        return env;
    }
    
    /// <summary>
    /// Get all port bindings including additional ones
    /// </summary>
    public Dictionary<int, int> GetAllPortBindings()
    {
        var ports = new Dictionary<int, int> { [Port] = Port };
        
        foreach (var kvp in AdditionalPortBindings)
        {
            ports[kvp.Key] = kvp.Value;
        }
        
        return ports;
    }
    
    /// <summary>
    /// Create a default configuration for testing
    /// </summary>
    public static LocalStackConfiguration CreateDefault()
    {
        return new LocalStackConfiguration
        {
            EnabledServices = new List<string> { "sqs", "sns", "kms", "iam" },
            Debug = true,
            PersistData = false,
            AutoRemove = true
        };
    }
    
    /// <summary>
    /// Create a configuration for performance testing
    /// </summary>
    public static LocalStackConfiguration CreateForPerformanceTesting()
    {
        return new LocalStackConfiguration
        {
            EnabledServices = new List<string> { "sqs", "sns", "kms" },
            Debug = false,
            PersistData = false,
            AutoRemove = true,
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["LOCALSTACK_API_KEY"] = "", // Use free tier
                ["DISABLE_CORS_CHECKS"] = "1",
                ["SKIP_INFRA_DOWNLOADS"] = "1"
            }
        };
    }
    
    /// <summary>
    /// Create a configuration for security testing
    /// </summary>
    public static LocalStackConfiguration CreateForSecurityTesting()
    {
        return new LocalStackConfiguration
        {
            EnabledServices = new List<string> { "sqs", "sns", "kms", "iam", "sts" },
            Debug = true,
            PersistData = false,
            AutoRemove = true,
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["ENFORCE_IAM"] = "1",
                ["IAM_LOAD_MANAGED_POLICIES"] = "1"
            }
        };
    }
    
    /// <summary>
    /// Create a configuration for comprehensive integration testing
    /// </summary>
    public static LocalStackConfiguration CreateForIntegrationTesting()
    {
        return new LocalStackConfiguration
        {
            EnabledServices = new List<string> { "sqs", "sns", "kms", "iam", "sts", "cloudformation" },
            Debug = true,
            PersistData = false,
            AutoRemove = true,
            HealthCheckTimeout = TimeSpan.FromMinutes(1),
            MaxHealthCheckRetries = 15,
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["DISABLE_CORS_CHECKS"] = "1",
                ["SKIP_INFRA_DOWNLOADS"] = "1",
                ["ENFORCE_IAM"] = "0", // Disable for easier testing
                ["LOCALSTACK_API_KEY"] = "", // Use free tier
                ["PERSISTENCE"] = "0"
            }
        };
    }
    
    /// <summary>
    /// Create a configuration with enhanced diagnostics
    /// </summary>
    public static LocalStackConfiguration CreateWithDiagnostics()
    {
        return new LocalStackConfiguration
        {
            EnabledServices = new List<string> { "sqs", "sns", "kms", "iam" },
            Debug = true,
            PersistData = false,
            AutoRemove = true,
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["DEBUG"] = "1",
                ["LS_LOG"] = "trace",
                ["DISABLE_CORS_CHECKS"] = "1",
                ["SKIP_INFRA_DOWNLOADS"] = "1"
            }
        };
    }
}