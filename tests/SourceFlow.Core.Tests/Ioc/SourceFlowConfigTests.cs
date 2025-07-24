using Microsoft.Extensions.DependencyInjection;

namespace SourceFlow.Core.Tests.Ioc
{
    [TestFixture]
    public class SourceFlowConfigTests
    {
        [Test]
        public void Constructor_InitializesServicesProperty()
        {
            var config = new IocExtensions.SourceFlowConfig();
            Assert.IsNull(config.Services); // Default is null
            config.Services = new ServiceCollection();
            Assert.IsNotNull(config.Services);
        }

        [Test]
        public void ImplementsInterface()
        {
            var config = new IocExtensions.SourceFlowConfig();
            Assert.IsInstanceOf<IocExtensions.ISourceFlowConfig>(config);
        }
    }
}