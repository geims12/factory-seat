using System.Text.Json.Nodes;
using LmuCareer.Core;
using LmuCareer.Core.Careers;
using LmuCareer.Core.Content;

namespace LmuCareer.Core.Tests;

/// <summary>
/// Setting a round up in LMU through its local API. The fixtures are LMU's own lists and session
/// preset from build 14200 (November 2026), trimmed to the fields the app reads.
/// </summary>
public sealed class LmuRaceSetupTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private static readonly ContentCatalog Catalog = ContentCatalog.Default;

    private static readonly IReadOnlyList<LmuVehicle> Vehicles =
        JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures, "lmu-vehicles.json")))!.AsArray()
            .Select(LmuVehicle.From).OfType<LmuVehicle>().ToList();

    private static readonly IReadOnlyList<LmuTrack> Tracks =
        JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures, "lmu-tracks.json")))!.AsArray()
            .Select(LmuTrack.From).OfType<LmuTrack>().ToList();

    private static JsonObject Preset() => JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures, "lmu-preset.json")))!.AsObject();

    private static BriefingRound Round(
        string folder = "ImolaWEC_2024", string layout = "layoutImola", string carFolder = "Ferrari_296GT3_2023",
        string carName = "Ferrari 296", string? number = "21", string? team = "Vista AF Corse", bool customTeam = false,
        IReadOnlyList<string>? liveryNumbers = null, string? startTime = "13:00", BriefingFloor? floor = null) =>
        new(1, 8, RoundState.Upcoming, null, false,
            new BriefingEvent("imola-6", "Imola 6 Hours", 6, 1),
            new BriefingTrack(folder, "Imola", layout, "Grand Prix", "Autodromo Enzo e Dino Ferrari"),
            new BriefingCar("GT3", carFolder, ["Ferrari 296 LMGT3"], carName, number, customTeam, team, liveryNumbers ?? []),
            new BriefingSettings(30, 2, 3, 12, startTime),
            new BriefingPitPlan(2, 15, 1))
        {
            Floor = floor,
        };

    private static EventInfo Event(string series) => Catalog.Events.First(e => e.Series == series && !e.Imsa && !e.Classic);

    private static LmuRacePlan Plan(BriefingRound round, string series = "world") =>
        LmuRaceSetup.Plan(round, Event(series), Catalog.Car(round.Car.Folder!), Vehicles);

    [Fact]
    public void Every_catalog_layout_has_a_scene_that_LMU_lists_for_that_track()
    {
        foreach (var track in Catalog.Tracks)
        {
            foreach (var layout in track.Layouts.Keys)
            {
                var scene = LmuRaceSetup.Scene(track.Folder, layout);
                Assert.True(scene is not null, $"no scene for {track.Folder}/{layout}");
                var listed = Tracks.SingleOrDefault(t => t.SceneDesc == scene);
                Assert.True(listed is not null, $"{scene} isn't in LMU's track list");
                Assert.Equal(track.Folder, listed.Folder);
            }
        }
    }

    [Fact]
    public void Scenes_are_one_per_layout()
    {
        var scenes = Catalog.Tracks.SelectMany(t => t.Layouts.Keys.Select(l => LmuRaceSetup.Scene(t.Folder, l))).ToList();
        Assert.Equal(scenes.Count, scenes.Distinct().Count());
        Assert.Equal("SPAWEC_ENDCE", LmuRaceSetup.Scene("spa_2023", "LAYOUTSPAEND"));
    }

    [Fact]
    public void A_WEC_round_races_on_the_newest_WEC_grid_the_car_is_on()
    {
        Assert.Equal(("WEC", 2026), LmuRaceSetup.Grid(Event("world"), Catalog.Car("Ferrari_296GT3_2023")));
        Assert.Equal(("ELMS", 2026), LmuRaceSetup.Grid(Event("european"), Catalog.Car("Ferrari_296GT3_2023")));
    }

    [Fact]
    public void A_car_without_the_events_series_races_on_its_own()
    {
        var elmsOnly = Catalog.Cars.First(c => c.Seasons is { } s && s.ContainsKey("ELMS") && !s.ContainsKey("WEC"));
        Assert.Equal("ELMS", LmuRaceSetup.Grid(Event("world"), elmsOnly)?.Series);
    }

    [Fact]
    public void GTE_Classic_is_the_2023_WEC_and_IMSA_is_not_set_up()
    {
        var classic = Catalog.Events.First(e => e.Classic);
        var gte = Catalog.Cars.First(c => c.Class == "GTE");
        Assert.Equal(("WEC", 2023), LmuRaceSetup.Grid(classic, gte));
        Assert.Null(LmuRaceSetup.Grid(Catalog.Events.First(e => e.Imsa), Catalog.Car("Ferrari_296GT3_2023")));
    }

    [Fact]
    public void The_plan_picks_the_players_entry_in_the_series_livery_and_the_rest_of_the_grid()
    {
        var plan = Plan(Round());

        Assert.Equal("IMOLAWEC", plan.SceneDesc);
        Assert.Equal("wec2026", plan.Championship);
        Assert.Equal("Vista AF Corse 2026 #21:WEC", plan.Player.Description);
        Assert.Equal(plan.Player.Id, plan.Grid[0]);

        var grid = plan.Grid.Skip(1).Select(id => Vehicles.Single(v => v.Id == id)).ToList();
        Assert.All(grid, v => Assert.True(v.OnGrid("WEC2026")));
        Assert.DoesNotContain(grid, v => v.Entry == plan.Player.Entry);
        Assert.Equal(grid.Count, grid.Select(v => v.Entry).Distinct().Count());
        Assert.Contains(grid, v => v.Classes.Contains("Hypercar"));
        Assert.Contains(grid, v => v.Entry == "Vista AF Corse 2026 #54");
        // Le Mans-only entries stay home: no LMP2 at Imola.
        Assert.DoesNotContain(grid, v => v.Classes.Contains("LMP2"));
        Assert.DoesNotContain(grid, v => v.Entry == "13 Autosport 2026 #13");
    }

    [Fact]
    public void At_Le_Mans_the_Le_Mans_livery_and_the_Le_Mans_entries_are_used()
    {
        var plan = Plan(Round(folder: "LeMans_2023", layout: "layoutLeMans"));
        Assert.Equal("LEMANSWEC", plan.SceneDesc);
        Assert.Equal("Vista AF Corse 2026 #21:LM", plan.Player.Description);
        var grid = plan.Grid.Select(id => Vehicles.Single(v => v.Id == id)).ToList();
        Assert.All(grid, v => Assert.Equal("LM", v.Livery));
        Assert.Contains(grid, v => v.Classes.Contains("LMP2"));
        Assert.True(grid.Count > Plan(Round()).Grid.Count);
    }

    [Fact]
    public void Without_a_number_the_teams_grid_numbers_find_the_car()
    {
        var plan = Plan(Round(number: null, liveryNumbers: ["54"]));
        Assert.Equal("54", plan.Player.Number);
    }

    [Fact]
    public void The_briefings_settings_go_in_as_they_are()
    {
        var plan = Plan(Round());
        Assert.Equal(30, plan.RaceMinutes);
        Assert.Equal(12, plan.TimeScale);
        Assert.Equal(2, plan.FuelUsage);
        Assert.Equal(3, plan.TyreWear);
        Assert.Equal(13 * 60, plan.StartMinutes);
        Assert.Null(plan.AiStrength);
        Assert.Null(plan.Damage);
        Assert.False(plan.Wet);

        Assert.Equal(-1, Plan(Round(startTime: null)).StartMinutes);
    }

    [Fact]
    public void New_Game_Plus_sets_the_floor()
    {
        var plan = Plan(Round(floor: new BriefingFloor(97, 2, true, 60)));
        Assert.Equal(97, plan.AiStrength);
        Assert.Equal(2, plan.AiAggression);
        Assert.Equal(60, plan.Damage);
        Assert.True(plan.Wet);
    }

    [Fact]
    public void What_the_app_cant_pick_says_so()
    {
        Assert.Throws<PlayerError>(() => Plan(Round(customTeam: true)));
        Assert.Throws<PlayerError>(() => Plan(Round(layout: "layoutMadeUp")));
        Assert.Throws<PlayerError>(() => Plan(Round(number: "999")));
        Assert.Throws<PlayerError>(() => LmuRaceSetup.Plan(Round(), Catalog.Events.First(e => e.Imsa), Catalog.Car("Ferrari_296GT3_2023"), Vehicles));
    }

    [Fact]
    public void The_plan_goes_into_LMUs_preset_fields()
    {
        var preset = Preset();
        var plan = Plan(Round(floor: new BriefingFloor(97, 2, true, 60)));
        LmuRaceSetup.ApplyTo(preset, plan);

        var options = preset["Player"]!["Game Options"]!;
        var conditions = preset["Player"]!["Race Conditions"]!;
        Assert.Equal(plan.Grid, preset["Grid"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(30, options["Race Time"]!.GetValue<int>());
        Assert.Equal(2, options["Race Finish Criteria"]!.GetValue<int>());
        Assert.Equal(30 / 1440.0, options["Race Length"]!.GetValue<double>(), 6);
        Assert.Equal(2, options["Fuel Consumption Multiplier"]!.GetValue<int>());
        Assert.Equal(3, options["Tire Wear Multiplier"]!.GetValue<int>());
        Assert.Equal(60, options["Damage Multiplier"]!.GetValue<int>());
        Assert.Equal(12, conditions["RaceTimeScale"]!.GetValue<int>());
        Assert.Equal(780, conditions["RaceStartingTime"]!.GetValue<int>());
        Assert.Equal(1.0, conditions["RealRoadTimeScaleRace"]!.GetValue<double>());
        Assert.True(conditions["Run Practice1"]!.GetValue<bool>());
        Assert.Equal(1, conditions["Num Qual Sessions"]!.GetValue<int>());
        Assert.All(preset["Weather"]!["Race"]!["Weather"]!.AsArray(), slot =>
        {
            Assert.Equal(LmuRaceSetup.WetSky, slot!["Sky"]!.GetValue<int>());
            Assert.Equal(100, slot["RainChance"]!.GetValue<int>());
        });
        // Qualifying's weather stays as it was.
        Assert.NotEqual(LmuRaceSetup.WetSky, preset["Weather"]!["Qualifying"]!["Weather"]![1]!["Sky"]!.GetValue<int>());
    }

    [Fact]
    public void A_dry_round_leaves_the_weather_and_damage_alone()
    {
        var preset = Preset();
        var before = preset["Weather"]!.ToJsonString();
        var damage = preset["Player"]!["Game Options"]!["Damage Multiplier"]!.GetValue<int>();
        LmuRaceSetup.ApplyTo(preset, Plan(Round()));
        Assert.Equal(before, preset["Weather"]!.ToJsonString());
        Assert.Equal(damage, preset["Player"]!["Game Options"]!["Damage Multiplier"]!.GetValue<int>());
    }

    [Fact]
    public void Practice_and_qualifying_are_as_the_player_chose()
    {
        var preset = Preset();
        var plan = LmuRaceSetup.Plan(Round(), Event("world"), Catalog.Car("Ferrari_296GT3_2023"), Vehicles, new LmuSessions(45, 2, 15, 1));
        LmuRaceSetup.ApplyTo(preset, plan);
        var options = preset["Player"]!["Game Options"]!;
        var conditions = preset["Player"]!["Race Conditions"]!;
        Assert.True(conditions["Run Practice1"]!.GetValue<bool>());
        Assert.Equal(45, options["practice length"]!.GetValue<int>());
        Assert.Equal(2.0, conditions["RealRoadTimeScalePractice"]!.GetValue<double>());
        Assert.Equal(1, conditions["Num Qual Sessions"]!.GetValue<int>());
        Assert.Equal(15, options["qualifying length"]!.GetValue<int>());
        Assert.Equal(1.0, conditions["RealRoadTimeScaleQualifying"]!.GetValue<double>());
        Assert.Equal(1.0, conditions["RealRoadTimeScaleRace"]!.GetValue<double>());
    }

    [Fact]
    public void A_session_at_zero_minutes_is_left_out()
    {
        var preset = Preset();
        var plan = LmuRaceSetup.Plan(Round(), Event("world"), Catalog.Car("Ferrari_296GT3_2023"), Vehicles, new LmuSessions(0, 0, 0, 1));
        LmuRaceSetup.ApplyTo(preset, plan);
        var conditions = preset["Player"]!["Race Conditions"]!;
        Assert.False(conditions["Run Practice1"]!.GetValue<bool>());
        Assert.Equal(0, conditions["Num Qual Sessions"]!.GetValue<int>());
        Assert.Equal(1, conditions["Num Race Sessions"]!.GetValue<int>());
    }

    [Fact]
    public void Session_choices_snap_to_what_the_app_offers()
    {
        Assert.Equal(new LmuSessions(30, 1, 0, 3), new LmuSessions(33, 1.2, -5, 2.8).Normalized());
        Assert.Equal(LmuSessions.Default, LmuSessions.Default.Normalized());
    }

    private static (Season Season, Round Round) Season(Contract? contract = null)
    {
        var round = new Round { Number = 1, EventName = "Qatar 1812 km", TrackFolder = "Qatar_2024", LayoutFile = "layoutQatar" };
        var season = new Season
        {
            Number = 1,
            Car = new CareerCar("Ferrari 296 LMGT3", "GT3", "", "") { Folder = "Ferrari_296GT3_2023" },
            Contract = contract,
            Rounds = [round, new Round { Number = 2, EventName = "Imola 6 Hours", TrackFolder = "ImolaWEC_2024", LayoutFile = "layoutImola" }],
        };
        return (season, round);
    }

    [Fact]
    public void Before_the_first_race_every_team_running_the_car_can_be_picked()
    {
        var (season, round) = Season();
        var entries = CarChoice.For(season, round, Catalog);
        Assert.Contains(new CarEntry("Vista AF Corse", "21"), entries);
        Assert.Contains(new CarEntry("Vista AF Corse", "54"), entries);
        Assert.All(entries, e => Assert.Contains(Catalog.Teams, t => t.Name == e.Team && t.Class == "GT3"));

        CarChoice.Choose(season, round, new CarEntry("Vista AF Corse", "54"), Catalog);
        Assert.Equal("54", season.Car.CarNumber);
        Assert.Equal("Vista AF Corse", season.Car.TeamName);
        Assert.Equal("Ferrari_296GT3_2023", season.Car.Folder);
    }

    [Fact]
    public void A_contract_limits_the_pick_to_its_numbers()
    {
        var (season, round) = Season(new Contract { TeamName = "Vista AF Corse", Numbers = ["54"] });
        Assert.Equal([new CarEntry("Vista AF Corse", "54")], CarChoice.For(season, round, Catalog));
        Assert.Throws<PlayerError>(() => CarChoice.Choose(season, round, new CarEntry("Vista AF Corse", "21"), Catalog));
    }

    [Fact]
    public void Once_the_season_has_raced_the_car_is_settled()
    {
        var (season, round) = Season();
        round.State = RoundState.Completed;
        Assert.Empty(CarChoice.For(season, season.Rounds[1], Catalog));
    }

    [Fact]
    public void The_picker_shows_the_livery_the_race_loads()
    {
        var ferrari = Catalog.Car("Ferrari_296GT3_2023");
        var imola = LmuRaceSetup.Livery("ImolaWEC_2024", Event("world"), ferrari, "Ferrari_296GT3_2023", new CarEntry("Vista AF Corse", "21"), Vehicles);
        var leMans = LmuRaceSetup.Livery("LeMans_2023", Event("world"), ferrari, "Ferrari_296GT3_2023", new CarEntry("Vista AF Corse", "54"), Vehicles);
        Assert.Equal("Vista AF Corse 2026 #21:WEC", imola?.Description);
        Assert.Equal("Vista AF Corse 2026 #54:LM", leMans?.Description);
        Assert.Equal(Plan(Round()).Player.Id, imola?.Id);
        Assert.Null(LmuRaceSetup.Livery("ImolaWEC_2024", Event("world"), ferrari, "Ferrari_296GT3_2023", new CarEntry("Nobody", "999"), Vehicles));
    }

    [Fact]
    public void Car_pictures_are_only_read_for_vehicle_ids()
    {
        var root = Path.Combine(Path.GetTempPath(), "lmucareer-noinstall-" + Guid.NewGuid().ToString("N"));
        Assert.Null(LmuCarImages.FrontAngle(root, "../../Settings"));
        Assert.Null(LmuCarImages.FrontAngle(root, "21_26_AFCO31910023"));
    }

    [Fact]
    public void Sessions_can_be_private_and_start_when_the_player_says()
    {
        var preset = Preset();
        var sessions = new LmuSessions(45, 1, 15, 1) { PracticePrivate = false, QualifyingPrivate = true, PracticeStart = 600, QualifyingStart = 840 };
        LmuRaceSetup.ApplyTo(preset, LmuRaceSetup.Plan(Round(), Event("world"), Catalog.Car("Ferrari_296GT3_2023"), Vehicles, sessions));
        var conditions = preset["Player"]!["Race Conditions"]!;
        Assert.False(conditions["PrivatePractice"]!.GetValue<bool>());
        Assert.Equal(600, conditions["Practice1StartingTime"]!.GetValue<int>());
        Assert.Equal(1, conditions["PrivateQualifying"]!.GetValue<int>());
        Assert.Equal(840, conditions["QualifyingStartingTime"]!.GetValue<int>());
    }

    [Fact]
    public void A_session_left_out_keeps_LMUs_own_privacy_and_start()
    {
        var preset = Preset();
        var before = preset["Player"]!["Race Conditions"]!["Practice1StartingTime"]!.GetValue<int>();
        LmuRaceSetup.ApplyTo(preset, LmuRaceSetup.Plan(Round(), Event("world"), Catalog.Car("Ferrari_296GT3_2023"), Vehicles,
            new LmuSessions(0, 0, 20, 1) { PracticeStart = 900 }));
        Assert.Equal(before, preset["Player"]!["Race Conditions"]!["Practice1StartingTime"]!.GetValue<int>());
    }

    [Fact]
    public void Start_times_snap_to_the_half_hour_and_default_stays_default()
    {
        var snapped = new LmuSessions(30, 0, 20, 1) { PracticeStart = 611, QualifyingStart = 5000 }.Normalized();
        Assert.Equal(600, snapped.PracticeStart);
        Assert.Equal(-1, snapped.QualifyingStart);
        Assert.Equal(-1, LmuSessions.Default.Normalized().PracticeStart);
    }

    private static IReadOnlyList<LmuTrack> TracksOwned(Func<string, bool> owned) =>
        Tracks.Select(t => t with { Owned = owned(t.Folder) }).ToList();

    private static IReadOnlyList<LmuVehicle> CarsOwned(Func<string, bool> owned) =>
        Vehicles.Select(v => v with { Owned = owned(v.Folder) }).ToList();

    [Fact]
    public void Everything_owned_means_every_pack()
    {
        var packs = LmuOwnership.Packs(Catalog, TracksOwned(_ => true), CarsOwned(_ => true));
        Assert.Empty(packs.NotOwned);
        Assert.Contains("2024-1", packs.Owned);
        Assert.DoesNotContain(ContentCatalog.BasePack, packs.Owned);
    }

    [Fact]
    public void A_pack_LMU_locks_is_not_owned()
    {
        var pack = Catalog.Packs.First(p => p.Id != ContentCatalog.BasePack
            && Catalog.Tracks.Any(t => t.Pack == p.Id) && Catalog.Cars.Any(c => c.Pack == p.Id));
        var tracks = Catalog.Tracks.Where(t => t.Pack == pack.Id).Select(t => t.Folder).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cars = Catalog.Cars.Where(c => c.Pack == pack.Id).Select(c => c.Folder).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var none = LmuOwnership.Packs(Catalog, TracksOwned(f => !tracks.Contains(f)), CarsOwned(f => !cars.Contains(f)));
        Assert.Contains(pack.Id, none.NotOwned);
        Assert.DoesNotContain(pack.Id, none.Owned);

        // Only its track locked: partly owned, so the player decides.
        var part = LmuOwnership.Packs(Catalog, TracksOwned(f => !tracks.Contains(f)), CarsOwned(_ => true));
        Assert.Contains(pack.Id, part.Unsure);
    }

    [Fact]
    public void A_pack_LMU_doesnt_list_is_left_to_the_player()
    {
        var packs = LmuOwnership.Packs(Catalog, TracksOwned(_ => true), CarsOwned(_ => true).Where(v => v.Folder != "Ferrari_296GT3_2023").ToList());
        var ferrariPack = Catalog.Car("Ferrari_296GT3_2023")!.Pack!;
        Assert.Contains(ferrariPack, packs.Unsure);
        Assert.Throws<PlayerError>(() => LmuOwnership.Packs(Catalog, [], Vehicles));
    }
}

