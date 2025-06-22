using SourceFlow.Core;

namespace SourceFlow.ConsoleApp.Services
{
    public abstract class CommandService<T> : ICommandService<T> where T : AggregateRoot
    {
        protected readonly IRepository<T> _repository;
        protected CommandService(IRepository<T> repository)
        {
            _repository = repository;
        }       
    }
}