using Shirokuro.Domain.Geometry;

namespace Shirokuro.Application.Preview;

/// <summary>Geometry-only preview; not a gameplay state or visibility model.</summary>
public sealed class BoardPreviewService
{
    public IReadOnlyList<HexCoordinate> Create(int radius)
    {
        if (radius is < 1 or > 6)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "Radius must be 1 to 6.");
        }

        var cells = new List<HexCoordinate>();
        for (var q = -radius; q <= radius; q++)
        {
            var rMin = Math.Max(-radius, -q - radius);
            var rMax = Math.Min(radius, -q + radius);
            for (var r = rMin; r <= rMax; r++)
            {
                cells.Add(new HexCoordinate(q, r));
            }
        }

        return cells;
    }
}
