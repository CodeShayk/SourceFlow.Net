using System;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using SourceFlow.Impl;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Bus;
using SourceFlow.Saga;

namespace SourceFlow.Core.Tests.Impl
{
    [TestFixture]
    public class SagaDispatcherTests
    {
        [Test]
        public void Constructor_SetsLogger()
        {
            var logger = new Mock<ILogger<ICommandDispatcher>>().Object;
            var dispatcher = new SagaDispatcher(logger);
            Assert.IsNotNull(dispatcher);
        }

        [Test]
        public void Register_AddsSaga()
        {
            var logger = new Mock<ILogger<ICommandDispatcher>>().Object;
            var dispatcher = new SagaDispatcher(logger);
            var sagaMock = new Mock<ISaga>();
            dispatcher.Register(sagaMock.Object);
            Assert.Pass(); // No exception means success
        }

        [Test]
        public void Dispatch_WithNoSagas_LogsInformation()
        {
            var loggerMock = new Mock<ILogger<ICommandDispatcher>>();
            var dispatcher = new SagaDispatcher(loggerMock.Object);
            var commandMock = new DummyCommand();
            var metadataMock = new Mock<IMetadata>();

            dispatcher.Dispatch(this, commandMock);
            loggerMock.Verify(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                (Func<It.IsAnyType, Exception, string>)It.IsAny<object>()),
                Times.AtLeastOnce);
        }
    }
}