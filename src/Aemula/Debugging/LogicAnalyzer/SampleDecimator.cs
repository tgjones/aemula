using System;

namespace Aemula.Debugging.LogicAnalyzer;

/// <summary>
/// Reduces a range of a <see cref="LogicAnalyzerRecorder"/> ring buffer down to a
/// fixed number of buckets, so a caller rendering to a bounded number of pixels
/// never has to look at more raw samples than that per frame. Pure and
/// ImGui/ImPlot-agnostic - see <see cref="Aemula.UI.LogicAnalyzer.LogicAnalyzerWindow"/>
/// for how the reduced buckets get turned into draw calls.
/// </summary>
public static class SampleDecimator
{
    /// <summary>
    /// The raw sample range covered by one bucket, as offsets from the range's own
    /// start (i.e. relative to whatever <c>visStart</c> a caller is using), for a
    /// range of <paramref name="count"/> samples split evenly into
    /// <paramref name="bucketCount"/> buckets. Both <see cref="ComputeMinMaxEnvelope"/>
    /// and <see cref="ComputeConstantOrMixed"/> partition their range using this same
    /// formula, so a caller mapping a bucket back to an x-pixel range (or a raw
    /// sample offset back to the bucket that covers it) gets boundaries that agree
    /// with what was actually reduced.
    /// </summary>
    public static (int Start, int End) GetBucketRange(int count, int bucketCount, int bucket)
    {
        var start = (int)((long)bucket * count / bucketCount);
        var end = (int)((long)(bucket + 1) * count / bucketCount);
        return (start, end);
    }

    /// <summary>
    /// For each bucket covering <paramref name="count"/> ring-buffer samples starting
    /// at absolute index <paramref name="visStart"/>, writes that bucket's min and max
    /// raw sample value into <paramref name="mins"/>/<paramref name="maxes"/> - the
    /// standard oscilloscope/waveform "envelope" reduction, which (unlike picking one
    /// representative sample per bucket) never aliases away a brief spike or toggle
    /// that falls inside a bucket wider than one screen pixel.
    /// </summary>
    /// <returns>
    /// The number of buckets actually written, which is <c>Math.Min(bucketCount, count)</c>
    /// - i.e. once there are fewer raw samples than requested buckets, this degenerates
    /// to one raw sample per bucket rather than producing empty buckets.
    /// </returns>
    public static int ComputeMinMaxEnvelope(
        ReadOnlySpan<ulong> buffer,
        int capacity,
        long visStart,
        int count,
        int bucketCount,
        Span<double> mins,
        Span<double> maxes)
    {
        if (count <= 0 || bucketCount <= 0)
        {
            return 0;
        }

        var actualBucketCount = Math.Min(bucketCount, count);
        var physStart = (int)(visStart % capacity);
        var wraps = (long)physStart + count > capacity;

        for (var bucket = 0; bucket < actualBucketCount; bucket++)
        {
            var (rangeStart, rangeEnd) = GetBucketRange(count, actualBucketCount, bucket);

            var min = double.PositiveInfinity;
            var max = double.NegativeInfinity;

            if (!wraps)
            {
                for (var i = rangeStart; i < rangeEnd; i++)
                {
                    var value = (double)buffer[physStart + i];
                    if (value < min) min = value;
                    if (value > max) max = value;
                }
            }
            else
            {
                for (var i = rangeStart; i < rangeEnd; i++)
                {
                    var value = (double)buffer[(int)((visStart + i) % capacity)];
                    if (value < min) min = value;
                    if (value > max) max = value;
                }
            }

            mins[bucket] = min;
            maxes[bucket] = max;
        }

        return actualBucketCount;
    }

    /// <summary>
    /// For each bucket covering <paramref name="count"/> ring-buffer samples starting
    /// at absolute index <paramref name="visStart"/>, writes that bucket's value into
    /// <paramref name="values"/> and whether every raw sample in the bucket actually
    /// shared that value into <paramref name="isMixed"/>. A parallel bool array is used
    /// rather than a sentinel "mixed" value, since bus values are arbitrary 64-bit
    /// patterns with no value left over to mean "mixed".
    /// </summary>
    /// <returns>
    /// The number of buckets actually written, which is <c>Math.Min(bucketCount, count)</c>
    /// - see <see cref="ComputeMinMaxEnvelope"/>'s remarks on the same point.
    /// </returns>
    public static int ComputeConstantOrMixed(
        ReadOnlySpan<ulong> buffer,
        int capacity,
        long visStart,
        int count,
        int bucketCount,
        Span<ulong> values,
        Span<bool> isMixed)
    {
        if (count <= 0 || bucketCount <= 0)
        {
            return 0;
        }

        var actualBucketCount = Math.Min(bucketCount, count);
        var physStart = (int)(visStart % capacity);
        var wraps = (long)physStart + count > capacity;

        for (var bucket = 0; bucket < actualBucketCount; bucket++)
        {
            var (rangeStart, rangeEnd) = GetBucketRange(count, actualBucketCount, bucket);

            var first = wraps
                ? buffer[(int)((visStart + rangeStart) % capacity)]
                : buffer[physStart + rangeStart];
            var mixed = false;

            if (!wraps)
            {
                for (var i = rangeStart + 1; i < rangeEnd; i++)
                {
                    if (buffer[physStart + i] != first)
                    {
                        mixed = true;
                        break;
                    }
                }
            }
            else
            {
                for (var i = rangeStart + 1; i < rangeEnd; i++)
                {
                    if (buffer[(int)((visStart + i) % capacity)] != first)
                    {
                        mixed = true;
                        break;
                    }
                }
            }

            values[bucket] = first;
            isMixed[bucket] = mixed;
        }

        return actualBucketCount;
    }
}
