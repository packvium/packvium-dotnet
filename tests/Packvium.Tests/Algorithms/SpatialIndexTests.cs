using System;
using System.Collections.Generic;
using System.Linq;
using Packvium.Algorithms;
using Xunit;

namespace Packvium.Tests.Algorithms;

/// <summary>
/// The index's answer cache (<c>test_spatial_index.py</c>): an answer kept across an insertion or
/// a fork must be the answer a freshly built index gives, and the cache stays bounded.
/// </summary>
public sealed class SpatialIndexTests
{
    private static SpatialIndex Build(IEnumerable<Bound> bounds, long side)
    {
        var index = new SpatialIndex(side, side, side);
        var position = 0;
        foreach (var bound in bounds)
        {
            index.Add(position++, bound);
        }
        return index;
    }

    private static int[] Query(SpatialIndex index, Bound box) =>
        [.. index.Query(box.X1, box.Y1, box.Z1, box.X2, box.Y2, box.Z2)];

    private static Bound RandomBound(Random random, long side)
    {
        var x = random.Next(0, (int)side - 1);
        var y = random.Next(0, (int)side - 1);
        var z = random.Next(0, (int)side - 1);
        return new Bound(x, y, z, random.Next(x + 1, (int)side + 1), random.Next(y + 1, (int)side + 1),
            random.Next(z + 1, (int)side + 1));
    }

    [Fact]
    public void AnInsertionOnAForkLeavesTheOriginalsAnswersAlone()
    {
        var index = new SpatialIndex(80, 80, 80);
        var box = new Bound(0, 0, 0, 20, 20, 20);
        index.Add(0, box);
        Assert.Equal([0], Query(index, box));
        var clone = index.Copy();
        clone.Add(1, new Bound(0, 0, 0, 5, 5, 5));
        Assert.Equal([0, 1], Query(clone, box));
        Assert.Equal([0], Query(index, box));
    }

    [Fact]
    public void TheQueryCacheIsBoundedEvenWithoutInsertions()
    {
        var index = new SpatialIndex(80, 80, 80);
        index.Add(0, new Bound(0, 0, 0, 20, 20, 20));
        for (var x = 0; x < 1100; x++)
        {
            index.Query(x * 10, 0, 0, (x * 10) + 20, 1, 1);
        }
        Assert.True(index.CachedAnswers <= 1024);
        Assert.Equal([0], Query(index, new Bound(0, 0, 0, 20, 20, 20)));
    }

    [Fact]
    public void AnInsertionCarriesOverABoundedNumberOfAnswersAndEachIsStillRight()
    {
        var first = new Bound(0, 0, 0, 20, 20, 20);
        var index = Build([first], 80);
        var spans = (from low in Enumerable.Range(0, 4) from high in Enumerable.Range(low + 1, 4 - low)
                     select (Low: low * 10L, High: high * 10L)).ToList();
        var queries = (from x in spans from y in spans from z in spans
                       select new Bound(x.Low, y.Low, z.Low, x.High, y.High, z.High)).ToList();
        foreach (var query in queries)
        {
            Query(index, query);
        }
        Assert.True(index.CachedAnswers > 128);
        var fork = index.Copy();
        var inserted = new Bound(70, 70, 70, 80, 80, 80);
        var forkInserted = new Bound(10, 10, 10, 30, 30, 30);
        index.Add(1, inserted);
        fork.Add(1, forkInserted);
        Assert.Equal(128, index.CachedAnswers);
        Assert.Equal(128, fork.CachedAnswers);
        var fresh = Build([first, inserted], 80);
        var freshFork = Build([first, forkInserted], 80);
        foreach (var query in queries.Append(inserted).Append(forkInserted))
        {
            Assert.Equal(Query(fresh, query), Query(index, query));
            Assert.Equal(Query(freshFork, query), Query(fork, query));
        }
    }

    public static TheoryData<int> Seeds => [.. Enumerable.Range(0, 10)];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void CachedQueriesSurviveInsertionsAndDivergentForks(int seed)
    {
        var random = new Random(seed);
        var bounds = new List<Bound>();
        var index = new SpatialIndex(100, 100, 100);
        var queries = Enumerable.Range(0, 30).Select(_ => RandomBound(random, 100)).ToList();
        queries.AddRange([new Bound(0, 0, 0, 100, 100, 100), new Bound(-10, -10, -10, 20, 20, 20)]);
        for (var step = 0; step < 20; step++)
        {
            var parent = index.Copy();
            var parentAnswers = queries.Select(query => Query(parent, query)).ToList();
            var bound = RandomBound(random, 100);
            index.Add(bounds.Count, bound);
            bounds.Add(bound);
            var fresh = Build(bounds, 100);
            for (var position = 0; position < queries.Count; position++)
            {
                var query = queries[position];
                Assert.Equal(parentAnswers[position], Query(parent, query));
                Assert.Equal(Query(fresh, query), Query(index, query));
                var overlapping = Enumerable.Range(0, bounds.Count).Where(i => Overlaps(bounds[i], query));
                Assert.Superset(overlapping.ToHashSet(), Query(index, query).ToHashSet());
            }
        }
    }

    private static bool Overlaps(Bound a, Bound b) =>
        a.X1 < b.X2 && b.X1 < a.X2 && a.Y1 < b.Y2 && b.Y1 < a.Y2 && a.Z1 < b.Z2 && b.Z1 < a.Z2;

    /// <summary>
    /// The cheapest-first count over prefix sums is exactly the largest number of items the
    /// resource admits, zero costs included (<c>test_solvers.py</c>).
    /// </summary>
    [Fact]
    public void TheAdditiveCardinalityBoundIsAnAdmissibleRelaxation()
    {
        foreach (var costs in new[] { new long[] { 2, 3, 5 }, [1, 4, 4, 9], [3, 3, 3, 7, 8], [0, 0, 2, 6] })
        {
            var sums = costs.Order().Select(cost => (Int128)cost).ToArray();
            for (var index = 1; index < sums.Length; index++)
            {
                sums[index] += sums[index - 1];
            }
            for (var capacity = 0L; capacity <= costs.Sum(); capacity++)
            {
                var exact = Enumerable.Range(0, 1 << costs.Length)
                    .Where(mask => Enumerable.Range(0, costs.Length).Where(bit => (mask >> bit & 1) == 1).Sum(bit => costs[bit]) <= capacity)
                    .Max(mask => int.PopCount(mask));
                Assert.Equal(exact, BeamPack.CountWithCapacity(sums, capacity));
            }
        }
    }
}
