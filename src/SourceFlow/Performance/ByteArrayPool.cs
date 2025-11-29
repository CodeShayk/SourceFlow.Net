using System;
using System.Buffers;
using System.Text.Json;

namespace SourceFlow.Performance
{
    /// <summary>
    /// Provides ArrayPool-based optimization for JSON serialization operations.
    /// Reduces allocations in high-throughput scenarios by reusing byte buffers.
    /// </summary>
    internal static class ByteArrayPool
    {
        private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Shared;

        /// <summary>
        /// Serializes an object to JSON using a pooled buffer.
        /// </summary>
        /// <typeparam name="T">The type of object to serialize.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <param name="options">Optional JsonSerializerOptions.</param>
        /// <returns>The JSON string representation.</returns>
        public static string Serialize<T>(T value, JsonSerializerOptions options = null)
        {
            if (value == null)
                return string.Empty;

            return SerializeCore(writer =>
            {
                if (options != null)
                    JsonSerializer.Serialize(writer, value, options);
                else
                    JsonSerializer.Serialize(writer, value);
            });
        }

        /// <summary>
        /// Serializes an object to JSON using a pooled buffer with runtime type information.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="inputType">The runtime type of the value.</param>
        /// <param name="options">Optional JsonSerializerOptions.</param>
        /// <returns>The JSON string representation.</returns>
        public static string Serialize(object value, Type inputType, JsonSerializerOptions options = null)
        {
            if (value == null)
                return string.Empty;

            return SerializeCore(writer =>
            {
                if (options != null)
                    JsonSerializer.Serialize(writer, value, inputType, options);
                else
                    JsonSerializer.Serialize(writer, value, inputType);
            });
        }

        private static string SerializeCore(Action<Utf8JsonWriter> writeAction)
        {
            using (var bufferWriter = new PooledBufferWriter(Pool))
            {
                using (var writer = new Utf8JsonWriter(bufferWriter))
                {
                    writeAction(writer);
                }

                var writtenSpan = bufferWriter.WrittenSpan;
                return System.Text.Encoding.UTF8.GetString(writtenSpan.ToArray());
            }
        }

        /// <summary>
        /// Deserializes JSON to an object using a pooled buffer.
        /// </summary>
        /// <typeparam name="T">The type to deserialize to.</typeparam>
        /// <param name="json">The JSON string to deserialize.</param>
        /// <param name="options">Optional JsonSerializerOptions.</param>
        /// <returns>The deserialized object.</returns>
        public static T Deserialize<T>(string json, JsonSerializerOptions options = null)
        {
            if (string.IsNullOrEmpty(json))
                return default(T);

            var byteCount = System.Text.Encoding.UTF8.GetByteCount(json);
            var buffer = Pool.Rent(byteCount);

            try
            {
                var bytesWritten = System.Text.Encoding.UTF8.GetBytes(json, 0, json.Length, buffer, 0);
                var span = new ReadOnlySpan<byte>(buffer, 0, bytesWritten);

                return options != null
                    ? JsonSerializer.Deserialize<T>(span, options)
                    : JsonSerializer.Deserialize<T>(span);
            }
            finally
            {
                Pool.Return(buffer);
            }
        }

        /// <summary>
        /// Deserializes JSON to an object using a pooled buffer with runtime type information.
        /// </summary>
        /// <param name="json">The JSON string to deserialize.</param>
        /// <param name="returnType">The runtime type to deserialize to.</param>
        /// <param name="options">Optional JsonSerializerOptions.</param>
        /// <returns>The deserialized object.</returns>
        public static object Deserialize(string json, Type returnType, JsonSerializerOptions options = null)
        {
            if (string.IsNullOrEmpty(json))
                return null;

            var byteCount = System.Text.Encoding.UTF8.GetByteCount(json);
            var buffer = Pool.Rent(byteCount);

            try
            {
                var bytesWritten = System.Text.Encoding.UTF8.GetBytes(json, 0, json.Length, buffer, 0);
                var span = new ReadOnlySpan<byte>(buffer, 0, bytesWritten);

                return options != null
                    ? JsonSerializer.Deserialize(span, returnType, options)
                    : JsonSerializer.Deserialize(span, returnType);
            }
            finally
            {
                Pool.Return(buffer);
            }
        }

        /// <summary>
        /// A pooled buffer writer that uses ArrayPool for efficient memory allocation.
        /// </summary>
        private sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
        {
            private readonly ArrayPool<byte> _pool;
            private byte[] _buffer;
            private int _index;

            public PooledBufferWriter(ArrayPool<byte> pool, int initialCapacity = 4096)
            {
                _pool = pool;
                _buffer = _pool.Rent(initialCapacity);
                _index = 0;
            }

            public ReadOnlySpan<byte> WrittenSpan => new ReadOnlySpan<byte>(_buffer, 0, _index);

            public void Advance(int count)
            {
                if (count < 0)
                    throw new ArgumentOutOfRangeException(nameof(count));
                if (_index + count > _buffer.Length)
                    throw new InvalidOperationException("Cannot advance past the end of the buffer.");

                _index += count;
            }

            public Memory<byte> GetMemory(int sizeHint = 0)
            {
                CheckAndResizeBuffer(sizeHint);
                return _buffer.AsMemory(_index);
            }

            public Span<byte> GetSpan(int sizeHint = 0)
            {
                CheckAndResizeBuffer(sizeHint);
                return _buffer.AsSpan(_index);
            }

            private void CheckAndResizeBuffer(int sizeHint)
            {
                if (sizeHint < 0)
                    throw new ArgumentOutOfRangeException(nameof(sizeHint));

                if (sizeHint == 0)
                    sizeHint = 1;

                var availableSpace = _buffer.Length - _index;

                if (sizeHint > availableSpace)
                {
                    var newSize = Math.Max(_buffer.Length * 2, _buffer.Length + sizeHint);
                    var newBuffer = _pool.Rent(newSize);

                    _buffer.AsSpan(0, _index).CopyTo(newBuffer);

                    _pool.Return(_buffer);
                    _buffer = newBuffer;
                }
            }

            public void Dispose()
            {
                if (_buffer != null)
                {
                    _pool.Return(_buffer);
                    _buffer = null;
                }
            }
        }
    }
}
