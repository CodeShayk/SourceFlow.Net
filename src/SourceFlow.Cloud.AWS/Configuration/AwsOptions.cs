using Amazon;

namespace SourceFlow.Cloud.AWS.Configuration;

public class AwsOptions
{
    public RegionEndpoint Region { get; set; } = RegionEndpoint.USEast1;
    public bool EnableCommandRouting { get; set; } = true;
    public bool EnableEventRouting { get; set; } = true;
    public string AccessKeyId { get; set; }
    public string SecretAccessKey { get; set; }
    public string SessionToken { get; set; }
    public int SqsReceiveWaitTimeSeconds { get; set; } = 20;
    public int SqsVisibilityTimeoutSeconds { get; set; } = 300;
    public int SqsMaxNumberOfMessages { get; set; } = 10;
    public int MaxRetries { get; set; } = 3;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
}