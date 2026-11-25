using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TokenUsageReport.Reporting;

public sealed record ModelPrice(decimal InputPer1M, decimal? CachedInputPer1M, decimal OutputPer1M, string Source);

public sealed record RetailMeter(string MeterName, decimal RetailPrice, string UnitOfMeasure, string ArmRegionName);

/// <summary>
/// Estimates token cost from Azure's public Retail Prices API (list prices, USD) or from customer-supplied overrides.
/// It only returns a price when a meter is unambiguous; otherwise there is no estimate rather than a guess.
/// </summary>
public sealed partial class PriceBook
{
    private const string RetailUrl = "https://prices.azure.com/api/retail/prices?api-version=2023-01-01-preview";

    [GeneratedRegex(@"^(?:(?<ver>\d{4})-)?(?:(?<kind>cd-inp|cchd-inp|cached-inp|inp|outp|out)-)?(?<sku>glbl|global|regnl|rgnl|regional|dz|dzone|datazone)$")]
    private static partial Regex MeterShape();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{2,64}$")]
    private static partial Regex SafeName();

    private readonly HttpClient _http;
    private readonly IReadOnlyDictionary<string, ModelPrice> _overrides;
    private readonly ConcurrentDictionary<string, Task<(ModelPrice? Price, bool Transient)>> _cache = new();

    public PriceBook(HttpClient? http = null, IReadOnlyDictionary<string, ModelPrice>? overrides = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _overrides = overrides ?? new Dictionary<string, ModelPrice>();
    }

    /// <summary>Parses {"model-or-deployment":{"input":0.15,"cachedInput":0.075,"output":0.6}} (USD per 1M tokens).</summary>
    public static IReadOnlyDictionary<string, ModelPrice> ParseOverrides(string? json)
    {
        var map = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return map;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                var p = entry.Value;
                if (!p.TryGetProperty("input", out var input) || !p.TryGetProperty("output", out var output))
                    throw new FormatException($"Price override '{entry.Name}' needs \"input\" and \"output\" (USD per 1M tokens).");
                decimal? cached = p.TryGetProperty("cachedInput", out var c) ? c.GetDecimal() : null;
                map[entry.Name] = new ModelPrice(input.GetDecimal(), cached, output.GetDecimal(), "Custom price (REPORT_PRICE_OVERRIDES)");
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new FormatException("REPORT_PRICE_OVERRIDES is not valid JSON of the form {\"model\":{\"input\":0.15,\"output\":0.6}}.", ex);
        }
        return map;
    }

    public async Task<ModelPrice?> GetAsync(string deployment, string modelBase, string version, string skuKind, string? region, CancellationToken ct)
    {
        if (_overrides.TryGetValue(deployment, out var custom) || _overrides.TryGetValue(modelBase, out custom)) return custom;
        if (skuKind == "" || !SafeName().IsMatch(modelBase)) return null;
        var key = $"{modelBase}|{version}|{skuKind}|{region}".ToLowerInvariant();
        var task = _cache.GetOrAdd(key, _ => FetchAsync(modelBase, version, skuKind, region, ct));
        var (price, transient) = await task;
        // A "no meter matches" answer is stable and stays cached; a network failure must not be remembered.
        if (transient) _cache.TryRemove(new KeyValuePair<string, Task<(ModelPrice?, bool)>>(key, task));
        return price;
    }

    private async Task<(ModelPrice? Price, bool Transient)> FetchAsync(string modelBase, string version, string skuKind, string? region, CancellationToken ct)
    {
        try
        {
            var filter = $"serviceName eq 'Foundry Models' and contains(meterName,'{modelBase}') and type eq 'Consumption'";
            var meters = new List<RetailMeter>();
            if (region is not null && Regex.IsMatch(region, "^[a-z0-9]+$"))
                meters = await ReadAsync($"{filter} and armRegionName eq '{region}'", ct);
            if (meters.Count == 0) meters = await ReadAsync(filter, ct);
            return (Select(meters, modelBase, version, skuKind), false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (null, true);
        }
    }

    private async Task<List<RetailMeter>> ReadAsync(string filter, CancellationToken ct)
    {
        var meters = new List<RetailMeter>();
        string? url = $"{RetailUrl}&$filter={Uri.EscapeDataString(filter)}";
        for (var page = 0; url is not null && page < 5; page++)
        {
            using var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("Items", out var items))
                foreach (var i in items.EnumerateArray())
                    meters.Add(new RetailMeter(
                        i.GetProperty("meterName").GetString() ?? "", i.GetProperty("retailPrice").GetDecimal(),
                        i.GetProperty("unitOfMeasure").GetString() ?? "", i.TryGetProperty("armRegionName", out var r) ? r.GetString() ?? "" : ""));
            url = doc.RootElement.TryGetProperty("NextPageLink", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        }
        return meters;
    }

    /// <summary>Maps a deployment SKU to a price kind. Batch, provisioned and fine-tuned SKUs are priced differently, so they get none.</summary>
    public static string SkuKindOf(string? sku) => sku switch
    {
        "GlobalStandard" => "glbl",
        "DataZoneStandard" => "dz",
        "Standard" => "regnl",
        _ => "",
    };

    /// <summary>"2024-07-18" -> "0718" (the version code used in meter names).</summary>
    public static string VersionCode(string? version) =>
        version is { Length: 10 } && version[4] == '-' && version[7] == '-' ? version[5..7] + version[8..10] : "";

    /// <summary>Splits "gpt-4o-mini-2024-07-18" into ("gpt-4o-mini", "2024-07-18").</summary>
    public static (string Base, string Version) SplitModel(string model)
    {
        var m = Regex.Match(model ?? "", @"^(?<base>.+?)-(?<ver>\d{4}-\d{2}-\d{2})$");
        return m.Success ? (m.Groups["base"].Value, m.Groups["ver"].Value) : (model ?? "", "");
    }

    public static ModelPrice? Select(IEnumerable<RetailMeter> meters, string modelBase, string version, string skuKind)
    {
        var prefix = modelBase.ToLowerInvariant() + "-";
        var candidates = new List<(string Ver, string Kind, RetailMeter Meter)>();
        foreach (var meter in meters)
        {
            var name = meter.MeterName.Trim().ToLowerInvariant();
            if (name.EndsWith(" tokens")) name = name[..^7];
            if (!name.StartsWith(prefix)) continue;
            var shape = MeterShape().Match(name[prefix.Length..]);
            if (!shape.Success || NormalizeSku(shape.Groups["sku"].Value) != skuKind) continue;
            candidates.Add((shape.Groups["ver"].Value, NormalizeKind(shape.Groups["kind"].Value), meter));
        }
        if (candidates.Count == 0) return null;

        var wanted = VersionCode(version);
        var versions = candidates.Select(c => c.Ver).Distinct().ToList();
        List<string> chosen;
        if (wanted != "" && versions.Contains(wanted)) chosen = new List<string> { wanted };
        else if (versions.Count == 1 && (wanted == "" || versions[0] == "")) chosen = versions; // single (or unversioned) meter
        else if (versions.Count >= 2) chosen = versions; // version not listed: only valid if every listed version has the same price (checked below)
        else return null;

        var priced = chosen.Select(v => Build(candidates.Where(c => c.Ver == v).ToList(), modelBase, v)).ToList();
        if (priced.Any(p => p is null)) return null;
        var first = priced[0]!;
        if (priced.Skip(1).Any(p => p!.InputPer1M != first.InputPer1M || p.OutputPer1M != first.OutputPer1M || p.CachedInputPer1M != first.CachedInputPer1M))
            return null;
        return chosen.Count > 1 ? first with { Source = first.Source + " (identical across listed versions)" } : first;
    }

    private static ModelPrice? Build(List<(string Ver, string Kind, RetailMeter Meter)> set, string modelBase, string ver)
    {
        RetailMeter? Find(string kind) => set.Where(c => c.Kind == kind).Select(c => c.Meter).OrderBy(m => m.RetailPrice).FirstOrDefault();
        var input = Find("in") ?? Find("");
        var output = Find("out");
        var cached = Find("cached");
        var embedding = Find("in") is null && Find("") is not null;
        if (input is null || (output is null && !embedding)) return null;
        var inputPrice = PerMillion(input);
        var outputPrice = embedding ? 0m : output is null ? null : PerMillion(output);
        var cachedPrice = cached is null ? null : PerMillion(cached);
        if (inputPrice is null || outputPrice is null || (cached is not null && cachedPrice is null)) return null;
        var label = $"{modelBase}{(ver == "" ? "" : "-" + ver)}";
        return new ModelPrice(inputPrice.Value, cachedPrice, outputPrice.Value, $"Azure list price ({label})");
    }

    private static decimal? PerMillion(RetailMeter m) => m.UnitOfMeasure switch
    {
        "1K" => m.RetailPrice * 1000m,
        "1M" => m.RetailPrice,
        _ => null,
    };

    private static string NormalizeSku(string s) => s switch
    {
        "glbl" or "global" => "glbl",
        "regnl" or "rgnl" or "regional" => "regnl",
        _ => "dz",
    };

    private static string NormalizeKind(string k) => k switch
    {
        "inp" => "in",
        "outp" or "out" => "out",
        "cd-inp" or "cchd-inp" or "cached-inp" => "cached",
        _ => "",
    };

    /// <summary>Cached input tokens are billed at the cached rate when one exists; they are part of the prompt tokens.</summary>
    public static decimal Cost(ModelPrice price, long prompt, long cached, long completion)
    {
        var cachedTokens = price.CachedInputPer1M is null ? 0 : Math.Min(cached, prompt);
        return ((prompt - cachedTokens) * price.InputPer1M + cachedTokens * (price.CachedInputPer1M ?? 0m) + completion * price.OutputPer1M) / 1_000_000m;
    }
}
