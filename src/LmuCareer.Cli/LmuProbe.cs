using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LmuCareer.Cli;

/// <summary>
/// Read-only probe of LMU's local REST API (docs/lmu-game-rest-api.md), to find where the race
/// settings live in the session preset. It only makes calls that read: it never sets a track, a car
/// or a preset, and never loads a session.
///
///   lmu state                    where the game is (menu, in a session, not running)
///   lmu dump <label>             save the catalog, the current preset and the state to lmu-probe/<label>/
///   lmu diff <a> <b>             what changed between two dumps' presets (labels or .json files)
///
/// Typical use: dump "before", change one setting in LMU's race weekend menu, dump "after", diff.
/// </summary>
internal static class LmuProbe
{
    private const string DefaultBase = "http://localhost:6397";
    private const string ProbeDir = "lmu-probe";

    // Read-only calls. requestPreset is a POST but only returns the current preset.
    private static readonly (string File, HttpMethod Method, string Path)[] DumpCalls =
    [
        ("state.json", HttpMethod.Get, "/navigation/state"),
        ("preset.json", HttpMethod.Post, "/rest/sessions/SessionPresets/requestPreset"),
        ("tracks.json", HttpMethod.Get, "/rest/sessions/getTracksAll"),
        ("vehicles.json", HttpMethod.Get, "/rest/sessions/getAllVehicles"),
    ];

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Run(string[] args)
    {
        var baseUrl = DefaultBase;
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--base" && i + 1 < args.Length) baseUrl = args[++i].TrimEnd('/');
            else rest.Add(args[i]);
        }

        return rest switch
        {
            ["state"] => State(baseUrl),
            ["dump", var label] => Dump(baseUrl, label),
            ["diff", var a, var b] => Diff(a, b),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.WriteLine("usage: lmucareer lmu state [--base http://localhost:6397]");
        Console.WriteLine("       lmucareer lmu dump <label> [--base ...]   saves to lmu-probe/<label>/");
        Console.WriteLine("       lmucareer lmu diff <label|file.json> <label|file.json>");
        Console.WriteLine();
        Console.WriteLine("Only reads from LMU. LMU must be running on this PC.");
        return 1;
    }

    private static int State(string baseUrl)
    {
        using var http = Client(baseUrl);
        var (ok, body, error) = Call(http, HttpMethod.Get, "/navigation/state");
        if (!ok)
        {
            Console.Error.WriteLine($"LMU isn't answering at {baseUrl}: {error}");
            return 1;
        }

        var state = Parse(body!)?["state"];
        Console.WriteLine($"navigationState: {state?["navigationState"]?.ToString() ?? "(none)"}");
        Console.WriteLine($"gameState:       {state?["gameState"]?.ToString() ?? "(none)"}");
        return 0;
    }

    private static int Dump(string baseUrl, string label)
    {
        var dir = Path.Combine(ProbeDir, label);
        Directory.CreateDirectory(dir);
        using var http = Client(baseUrl);

        var failed = 0;
        foreach (var (file, method, path) in DumpCalls)
        {
            var (ok, body, error) = Call(http, method, path);
            if (!ok)
            {
                Console.WriteLine($"  ! {method} {path}: {error}");
                failed++;
                if (file == "state.json")
                {
                    Console.Error.WriteLine($"LMU isn't answering at {baseUrl}. Is the game running?");
                    return 1;
                }
                continue;
            }

            var target = Path.Combine(dir, file);
            if (Parse(body!) is { } node)
            {
                File.WriteAllText(target, node.ToJsonString(Pretty), new UTF8Encoding(false));
                Console.WriteLine($"  {file,-14} {Describe(node)}");
            }
            else
            {
                File.WriteAllText(target + ".txt", body, new UTF8Encoding(false));
                Console.WriteLine($"  {file,-14} not JSON, saved as text ({body!.Length} chars)");
            }
        }

        Console.WriteLine($"Saved to {Path.GetFullPath(dir)}");
        Console.WriteLine("These files hold your in-game driver name. Don't commit them as they are.");
        return failed == 0 ? 0 : 2;
    }

    private static int Diff(string a, string b)
    {
        var left = Parse(File.ReadAllText(PresetPath(a)));
        var right = Parse(File.ReadAllText(PresetPath(b)));
        if (left is null || right is null)
        {
            Console.Error.WriteLine("Both files must be JSON.");
            return 1;
        }

        var before = Flatten(left);
        var after = Flatten(right);
        var changes = 0;
        foreach (var path in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            var had = before.TryGetValue(path, out var x);
            var has = after.TryGetValue(path, out var y);
            if (had && has && x == y) continue;
            changes++;
            if (!had) Console.WriteLine($"+ {path} = {y}");
            else if (!has) Console.WriteLine($"- {path} (was {x})");
            else Console.WriteLine($"~ {path}: {x} -> {y}");
        }

        Console.WriteLine(changes == 0 ? "No differences." : $"{changes} difference(s).");
        return 0;
    }

    /// <summary>A label means lmu-probe/&lt;label&gt;/preset.json; anything that exists as a file is used as is.</summary>
    private static string PresetPath(string arg) =>
        File.Exists(arg) ? arg : Path.Combine(ProbeDir, arg, "preset.json");

    private static HttpClient Client(string baseUrl) =>
        new() { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };

    private static (bool Ok, string? Body, string? Error) Call(HttpClient http, HttpMethod method, string path)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            using var response = http.Send(request);
            var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                return (false, null, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            return (true, Decode(bytes), null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return (false, null, e.Message);
        }
    }

    /// <summary>
    /// LMU sends some text in a Windows codepage rather than UTF-8 (Portim\xE3o). Strict UTF-8 first,
    /// then Windows-1252's printable range via Latin-1, which matches it for accented letters.
    /// </summary>
    private static string Decode(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static JsonNode? Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Describe(JsonNode node) => node switch
    {
        JsonArray array => $"{array.Count} entries",
        JsonObject obj => $"{obj.Count} keys: {string.Join(", ", obj.Select(p => p.Key).Take(8))}{(obj.Count > 8 ? ", ..." : "")}",
        _ => node.ToJsonString(),
    };

    /// <summary>Every leaf as path -> JSON text, e.g. Player["Game Options"].Opponents -> 0.</summary>
    private static Dictionary<string, string> Flatten(JsonNode root)
    {
        var leaves = new Dictionary<string, string>(StringComparer.Ordinal);
        void Walk(JsonNode? node, string path)
        {
            switch (node)
            {
                case JsonObject obj:
                    if (obj.Count == 0) leaves[path] = "{}";
                    foreach (var (key, value) in obj) Walk(value, path + Key(key));
                    break;
                case JsonArray array:
                    if (array.Count == 0) leaves[path] = "[]";
                    for (var i = 0; i < array.Count; i++) Walk(array[i], $"{path}[{i}]");
                    break;
                default:
                    leaves[path] = node?.ToJsonString(Pretty) ?? "null";
                    break;
            }
        }

        Walk(root, "");
        return leaves.ToDictionary(p => p.Key.TrimStart('.'), p => p.Value, StringComparer.Ordinal);
    }

    private static string Key(string key) =>
        key.All(c => char.IsLetterOrDigit(c) || c == '_') ? "." + key : $"[\"{key}\"]";
}
