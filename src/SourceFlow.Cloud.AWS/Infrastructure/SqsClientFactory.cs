using Amazon;
using Amazon.SQS;
using SourceFlow.Cloud.AWS.Configuration;

namespace SourceFlow.Cloud.AWS.Infrastructure;

public static class SqsClientFactory
{
    public static IAmazonSQS CreateClient(AwsOptions options)
    {
        var config = new AmazonSQSConfig
        {
            RegionEndpoint = options.Region,
            MaxErrorRetry = options.MaxRetries
        };

        if (!string.IsNullOrEmpty(options.AccessKeyId) && !string.IsNullOrEmpty(options.SecretAccessKey))
        {
            config.AuthenticationRegion = options.Region.SystemName;
            // Use credentials if provided, otherwise rely on default credential chain
            return string.IsNullOrEmpty(options.SessionToken)
                ? new AmazonSQSClient(options.AccessKeyId, options.SecretAccessKey, config)
                : new AmazonSQSClient(options.AccessKeyId, options.SecretAccessKey, options.SessionToken, config);
        }

        return new AmazonSQSClient(config);
    }
}