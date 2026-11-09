using LmuCareer.Core.Content;

namespace LmuCareer.Core.Careers;

/// <summary>An entry on LMU's grid the player can race as: a team running the car, and one of its numbers.</summary>
public sealed record CarEntry(string Team, string Number);

/// <summary>
/// Picking the season's livery in the app, before its first race: Factory Seat sets the race up in
/// LMU, so the player no longer picks it in the game's menus. Racing a different one still works,
/// as before: the matcher asks, and counting it makes that the season's car.
/// </summary>
public static class CarChoice
{
    /// <summary>
    /// The entries the player can pick for a round: the contract team's numbers when there is one,
    /// else every team in the class that runs the car. Empty when there's nothing to pick: a guest
    /// drive (the team's car), a Race Control custom team car, or once the season has raced.
    /// </summary>
    public static IReadOnlyList<CarEntry> For(Season season, Round round, ContentCatalog catalog)
    {
        if (round.Guest || round.Test is not null || HasRaced(season, round)) return [];
        var car = season.CarFor(round);
        if (car.CustomTeam) return [];
        var folder = car.Folder.Length > 0 ? car.Folder : catalog.CarByType(car.CarType)?.Folder ?? "";

        var contract = season.ContractFor(round);
        if (contract is { TeamName.Length: > 0 } && contract.Numbers.Count > 0)
            return contract.Numbers.Select(n => new CarEntry(contract.TeamName, n)).ToList();

        return catalog.Teams
            .Where(t => t.Class.Equals(car.CarClass, StringComparison.OrdinalIgnoreCase)
                && (contract is not { TeamName.Length: > 0 } || t.Name.Equals(contract.TeamName, StringComparison.OrdinalIgnoreCase))
                && (t.Cars.Count == 0 || t.Cars.Contains(folder, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(t => t.Cars.Count == 0 ? 1 : 0).ThenBy(t => t.Tier).ThenBy(t => t.Name, StringComparer.Ordinal)
            .SelectMany(t => t.Numbers.Select(n => new CarEntry(t.Name, n)))
            .DistinctBy(e => (e.Team, e.Number))
            .ToList();
    }

    /// <summary>Makes the entry the round's car for the season (or its other series): number and team.</summary>
    public static void Choose(Season season, Round round, CarEntry entry, ContentCatalog catalog)
    {
        if (!For(season, round, catalog).Contains(entry))
            throw new PlayerError("That car isn't one you can race this season.");
        if (season.SeriesOf(round) is { } series) series.Car = series.Car with { CarNumber = entry.Number, TeamName = entry.Team };
        else season.Car = season.Car with { CarNumber = entry.Number, TeamName = entry.Team };
    }

    /// <summary>Once a race of the round's championship has counted, the season's car is settled.</summary>
    private static bool HasRaced(Season season, Round round) =>
        season.SeriesRounds(round.SeriesId).Any(r => r.State == RoundState.Completed);
}
