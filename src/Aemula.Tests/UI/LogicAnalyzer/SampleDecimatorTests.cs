using System;
using System.Threading.Tasks;
using Aemula.UI.LogicAnalyzer;

namespace Aemula.Tests.UI.LogicAnalyzer;

public class SampleDecimatorTests
{
    private static ulong[] MakeBuffer(int capacity, Func<int, ulong> valueAt)
    {
        var buffer = new ulong[capacity];
        for (var i = 0; i < capacity; i++)
        {
            buffer[i] = valueAt(i);
        }
        return buffer;
    }

    [Test]
    public async Task MinMaxEnvelope_EvenlyDivisibleBuckets_ComputesPerBucketMinMax()
    {
        // 8 samples, 4 buckets of 2 samples each.
        var buffer = MakeBuffer(8, i => (ulong)(new[] { 5, 1, 3, 3, 9, 0, 4, 4 })[i]);

        var mins = new double[4];
        var maxes = new double[4];

        var actualBucketCount = SampleDecimator.ComputeMinMaxEnvelope(buffer, buffer.Length, 0, 8, 4, mins, maxes);

        await Assert.That(actualBucketCount).IsEqualTo(4);
        await Assert.That(mins).IsEquivalentTo(new double[] { 1, 3, 0, 4 });
        await Assert.That(maxes).IsEquivalentTo(new double[] { 5, 3, 9, 4 });
    }

    [Test]
    public async Task MinMaxEnvelope_CountNotEvenlyDivisibleByBucketCount_CoversEverySampleExactlyOnce()
    {
        // 10 samples split into 3 buckets: [0,3), [3,6), [6,10).
        var buffer = MakeBuffer(10, i => (ulong)i);

        var mins = new double[3];
        var maxes = new double[3];

        var actualBucketCount = SampleDecimator.ComputeMinMaxEnvelope(buffer, buffer.Length, 0, 10, 3, mins, maxes);

        await Assert.That(actualBucketCount).IsEqualTo(3);
        await Assert.That(mins).IsEquivalentTo(new double[] { 0, 3, 6 });
        await Assert.That(maxes).IsEquivalentTo(new double[] { 2, 5, 9 });

        var (start0, end0) = SampleDecimator.GetBucketRange(10, 3, 0);
        var (start1, end1) = SampleDecimator.GetBucketRange(10, 3, 1);
        var (start2, end2) = SampleDecimator.GetBucketRange(10, 3, 2);
        await Assert.That((start0, end0)).IsEqualTo((0, 3));
        await Assert.That((start1, end1)).IsEqualTo((3, 6));
        await Assert.That((start2, end2)).IsEqualTo((6, 10));
    }

    [Test]
    public async Task ConstantOrMixed_DetectsConstantAndMixedBuckets()
    {
        // Bucket 0: [7,7,7] constant. Bucket 1: [7,9] mixed.
        var buffer = MakeBuffer(5, i => (ulong)(new[] { 7, 7, 7, 7, 9 })[i]);

        var values = new ulong[2];
        var isMixed = new bool[2];

        var actualBucketCount = SampleDecimator.ComputeConstantOrMixed(buffer, buffer.Length, 0, 5, 2, values, isMixed);

        await Assert.That(actualBucketCount).IsEqualTo(2);
        await Assert.That(values[0]).IsEqualTo(7UL);
        await Assert.That(isMixed[0]).IsFalse();
        await Assert.That(values[1]).IsEqualTo(7UL);
        await Assert.That(isMixed[1]).IsTrue();
    }

    [Test]
    public async Task BucketCountAtLeastCount_DegeneratesToOneRawSamplePerBucket()
    {
        var buffer = MakeBuffer(4, i => (ulong)(i * 10));

        var mins = new double[10];
        var maxes = new double[10];

        var actualBucketCount = SampleDecimator.ComputeMinMaxEnvelope(buffer, buffer.Length, 0, 4, 10, mins, maxes);

        await Assert.That(actualBucketCount).IsEqualTo(4);
        for (var i = 0; i < actualBucketCount; i++)
        {
            await Assert.That(mins[i]).IsEqualTo((double)(i * 10));
            await Assert.That(maxes[i]).IsEqualTo((double)(i * 10));
        }
    }

    [Test]
    public async Task MinMaxEnvelope_RingBufferWraparound_ReadsCorrectValuesAcrossTheWrap()
    {
        // Capacity 5; phys slots hold abs values 5,6,7,3,4 respectively (abs %
        // capacity). visStart=3, count=5 covers abs 3..7 exactly, wrapping once
        // from phys 4 back to phys 0.
        var capacity = 5;
        var buffer = new ulong[] { 5, 6, 7, 3, 4 };

        var mins = new double[1];
        var maxes = new double[1];

        var actualBucketCount = SampleDecimator.ComputeMinMaxEnvelope(buffer, capacity, 3, 5, 1, mins, maxes);

        await Assert.That(actualBucketCount).IsEqualTo(1);
        // Covers abs 3,4,5,6,7 -> values 3,4,5,6,7.
        await Assert.That(mins[0]).IsEqualTo(3.0);
        await Assert.That(maxes[0]).IsEqualTo(7.0);
    }

    [Test]
    public async Task ConstantOrMixed_RingBufferWraparound_DetectsMixedAcrossTheWrap()
    {
        var capacity = 4;
        var buffer = new ulong[] { 1, 2, 9, 9 }; // phys 0..3

        // visStart=3, count=2 covers abs 3 (phys 3 -> 9) and abs 4 (phys 0 -> 1): mixed.
        var values = new ulong[1];
        var isMixed = new bool[1];

        var actualBucketCount = SampleDecimator.ComputeConstantOrMixed(buffer, capacity, 3, 2, 1, values, isMixed);

        await Assert.That(actualBucketCount).IsEqualTo(1);
        await Assert.That(isMixed[0]).IsTrue();
    }
}
