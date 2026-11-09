using Shirokuro.Application.Preview;
using Shirokuro.Domain.Geometry;

namespace Shirokuro.Domain.Tests.Geometry;

public sealed class HexCoordinateTests
{
    [Theory]
    [InlineData(0, 0, 0, 0, 0)]
    [InlineData(0, 0, 1, 0, 1)]
    [InlineData(0, 0, -2, 1, 2)]
    [InlineData(2, -1, -1, 1, 3)]
    public void Distance_IsSymmetricAndCorrect(int q1, int r1, int q2, int r2, int distance)
    {
        var a = new HexCoordinate(q1, r1);
        var b = new HexCoordinate(q2, r2);
        Assert.Equal(distance, a.DistanceTo(b));
        Assert.Equal(distance, b.DistanceTo(a));
    }

    [Fact]
    public void Neighbors_AreSixDistinctAdjacentCoordinates()
    {
        var origin = new HexCoordinate(3, -2);
        var neighbors = origin.Neighbors().ToArray();

        Assert.Equal(6, neighbors.Distinct().Count());
        Assert.All(neighbors, neighbor => Assert.Equal(1, origin.DistanceTo(neighbor)));
    }

    [Theory]
    [InlineData(1, 7)]
    [InlineData(2, 19)]
    [InlineData(3, 37)]
    public void Preview_ContainsExpectedHexCount(int radius, int expected)
    {
        var cells = new BoardPreviewService().Create(radius);
        Assert.Equal(expected, cells.Count);
        Assert.All(cells, cell => Assert.True(cell.DistanceTo(default) <= radius));
        Assert.Equal(cells.Count, cells.Distinct().Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(7)]
    public void Preview_RejectsOutOfRangeRadius(int radius)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoardPreviewService().Create(radius));
    }
}
