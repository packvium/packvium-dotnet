using System.Linq;
using Packvium.Domain;
using Xunit;

namespace Packvium.Tests.Domain;

/// <summary>Expected values were produced by the reference engine at v1.0.0, not derived by hand.</summary>
public sealed class HullTests
{
    private static readonly Vertex[] Wedge = [new(0, 0, 0), new(10, 0, 0), new(0, 10, 0), new(0, 0, 10)];

    [Fact]
    public void BuildsAWedgeWithTheReferenceAxesAndVolume()
    {
        var shape = HullShape.Of(Wedge);
        Assert.Equal(166, shape.Volume);
        Assert.Equal([new(0, 0, 1), new(0, 1, 0), new(1, 0, 0), new(1, 1, 1)], shape.FaceAxes.ToArray());
        Assert.Equal(
            [new(0, 0, 1), new(0, 1, -1), new(0, 1, 0), new(1, -1, 0), new(1, 0, -1), new(1, 0, 0)],
            shape.EdgeDirections.ToArray());
    }

    [Fact]
    public void ACubeHasOnlyTheThreeUnitAxes()
    {
        var cube = from x in new long[] { 0, 4 } from y in new long[] { 0, 4 } from z in new long[] { 0, 4 }
            select new Vertex(x, y, z);
        var shape = HullShape.Of(cube);
        Assert.Equal(64, shape.Volume);
        Assert.Equal([new(0, 0, 1), new(0, 1, 0), new(1, 0, 0)], shape.FaceAxes.ToArray());
    }

    [Fact]
    public void RotatesAsAProperRotationNotAMirror()
    {
        Vertex[] vertices = [new(0, 0, 0), new(10, 0, 0), new(0, 20, 0), new(0, 0, 30)];
        Assert.Equal([new(10, 0, 0), new(0, 0, 0), new(10, 0, 20), new(10, 30, 0)],
            Hull.Rotate(vertices, Rotation.LHW).ToArray());
    }

    [Fact]
    public void TouchingIsContactAndOverlapIsCollision()
    {
        var shape = HullShape.Of(Wedge);
        Assert.False(Hull.Collide(shape, new Vertex(0, 0, 0), shape, new Vertex(4, 4, 4)));
        Assert.True(Hull.Collide(shape, new Vertex(0, 0, 0), shape, new Vertex(2, 2, 2)));
    }

    [Theory]
    [InlineData("a convex hull needs at least 4 vertices, got 3", 0)]
    [InlineData("convex hull vertices are coplanar and enclose no volume", 1)]
    [InlineData("convex hull vertices must be unique", 2)]
    public void RefusesADegenerateHull(string message, int caseIndex)
    {
        Vertex[][] cases =
        [
            [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)],
            [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0)],
            [new(0, 0, 0), new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)],
        ];
        Assert.Equal(message, Assert.Throws<DegenerateHullException>(() => Hull.Validate(cases[caseIndex])).Message);
    }
}
