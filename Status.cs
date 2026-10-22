using System.Text.Json;

namespace OpenBancho;

/// <summary>
/// Beatmap status: data/status.txt overrides ("set:123 = loved" or "&lt;md5&gt; = ranked"), then the public mirror
/// (the same one open!direct uses), cached. Unknown maps report "unknown" and show no banner.
/// </summary>
public sealed class StatusLookup
{
    readonly Store store;
    readonly string mirror;
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(8) };
    readonly Dictionary<string, DateTime> failedUntil = new();
    Dictionary<string, string> overrides = new();
    DateTime overridesStamp;
    readonly string overridesFile = Path.Combine(Directory.GetCurrentDirectory(), "data", "status.txt");

    public StatusLookup(Store store, string mirror)
    {
        this.store = store;
        this.mirror = mirror;
        http.DefaultRequestHeaders.UserAgent.TryParseAdd("open-bancho/1");
        if (!File.Exists(overridesFile))
            File.WriteAllText(overridesFile, "# Estado de los mapas en este servidor. Una línea por mapa:\n# set:<id del set> = loved\n# <md5 del .osu> = ranked\n# Estados: ranked, approved, qualified, loved, pending, graveyard\n");
    }

    string? Override(string hash, int setId)
    {
        lock (failedUntil) return OverrideLocked(hash, setId);
    }

    string? OverrideLocked(string hash, int setId)
    {
        var stamp = File.GetLastWriteTimeUtc(overridesFile);
        if (stamp != overridesStamp)
        {
            overridesStamp = stamp;
            overrides = File.ReadAllLines(overridesFile)
                .Where(l => !l.TrimStart().StartsWith('#') && l.Contains('='))
                .Select(l => l.Split('=', 2))
                .GroupBy(p => p[0].Trim().ToUpperInvariant())
                .ToDictionary(g => g.Key, g => g.Last()[1].Trim().ToLowerInvariant());
        }
        return overrides.TryGetValue(hash.ToUpperInvariant(), out var a) ? a
            : setId > 0 && overrides.TryGetValue($"SET:{setId}", out var b) ? b : null;
    }

    /// <summary>Last known status without network access (for pp decisions).</summary>
    public string Cached(string hash, int setId) =>
        Override(hash, setId) ?? store.StatusFor(hash.ToUpperInvariant()) ?? (setId > 0 ? store.StatusFor($"set:{setId}") : null) ?? "unknown";

    public async Task<string> Get(string hash, int setId)
    {
        var known = Cached(hash, setId);
        if (known != "unknown") return known;
        string key = hash.ToUpperInvariant();
        lock (failedUntil) if (failedUntil.TryGetValue(key, out var until) && until > DateTime.UtcNow) return "unknown";

        string? found = await Ask($"{mirror}/api/v2/md5/{hash.ToLowerInvariant()}");
        if (found == null && setId > 0) found = await Ask($"{mirror}/api/v2/s/{setId}");
        if (found != null)
        {
            store.SetStatus(key, found);
            return found;
        }
        lock (failedUntil) failedUntil[key] = DateTime.UtcNow.AddMinutes(10);
        return "unknown";
    }

    async Task<string?> Ask(string url)
    {
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync(url));
            return Read(doc.RootElement);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or UriFormatException) { return null; }
    }

    static string? Read(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (e.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String) return s.GetString()!.ToLowerInvariant();
        if (e.TryGetProperty("ranked", out var r) && r.ValueKind == JsonValueKind.Number)
            return r.GetInt32() switch { 1 => "ranked", 2 => "approved", 3 => "qualified", 4 => "loved", -2 => "graveyard", _ => "pending" };
        foreach (var inner in new[] { "set", "beatmapset", "data" })
            if (e.TryGetProperty(inner, out var x) && Read(x) is { } v) return v;
        return null;
    }
}
