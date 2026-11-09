using System.Text.Json;

namespace Shirokuro.Application.Lobbies;

/// <summary>
/// Loads the canonical roster embedded from src/shared/heroes.json at build
/// time. This avoids a second, potentially drifting hero allow-list in .NET.
/// </summary>
public static class OriginalHeroRoster
{
    private const string ResourceName = "Shirokuro.Application.Heroes.OriginalHeroes.json";

    private static readonly Lazy<IReadOnlyList<string>> IdsHolder = new(LoadCore);

    public static IReadOnlyList<string> Ids => IdsHolder.Value;

    private static IReadOnlyList<string> LoadCore()
    {
        using var stream = typeof(OriginalHeroRoster).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Embedded original hero roster not found.");
        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement;

        if (root.GetProperty("schemaVersion").GetInt32() != 1)
        {
            throw new InvalidOperationException("Unsupported hero roster schema.");
        }

        var ids = root.GetProperty("heroes").EnumerateArray()
            .Select(h => h.GetProperty("id").GetString())
            .Select(id => string.IsNullOrWhiteSpace(id)
                ? throw new InvalidOperationException("Invalid hero ID in roster.") : id)
            .ToArray();

        if (ids.Length == 0 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
        {
            throw new InvalidOperationException("Empty or duplicate hero IDs in the source roster.");
        }

        return Array.AsReadOnly(ids);
    }
}

