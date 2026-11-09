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
    public async Task<(string Navigation, string Game)?> StateAsync(CancellationToken cancel)
    {
        try
        {
            var state = (await GetAsync("/navigation/state", cancel))?["state"];
            return state?["navigationState"]?.ToString() is { } nav ? (nav, state["gameState"]?.ToString() ?? "") : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<LmuVehicle>> VehiclesAsync(CancellationToken cancel) =>
        (await GetAsync("/rest/sessions/getAllVehicles", cancel) as JsonArray ?? []).Select(LmuVehicle.From).OfType<LmuVehicle>().ToList();

    public async Task<IReadOnlyList<LmuTrack>> TracksAsync(CancellationToken cancel) =>
        (await GetAsync("/rest/sessions/getTracksAll", cancel) as JsonArray ?? []).Select(LmuTrack.From).OfType<LmuTrack>().ToList();

    /// <summary>
    /// Sets the race up and loads it, so the player lands in the weekend. <paramref name="beforeLoad"/>
    /// runs once everything is set and checked, just before the load (the app starts the race weekend
    /// there, so only races from now on count). <paramref name="step"/> hears each stage.
    /// </summary>
    public async Task SetUpAsync(LmuRacePlan plan, LmuTrack track, Func<Task> beforeLoad, Action<string> step, CancellationToken cancel)
    {
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

        await beforeLoad();

        step("load");
        var save = await PostJsonAsync("/rest/sessions/SaveLoad/generateSaveFileFromSessionPreset", set, cancel)
            ?? throw new PlayerError("LMU couldn't build the race weekend. Go to LMU's main menu and try again.");
        var load = new JsonObject { ["save"] = save };
        if (plan.AiStrength is { } patchStrength) load["patch"] = new JsonObject { ["aiStrength"] = patchStrength };
        await PostJsonAsync("/rest/sessions/SaveLoad/loadGame", load, cancel);

        // The call returns before the load is done.
        var until = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancel);
            if ((await StateAsync(cancel))?.Navigation == "NAV_EVENT") return;
        }
        throw new PlayerError("LMU is still loading the weekend. Check the game: if it loaded, go race; the weekend has started.");
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
