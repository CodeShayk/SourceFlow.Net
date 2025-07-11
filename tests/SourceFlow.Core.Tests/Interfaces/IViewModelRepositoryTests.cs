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
            var mock = new Mock<IViewModelRepository>();
            mock.Setup(r => r.GetByIdAsync<DummyViewModel>(1)).ReturnsAsync(new DummyViewModel { Id = 1 });
            var result = await mock.Object.GetByIdAsync<DummyViewModel>(1);
            Assert.That(result, Is.Not.Null);
            Assert.That(1, Is.EqualTo(result.Id));
        }

        [Test]
        public async Task PersistAsync_DoesNotThrow()
        {
            var mock = new Mock<IViewModelRepository>();
            mock.Setup(r => r.PersistAsync(It.IsAny<DummyViewModel>())).Returns(Task.CompletedTask);
            Assert.DoesNotThrowAsync(async () => await mock.Object.PersistAsync(new DummyViewModel()));
        }

        [Test]
        public async Task DeleteAsync_DoesNotThrow()
        {
            var mock = new Mock<IViewModelRepository>();
            mock.Setup(r => r.DeleteAsync(It.IsAny<DummyViewModel>())).Returns(Task.CompletedTask);
            Assert.DoesNotThrowAsync(async () => await mock.Object.DeleteAsync(new DummyViewModel()));
        }
    }
}