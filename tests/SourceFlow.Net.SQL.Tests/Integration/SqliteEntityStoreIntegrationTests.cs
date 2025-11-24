using Microsoft.Data.Sqlite;
using SourceFlow.Net.SQL.Tests.Helpers;
using SourceFlow.Net.SQL.Tests.TestModels;

namespace SourceFlow.Net.SQL.Tests.Integration
{
    [TestFixture]
    public class SqliteEntityStoreIntegrationTests
    {
        private SqliteConnection? _connection;
        private SqliteEntityStore? _store;

        [SetUp]
        public async Task Setup()
        {
            _connection = await SqliteTestHelper.CreateInMemoryDatabase();
            await SqliteTestHelper.CreateEntitiesTable(_connection);
            _store = new SqliteEntityStore(_connection);
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
        public async Task Persist_NewEntity_StoresEntityInDatabase()
        {
            // Arrange
            var entity = new TestEntity
            {
                Id = 1,
                Name = "Test Entity",
                Description = "Test Description",
                Value = 42
            };

            // Act
            await _store!.Persist(entity);

            // Assert
            var retrieved = await _store.Get<TestEntity>(1);
            Assert.That(retrieved, Is.Not.Null);
            Assert.That(retrieved.Id, Is.EqualTo(1));
            Assert.That(retrieved.Name, Is.EqualTo("Test Entity"));
            Assert.That(retrieved.Description, Is.EqualTo("Test Description"));
            Assert.That(retrieved.Value, Is.EqualTo(42));
        }

        [Test]
        public async Task Persist_ExistingEntity_UpdatesEntity()
        {
            // Arrange
            var entity = new TestEntity
            {
                Id = 1,
                Name = "Original Name",
                Description = "Original Description",
                Value = 10
            };

            await _store!.Persist(entity);

            // Act - Update the entity
            entity.Name = "Updated Name";
            entity.Description = "Updated Description";
            entity.Value = 20;
            await _store.Persist(entity);

            // Assert
            var retrieved = await _store.Get<TestEntity>(1);
            Assert.That(retrieved.Name, Is.EqualTo("Updated Name"));
            Assert.That(retrieved.Description, Is.EqualTo("Updated Description"));
            Assert.That(retrieved.Value, Is.EqualTo(20));
        }

        [Test]
        public async Task Get_NonExistentEntity_ThrowsInvalidOperationException()
        {
            // Act & Assert
            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _store!.Get<TestEntity>(999));

            Assert.That(ex!.Message, Does.Contain("not found"));
        }

        [Test]
        public async Task Delete_ExistingEntity_RemovesEntityFromDatabase()
        {
            // Arrange
            var entity = new TestEntity
            {
                Id = 1,
                Name = "Test Entity",
                Description = "Test Description",
                Value = 42
            };

            await _store!.Persist(entity);

            // Act
            await _store.Delete(entity);

            // Assert
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _store.Get<TestEntity>(1));
        }

        [Test]
        public void Delete_NonExistentEntity_ThrowsInvalidOperationException()
        {
            // Arrange
            var entity = new TestEntity { Id = 999, Name = "Non-existent" };

            // Act & Assert
            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _store!.Delete(entity));

            Assert.That(ex!.Message, Does.Contain("not found"));
        }

        [Test]
        public void Persist_EntityWithInvalidId_ThrowsArgumentException()
        {
            // Arrange
            var entity = new TestEntity { Id = 0, Name = "Invalid" };

            // Act & Assert
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await _store!.Persist(entity));
        }

        [Test]
        public void Persist_NullEntity_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await _store!.Persist<TestEntity>(null!));
        }

        [Test]
        public void Get_InvalidId_ThrowsArgumentException()
        {
            // Act & Assert
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await _store!.Get<TestEntity>(0));
        }

        [Test]
        public async Task Persist_MultipleEntities_StoresAllEntities()
        {
            // Arrange & Act
            for (int i = 1; i <= 5; i++)
            {
                var entity = new TestEntity
                {
                    Id = i,
                    Name = $"Entity {i}",
                    Description = $"Description {i}",
                    Value = i * 10
                };
                await _store!.Persist(entity);
            }

            // Assert
            for (int i = 1; i <= 5; i++)
            {
                var retrieved = await _store!.Get<TestEntity>(i);
                Assert.That(retrieved.Id, Is.EqualTo(i));
                Assert.That(retrieved.Name, Is.EqualTo($"Entity {i}"));
                Assert.That(retrieved.Value, Is.EqualTo(i * 10));
            }
        }

        [Test]
        public async Task Persist_SameIdDifferentOperations_MaintainsDataIntegrity()
        {
            // Arrange
            var entity = new TestEntity
            {
                Id = 1,
                Name = "First Version",
                Description = "Description 1",
                Value = 100
            };

            // Act - Create
            await _store!.Persist(entity);
            var v1 = await _store.Get<TestEntity>(1);

            // Act - Update
            entity.Name = "Second Version";
            entity.Value = 200;
            await _store.Persist(entity);
            var v2 = await _store.Get<TestEntity>(1);

            // Assert
            Assert.That(v1.Name, Is.EqualTo("First Version"));
            Assert.That(v1.Value, Is.EqualTo(100));
            Assert.That(v2.Name, Is.EqualTo("Second Version"));
            Assert.That(v2.Value, Is.EqualTo(200));
        }
    }
}
