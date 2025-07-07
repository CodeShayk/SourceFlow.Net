using System.Collections.Generic;
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace SourceFlow
{
    /// <summary>
    /// Base class that transforms data from an events to a view model.
    /// </summary>
    /// <typeparam name="TViewModel"></typeparam>
    public abstract class BaseDataView<TViewModel> : IDataView<TViewModel>
        where TViewModel : class, IViewModel
    {
        /// <summary>
        /// Collection of event handlers registered for this saga.
        /// </summary>
        public ICollection<Tuple<Type, IProjection>> Projections { get; }

        /// <summary>
        /// The repository used to access and persist view entity.
        /// </summary>
        protected IViewRepository repository;

        /// <summary>
        /// Logger for the view to log events and errors.
        /// </summary>
        protected ILogger logger;

        protected BaseDataView()
        {
            Projections = new List<Tuple<Type, IProjection>>();

            RegisterProjections();
        }

        /// <summary>
        /// Registers all event handlers for the event types that this saga handles.
        /// </summary>
        private void RegisterProjections()
        {
            var interfaces = this.GetType().GetInterfaces();
            foreach (var iface in interfaces)
            {
                if (iface.IsGenericType &&
                    iface.GetGenericTypeDefinition() == typeof(IProjection<>))
                {
                    Projections.Add(new Tuple<Type, IProjection>(iface.GetGenericArguments()[0], (IProjection)this));
                }
            }
        }

        /// <summary>
        /// Transforms the view model based on the event received.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        async Task IDataView.TransformAsync<TEvent>(TEvent @event)
        {
            var tasks = new List<Task>();

            foreach (var projection in Projections)
            {
                if (!projection.Item1.Equals(@event.GetType()) ||
                        !IsGenericEventHandler(projection.Item2, @event.GetType()))
                    continue;

                var method = typeof(IProjection<>)
                            .MakeGenericType(@event.GetType())
                            .GetMethod(nameof(IProjection<TEvent>.ProjectAsync));

                var task = (Task)method.Invoke(projection.Item2, new object[] { @event });

                logger?.LogInformation("Action=View_Projection, Event={Event}, SequenceNo={No}, Aggregate={Aggregate},  DataView={View}, Handler:{Handler}",
                        @event.GetType().Name, @event.SequenceNo, @event.Entity.Type.Name, GetType().Name, method.Name);

                tasks.Add(task);
            }

            if (!tasks.Any())
                return;

            await Task.WhenAll(tasks);
        }

        private static bool IsGenericEventHandler(IProjection instance, Type eventType)
        {
            if (instance == null || eventType == null)
                return false;

            var handlerType = typeof(IProjection<>).MakeGenericType(eventType);
            return handlerType.IsAssignableFrom(instance.GetType());
        }
    }
}