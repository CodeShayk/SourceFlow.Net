using NUnit.Framework;
using Moq;
using System.Threading.Tasks;

namespace SourceFlow.Core.Tests.Interfaces
{
    public class IViewModelRepositoryTests
    {
        public class DummyViewModel : IViewModel
        { public int Id { get; set; } }

        [Test]
        public async Task GetByIdAsync_ReturnsModel()
        {
            var mock = new Mock<IViewRepository>();
            mock.Setup(r => r.Get<DummyViewModel>(1)).ReturnsAsync(new DummyViewModel { Id = 1 });
            var result = await mock.Object.Get<DummyViewModel>(1);
            Assert.That(result, Is.Not.Null);
            Assert.That(1, Is.EqualTo(result.Id));
        }

        [Test]
        public async Task PersistAsync_DoesNotThrow()
        {
            var mock = new Mock<IViewRepository>();
            mock.Setup(r => r.Persist(It.IsAny<DummyViewModel>())).Returns(Task.CompletedTask);
            Assert.DoesNotThrowAsync(async () => await mock.Object.Persist(new DummyViewModel()));
        }

        [Test]
        public async Task DeleteAsync_DoesNotThrow()
        {
            var mock = new Mock<IViewRepository>();
            mock.Setup(r => r.Delete(It.IsAny<DummyViewModel>())).Returns(Task.CompletedTask);
            Assert.DoesNotThrowAsync(async () => await mock.Object.Delete(new DummyViewModel()));
        }
    }
}