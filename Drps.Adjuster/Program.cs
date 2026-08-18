using Drps.Adjuster.Configuration;
using Drps.Adjuster.OptionsFlow;
using Drps.Adjuster.Scoring;
using Drps.Adjuster.Sizing;
using Drps.Ingestion;
using Drps.Ingestion.Feeders;
using Drps.Ingestion.Persistence;
using Drps.Ingestion.Verification;
using Drps.Ledger;
using Drps.Shared.Notifications;
using Drps.Shared.Positioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

// Same shared-secrets convention as Drps.Ingestion's Program.cs - Adjuster needs the same
// Alpaca key pair Drps.Ingestion already reads for AlpacaFeeder's bar fetches, this time for
// AlpacaAccountFeeder's account-state call. Added after user-secrets so it overrides on any
// matching key; anything only present in user-secrets still falls through untouched.
var sharedSecretsPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".APIKeys", "shared-APIKeys.json");
builder.Configuration.AddJsonFile(sharedSecretsPath, optional: true, reloadOnChange: false);

builder.Services.Configure<AdjusterSettings>(builder.Configuration.GetSection("Adjuster"));

// Reused directly from Drps.Ingestion - confirmed decision, no second DbContext or
// duplicate EF configuration. Same connection string config key as every other project so
// all of them point at the same database.
builder.Services.AddDbContext<DrpsDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Drps")
        ?? @"Server=(localdb)\mssqllocaldb;Database=Drps;Trusted_Connection=True;"));

// Real, Drps.Ledger-backed implementation (LedgerPortfolioStateProvider's own doc comment) -
// Scoped, not Singleton, since it depends on the scoped DrpsDbContext registered above.
// StubPortfolioStateProvider (Drps.Adjuster.Positioning) is left in place as a
// reference/fallback, just no longer the active registration.
builder.Services.AddScoped<IPortfolioStateProvider, LedgerPortfolioStateProvider>();

// Position-count-cap displacement's write path (CLAUDE.md's "Adjuster: Concurrent-Position-
// Cap Displacement, 10% Relative Composite-Score Margin," 2026-08-01) - same registration
// shape as Drps.Gate's Program.cs (LedgerLifecycleStampService/ILifecycleNotificationService),
// the only other real consumer of this class today. Structural notification seam only, not
// live notification behavior - see ILifecycleNotificationService's own doc comment; does not
// advance the existing "Pushover parked" decision.
builder.Services.AddScoped<ILifecycleNotificationService, NoOpLifecycleNotificationService>();
builder.Services.AddScoped<LedgerLifecycleStampService>();

// Same "Alpaca" config section/credential pair Drps.Ingestion's AlpacaFeeder already binds,
// this time pointed at the account/trading base URL (AlpacaOptions.BaseUrl) rather than the
// market-data base AlpacaFeeder uses - AlpacaAccountFeeder's own doc comment explains why
// GET /v2/account under that base is still read-only account state, not order placement.
builder.Services.Configure<AlpacaOptions>(builder.Configuration.GetSection("Alpaca"));
builder.Services.AddHttpClient(AlpacaAccountFeeder.HttpClientName, (sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<AlpacaOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
    client.DefaultRequestHeaders.Add("APCA-API-KEY-ID", options.KeyId);
    client.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", options.SecretKey);
});
builder.Services.AddScoped<AlpacaAccountFeeder>();

// Insider Form 4 Adjuster Multiplier Decision (CLAUDE.md 2026-07-19) - Scoped, not
// Singleton, since it depends on the scoped DrpsDbContext registered above. Same
// registration shape as Drps.Gate's SectorLookupService/EarningsLookupService.
builder.Services.AddScoped<InsiderLookupService>();

// Options-flow (CBOE delayed-quotes put/call volume ratio) Adjuster multiplier - same
// registration shape as InsiderLookupService/AlpacaAccountFeeder above. No auth, no
// BaseAddress/header setup needed on the named HttpClient (CboeOptionsChainClient builds its
// own full URL per call, unlike AlpacaAccountFeeder's key-bearing client).
builder.Services.Configure<OptionsFlowMultiplierOptions>(builder.Configuration.GetSection("OptionsFlowMultiplier"));
builder.Services.AddHttpClient(CboeOptionsChainClient.HttpClientName);
builder.Services.AddScoped<CboeOptionsChainClient>();

// AdjusterSizingService is stateless (every input is a call parameter, no constructor-time
// clock or per-scan state) - fully DI-resolvable like GateQualityScorer/GateCompositeService
// would be if they didn't need a per-scan GateParameters row at construction. AdjusterScanService
// ties it together with persistence, the account feeder, and the portfolio-state seam - same
// fully-DI-resolvable pattern as GateScanService, no clock dependency; RunScanAsync takes the
// as-of timestamp as a call parameter, not a constructor-injected Func<DateTime>.
builder.Services.AddScoped<AdjusterSizingService>();
builder.Services.AddScoped<AdjusterScanService>();

// Fully qualified: Drps.Ingestion.Worker (pulled in via the Drps.Ingestion.Persistence
// using above's sibling namespace) would otherwise collide with this project's own Worker.
builder.Services.AddHostedService<Drps.Adjuster.Worker>();

var host = builder.Build();
host.Run();
