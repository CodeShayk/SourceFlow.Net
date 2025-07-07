using Microsoft.Extensions.DependencyInjection;

namespace SourceFlow
{
    /// <summary>
    /// Configuration class for SourceFlow.
    /// </summary>
    public class SourceFlowConfig : ISourceFlowConfig
    {
        /// <summary>
        /// Service collection for SourceFlow configuration.
        /// </summary>
        public IServiceCollection Services { get; set; }
    }
}