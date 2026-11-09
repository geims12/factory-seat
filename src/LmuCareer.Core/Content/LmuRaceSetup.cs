using System.Text.Json.Nodes;
using LmuCareer.Core.Careers;

namespace LmuCareer.Core.Content;

/// <summary>One entry of LMU's car list (<c>GET /rest/sessions/getAllVehicles</c>): a car in one livery on one season's grid.</summary>
/// <param name="Id">What LMU's session preset puts in its grid, e.g. 21_26_AFCO31910023.</param>
/// <param name="Folder">The car's folder under Installed\Vehicles, from the .VEH path.</param>
/// <param name="Description">LMU's name for the entry, e.g. "Vista AF Corse 2026 #21:LM": team, year and number, then the livery after the colon.</param>
/// <param name="Classes">LMU's tags: the model, the class and the season grid, e.g. Ferrari_296_LMGT3, GT3, WEC2026.</param>
public sealed record LmuVehicle(string Id, string Folder, string Description, IReadOnlyList<string> Classes, string Number, string Team, bool Owned)
{
    /// <summary>The entry without its livery: one car on the grid ("Vista AF Corse 2026 #21").</summary>
    public string Entry => Description.Split(':')[0];

    /// <summary>The livery after the colon: LM (Le Mans), WEC, ELMS and the like; empty when there's none.</summary>
    public string Livery => Description.Contains(':') ? Description[(Description.IndexOf(':') + 1)..] : "";

    public bool OnGrid(string tag) => Classes.Contains(tag, StringComparer.OrdinalIgnoreCase);

    public static LmuVehicle? From(JsonNode? node)
    {
        if (node is not JsonObject o || o["id"]?.ToString() is not { Length: > 0 } id) return null;
        var vehFile = o["vehFile"]?.ToString() ?? "";
        var parts = vehFile.Split('\\', '/');
        var at = Array.FindIndex(parts, p => p.Equals("Vehicles", StringComparison.OrdinalIgnoreCase));
        var folder = at >= 0 && at + 1 < parts.Length ? parts[at + 1] : "";
        var classes = o["classes"] is JsonArray a ? a.Select(c => c?.ToString() ?? "").Where(c => c.Length > 0).ToList() : [];
        return new LmuVehicle(id, folder, o["desc"]?.ToString() ?? "", classes,
            o["number"]?.ToString() ?? "", o["fullTeam"]?.ToString() ?? o["team"]?.ToString() ?? "",
            o["isOwned"] is JsonValue owned && owned.TryGetValue(out bool isOwned) ? isOwned : true);
    }
}

/// <summary>A track layout in LMU's list (<c>GET /rest/sessions/getTracksAll</c>).</summary>
/// <param name="Id">What <c>POST /rest/race/track</c> takes.</param>
/// <param name="SceneDesc">The layout's key, e.g. SPAWEC_ENDCE.</param>
/// <param name="Folder">LMU's cmpName, which is the track's folder under Installed\Locations.</param>
public sealed record LmuTrack(string Id, string SceneDesc, string Folder)
{
    public static LmuTrack? From(JsonNode? node) =>
        node is JsonObject o && o["id"]?.ToString() is { Length: > 0 } id && o["sceneDesc"]?.ToString() is { Length: > 0 } scene
            ? new LmuTrack(id, scene, o["cmpName"]?.ToString() ?? "")
            : null;
}

/// <summary>
/// Practice and qualifying for a race weekend set up from the app: lengths in minutes (0 leaves
/// the session out) and each session's time scale, which in LMU is how fast the track rubbers in
/// (0 static, 1 normal, 2 twice as fast...), not the clock.
/// </summary>
public sealed record LmuSessions(int PracticeMinutes, double PracticeTimeScale, int QualifyingMinutes, double QualifyingTimeScale)
{
    public static readonly LmuSessions Default = new(30, 0, 20, 1);

    public static readonly IReadOnlyList<int> PracticeLengths = [10, 15, 20, 30, 45, 60, 90, 120, 180];
    public static readonly IReadOnlyList<int> QualifyingLengths = [10, 15, 20, 30, 45, 60];
    public static readonly IReadOnlyList<double> TimeScales = [0, 1, 2, 3, 5];

    /// <summary>The nearest values the app offers: a session is off or one of the lengths.</summary>
    public LmuSessions Normalized() => new(
        Snap(PracticeMinutes, PracticeLengths), TimeScales.MinBy(x => Math.Abs(x - PracticeTimeScale)),
        Snap(QualifyingMinutes, QualifyingLengths), TimeScales.MinBy(x => Math.Abs(x - QualifyingTimeScale)));

    private static int Snap(int minutes, IReadOnlyList<int> lengths) =>
        minutes <= 0 ? 0 : lengths.MinBy(l => Math.Abs(l - minutes));
}

/// <summary>
/// Everything Factory Seat sends LMU to set up a round's race, worked out from its briefing.
/// See docs/lmu-game-rest-api.md for the calls and fields.
/// </summary>
/// <param name="Championship">LMU's championship template, which sets the BoP: wec2026, elms2025...</param>
/// <param name="Grid">Vehicle ids, the player's car first.</param>
/// <param name="StartMinutes">Race start, minutes from midnight; -1 for the track's default.</param>
/// <param name="AiStrength">New Game+'s AI Strength; null leaves the player's own.</param>
/// <param name="AiAggression">New Game+'s AI aggression (2 = High); null leaves the player's own.</param>
/// <param name="Damage">New Game+'s damage floor in percent; null leaves the preset's.</param>
public sealed record LmuRacePlan(
    string SceneDesc,
    string Championship,
    LmuVehicle Player,
    IReadOnlyList<string> Grid,
    int RaceMinutes,
    int TimeScale,
    int FuelUsage,
    int TyreWear,
    int StartMinutes,
    int? AiStrength,
    int? AiAggression,
    int? Damage,
    bool Wet)
{
    public LmuSessions Sessions { get; init; } = LmuSessions.Default;
}

public static class LmuRaceSetup
{
    /// <summary>
    /// LMU's key for each layout, by track folder and layout file. Results files only name the
    /// layout file and LMU's track list only the key, and the layout packages are encrypted, so this
    /// is kept by hand. Imola's Grand Prix layout is confirmed by a race; the rest follow the same naming.
    /// </summary>
    private static readonly Dictionary<(string Folder, string Layout), string> Scenes = new(new FolderLayoutComparer())
    {
        [("BahrainWEC_2023", "layoutBahrain")] = "BAHRAINWEC",
        [("BahrainWEC_2023", "layoutEndurance")] = "BAHRAINWEC_ENDCE",
        [("BahrainWEC_2023", "layoutOuter")] = "BAHRAINWEC_OUTER",
        [("BahrainWEC_2023", "layoutPaddock")] = "BAHRAINWEC_PADDOCK",
        [("Barcelona_2025", "layoutELMS")] = "BARCELONAELMS",
        [("CotAWEC_2024", "layoutCota")] = "COTAWEC",
        [("CotAWEC_2024", "layoutNational")] = "COTAWEC_NATIONAL",
        [("Daytona_2026", "layoutDaytona")] = "DAYTONARC",
        [("FujiWEC_2023", "layoutFuji")] = "FUJIWEC",
        [("FujiWEC_2023", "layoutFujiCl")] = "FUJIWEC_CL",
        [("ImolaWEC_2024", "layoutImola")] = "IMOLAWEC",
        [("ImolaWEC_2024", "layoutImolaELMS")] = "IMOLAELMS",
        [("Interlagos_2024", "layoutInterlagos")] = "INTERLAGOSWEC",
        [("LagunaSeca_2026", "layoutLaguna")] = "LAGUNASECA",
        [("LeMans_2023", "layoutLeMans")] = "LEMANSWEC",
        [("LeMans_2023", "layoutMulsanne")] = "LEMANSWEC_MULSANNE",
        [("LongBeach_2026", "layoutLongBeach")] = "LONGBEACH",
        [("Monza_2023", "layoutMonza")] = "MONZAWEC",
        [("Monza_2023", "layoutGrande")] = "MONZAWEC_GRANDE",
        [("PaulRicard_2025", "layoutELMS")] = "PAULRICARDELMS",
        [("PaulRicard_2025", "layout1A")] = "PAULRICARD1A",
        [("PaulRicard_2025", "layout1AV2")] = "PAULRICARD1A-V2",
        [("PaulRicard_2025", "layout1AV2Short")] = "PAULRICARD1A-V2-SHORT",
        [("PaulRicard_2025", "layout3A")] = "PAULRICARD3A",
        [("PortimaoWEC_2023", "layoutPortimao")] = "PORTIMAOWEC",
        [("PortimaoWEC_2023", "layoutPortimaoELMS")] = "PORTIMAOELMS",
        [("Qatar_2024", "layoutQatar")] = "QATARWEC",
        [("Qatar_2024", "layoutQatarShort")] = "QATARWEC_SHORT",
        [("RoadAtlanta_2026", "layoutRoadAtlanta")] = "ROADATLANTA",
        [("Sebring_2023", "layoutSebring")] = "SEBRINGWEC",
        [("Sebring_2023", "layoutSchool")] = "SEBRINGWEC_SCHOOL",
        [("Silverstone_2025", "layoutELMS")] = "SILVERSTONEELMS",
        [("Silverstone_2025", "layoutWEC")] = "SILVERSTONEWEC",
        [("Silverstone_2025", "layoutInternational")] = "SILVERSTONE_INTERNATIONAL",
        [("Silverstone_2025", "layoutNational")] = "SILVERSTONE_NATIONAL",
        [("Spa_2023", "layoutSpa")] = "SPAWEC",
        [("Spa_2023", "layoutSpaELMS")] = "SPAELMS",
        [("Spa_2023", "layoutSpaEnd")] = "SPAWEC_ENDCE",
    };

    /// <summary>
    /// The sky for a wet round's race. LMU's first rainy sky is 5 (a drizzle that set a 75% rain
    /// chance); 8 is rFactor 2's "overcast and rain", heavy enough to put cars on wet tyres, which
    /// is what makes a New Game+ round count as wet. Not yet confirmed in LMU.
    /// </summary>
    public const int WetSky = 8;

    public static string? Scene(string folder, string layout) => Scenes.GetValueOrDefault((folder, layout));

    /// <summary>
    /// The LMU season grid a round is raced on, as LMU tags it (WEC2026, ELMS2025): the event's
    /// championship, in the newest year the car races it (2023 for GTE Classic). Null for IMSA and
    /// guest drives, which LMU doesn't run as a championship template.
    /// </summary>
    public static (string Series, int Year)? Grid(EventInfo? ev, CarInfo? car)
    {
        if (ev is { Imsa: true } || ev?.Series is "guest" or "imsa") return null;
        var seasons = car?.Seasons;
        if (ev is { Classic: true }) return ("WEC", 2023);

        var wanted = ev?.Series switch
        {
            "world" or "world-base" => "WEC",
            "european" or "european-base" => "ELMS",
            _ => null,
        };
        string? series = wanted is not null && seasons?.ContainsKey(wanted) == true ? wanted
            : seasons?.ContainsKey("WEC") == true ? "WEC"
            : seasons?.ContainsKey("ELMS") == true ? "ELMS"
            : wanted;
        if (series is null) return null;
        var year = seasons?.GetValueOrDefault(series) is { Count: > 0 } years ? years.Max() : 0;
        return year > 0 ? (series, year) : null;
    }

    /// <summary>Works out the race to set up; a <see cref="PlayerError"/> says what the player has to do by hand instead.</summary>
    public static LmuRacePlan Plan(BriefingRound round, EventInfo? ev, CarInfo? carInfo, IReadOnlyList<LmuVehicle> vehicles,
        LmuSessions? sessions = null)
    {
        if (round.Test is not null) throw new PlayerError("Test days are run in practice: set this one up in LMU yourself.");
        var scene = Scene(round.Track.Folder, round.Track.LayoutFile)
            ?? throw new PlayerError("Factory Seat doesn't know this layout in LMU's track list yet: set this race up in LMU yourself.");
        if (Grid(ev, carInfo) is not var (series, year))
            throw new PlayerError("This race isn't on a WEC or ELMS grid in LMU: set it up in LMU yourself.");
        var car = round.Car;
        if (car.CustomTeam) throw new PlayerError("Factory Seat can't pick a Race Control custom team car: set this race up in LMU yourself.");
        if (car.Folder is not { Length: > 0 } folder) throw new PlayerError("Factory Seat doesn't know your car's folder yet: set this race up in LMU yourself.");

        var tag = $"{series}{year}";
        var leMans = IsLeMans(round.Track.Folder);
        int LiveryRank(LmuVehicle v) => Rank(v, series, leMans);

        var onGrid = vehicles.Where(v => v.OnGrid(tag)).ToList();
        var numbers = car.Number is { Length: > 0 } number ? [number] : car.LiveryNumbers;
        var player = Entry(onGrid, folder, numbers, car.Team, series, leMans)
            ?? throw new PlayerError("LMU's {grid} grid has no {car}{number}: set this race up in LMU yourself.",
                ("grid", $"{series} {year}"), ("car", car.Name), ("number", car.Number is { Length: > 0 } n ? " #" + n : ""));

        // The rest of the season's grid, one livery per car, cars the player owns only. Entries LMU
        // only has in a Le Mans livery (the WEC's LMP2s, one-off GT3s) race Le Mans and nowhere else.
        var field = onGrid
            .Where(v => v.Owned && v.Entry != player.Entry)
            .GroupBy(v => v.Entry)
            .Where(g => leMans || !g.All(v => v.Livery.Equals("LM", StringComparison.OrdinalIgnoreCase)))
            .Select(g => g.OrderBy(LiveryRank).First())
            .OrderBy(v => v.Description, StringComparer.Ordinal)
            .Select(v => v.Id);

        var floor = round.Floor;
        return new LmuRacePlan(
            scene,
            $"{series.ToLowerInvariant()}{year}",
            player,
            [player.Id, .. field],
            round.Settings.RaceMinutes,
            Math.Clamp(round.Settings.TimeScale, 1, RaceFormats.MaxTimeScale),
            round.Settings.FuelUsage,
            round.Settings.TyreWear,
            StartMinutes(round.Settings.StartTime),
            floor?.AiStrength,
            floor?.AiAggression,
            floor?.MinDamage,
            floor?.Wet == true)
        {
            Sessions = (sessions ?? LmuSessions.Default).Normalized(),
        };
    }

    /// <summary>
    /// The player's entry on a season's grid, in the livery the race uses: the car, one of the
    /// numbers (any when there are none), the team's own entry first, then Le Mans' livery at Le Mans
    /// and the series' elsewhere. Null when LMU's grid doesn't have it.
    /// </summary>
    public static LmuVehicle? Entry(IEnumerable<LmuVehicle> onGrid, string folder, IReadOnlyList<string> numbers, string? team,
        string series, bool leMans) =>
        onGrid
            .Where(v => v.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase) && (numbers.Count == 0 || numbers.Contains(v.Number)))
            .OrderBy(v => team is { Length: > 0 } && v.Team.Equals(team, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(v => Rank(v, series, leMans))
            .FirstOrDefault();

    /// <summary>
    /// The livery an entry shows in a round, for the car picker: the same one Set it up in LMU for
    /// me loads. Null when the round isn't on a grid the app sets up, or LMU's list doesn't have it.
    /// </summary>
    public static LmuVehicle? Livery(string trackFolder, EventInfo? ev, CarInfo? car, string carFolder, CarEntry entry,
        IReadOnlyList<LmuVehicle> vehicles)
    {
        if (Grid(ev, car) is not var (series, year)) return null;
        var tag = $"{series}{year}";
        return Entry(vehicles.Where(v => v.OnGrid(tag)), carFolder, [entry.Number], entry.Team, series, IsLeMans(trackFolder));
    }

    private static bool IsLeMans(string trackFolder) => trackFolder.StartsWith("LeMans", StringComparison.OrdinalIgnoreCase);

    /// <summary>Lower is better: Le Mans' livery at Le Mans; the series' livery elsewhere, then any but Le Mans'.</summary>
    private static int Rank(LmuVehicle v, string series, bool leMans) =>
        leMans ? (v.Livery.Equals("LM", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            : v.Livery.Equals(series, StringComparison.OrdinalIgnoreCase) ? 0 : v.Livery.Equals("LM", StringComparison.OrdinalIgnoreCase) ? 2 : 1;

    /// <summary>"13:00" as minutes from midnight; -1 (LMU's track default) when there's none.</summary>
    public static int StartMinutes(string? time) =>
        TimeOnly.TryParseExact(time ?? "", "HH:mm", out var t) ? t.Hour * 60 + t.Minute : -1;

    /// <summary>
    /// Writes the plan into a session preset from <c>requestPreset</c>, for <c>applyPreset</c> and
    /// <c>generateSaveFileFromSessionPreset</c>. Practice and qualifying are as the player chose
    /// them in the app; the rest of the preset is left as LMU had it.
    /// </summary>
    public static void ApplyTo(JsonObject preset, LmuRacePlan plan)
    {
        var player = Child(preset, "Player");
        var options = Child(player, "Game Options");
        var conditions = Child(player, "Race Conditions");

        preset["Grid"] = new JsonArray(plan.Grid.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray());

        options["Race Time"] = plan.RaceMinutes;
        options["Race Finish Criteria"] = 2; // timed
        options["Race Length"] = plan.RaceMinutes / 1440.0;
        options["Fuel Consumption Multiplier"] = plan.FuelUsage;
        options["Tire Wear Multiplier"] = plan.TyreWear;
        if (plan.Damage is { } damage) options["Damage Multiplier"] = damage;

        conditions["RaceTimeScale"] = plan.TimeScale;
        conditions["RaceStartingTime"] = plan.StartMinutes;
        // The sessions' time scales are how fast the track rubbers in, not the clock. LMU keeps the
        // player's last choice, so each is set: the race's at normal.
        var sessions = plan.Sessions;
        conditions["RealRoadTimeScalePractice"] = sessions.PracticeTimeScale;
        conditions["RealRoadTimeScaleQualifying"] = sessions.QualifyingTimeScale;
        conditions["RealRoadTimeScaleRace"] = 1.0;
        conditions["Run Practice1"] = sessions.PracticeMinutes > 0;
        conditions["Run Practice2"] = false;
        conditions["Run Practice3"] = false;
        conditions["Run Practice4"] = false;
        conditions["Run Warmup"] = false;
        conditions["Num Qual Sessions"] = sessions.QualifyingMinutes > 0 ? 1 : 0;
        conditions["Num Race Sessions"] = 1;
        if (sessions.PracticeMinutes > 0) options["practice length"] = sessions.PracticeMinutes;
        if (sessions.QualifyingMinutes > 0) options["qualifying length"] = sessions.QualifyingMinutes;

        if (plan.Wet && preset["Weather"]?["Race"]?["Weather"] is JsonArray slots)
        {
            foreach (var slot in slots.OfType<JsonObject>())
            {
                slot["Sky"] = WetSky;
                slot["RainChance"] = 100;
            }
        }
    }

    private static JsonObject Child(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject child) return child;
        child = new JsonObject();
        parent[name] = child;
        return child;
    }

    private sealed class FolderLayoutComparer : IEqualityComparer<(string Folder, string Layout)>
    {
        public bool Equals((string Folder, string Layout) a, (string Folder, string Layout) b) =>
            a.Folder.Equals(b.Folder, StringComparison.OrdinalIgnoreCase) && a.Layout.Equals(b.Layout, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Folder, string Layout) key) =>
            HashCode.Combine(key.Folder.ToUpperInvariant(), key.Layout.ToUpperInvariant());
    }
}
