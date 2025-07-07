using System.Collections.Generic;
using System;
using System.Data;
using System.Threading.Tasks;

namespace SourceFlow
{
    /// <summary>
    /// Interface for a view that projects data from an event into a data model.
    /// </summary>
    /// <typeparam name="TViewModel"></typeparam>
    public interface IDataView<TViewModel> : IDataView where TViewModel : class, IViewModel
    {
    }

    /// <summary>
    /// Interface for a view that projects data from an event into a data model.
    /// </summary>
    public interface IDataView
    {
        /// <summary>
        /// List of projections for the data view.
        /// </summary>
        ICollection<Tuple<Type, IProjection>> Projections { get; }

        /// <summary>
        /// Transforms data from an event into a data model.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        Task TransformAsync<TEvent>(TEvent @event)
            where TEvent : IEvent;
    }
}