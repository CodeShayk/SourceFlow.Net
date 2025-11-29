using Moq;
using SourceFlow.Projections;

namespace SourceFlow.Core.Tests.Interfaces
{
    public class IViewModelRepositoryTests
    {
        public class DummyViewModel : IViewModel
        { public int Id { get; set; } }

        [Test]
        public async Task GetByIdAsync_ReturnsModel()
        {
            var mock = new Mock<IViewModelStoreAdapter>();
            mock.Setup(r => r.Find<DummyViewModel>(1)).ReturnsAsync(new DummyViewModel { Id = 1 });
            var result = await mock.Object.Find<DummyViewModel>(1);
            Assert.That(result, Is.Not.Null);
            Assert.That(1, Is.EqualTo(result.Id));
        }

        [Test]
        public async Task PersistAsync_DoesNotThrow()
        {
            var mock = new Mock<IViewModelStoreAdapter>();
            mock.Setup(r => r.Persist(It.IsAny<DummyViewModel>())).Returns<DummyViewModel>(vm => Task.FromResult(vm));
            Assert.DoesNotThrowAsync(async () => await mock.Object.Persist(new DummyViewModel()));
        }
    }
}