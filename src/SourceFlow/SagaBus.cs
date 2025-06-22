using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;

namespace SourceFlow
{
    internal class SagaBus : ISagaBus
    {
        private readonly List<ISaga> _sagas;

        public SagaBus()
            => _sagas = new List<ISaga>();

        public async Task PublishAsync<TEvent>(TEvent @event)
             where TEvent : IDomainEvent
        {
            var tasks = new List<Task>();

            if (_sagas.Any())
            {
                foreach (var saga in _sagas)
                    tasks.Add(saga.HandleAsync(@event));
            }

            await Task.WhenAll(tasks);
        }

        async Task ISagaBus.Replay<TAggregateRoot>(TAggregateRoot aggregateRoot)
        {
            var tasks = new List<Task>();

            foreach (var saga in _sagas
                 .Where(saga => typeof(ISaga<TAggregateRoot>).IsAssignableFrom(saga.GetType()))
                .Cast<ISaga<TAggregateRoot>>())
                tasks.Add(saga.Replay(aggregateRoot));

            await Task.WhenAll(tasks);
        }

        void ISagaBus.RegisterSaga<TAggregateRoot>(ISaga<TAggregateRoot> saga)
        {
            _sagas.Add(saga);
        }
    }
}