using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SourceFlow.Messaging;


namespace SourceFlow.Projections
{
    public abstract class View : IView
    {
        protected IViewModelStoreAdapter viewModelStore;
        protected ILogger<IView> logger;

        protected View(IViewModelStoreAdapter viewModelStore, ILogger<IView> logger)
        {
            this.viewModelStore = viewModelStore ?? throw new ArgumentNullException(nameof(viewModelStore));            ;
            this.logger = logger;
        }


        /// <summary>
        /// Determines whether the specified view instance can handle the given event type.
        /// </summary>
        /// <param name="instance">The view instance to evaluate. Must not be <see langword="null"/>.</param>
        /// <param name="eventType">The type of the event to check. Must not be <see langword="null"/>.</param>
        /// <returns><see langword="true"/> if the saga instance can handle the specified event type; otherwise, <see
        /// langword="false"/>.</returns>
        internal static bool CanHandle(IView instance, Type eventType)
        {
            if (instance == null || eventType == null)
                return false;

            var handlerType = typeof(IProjectOn<>).MakeGenericType(eventType);
            return handlerType.IsAssignableFrom(instance.GetType());
        }

        async Task IView.Apply<TEvent>(TEvent @event)
        {
            var viewType = GetType();
            var eventName = typeof(TEvent).Name;

            if (!(this is IProjectOn<TEvent> handles))
            {
                logger?.LogWarning("Action=View_CannotHandle, View={View}, Event={Event}, Reason=NotImplementingIProjectOn", viewType, eventName);
                return;
            }

            logger?.LogInformation("Action=View_Starting, View={View}, Event={Event}", viewType, eventName);
                        
            await handles.Apply(@event);

            logger?.LogInformation("Action=View_Handled, View={View}, Event={Event}, Payload={Payload}, SequenceNo={No}",
                    viewType, eventName, @event.Payload.GetType().Name, ((IMetadata)@event).Metadata?.SequenceNo);

        }
    }
}
