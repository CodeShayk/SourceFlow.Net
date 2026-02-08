using System;

namespace SourceFlow.Cloud.AWS.Attributes;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class AwsEventRoutingAttribute : Attribute
{
    public string TopicArn { get; set; }
    public bool RouteToAws { get; set; } = true;
}