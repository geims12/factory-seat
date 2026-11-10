using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using LmuCareer.Core;
using LmuCareer.Core.Content;

namespace LmuCareer.Host;

/// <summary>
/// LMU's local REST API, the one its own menus use (docs/lmu-game-rest-api.md). It answers only
/// while the game runs, on this PC: nothing here goes on the network. Used only when the player
/// asks Factory Seat to set a race up.
/// </summary>
public sealed class LmuGame : IDisposable
{
    public const string BaseUrl = "http://localhost:6397";

    private readonly HttpClient _http = new() { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(30) };

    public void Dispose() => _http.Dispose();

    /// <summary>Where the game is: navigationState and gameState; null when LMU isn't running.</summary>
    public async Task<(string Navigation, string Game)?> StateAsync(CancellationToken cancel) =>
        await ReadinessAsync(cancel) is { Readiness: not LmuReadiness.NotRunning } state ? (state.Navigation, state.Game) : null;

    /// <summary>
    /// Whether a race can be set up now. LMU's offline menu reports NAV_MAIN_MENU (or NAV_SETUP),
    /// GSTATE_SETUP, SETTING_GRANDPRIX and not loading. After a session LMU can keep reporting
    /// NAV_EVENT and GSTATE_DYN even with its home screen showing: when GetGameState says that
    /// session sits in the monitor (no replay, the car loaded), it's a retained session the player
    /// can choose to replace. Anything else (driving, a replay, loading, multiplayer) is busy.
    /// Credit: thebaz's windows-weekend branch found the retained session.
    /// </summary>
    public async Task<LmuState> ReadinessAsync(CancellationToken cancel)
    {
        JsonNode? root;
        try
        {
            root = await GetAsync("/navigation/state", cancel);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or LmuApiException && !cancel.IsCancellationRequested)
        {
            return new(LmuReadiness.NotRunning, "", "", "", null);
        }

        var state = root?["state"];
        var navigation = state?["navigationState"]?.ToString() ?? "";
        var game = state?["gameState"]?.ToString() ?? "";
        var mode = state?["settingMode"]?.ToString() ?? "";
        bool? loading = root?["loadingStatus"]?["loading"] is JsonValue l && l.TryGetValue(out bool b) ? b : null;
        LmuState As(LmuReadiness readiness) => new(readiness, navigation, game, mode, loading);
        if (navigation.Length == 0) return As(LmuReadiness.NotRunning);

        var idleOffline = loading == false && mode == "SETTING_GRANDPRIX";
        if (idleOffline && navigation is "NAV_MAIN_MENU" or "NAV_SETUP" && game == "GSTATE_SETUP") return As(LmuReadiness.Menu);
        if (idleOffline && navigation == "NAV_EVENT" && game == "GSTATE_DYN")
        {
            JsonNode? session;
            try
            {
                session = await GetAsync("/rest/sessions/GetGameState", cancel);
            }
            catch (LmuApiException)
            {
                return As(LmuReadiness.Busy);
            }
            if (Bool(session?["inMonitor"]) == true && Bool(session?["isReplayActive"]) == false && Bool(session?["playerVehicleLoaded"]) == true)
                return As(LmuReadiness.RetainedSession);
        }
        return As(LmuReadiness.Busy);
    }

    /// <summary>
    /// Stops unless LMU is on its offline menu, or holds a retained session the player chose to
    /// replace. Checked before the setup starts and again just before the load.
    /// </summary>
    private async Task<LmuState> EnsureReadyAsync(bool replaceRetained, CancellationToken cancel)
    {
        var state = await ReadinessAsync(cancel);
        return state.Readiness switch
        {
            LmuReadiness.Menu => state,
            LmuReadiness.RetainedSession when replaceRetained => state,
            LmuReadiness.RetainedSession => throw new LmuRetainedSessionException(),
            LmuReadiness.NotRunning => throw new PlayerError("LMU isn't running. Start it, go to its main menu, and try again."),
            _ => throw new PlayerError("LMU is busy ({state}). Go to its offline Race Weekend menu, and try again.", ("state", state.Describe())),
        };
    }

    private static bool? Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue(out bool b) ? b : null;

    public async Task<IReadOnlyList<LmuVehicle>> VehiclesAsync(CancellationToken cancel) =>
        (await GetAsync("/rest/sessions/getAllVehicles", cancel) as JsonArray ?? []).Select(LmuVehicle.From).OfType<LmuVehicle>().ToList();

    public async Task<IReadOnlyList<LmuTrack>> TracksAsync(CancellationToken cancel) =>
        (await GetAsync("/rest/sessions/getTracksAll", cancel) as JsonArray ?? []).Select(LmuTrack.From).OfType<LmuTrack>().ToList();

    /// <summary>
    /// Sets the race up and loads it, so the player lands in the weekend. <paramref name="beforeLoad"/>
    /// runs once everything is set and checked, just before the load (the app starts the race weekend
    /// there, so only races from now on count). <paramref name="step"/> hears each stage.
    /// </summary>
    /// <param name="replaceRetained">The player confirmed replacing a session LMU still holds.</param>
    /// <param name="load">Load the weekend. Off: stop with it all set on LMU's menu, where LMU's own
    /// settings screen reads it fresh (its loader calls requestPreset) for the player to check and start.</param>
    public async Task SetUpAsync(LmuRacePlan plan, LmuTrack track, bool replaceRetained, bool load, Func<Task> beforeLoad, Action<string> step,
        CancellationToken cancel)
    {
        if ((await EnsureReadyAsync(replaceRetained, cancel)).Readiness == LmuReadiness.RetainedSession)
        {
            step("release");
            await ReleaseRetainedAsync(cancel);
        }

        step("track");
        await PostJsonAsync("/rest/sessions/Championship/setCurrentChampionshipTemplate", new JsonObject { ["name"] = plan.Championship }, cancel);
        await Settle(cancel);
        await PostTextAsync("/rest/race/track", track.Id, cancel);
        await Settle(cancel);
        var defaults = await PostJsonAsync("/rest/sessions/SessionPresets/getDefaultPresetForTrack", new JsonObject { ["trackName"] = plan.SceneDesc }, cancel)
            ?? throw new PlayerError("LMU didn't give the track's default session. Go to LMU's main menu and try again.");
        await PostJsonAsync("/rest/sessions/SessionPresets/applyPreset", defaults, cancel);
        await Settle(cancel);

        step("car");
        // After applyPreset, which resets the car to the preset's.
        await PostTextAsync("/rest/garage/SetCurrentVehicle", new JsonObject { ["id"] = plan.Player.Id }.ToJsonString(), cancel);
        await Settle(cancel);

        step("settings");
        if (await PostJsonAsync("/rest/sessions/SessionPresets/requestPreset", null, cancel) is not JsonObject preset)
            throw new PlayerError("LMU didn't give its race settings. Go to LMU's main menu and try again.");
        LmuRaceSetup.ApplyTo(preset, plan);
        await PostJsonAsync("/rest/sessions/SessionPresets/applyPreset", preset, cancel);
        await Settle(cancel);
        // Player settings, not session ones: the menus show their own copy, so set these last.
        if (plan.AiStrength is { } strength) await SetOptionAsync("GAMEOPT_ai_driverstrength", strength, cancel);
        if (plan.AiAggression is { } aggression) await SetOptionAsync("GAMEOPT_ai_aggression", aggression, cancel);

        step("check");
        var set = await PostJsonAsync("/rest/sessions/SessionPresets/requestPreset", null, cancel) as JsonObject
            ?? throw new PlayerError("LMU didn't give its race settings. Go to LMU's main menu and try again.");
        Check(set, plan);
        await CheckSelectionAsync(plan, cancel);

        // The save LMU builds must hold what was checked; nothing is loaded otherwise.
        var save = await PostJsonAsync("/rest/sessions/SaveLoad/generateSaveFileFromSessionPreset", set, cancel) as JsonObject
            ?? throw new PlayerError("LMU couldn't build the race weekend. Go to LMU's main menu and try again.");
        if (save["SessionPreset"] is not JsonObject saved)
            throw new PlayerError("LMU couldn't build the race weekend. Go to LMU's main menu and try again.");
        Check(saved, plan);
        // The player may have started something in LMU meanwhile.
        await EnsureReadyAsync(false, cancel);

        await beforeLoad();
        if (!load) return;

        step("load");
        var request = new JsonObject { ["save"] = save.DeepClone() };
        if (plan.AiStrength is { } patchStrength) request["patch"] = new JsonObject { ["aiStrength"] = patchStrength };
        // Never retried: a timeout doesn't prove LMU didn't get it.
        await PostJsonAsync("/rest/sessions/SaveLoad/loadGame", request, cancel);

        // The call returns before the load is done.
        var until = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancel);
            var now = await ReadinessAsync(cancel);
            if (now.Navigation == "NAV_EVENT" && now.Loading != true) return;
        }
        throw new PlayerError("LMU is still loading the weekend. Check the game: if it loaded, go race; the weekend has started.");
    }

    /// <summary>
    /// Ends the session LMU still holds, the way its own exit does (NAV_TO_MAIN_MENU), and waits for
    /// the offline menu. Loading over a retained session left LMU's screen on its home page with the
    /// weekend loaded behind it: the menu only moves to the loading and weekend screens when a load
    /// starts from the menu.
    /// </summary>
    private async Task<LmuState> ReleaseRetainedAsync(CancellationToken cancel)
    {
        // LMU's menu sends this with a JSON null body; with no body at all the call never answers.
        using (var content = new StringContent("null", Encoding.UTF8, "application/json"))
        using (var response = await _http.PostAsync("/navigation/action/NAV_TO_MAIN_MENU", content, cancel))
            await Read(response, "/navigation/action/NAV_TO_MAIN_MENU", cancel);

        var until = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancel);
            if (await ReadinessAsync(cancel) is { Readiness: LmuReadiness.Menu } menu)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancel);
                return menu;
            }
        }
        throw new PlayerError("LMU didn't close its earlier session. Go to its main menu, and try again.");
    }

    /// <summary>
    /// The track and car LMU's session screen holds (GET /rest/garage/UIScreen/SessionSetup), as the
    /// menus read them. A build without that screen falls back to the preset's own check.
    /// </summary>
    private async Task CheckSelectionAsync(LmuRacePlan plan, CancellationToken cancel)
    {
        JsonNode? selection;
        try
        {
            selection = await GetAsync("/rest/garage/UIScreen/SessionSetup", cancel);
        }
        catch (LmuApiException)
        {
            return;
        }
        var scene = selection?["trackInfo"]?["sceneDesc"]?.ToString();
        var car = selection?["selectedCar"]?["id"]?.ToString();
        if (scene is not null && !scene.Equals(plan.SceneDesc, StringComparison.OrdinalIgnoreCase))
            throw new PlayerError("LMU didn't take the {setting} Factory Seat sent. Set this race up in LMU yourself.", ("setting", "track"));
        if (car is not null && car != plan.Player.Id)
            throw new PlayerError("LMU didn't take the {setting} Factory Seat sent. Set this race up in LMU yourself.", ("setting", "car"));
    }

    /// <summary>What LMU holds after the setup must be what was sent; a mismatch stops before the load.</summary>
    private static void Check(JsonObject preset, LmuRacePlan plan)
    {
        var options = preset["Player"]?["Game Options"];
        var conditions = preset["Player"]?["Race Conditions"];
        var scene = preset["Player"]?["SCENE"]?["Scene Description"]?.ToString();
        var vehicle = preset["Player"]?["DRIVER"]?["Vehicle"]?.ToString();
        var grid = preset["Grid"] as JsonArray;

        string? wrong =
            !string.Equals(scene, plan.SceneDesc, StringComparison.OrdinalIgnoreCase) ? "track"
            : vehicle is null || !plan.Player.Description.StartsWith(vehicle, StringComparison.OrdinalIgnoreCase) ? "car"
            : Int(options?["Race Time"]) != plan.RaceMinutes ? "race length"
            : Int(options?["Fuel Consumption Multiplier"]) != plan.FuelUsage ? "Fuel Usage"
            : Int(options?["Tire Wear Multiplier"]) != plan.TyreWear ? "Tyre Wear"
            : Int(conditions?["RaceTimeScale"]) != plan.TimeScale ? "Time Scale"
            : grid is null || grid.Count != plan.Grid.Count || grid[0]?.ToString() != plan.Player.Id ? "grid"
            : null;
        if (wrong is not null)
            throw new PlayerError("LMU didn't take the {setting} Factory Seat sent. Set this race up in LMU yourself.", ("setting", wrong));
    }

    private static int? Int(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue(out int i)) return i;
        return v.TryGetValue(out double d) ? (int)Math.Round(d) : null;
    }

    private Task SetOptionAsync(string key, int value, CancellationToken cancel) =>
        PostJsonAsync("/rest/options/long", new JsonObject { ["optionEntry"] = key, ["value"] = value }, cancel);

    /// <summary>LMU's menus take a moment to apply each change; going faster was unreliable.</summary>
    private static Task Settle(CancellationToken cancel) => Task.Delay(500, cancel);

    private async Task<JsonNode?> GetAsync(string path, CancellationToken cancel)
    {
        using var response = await _http.GetAsync(path, cancel);
        return await Read(response, path, cancel);
    }

    private async Task<JsonNode?> PostJsonAsync(string path, JsonNode? body, CancellationToken cancel)
    {
        using var content = body is null ? null : new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(path, content, cancel);
        return await Read(response, path, cancel);
    }

    /// <summary>Two of LMU's calls want their body as plain text: the track id, and the car as a JSON string.</summary>
    private async Task<JsonNode?> PostTextAsync(string path, string body, CancellationToken cancel)
    {
        using var content = new StringContent(body, Encoding.UTF8, "text/plain");
        using var response = await _http.PostAsync(path, content, cancel);
        return await Read(response, path, cancel);
    }

    private static async Task<JsonNode?> Read(HttpResponseMessage response, string path, CancellationToken cancel)
    {
        if (!response.IsSuccessStatusCode)
            throw new LmuApiException($"LMU answered {(int)response.StatusCode} to {path}.");
        var bytes = await response.Content.ReadAsByteArrayAsync(cancel);
        if (bytes.Length == 0) return null;
        // Some of LMU's text comes in a Windows codepage rather than UTF-8 (Portim\xE3o).
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(bytes);
        }
        try
        {
            return JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return JsonValue.Create(text);
        }
    }
}

/// <summary>LMU answered, but not with success: a bug report's worth, not something the player did.</summary>
public sealed class LmuApiException(string message) : Exception(message);

public enum LmuReadiness { NotRunning, Menu, RetainedSession, Busy }

/// <summary>What /navigation/state says, read for whether a race can be set up.</summary>
public sealed record LmuState(LmuReadiness Readiness, string Navigation, string Game, string SettingMode, bool? Loading)
{
    /// <summary>The states only, never player, car or track data: for the player's message.</summary>
    public string Describe() =>
        $"{(Navigation.Length > 0 ? Navigation : "?")}, {(Game.Length > 0 ? Game : "?")}, {(SettingMode.Length > 0 ? SettingMode : "?")}"
        + (Loading == true ? ", loading" : "");
}

/// <summary>LMU still holds an offline session; setting a race up replaces it, so the player confirms first.</summary>
public sealed class LmuRetainedSessionException() : Exception("LMU still holds an offline session.");
