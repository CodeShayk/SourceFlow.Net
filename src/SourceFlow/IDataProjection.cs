using System.Threading.Tasks;

namespace SourceFlow
{
    /// <summary>
    /// Interface for data projections in the event-driven architecture.
    /// </summary>
    public interface IDataProjection
    {
    }

    /// <summary>
    /// Interface for data projections from given events to a specific data view.
    /// </summary>
    /// <typeparam name="TDataView"></typeparam>
    public interface IDataProjection<TDataView> : IDataProjection where TDataView : IViewModel
    {
        /// <summary>
        /// Applies the specified event to the data projection, updating the data view accordingly.
        /// </summary>
        /// <param name="event"></param>
        Task ApplyAsync(IEvent @event);
    }
}