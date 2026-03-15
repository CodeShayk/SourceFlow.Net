using Amazon;
using Amazon.SimpleNotificationService;
using SourceFlow.Cloud.AWS.Configuration;

namespace SourceFlow.Cloud.AWS.Infrastructure;

public static class SnsClientFactory
{
    public static IAmazonSimpleNotificationService CreateClient(AwsOptions options)
    {
        var config = new AmazonSimpleNotificationServiceConfig
        {
            RegionEndpoint = options.Region,
            MaxErrorRetry = options.MaxRetries
        };

        if (!string.IsNullOrEmpty(options.AccessKeyId) && !string.IsNullOrEmpty(options.SecretAccessKey))
        {
            config.AuthenticationRegion = options.Region.SystemName;
            // Use credentials if provided, otherwise rely on default credential chain
            return string.IsNullOrEmpty(options.SessionToken)
                ? new AmazonSimpleNotificationServiceClient(options.AccessKeyId, options.SecretAccessKey, config)
                : new AmazonSimpleNotificationServiceClient(options.AccessKeyId, options.SecretAccessKey, options.SessionToken, config);
        }

        return new AmazonSimpleNotificationServiceClient(config);
    }
}
