using Microsoft.Data.Sqlite;
using SourceFlow.Net.SQL.Tests.Helpers;
using SourceFlow.Net.SQL.Tests.TestModels;

namespace SourceFlow.Net.SQL.Tests.Integration
{
    [TestFixture]
    public class SqliteCommandStoreIntegrationTests
    {
        private SqliteConnection? _connection;
        private SqliteCommandStore? _store;

        [SetUp]
        public async Task Setup()
        {
            _connection = await SqliteTestHelper.CreateInMemoryDatabase();
            await SqliteTestHelper.CreateCommandsTable(_connection);
            _store = new SqliteCommandStore(_connection);
        }

        [TearDown]
        public async Task TearDown()
        {
            if (_connection != null)
            {
                await _connection.CloseAsync();
                await _connection.DisposeAsync();
            }
        }

        [Test]
        public async Task Append_ValidCommand_StoresCommandInDatabase()
        {
            // Arrange
            var payload = new TestPayload { Action = "Create", Data = "Test data" };
            var command = new TestCommand(entityId: 1, payload);
            command.Metadata.SequenceNo = 1;

            // Act
            await _store!.Append(command);

            // Assert
            var commands = await _store.Load(1);
            var commandsList = commands.ToList();

            Assert.That(commandsList, Has.Count.EqualTo(1));
            Assert.That(commandsList[0].Entity.Id, Is.EqualTo(1));
            Assert.That(commandsList[0].Metadata.SequenceNo, Is.EqualTo(1));
        }

        [Test]
        public async Task Append_MultipleCommands_StoresAllCommandsInOrder()
        {
            // Arrange
            var payload1 = new TestPayload { Action = "Create", Data = "First" };
            var command1 = new TestCommand(entityId: 1, payload1);
            command1.Metadata.SequenceNo = 1;

            var payload2 = new TestPayload { Action = "Update", Data = "Second" };
            var command2 = new TestCommand(entityId: 1, payload2);
            command2.Metadata.SequenceNo = 2;

            var payload3 = new TestPayload { Action = "Delete", Data = "Third" };
            var command3 = new TestCommand(entityId: 1, payload3);
            command3.Metadata.SequenceNo = 3;

            // Act
            await _store!.Append(command1);
            await _store.Append(command2);
            await _store.Append(command3);

            // Assert
            var commands = await _store.Load(1);
            var commandsList = commands.ToList();

            Assert.That(commandsList, Has.Count.EqualTo(3));
            Assert.That(commandsList[0].Metadata.SequenceNo, Is.EqualTo(1));
            Assert.That(commandsList[1].Metadata.SequenceNo, Is.EqualTo(2));
            Assert.That(commandsList[2].Metadata.SequenceNo, Is.EqualTo(3));
        }

        [Test]
        public async Task Load_NonExistentEntity_ReturnsEmptyList()
        {
            // Act
            var commands = await _store!.Load(999);

            // Assert
            Assert.That(commands, Is.Empty);
        }

        [Test]
        public async Task Append_CommandsForDifferentEntities_StoresSeparately()
        {
            // Arrange
            var payload1 = new TestPayload { Action = "Create", Data = "Entity 1" };
            var command1 = new TestCommand(entityId: 1, payload1);
            command1.Metadata.SequenceNo = 1;

            var payload2 = new TestPayload { Action = "Create", Data = "Entity 2" };
            var command2 = new TestCommand(entityId: 2, payload2);
            command2.Metadata.SequenceNo = 1;

            // Act
            await _store!.Append(command1);
            await _store.Append(command2);

            // Assert
            var commands1 = await _store.Load(1);
            var commands2 = await _store.Load(2);

            Assert.That(commands1.Count(), Is.EqualTo(1));
            Assert.That(commands2.Count(), Is.EqualTo(1));
            Assert.That(commands1.First().Entity.Id, Is.EqualTo(1));
            Assert.That(commands2.First().Entity.Id, Is.EqualTo(2));
        }

        [Test]
        public void Append_NullCommand_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await _store!.Append(null!));
        }

        [Test]
        public async Task Append_DuplicateSequenceNumber_ThrowsException()
        {
            // Arrange
            var payload1 = new TestPayload { Action = "Create", Data = "First" };
            var command1 = new TestCommand(entityId: 1, payload1);
            command1.Metadata.SequenceNo = 1;

            var payload2 = new TestPayload { Action = "Update", Data = "Second" };
            var command2 = new TestCommand(entityId: 1, payload2);
            command2.Metadata.SequenceNo = 1; // Same sequence number

            // Act
            await _store!.Append(command1);

            // Assert
            Assert.ThrowsAsync<SqliteException>(async () =>
                await _store.Append(command2));
        }

        [Test]
        public async Task Load_AfterMultipleAppends_ReturnsCommandsInCorrectOrder()
        {
            // Arrange
            for (int i = 1; i <= 10; i++)
            {
                var payload = new TestPayload { Action = $"Action{i}", Data = $"Data{i}" };
                var command = new TestCommand(entityId: 1, payload);
                command.Metadata.SequenceNo = i;
                await _store!.Append(command);
            }

            // Act
            var commands = await _store!.Load(1);
            var commandsList = commands.ToList();

            // Assert
            Assert.That(commandsList, Has.Count.EqualTo(10));
            for (int i = 0; i < 10; i++)
            {
                Assert.That(commandsList[i].Metadata.SequenceNo, Is.EqualTo(i + 1));
            }
        }
    }
}
