namespace Shirokuro.Domain.Geometry;

/// <summary>Axial hex coordinate; the third cube component is -Q-R.</summary>
public readonly record struct HexCoordinate(int Q, int R)
{
    public int S => -Q - R;

    public int DistanceTo(HexCoordinate other)
    {
        return (Math.Abs(Q - other.Q) + Math.Abs(R - other.R) + Math.Abs(S - other.S)) / 2;
    }

    public IEnumerable<HexCoordinate> Neighbors()
    {
        yield return new HexCoordinate(Q + 1, R);
        yield return new HexCoordinate(Q + 1, R - 1);
        yield return new HexCoordinate(Q, R - 1);
        yield return new HexCoordinate(Q - 1, R);
        yield return new HexCoordinate(Q - 1, R + 1);
        yield return new HexCoordinate(Q, R + 1);
    }
}
