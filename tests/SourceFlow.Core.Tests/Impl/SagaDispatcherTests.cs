using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using SourceFlow.Saga;
using SourceFlow.Messaging.Commands;
using SourceFlow.Messaging.Commands.Impl;

namespace SourceFlow.Core.Tests.Impl
{
    [TestFixture]
    public class SagaDispatcherTests
    {
        [Test]
        public void Constructor_SetsLogger()
        {
            var logger = new Mock<ILogger<ICommandSubscriber>>().Object;
            var sagas = new Mock<IEnumerable<ISaga>>().Object;
            var dispatcher = new CommandSubscriber(sagas, logger);
            Assert.IsNotNull(dispatcher);
        }

       
        [Test]
        public async Task Dispatch_WithNoSagas_LogsInformation()
        {
            var loggerMock = new Mock<ILogger<ICommandSubscriber>>();
            // Use an empty list instead of a mock to avoid null reference issues
            var sagas = new List<ISaga>();

            var dispatcher = new CommandSubscriber(sagas, loggerMock.Object);
            var commandMock = new DummyCommand();

            await dispatcher.Subscribe(commandMock);
            loggerMock.Verify(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()),
                Times.AtLeastOnce);
        }
    }
}