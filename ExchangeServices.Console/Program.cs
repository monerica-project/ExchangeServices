using ExchangeServices;
using ExchangeServices.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// ExchangeServices debug console.
//
// A standalone harness for exercising the shared ExchangeServices library without
// running a website. Reads the same config sections the web apps use (from this
// project's appsettings.json + an optional, gitignored appsettings.Secrets.json),
// wires them up with AddExchangeServices, and lets you list exchanges, fetch their
// currency catalogs, and pull a live price — all from the command line.
//
// Usage:
//   dotnet run -- list                 List every registered exchange (price + currency APIs)
//   dotnet run -- currencies           Fetch currency counts for ALL exchanges
//   dotnet run -- currencies <key>     Fetch + print the currency catalog for one exchange
//   dotnet run -- price <key>          Get the XMR->USDT sell (and buy) price for one exchange
//   dotnet run -- price                Get the XMR->USDT sell price for ALL exchanges
//
// <key> is the exchange's ExchangeKey, e.g. "exolix", "changenow", "bitcoinvn".

var builder = Host.CreateApplicationBuilder(args);

// Quiet the framework noise so the report is readable; flip to Information to debug HTTP.
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddJsonFile("appsettings.Secrets.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables();

builder.Services.AddExchangeServices(builder.Configuration);

using var host = builder.Build();

var command = (args.Length > 0 ? args[0] : "list").ToLowerInvariant();
var key = args.Length > 1 ? args[1] : null;

var currencyApis = host.Services.GetServices<IExchangeCurrencyApi>().ToList();
var priceApis = host.Services.GetServices<IExchangePriceApi>().ToList();

switch (command)
{
    case "list":
        ListExchanges(currencyApis, priceApis);
        break;

    case "currencies":
        await CurrenciesAsync(currencyApis, key);
        break;

    case "price":
        await PriceAsync(priceApis, key);
        break;

    default:
        Console.WriteLine($"Unknown command '{command}'. Try: list | currencies [key] | price [key]");
        break;
}

return;

static void ListExchanges(List<IExchangeCurrencyApi> currencyApis, List<IExchangePriceApi> priceApis)
{
    Console.WriteLine($"Currency APIs ({currencyApis.Count}):");
    foreach (var c in currencyApis.OrderBy(c => c.ExchangeKey))
        Console.WriteLine($"  {c.ExchangeKey}");

    Console.WriteLine();
    Console.WriteLine($"Price APIs ({priceApis.Count}):");
    foreach (var p in priceApis.OrderBy(p => p.ExchangeKey))
        Console.WriteLine($"  {p.ExchangeKey,-16} {p.SiteName}");
}

static async Task CurrenciesAsync(List<IExchangeCurrencyApi> currencyApis, string? key)
{
    if (key is null)
    {
        // Health check across every exchange: how many currencies each returns.
        Console.WriteLine("Fetching currency counts for all exchanges...\n");
        foreach (var api in currencyApis.OrderBy(c => c.ExchangeKey))
        {
            try
            {
                var list = await api.GetCurrenciesAsync();
                Console.WriteLine($"  {api.ExchangeKey,-16} {list.Count,6} currencies");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {api.ExchangeKey,-16}   ERROR: {ex.Message}");
            }
        }
        return;
    }

    var match = currencyApis.FirstOrDefault(c => c.ExchangeKey.Equals(key, StringComparison.OrdinalIgnoreCase));
    if (match is null)
    {
        Console.WriteLine($"No currency API with key '{key}'. Use 'list' to see them.");
        return;
    }

    Console.WriteLine($"Fetching currencies for '{match.ExchangeKey}'...\n");
    var currencies = await match.GetCurrenciesAsync();
    Console.WriteLine($"{currencies.Count} currencies. First 40:\n");
    foreach (var c in currencies.Take(40))
        Console.WriteLine($"  {c.Ticker,-12} {c.Network,-20} ({c.ExchangeId})");
    if (currencies.Count > 40)
        Console.WriteLine($"  ... and {currencies.Count - 40} more");
}

static async Task PriceAsync(List<IExchangePriceApi> priceApis, string? key)
{
    // A representative pair available almost everywhere.
    var query = new PriceQuery(new AssetRef("XMR"), new AssetRef("USDT", "Tron"));

    var targets = key is null
        ? priceApis.OrderBy(p => p.ExchangeKey).ToList()
        : priceApis.Where(p => p.ExchangeKey.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();

    if (key is not null && targets.Count == 0)
    {
        Console.WriteLine($"No price API with key '{key}'. Use 'list' to see them.");
        return;
    }

    Console.WriteLine($"XMR -> USDT price ({query.Key}):\n");
    foreach (var api in targets)
    {
        try
        {
            var sell = await api.GetSellPriceAsync(query);
            var buy = api is IExchangeBuyPriceApi b ? await b.GetBuyPriceAsync(query) : null;
            var sellStr = sell is null ? "n/a" : sell.Price.ToString("0.######");
            var buyStr = buy is null ? "n/a" : buy.Price.ToString("0.######");
            Console.WriteLine($"  {api.ExchangeKey,-16} sell={sellStr,14}  buy={buyStr,14}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {api.ExchangeKey,-16} ERROR: {ex.Message}");
        }
    }
}
