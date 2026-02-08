namespace SourceFlow.Cloud.Azure.Attributes;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class AzureCommandRoutingAttribute : Attribute
{
    public string QueueName { get; set; } = string.Empty;
    public bool RouteToAzure { get; set; } = true;
    public bool RequireSession { get; set; } = true; // FIFO ordering
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class AzureEventRoutingAttribute : Attribute
{
    public string TopicName { get; set; } = string.Empty;
    public bool RouteToAzure { get; set; } = true;
}