using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Bus;
using SourceFlow.Impl;

namespace SourceFlow.Core.Tests.Impl
{
    [TestFixture]
    public class CommandBusTests
    {
        [Test]
        public void Constructor_SetsDependencies()
        {
            var store = new Mock<ICommandStore>().Object;
            var logger = new Mock<ILogger<ICommandBus>>().Object;
            var bus = new CommandBus(store, logger);
            Assert.IsNotNull(bus);
        }

        [Test]
        public void Publish_NullCommand_ThrowsArgumentNullException()
        {
            var store = new Mock<ICommandStore>().Object;
            var logger = new Mock<ILogger<ICommandBus>>().Object;
            var bus = (ICommandBus)new CommandBus(store, logger);
            Assert.ThrowsAsync<ArgumentNullException>(async () => await bus.Publish<ICommand>(null));
        }

        [Test]
        public async Task Publish_ValidCommand_InvokesDispatchers()
        {
            var storeMock = new Mock<ICommandStore>();
            storeMock.Setup(s => s.GetNextSequenceNo(It.IsAny<int>())).ReturnsAsync(1);
            storeMock.Setup(s => s.Append(It.IsAny<ICommand>())).Returns(Task.CompletedTask);
            var logger = new Mock<ILogger<ICommandBus>>().Object;
            var bus = new CommandBus(storeMock.Object, logger);
            var commandMock = new DummyCommand();
            bool dispatcherCalled = false;
            bus.Dispatchers += (s, c) => dispatcherCalled = true;
            await ((ICommandBus)bus).Publish(commandMock);
            Assert.IsTrue(dispatcherCalled);
        }

        [Test]
        public async Task Replay_NoCommands_DoesNothing()
        {
            var storeMock = new Mock<ICommandStore>();
            storeMock.Setup(s => s.Load(It.IsAny<int>())).ReturnsAsync((IEnumerable<ICommand>)null);
            var logger = new Mock<ILogger<ICommandBus>>().Object;
            var bus = (ICommandBus)new CommandBus(storeMock.Object, logger);
            await bus.Replay(42);
            Assert.Pass();
        }
    }
}