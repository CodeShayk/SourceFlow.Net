using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceFlow.Performance
{
    /// <summary>
    /// Provides ArrayPool-based optimization for collecting and executing tasks.
    /// Reduces allocations in high-throughput scenarios.
    /// </summary>
    internal static class TaskBufferPool
    {
        private static readonly ArrayPool<Task> Pool = ArrayPool<Task>.Shared;

        /// <summary>
        /// Executes a collection of tasks using pooled array buffers to reduce allocations.
        /// </summary>
        /// <typeparam name="T">The type of items to process.</typeparam>
        /// <param name="items">The collection of items to process.</param>
        /// <param name="taskFactory">Function that creates a task for each item.</param>
        /// <returns>A task that completes when all tasks are complete.</returns>
        public static async Task ExecuteAsync<T>(IEnumerable<T> items, Func<T, Task> taskFactory)
        {
            if (items == null)
                throw new ArgumentNullException(nameof(items));
            if (taskFactory == null)
                throw new ArgumentNullException(nameof(taskFactory));

            // For ICollection, we can optimize by knowing the count upfront
            if (items is ICollection<T> collection)
            {
                var count = collection.Count;
                if (count == 0)
                    return;

                // For very small counts, just use direct execution without pooling
                if (count == 1)
                {
                    using (var enumerator = collection.GetEnumerator())
                    {
                        if (enumerator.MoveNext())
                            await taskFactory(enumerator.Current);
                    }
                    return;
                }

                // Rent array from pool
                var taskBuffer = Pool.Rent(count);
                try
                {
                    var index = 0;
                    foreach (var item in collection)
                    {
                        taskBuffer[index++] = taskFactory(item);
                    }

                    // Create array segment to avoid allocating the full buffer
                    var tasks = new ArraySegment<Task>(taskBuffer, 0, count);
                    // Cast to IEnumerable to avoid ambiguity with ReadOnlySpan overload in .NET 9+
                    await Task.WhenAll((IEnumerable<Task>)tasks);
                }
                finally
                {
                    // Clear references to prevent memory leaks
                    Array.Clear(taskBuffer, 0, count);
                    Pool.Return(taskBuffer);
                }
            }
            else
            {
                // For non-collection enumerables, use temporary list
                // This is still better than allocating in the calling code
                var taskList = new List<Task>();
                foreach (var item in items)
                {
                    taskList.Add(taskFactory(item));
                }

                if (taskList.Count > 0)
                    await Task.WhenAll(taskList);
            }
        }

        /// <summary>
        /// Collects tasks from items and returns them as a pooled array.
        /// Caller is responsible for returning the buffer via ReturnBuffer.
        /// </summary>
        /// <typeparam name="T">The type of items to process.</typeparam>
        /// <param name="items">The collection of items to process.</param>
        /// <param name="taskFactory">Function that creates a task for each item.</param>
        /// <param name="actualCount">The actual number of tasks in the buffer.</param>
        /// <returns>A rented array buffer containing the tasks.</returns>
        public static Task[] RentAndCollect<T>(ICollection<T> items, Func<T, Task> taskFactory, out int actualCount)
        {
            if (items == null)
                throw new ArgumentNullException(nameof(items));
            if (taskFactory == null)
                throw new ArgumentNullException(nameof(taskFactory));

            actualCount = items.Count;
            if (actualCount == 0)
                return Array.Empty<Task>();

            var buffer = Pool.Rent(actualCount);
            var index = 0;
            foreach (var item in items)
            {
                buffer[index++] = taskFactory(item);
            }

            return buffer;
        }

        /// <summary>
        /// Returns a rented buffer back to the pool.
        /// </summary>
        /// <param name="buffer">The buffer to return.</param>
        /// <param name="count">The number of items that were used in the buffer.</param>
        public static void ReturnBuffer(Task[] buffer, int count)
        {
            if (buffer == null || buffer.Length == 0)
                return;

            // Clear references to prevent memory leaks
            Array.Clear(buffer, 0, count);
            Pool.Return(buffer);
        }
    }
}
