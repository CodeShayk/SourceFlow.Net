using System;
using Amazon;

namespace SourceFlow.Cloud.AWS.Configuration;

public class AwsOptions
{
    public RegionEndpoint Region { get; set; } = RegionEndpoint.USEast1;
    public bool EnableCommandRouting { get; set; } = true;
    public bool EnableEventRouting { get; set; } = true;

    [Obsolete("Provide AWS credentials via the SDK credential chain (environment variables, IAM roles, or ~/.aws/credentials). Storing credentials in configuration is insecure.")]
    public string AccessKeyId { get; set; }

    [Obsolete("Provide AWS credentials via the SDK credential chain (environment variables, IAM roles, or ~/.aws/credentials). Storing credentials in configuration is insecure.")]
    public string SecretAccessKey { get; set; }

    [Obsolete("Provide AWS credentials via the SDK credential chain (environment variables, IAM roles, or ~/.aws/credentials). Storing credentials in configuration is insecure.")]
    public string SessionToken { get; set; }
    public int SqsReceiveWaitTimeSeconds { get; set; } = 20;
    public int SqsVisibilityTimeoutSeconds { get; set; } = 300;
    public int SqsMaxNumberOfMessages { get; set; } = 10;
    public int MaxRetries { get; set; } = 3;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
}
