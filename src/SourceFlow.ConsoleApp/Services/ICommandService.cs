using SourceFlow.Core;

namespace SourceFlow.ConsoleApp.Services
{
    public interface ICommandService<T> where T : AggregateRoot
    {
    }
}