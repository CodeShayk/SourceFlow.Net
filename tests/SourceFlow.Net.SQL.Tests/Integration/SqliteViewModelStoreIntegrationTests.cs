using Microsoft.Data.Sqlite;
using SourceFlow.Net.SQL.Tests.Helpers;
using SourceFlow.Net.SQL.Tests.TestModels;

namespace SourceFlow.Net.SQL.Tests.Integration
{
    [TestFixture]
    public class SqliteViewModelStoreIntegrationTests
    {
        private SqliteConnection? _connection;
        private SqliteViewModelStore? _store;

        [SetUp]
        public async Task Setup()
        {
            _connection = await SqliteTestHelper.CreateInMemoryDatabase();
            await SqliteTestHelper.CreateViewModelsTable(_connection);
            _store = new SqliteViewModelStore(_connection);
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
        public async Task Persist_NewViewModel_StoresViewModelInDatabase()
        {
            // Arrange
            var viewModel = new TestViewModel
            {
                Id = 1,
                DisplayName = "Test View",
                Status = "Active",
                Count = 10
            };

            // Act
            await _store!.Persist(viewModel);

            // Assert
            var retrieved = await _store.Get<TestViewModel>(1);
            Assert.That(retrieved, Is.Not.Null);
            Assert.That(retrieved.Id, Is.EqualTo(1));
            Assert.That(retrieved.DisplayName, Is.EqualTo("Test View"));
            Assert.That(retrieved.Status, Is.EqualTo("Active"));
            Assert.That(retrieved.Count, Is.EqualTo(10));
        }

        [Test]
        public async Task Persist_ExistingViewModel_UpdatesViewModel()
        {
            // Arrange
            var viewModel = new TestViewModel
            {
                Id = 1,
                DisplayName = "Original Name",
                Status = "Pending",
                Count = 5
            };

            await _store!.Persist(viewModel);

            // Act - Update the view model
            viewModel.DisplayName = "Updated Name";
            viewModel.Status = "Completed";
            viewModel.Count = 15;
            await _store.Persist(viewModel);

            // Assert
            var retrieved = await _store.Get<TestViewModel>(1);
            Assert.That(retrieved.DisplayName, Is.EqualTo("Updated Name"));
            Assert.That(retrieved.Status, Is.EqualTo("Completed"));
            Assert.That(retrieved.Count, Is.EqualTo(15));
        }

        [Test]
        public async Task Get_NonExistentViewModel_ThrowsInvalidOperationException()
        {
            // Act & Assert
            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _store!.Get<TestViewModel>(999));

            Assert.That(ex!.Message, Does.Contain("not found"));
        }

        [Test]
        public async Task Delete_ExistingViewModel_RemovesViewModelFromDatabase()
        {
            // Arrange
            var viewModel = new TestViewModel
            {
                Id = 1,
                DisplayName = "Test View",
                Status = "Active",
                Count = 10
            };

            await _store!.Persist(viewModel);

            // Act
            await _store.Delete(viewModel);

            // Assert
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _store.Get<TestViewModel>(1));
        }

        [Test]
        public void Delete_NonExistentViewModel_ThrowsInvalidOperationException()
        {
            // Arrange
            var viewModel = new TestViewModel { Id = 999, DisplayName = "Non-existent" };

            // Act & Assert
            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _store!.Delete(viewModel));

            Assert.That(ex!.Message, Does.Contain("not found"));
        }

        [Test]
        public void Persist_ViewModelWithInvalidId_ThrowsArgumentException()
        {
            // Arrange
            var viewModel = new TestViewModel { Id = 0, DisplayName = "Invalid" };

            // Act & Assert
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await _store!.Persist(viewModel));
        }

        [Test]
        public void Persist_NullViewModel_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await _store!.Persist<TestViewModel>(null!));
        }

        [Test]
        public void Get_InvalidId_ThrowsArgumentException()
        {
            // Act & Assert
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await _store!.Get<TestViewModel>(0));
        }

        [Test]
        public async Task Persist_MultipleViewModels_StoresAllViewModels()
        {
            // Arrange & Act
            for (int i = 1; i <= 5; i++)
            {
                var viewModel = new TestViewModel
                {
                    Id = i,
                    DisplayName = $"View {i}",
                    Status = i % 2 == 0 ? "Active" : "Inactive",
                    Count = i * 10
                };
                await _store!.Persist(viewModel);
            }

            // Assert
            for (int i = 1; i <= 5; i++)
            {
                var retrieved = await _store!.Get<TestViewModel>(i);
                Assert.That(retrieved.Id, Is.EqualTo(i));
                Assert.That(retrieved.DisplayName, Is.EqualTo($"View {i}"));
                Assert.That(retrieved.Count, Is.EqualTo(i * 10));
            }
        }

        [Test]
        public async Task Persist_SameIdDifferentOperations_MaintainsDataIntegrity()
        {
            // Arrange
            var viewModel = new TestViewModel
            {
                Id = 1,
                DisplayName = "First Version",
                Status = "Draft",
                Count = 1
            };

            // Act - Create
            await _store!.Persist(viewModel);
            var v1 = await _store.Get<TestViewModel>(1);

            // Act - Update
            viewModel.DisplayName = "Second Version";
            viewModel.Status = "Published";
            viewModel.Count = 2;
            await _store.Persist(viewModel);
            var v2 = await _store.Get<TestViewModel>(1);

            // Assert
            Assert.That(v1.DisplayName, Is.EqualTo("First Version"));
            Assert.That(v1.Status, Is.EqualTo("Draft"));
            Assert.That(v1.Count, Is.EqualTo(1));
            Assert.That(v2.DisplayName, Is.EqualTo("Second Version"));
            Assert.That(v2.Status, Is.EqualTo("Published"));
            Assert.That(v2.Count, Is.EqualTo(2));
        }

        [Test]
        public async Task Persist_ReadModels_SupportsProjectionScenarios()
        {
            // Arrange - Simulate a projection building read models
            var viewModels = new[]
            {
                new TestViewModel { Id = 1, DisplayName = "User 1", Status = "Active", Count = 5 },
                new TestViewModel { Id = 2, DisplayName = "User 2", Status = "Active", Count = 10 },
                new TestViewModel { Id = 3, DisplayName = "User 3", Status = "Inactive", Count = 0 }
            };

            // Act - Persist all read models
            foreach (var vm in viewModels)
            {
                await _store!.Persist(vm);
            }

            // Assert - Verify all read models are accessible
            var vm1 = await _store!.Get<TestViewModel>(1);
            var vm2 = await _store.Get<TestViewModel>(2);
            var vm3 = await _store.Get<TestViewModel>(3);

            Assert.That(vm1.Status, Is.EqualTo("Active"));
            Assert.That(vm2.Status, Is.EqualTo("Active"));
            Assert.That(vm3.Status, Is.EqualTo("Inactive"));
        }

        [Test]
        public async Task Delete_ThenPersist_CreatesNewViewModel()
        {
            // Arrange
            var viewModel = new TestViewModel
            {
                Id = 1,
                DisplayName = "Original",
                Status = "Active",
                Count = 10
            };

            // Act - Create, Delete, then Create again
            await _store!.Persist(viewModel);
            await _store.Delete(viewModel);

            viewModel.DisplayName = "Recreated";
            viewModel.Status = "New";
            viewModel.Count = 20;
            await _store.Persist(viewModel);

            // Assert
            var retrieved = await _store.Get<TestViewModel>(1);
            Assert.That(retrieved.DisplayName, Is.EqualTo("Recreated"));
            Assert.That(retrieved.Status, Is.EqualTo("New"));
            Assert.That(retrieved.Count, Is.EqualTo(20));
        }
    }
}
