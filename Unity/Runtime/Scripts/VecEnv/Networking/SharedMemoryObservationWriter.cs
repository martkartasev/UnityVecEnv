using System;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;
using ExternalCommunication;
using Google.Protobuf;

namespace Scripts.VecEnv.Networking
{
    /// <summary>
    /// Publishes the bulk fields of a batched observation through a Windows
    /// named memory mapping. HTTP remains the command and completion barrier.
    /// </summary>
    internal sealed class SharedMemoryObservationWriter : IDisposable
    {
        internal const int Version = 1;
        internal const int HeaderSize = 64;
        internal const int SlotCount = 2;

        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("UVESHM01");

        private readonly int _channel;
        private MemoryMappedFile _mapping;
        private MemoryMappedViewAccessor _view;
        private long _payloadSize = -1;
        private long _sequence;
        private bool _disposed;

        internal SharedMemoryObservationWriter(int channel)
        {
            _channel = channel;
        }

        internal bool TryWriteAndClear(BatchedObservation observation)
        {
            if (observation == null)
            {
                throw new ArgumentNullException(nameof(observation));
            }

            var payloadSize = CalculatePayloadSize(observation);
            if (payloadSize == 0)
            {
                return false;
            }

            EnsureMapping(payloadSize);

            var sequence = checked(_sequence + 1);
            var slot = (int)((sequence - 1) % SlotCount);
            var destinationOffset = checked((long)HeaderSize + slot * payloadSize);

            WritePayload(observation, destinationOffset, payloadSize);

            // Publish the slot only after every payload byte is visible. The
            // HTTP response is sent after this method returns and acts as the
            // cross-process completion signal.
            Thread.MemoryBarrier();
            _view.Write(20, slot);
            _view.Write(24, payloadSize);
            _view.Write(32, sequence);
            Thread.MemoryBarrier();
            _sequence = sequence;

            ClearPayload(observation);
            return true;
        }

        internal static string MappingName(int channel, long payloadSize)
        {
            return $"Local\\UnityVecEnv.Observations.{channel}.{payloadSize}";
        }

        private void EnsureMapping(long payloadSize)
        {
            ThrowIfDisposed();
            if (_mapping != null && _payloadSize == payloadSize)
            {
                return;
            }

            DisposeMapping();

            var capacity = checked((long)HeaderSize + SlotCount * payloadSize);
            _mapping = MemoryMappedFile.CreateOrOpen(
                MappingName(_channel, payloadSize),
                capacity,
                MemoryMappedFileAccess.ReadWrite);
            _view = _mapping.CreateViewAccessor(0, capacity, MemoryMappedFileAccess.ReadWrite);
            _payloadSize = payloadSize;

            _view.WriteArray(0, Magic, 0, Magic.Length);
            _view.Write(8, Version);
            _view.Write(12, HeaderSize);
            _view.Write(16, SlotCount);
            _view.Write(20, 0);
            _view.Write(24, payloadSize);
            _view.Write(32, 0L);
        }

        private unsafe void WritePayload(BatchedObservation observation, long destinationOffset, long payloadSize)
        {
            byte* basePointer = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref basePointer);
            try
            {
                var cursor = destinationOffset;
                CopyToMappedView(observation.ContinuousF32, basePointer, ref cursor);
                CopyToMappedView(observation.DiscreteI32, basePointer, ref cursor);
                foreach (var visual in observation.Visual)
                {
                    CopyToMappedView(visual.Data, basePointer, ref cursor);
                }

                if (cursor != destinationOffset + payloadSize)
                {
                    throw new InvalidOperationException(
                        $"Shared-memory observation writer consumed {cursor - destinationOffset} bytes, " +
                        $"expected {payloadSize}.");
                }
            }
            finally
            {
                if (basePointer != null)
                {
                    _view.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }

        private unsafe void CopyToMappedView(ByteString source, byte* basePointer, ref long cursor)
        {
            if (source == null || source.Length == 0)
            {
                return;
            }

            var destination = new Span<byte>(
                basePointer + _view.PointerOffset + cursor,
                source.Length);
            source.Span.CopyTo(destination);
            cursor += source.Length;
        }

        private static long CalculatePayloadSize(BatchedObservation observation)
        {
            long size = observation.ContinuousF32?.Length ?? 0;
            size = checked(size + (observation.DiscreteI32?.Length ?? 0));
            foreach (var visual in observation.Visual)
            {
                size = checked(size + (visual.Data?.Length ?? 0));
            }

            return size;
        }

        private static void ClearPayload(BatchedObservation observation)
        {
            observation.ContinuousF32 = ByteString.Empty;
            observation.DiscreteI32 = ByteString.Empty;
            foreach (var visual in observation.Visual)
            {
                visual.Data = ByteString.Empty;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SharedMemoryObservationWriter));
            }
        }

        private void DisposeMapping()
        {
            _view?.Dispose();
            _view = null;
            _mapping?.Dispose();
            _mapping = null;
            _payloadSize = -1;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeMapping();
        }
    }
}
