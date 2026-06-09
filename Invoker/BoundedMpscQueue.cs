// Copyright (c) ktsu.dev
// All rights reserved.
// Licensed under the MIT license.

namespace ktsu.Invoker;

using System.Threading;

/// <summary>
/// A bounded, lock-free multi-producer/single-consumer queue based on Dmitry Vyukov's bounded MPMC
/// algorithm (which also remains correct for multiple concurrent consumers).
/// </summary>
/// <remarks>
/// The queue pre-allocates a fixed-size array of cells in its constructor; thereafter
/// <see cref="TryEnqueue"/> and <see cref="TryDequeue"/> allocate nothing, take no locks, and never
/// block. Each cell carries a sequence number that producers and consumers advance with a single
/// compare-and-swap, which is what makes the structure safe for any number of concurrent producers
/// and consumers.
///
/// <para>
/// It is used by <see cref="Invoker"/> to back the non-blocking <see cref="Invoker.TryBeginInvoke"/>
/// path. Note that this queue is <b>not</b> intended for use from a hard real-time audio thread: while
/// it is allocation-free, the actions it carries (and anything they capture) are not, and audio→UI
/// telemetry should instead flow through a dedicated single-producer/single-consumer ring buffer.
/// </para>
/// </remarks>
/// <typeparam name="T">The type of elements stored in the queue.</typeparam>
internal sealed class BoundedMpscQueue<T>
{
	private struct Cell
	{
		public long Sequence;
		public T? Item;
	}

	private readonly Cell[] buffer;
	private readonly int mask;

	// Producer and consumer cursors. Kept on separate cache lines would be ideal, but they are only
	// ever advanced via Interlocked, so contention is bounded by the CAS itself.
	private long enqueuePos;
	private long dequeuePos;

	/// <summary>
	/// Gets the maximum number of elements the queue can hold at once.
	/// </summary>
	public int Capacity { get; }

	/// <summary>
	/// Initializes a new instance of the <see cref="BoundedMpscQueue{T}"/> class.
	/// </summary>
	/// <param name="capacity">The requested capacity; rounded up to the next power of two (minimum 2).</param>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="capacity"/> is less than one.</exception>
	public BoundedMpscQueue(int capacity)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

		int size = NextPower2(Math.Max(capacity, 2));
		buffer = new Cell[size];
		mask = size - 1;
		Capacity = size;

		for (int i = 0; i < size; i++)
		{
			buffer[i].Sequence = i;
		}
	}

	/// <summary>
	/// Attempts to add an element to the queue.
	/// </summary>
	/// <param name="item">The element to add.</param>
	/// <returns><see langword="true"/> if the element was added; <see langword="false"/> if the queue was full.</returns>
	public bool TryEnqueue(T item)
	{
		while (true)
		{
			long pos = Volatile.Read(ref enqueuePos);
			int index = (int)pos & mask;
			long seq = Volatile.Read(ref buffer[index].Sequence);
			long diff = seq - pos;

			if (diff == 0)
			{
				// The cell is free and it is our turn to claim it.
				if (Interlocked.CompareExchange(ref enqueuePos, pos + 1, pos) == pos)
				{
					buffer[index].Item = item;
					Volatile.Write(ref buffer[index].Sequence, pos + 1);
					return true;
				}
			}
			else if (diff < 0)
			{
				// The cell still holds an unconsumed element: the queue is full.
				return false;
			}

			// diff > 0: another producer claimed this slot; retry with the refreshed cursor.
		}
	}

	/// <summary>
	/// Attempts to remove and return the oldest element in the queue.
	/// </summary>
	/// <param name="item">When this method returns <see langword="true"/>, contains the dequeued element; otherwise the default value.</param>
	/// <returns><see langword="true"/> if an element was removed; <see langword="false"/> if the queue was empty.</returns>
	public bool TryDequeue(out T? item)
	{
		while (true)
		{
			long pos = Volatile.Read(ref dequeuePos);
			int index = (int)pos & mask;
			long seq = Volatile.Read(ref buffer[index].Sequence);
			long diff = seq - (pos + 1);

			if (diff == 0)
			{
				if (Interlocked.CompareExchange(ref dequeuePos, pos + 1, pos) == pos)
				{
					item = buffer[index].Item;
					buffer[index].Item = default;
					Volatile.Write(ref buffer[index].Sequence, pos + mask + 1);
					return true;
				}
			}
			else if (diff < 0)
			{
				// The cell has not been published yet: the queue is empty.
				item = default;
				return false;
			}

			// diff > 0: another consumer took this slot; retry with the refreshed cursor.
		}
	}

	private static int NextPower2(int v)
	{
		v--;
		v |= v >> 1;
		v |= v >> 2;
		v |= v >> 4;
		v |= v >> 8;
		v |= v >> 16;
		v++;
		return v;
	}
}
