using System.Net.Http;
using DevBar.Core;
using System.Text.Json;
using Windows.Devices.Geolocation;

namespace DevBar.Modules.Jarvis.Context;

internal sealed record Place(string City, string Region, string Country, double Lat, double Lon, string Source)
{
    public string Describe() =>
        string.Join(", ", new[] { City, Region, Country }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
}

/// <summary>
/// Where the user is, for "what's the weather", "what time is it in…", and
/// general grounding. Order: a place typed in settings → Windows location
/// services (precise, only if "Let desktop apps access your location" is on)
/// → IP geolocation (city-level, can be off if you're on a VPN). Looked up
/// at most every 30 minutes, only when a conversation starts — never polled.
/// </summary>
internal static class LocationService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static Place? _cached;
    private static DateTime _cachedAt;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    public static Place? Current => _cached;

    /// <summary>Forget the cached place (settings changed).</summary>
    public static void Invalidate() => _cached = null;

    public static async Task<Place?> GetAsync(JarvisConfig cfg)
    {
        if (!cfg.UseLocation) return null;
        if (_cached != null && DateTime.UtcNow - _cachedAt < Ttl) return _cached;

        Place? place = null;
        if (!string.IsNullOrWhiteSpace(cfg.HomeLocation)) place = await GeocodeAsync(cfg.HomeLocation);
        place ??= await FromWindowsAsync();
        place ??= await FromIpAsync();

        if (place != null)
        {
            _cached = place;
            _cachedAt = DateTime.UtcNow;
        }
        return place ?? _cached;
    }

    private static async Task<Place?> FromWindowsAsync()
    {
        try
        {
            if (await Geolocator.RequestAccessAsync() != GeolocationAccessStatus.Allowed) return null;
            var locator = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
            var pos = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(4));
            double lat = pos.Coordinate.Point.Position.Latitude, lon = pos.Coordinate.Point.Position.Longitude;

            // Free, keyless reverse geocoding for client-side use.
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(FormattableString.Invariant(
                $"https://api.bigdatacloud.net/data/reverse-geocode-client?latitude={lat}&longitude={lon}&localityLanguage=en")));
            var r = doc.RootElement;
            return new Place(Str(r, "city") is { Length: > 0 } c ? c : Str(r, "locality"), Str(r, "principalSubdivision"),
                Str(r, "countryName"), lat, lon, "Windows location");
        }
        catch { return null; }
    }

    private static async Task<Place?> FromIpAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://ipinfo.io/json"));
            var r = doc.RootElement;
            var loc = Str(r, "loc").Split(',');
            if (loc.Length != 2) return null;
            return new Place(Str(r, "city"), Str(r, "region"), CountryName(Str(r, "country")),
                double.Parse(loc[0], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(loc[1], System.Globalization.CultureInfo.InvariantCulture), "IP address (approximate)");
        }
        catch { return null; }
    }

    /// <summary>Place name → coordinates via Open-Meteo's free geocoder (also used by the weather tool for other cities).</summary>
    public static async Task<Place?> GeocodeAsync(string name)
    {
        try
        {
            var city = name.Split(',')[0].Trim();
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(
                $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=5&language=en&format=json"));
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;

            // "Pune, India" — prefer the result whose country/region matches the rest of the text.
            var hint = name.Contains(',') ? name[(name.IndexOf(',') + 1)..].Trim() : "";
            var best = results.EnumerateArray().FirstOrDefault(x =>
                hint.Length > 0 && (Str(x, "country").Contains(hint, StringComparison.OrdinalIgnoreCase)
                                    || Str(x, "admin1").Contains(hint, StringComparison.OrdinalIgnoreCase)));
            if (best.ValueKind == JsonValueKind.Undefined) best = results[0];
            return new Place(Str(best, "name"), Str(best, "admin1"), Str(best, "country"),
                best.GetProperty("latitude").GetDouble(), best.GetProperty("longitude").GetDouble(), "your settings");
        }
        catch { return null; }
    }

    private static string CountryName(string code)
    {
        try { return new System.Globalization.RegionInfo(code).EnglishName; }
        catch { return code; }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
