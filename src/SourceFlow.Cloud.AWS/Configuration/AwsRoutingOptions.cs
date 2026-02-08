namespace SourceFlow.Cloud.AWS.Configuration;

public class AwsRoutingOptions
{
    public Dictionary<string, string> CommandRoutes { get; set; } = new Dictionary<string, string>();
    public Dictionary<string, string> EventRoutes { get; set; } = new Dictionary<string, string>();
    public List<string> ListeningQueues { get; set; } = new List<string>();
    public string DefaultRouting { get; set; } = "Local";
}