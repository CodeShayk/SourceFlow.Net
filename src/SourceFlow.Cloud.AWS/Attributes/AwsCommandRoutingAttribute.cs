using System;

namespace SourceFlow.Cloud.AWS.Attributes;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class AwsCommandRoutingAttribute : Attribute
{
    public string QueueUrl { get; set; }
    public bool RouteToAws { get; set; } = true;
}