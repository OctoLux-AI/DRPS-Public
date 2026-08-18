using System.Reflection;
using Drps.Ingestion;
using Drps.Ingestion.Feeders;
using Drps.Ingestion.Logging;
using Drps.Ingestion.Orchestration;
using Drps.Ingestion.Persistence;
using Drps.Ingestion.Persistence.Seeding;
using Drps.Shared;
using Drps.Shared.BuildCurrency;
using Drps.Shared.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

// Scheduling resilience audit (this session): registers WindowsServiceLifetime as the host's
// IHostLifetime, but ONLY when WindowsServiceHelpers.IsWindowsService() is actually true (i.e.
// running under the Service Control Manager, not interactively) - AddWindowsService's own
// internal check, not something this call site has to guard itself. Under `dotnet run` in a
// dev terminal this is a genuine no-op: IsWindowsService() is false, so the default console
// IHostLifetime is left untouched and startup/shutdown behavior is unchanged. This is the
// HostApplicationBuilder-era equivalent of the older IHostBuilder.UseWindowsService() extension
// referenced in the audit - same effect, correct API for the minimal-hosting model this
// codebase already uses (Host.CreateApplicationBuilder, not Host.CreateDefaultBuilder).
builder.Services.AddWindowsService(options => options.ServiceName = "DRPS Ingestion");

// Additive alongside the default console provider (already registered by
// Host.CreateApplicationBuilder) so runs can be reviewed after the fact
// without console access. ContentRootPath defaults to the working directory,
// which is Drps.Ingestion/ when run via `dotnet run` from that project.
builder.Logging.AddProvider(
    new FileLoggerProvider(Path.Combine(builder.Environment.ContentRootPath, "Logs")));

// Added last so it overrides user-secrets on any matching key; anything only
// present in user-secrets (not yet in the shared file) still falls through untouched.
// Shared across machines/products (e.g. also read by CapitalFill) but never committed to source control.
//
// Fail-open fix (CLAUDE.md, 2026-08-04): AddJsonFile's own optional:true only covers "file
// doesn't exist" - a malformed or locked file's real read/parse is deferred until
// builder.Build(), which used to throw there uncaught, crashing this whole process before
// host.Run() ever ran. SharedSecretsProbe.Probe reads/parses the file right here, eagerly, so a
// bad file is caught at this specific point rather than inside the much broader
// builder.Build() call - AddJsonFile is only ever invoked once the file is confirmed either
// absent or genuinely loadable.
var sharedSecretsPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".APIKeys", "shared-APIKeys.json");
var sharedSecretsProbeResult = SharedSecretsProbe.Probe(sharedSecretsPath);
if (sharedSecretsProbeResult.Status != SharedSecretsProbeStatus.FailedToLoad)
{
    builder.Configuration.AddJsonFile(sharedSecretsPath, optional: true, reloadOnChange: false);
}

// Single source of truth for the Watchlist array, shared with Drps.Calculator so the two
// engines can never silently drift out of sync (previously each carried its own
// independent copy in its own appsettings.json - see CLAUDE.md's watchlist-duplication
// note). Not a secret, so - unlike the shared secrets file above - this lives in the repo
// root and is checked into source control. optional: false - a missing watchlist is a
// fail-closed startup error, not a silent empty-array default.
var sharedWatchlistPath = Path.Combine(
    builder.Environment.ContentRootPath, "..", "shared-watchlist.appsettings.json");
builder.Configuration.AddJsonFile(sharedWatchlistPath, optional: false, reloadOnChange: false);
builder.Services.Configure<WatchlistOptions>(builder.Configuration.GetSection("Watchlist"));

builder.Services.Configure<IngestionSettings>(builder.Configuration.GetSection("Ingestion"));
builder.Services.Configure<AlpacaOptions>(builder.Configuration.GetSection("Alpaca"));
builder.Services.AddHttpClient(AlpacaFeeder.HttpClientName, (sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<AlpacaOptions>>().Value;
    client.BaseAddress = new Uri("https://data.alpaca.markets");
    client.DefaultRequestHeaders.Add("APCA-API-KEY-ID", options.KeyId);
    client.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", options.SecretKey);
});
builder.Services.AddScoped<IMarketDataFeeder, AlpacaFeeder>();

builder.Services.Configure<TiingoOptions>(builder.Configuration.GetSection("Tiingo"));
builder.Services.AddHttpClient(TiingoFeeder.HttpClientName, client =>
{
    client.BaseAddress = new Uri("https://api.tiingo.com");
});
builder.Services.AddScoped<IMarketDataFeeder, TiingoFeeder>();

// Ex-dividend data - CLAUDE.md's "Ex-Dividend Source: Tiingo Replaces Finnhub" decision
// (2026-08-01, "Built and Verified" addendum same date): FinnhubExDividendFeeder's DI
// registration is retired here - confirmed SourceStatuses already shows it permanently Dead
// (200 consecutive failures, MatchedObservationCount=0, LastSuccessAt=NULL - a genuine 403
// plan-tier wall, never worked once) and zero RawExDividendObservation rows were ever
// written by it. Ex-dividend extraction now happens inline inside TiingoFeeder, riding its
// existing OHLCV HTTP call (see TiingoExDividendMapper) - no dedicated feeder is needed for
// that path. FinnhubExDividendFeeder.cs, ExDividendIngestionRunner.cs, and ExDividendWorker.cs
// are all left in place, unmodified and undeleted (only stopped from running) -
// FinnhubExDividendFeeder's own IExDividendFeeder/HttpClient registrations are removed below
// (it can never recover from its permanent plan-tier 403, so there's no reason to leave it
// registered relying on the dead-source skip to silently no-op it forever); ExDividendWorker's
// hosted-service registration (its scheduled invocation) is removed further down, near
// Worker's own registration. ExDividendIngestionRunner's own AddScoped registration is left in
// place below - IEnumerable<IExDividendFeeder> simply resolves empty now that no feeder
// implements it, which is harmless; nothing constructs ExDividendIngestionRunner in production
// once ExDividendWorker is no longer hosted, but the class stays DI-buildable rather than torn
// out. Configure<FinnhubOptions> below stays - FinnhubSectorFeeder (a genuinely different
// Finnhub use) still needs it.
builder.Services.Configure<FinnhubOptions>(builder.Configuration.GetSection("Finnhub"));
builder.Services.AddScoped<ExDividendIngestionRunner>();

// Sector/industry classification - reuses the Configure<FinnhubOptions> call directly above
// (no second Configure<FinnhubOptions> call needed). ISectorFeeder is its own domain
// (SectorSourceType, not SourceType), see ISectorFeeder's own doc comment.
builder.Services.AddHttpClient(FinnhubSectorFeeder.HttpClientName);
builder.Services.AddScoped<ISectorFeeder, FinnhubSectorFeeder>();
builder.Services.AddScoped<SectorIngestionRunner>();

// Extracted here (rather than only inline inside the AddDbContext<DrpsDbContext> call below) so
// SqlDma5AlignedCandidateSource's own registration - EarningsWorker's ticker source, further
// down - can reuse the identical resolved connection string instead of a second, independently
// falling-back copy of the same LocalDB literal.
var drpsConnectionString = builder.Configuration.GetConnectionString("Drps")
    ?? @"Server=(localdb)\mssqllocaldb;Database=Drps;Trusted_Connection=True;";

// Earnings-calendar data (Finnhub /calendar/earnings) - built and unit-tested 2026-07-19 but
// never registered here until this task (2026-08-02). This gap is what left
// RawEarningsObservations permanently empty, which capped GateQualityScorer's output at WATCH
// unconditionally for every candidate (CLAUDE.md's Earnings Blackout Gate Decision, 2026-07-19)
// since the day this feeder was built - confirmed via a live diagnostic call the same day this
// registration was added that /calendar/earnings is NOT plan-gated on the current key (real 200
// responses, real data), unlike Finnhub's /stock/dividend endpoint. Reuses the
// Configure<FinnhubOptions> call above - same credential FinnhubSectorFeeder already uses. No
// BaseAddress override on the named client: FinnhubEarningsFeeder builds its own full URL from
// FinnhubOptions.BaseUrl, same convention as FinnhubSectorFeeder's own client registration
// immediately above. No ISectorFeeder-style interface exists for this feeder - a single
// concrete Finnhub implementation, registered directly, matching FinnhubEarningsFeeder's own
// "no interface abstraction built yet" shape.
builder.Services.AddHttpClient(FinnhubEarningsFeeder.HttpClientName);
builder.Services.AddScoped<FinnhubEarningsFeeder>();
builder.Services.AddScoped<EarningsIngestionRunner>();

// EarningsWorker's ticker source - CLAUDE.md's "FIX: Repoint EarningsWorker to Calculator's
// dynamic DMA-5-aligned candidate list" (2026-08-08). Raw SqlClient against the same "Drps"
// LocalDB (drpsConnectionString, above, shared with DrpsDbContext's own registration further
// down) - no ProjectReference to Drps.Calculator, which owns the RollingDmaStates table this reads.
builder.Services.AddScoped<IDma5AlignedCandidateSource>(_ => new SqlDma5AlignedCandidateSource(drpsConnectionString));

// SEC EDGAR - second, independent sector/industry source, genuinely informational only
// (never compared against Finnhub's SectorValue - incompatible taxonomies, see
// RawSectorObservation's doc comment). SecEdgarOptions.UserAgent now carries a real contact
// string (fixed 2026-07-27, was a placeholder) - see SecEdgarOptions' own comment.
// SecEdgarCikResolver and SecEdgarRateLimiter
// are singletons deliberately, not scoped - both hold process-wide state (a 24-hour ticker
// cache and a "last request" timestamp respectively) that must survive across the fresh DI
// scope SectorWorker creates per ticker in its watchlist loop.
builder.Services.Configure<SecEdgarOptions>(builder.Configuration.GetSection("SecEdgar"));
// Confirmed empirically (2026-07-23): every call to company_tickers.json 403'd with no
// User-Agent set at the client level - SEC's fair-use enforcement blocks requests lacking a
// descriptive identifying header outright, before the per-request header
// SecEdgarCikResolver.EnsureFreshAsync already sets (from the same SecEdgarOptions.UserAgent
// below) is even evaluated. Set here too, at client-registration time, as the actual fix -
// same (sp, client) pattern AlpacaFeeder's registration already uses above.
builder.Services.AddHttpClient(SecEdgarCikResolver.HttpClientName, (sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<SecEdgarOptions>>().Value;
    client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
});
builder.Services.AddSingleton<SecEdgarCikResolver>();
builder.Services.AddSingleton<SecEdgarRateLimiter>();
builder.Services.AddHttpClient(SecEdgarSectorFeeder.HttpClientName);
builder.Services.AddScoped<ISectorFeeder, SecEdgarSectorFeeder>();

// Regime data (VIX/VXN/VIX3M) - CLAUDE.md's "Regime Data Sourcing" decision (2026-07-26).
// No credentials needed for either source (Cboe's CDN export and FRED's fredgraph.csv
// export are both free/unauthenticated), only the endpoint URLs themselves. Five feeder
// instances registered directly via factory delegates (not IOptions-bound) since each is
// permanently bound to one fixed ticker/URL pair, not a generic-across-any-symbol client.
//
// CboeRegimeClient's CDN export responded in low single-digit seconds every time it was
// tested, so no timeout override is needed there beyond HttpClient's own 100s default.
//
// FredRegimeFeeder no longer uses HttpClient at all (CLAUDE.md's "Production FredRegimeFeeder
// curl.exe Fix", 2026-07-28) - a direct live-trial reproduction against this feeder's own
// real, unmodified code path found HttpClient failed 4 of 8 attempts (50%), each burning
// ~414s exhausting all 3 Polly retries before giving up, matching the real 2026-07-27
// VIX3M/FRED transport-failure incident. CurlFredCsvTransport (shared across both FRED
// feeder instances below - stateless, safe as a singleton) shells out to curl.exe instead,
// which has never failed in either investigation against these same URLs.
builder.Services.AddHttpClient(CboeRegimeFeeder.HttpClientName);
builder.Services.AddSingleton<IFredCsvTransport, CurlFredCsvTransport>();
builder.Services.AddScoped<IRegimeFeeder>(sp => new CboeRegimeFeeder(
    sp.GetRequiredService<IHttpClientFactory>(), "VIX", CboeRegimeFeeder.VixUrl,
    sp.GetRequiredService<ILogger<CboeRegimeFeeder>>()));
builder.Services.AddScoped<IRegimeFeeder>(sp => new CboeRegimeFeeder(
    sp.GetRequiredService<IHttpClientFactory>(), "VXN", CboeRegimeFeeder.VxnUrl,
    sp.GetRequiredService<ILogger<CboeRegimeFeeder>>()));
builder.Services.AddScoped<IRegimeFeeder>(sp => new CboeRegimeFeeder(
    sp.GetRequiredService<IHttpClientFactory>(), "VIX3M", CboeRegimeFeeder.Vix3mUrl,
    sp.GetRequiredService<ILogger<CboeRegimeFeeder>>()));
builder.Services.AddScoped<IRegimeFeeder>(sp => new FredRegimeFeeder(
    sp.GetRequiredService<IFredCsvTransport>(), "VXN", FredRegimeFeeder.VxnUrl, "VXNCLS",
    sp.GetRequiredService<ILogger<FredRegimeFeeder>>()));
builder.Services.AddScoped<IRegimeFeeder>(sp => new FredRegimeFeeder(
    sp.GetRequiredService<IFredCsvTransport>(), "VIX3M", FredRegimeFeeder.Vix3mUrl, "VXVCLS",
    sp.GetRequiredService<ILogger<FredRegimeFeeder>>()));
builder.Services.AddScoped<RegimeIngestionRunner>();

builder.Services.AddDbContext<DrpsDbContext>(options => options.UseSqlServer(drpsConnectionString));
builder.Services.AddScoped<SourceStatusTracker>();
builder.Services.AddScoped<IngestionRunner>();
builder.Services.AddScoped<BarReconciliationService>();
builder.Services.AddScoped<IngestionJob>();

// On-demand manual trigger (CLAUDE.md's "Execution Layer: On-Demand Manual Trigger for
// Ingestion/Calculator/Gate Workers", 2026-07-24) - registers the real process args so
// Worker's DI-resolved constructor can check for "--run-now" once at startup. Same optional-
// seam shape as Worker's own nowProvider parameter: nothing else in this project registers
// string[], so this is unambiguous, and Worker's tests (which construct it directly, not via
// DI) are unaffected since the args parameter simply defaults to null there.
builder.Services.AddSingleton(args);
builder.Services.AddHostedService<Worker>();

// ExDividendWorker's scheduled invocation is retired here - CLAUDE.md's "Ex-Dividend Source:
// Tiingo Replaces Finnhub" decision (2026-08-01). Ex-dividend data now arrives for free on
// every Worker-driven TiingoFeeder OHLCV call (see TiingoExDividendMapper), so the dedicated
// daily 03:00 loop built specifically for Finnhub's separate, deliberately-infrequent call has
// no remaining purpose. ExDividendWorker.cs is left in place, unmodified and undeleted - only
// this AddHostedService registration (its "scheduled invocation") is removed, so it no longer
// runs.

// Retimed to 03:27 (CLAUDE.md's "FIX: Repoint SectorWorker to Calculator's dynamic DMA-5-
// aligned candidate list", 2026-08-08 - was 03:00, ahead of Calculator's own 03:20 run) so this
// Worker's candidate list (IDma5AlignedCandidateSource, registered above - reused as-is from
// EarningsWorker's own 2026-08-08 fix, not a second registration) reflects that same night's
// real DMA-5 alignment state, after Calculator's 03:20 run and EarningsWorker's own 03:25 run.
// Kept as its own hosted service per this codebase's one-worker-per-entity-domain convention.
builder.Services.AddHostedService<SectorWorker>();

// Retimed to 03:25 (CLAUDE.md's "FIX: Repoint EarningsWorker to Calculator's dynamic DMA-5-
// aligned candidate list", 2026-08-08 - was 03:05, ahead of Calculator's own 03:20 run) so this
// Worker's candidate list (IDma5AlignedCandidateSource, registered above) reflects that same
// night's real DMA-5 alignment state, deliberately still ahead of Gate's 03:35 scan - Gate's
// earnings-blackout hard gate reads RawEarningsObservations via EarningsLookupService, so this
// must have already committed for the day before Gate scans. See EarningsWorker's own doc
// comment for the full reasoning.
builder.Services.AddHostedService<EarningsWorker>();

// Ticker-universe snapshot ingestion - ported from CapitalFill's TickerUniverseService (see
// UniverseIngestionRunner's own doc comment). No credentials needed (NasdaqTrader's SymDir
// files are free/unauthenticated), only a User-Agent header, same courtesy CapitalFill's own
// client already sends. Same daily 03:00 cadence class as SectorWorker - a single global
// refresh, not per-watchlist-symbol, so it runs once per scheduled day regardless of
// watchlist size.
builder.Services.AddHttpClient(UniverseIngestionRunner.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "DRPS.Ingestion/1.0 contact@octolux.ai");
});
builder.Services.AddScoped<UniverseIngestionRunner>();
builder.Services.AddHostedService<UniverseWorker>();

// Nightly full-universe bar pull - reads that day's UniverseSnapshot (above) in full, no
// eligibility filter, per CLAUDE.md's "Ingestion Eligibility: No Pool Bifurcation" decision.
// Reuses AlpacaFeeder's own named HttpClient (already registered above with the real base
// address and auth headers) rather than a second registration - same account/credentials,
// just a different (batched, multi-symbol) endpoint shape. Runs 10 minutes after
// UniverseWorker (see UniverseBarSweepWorker's own doc comment for why).
builder.Services.AddScoped<IAlpacaBatchBarFeeder, AlpacaBatchBarFeeder>();
builder.Services.AddScoped<UniverseBarSweepRunner>();
builder.Services.AddHostedService<UniverseBarSweepWorker>();

// Weekly (not nightly) Alpaca-vs-Tiingo data-quality audit - CLAUDE.md's "Weekly Data-Quality
// Audit: Alpaca vs. Tiingo Variance" (2026-07-22). Reads RawOhlcvBar directly, no new feeder or
// credentials needed. Data collection only - no threshold, no pass/fail judgment, no Pushover
// wiring yet; see WeeklyVarianceAuditService's own doc comment.
builder.Services.AddScoped<WeeklyVarianceAuditService>();
builder.Services.AddHostedService<WeeklyVarianceAuditWorker>();

// Same daily 03:00 cadence class as SectorWorker/UniverseWorker - no watchlist loop, a
// single global sweep across all five registered IRegimeFeeder instances.
builder.Services.AddHostedService<RegimeWorker>();

// DI-lifetime-violation fix (this session): ValidateOnBuild defaults to true only when
// EnvironmentName == Development, so a captive-dependency bug (a singleton IHostedService
// constructor-injecting a scoped service, e.g. the SectorWorker/EarningsWorker ->
// IDma5AlignedCandidateSource bug this same fix corrects) would silently pass builder.Build()
// in Production - exactly where these workers actually run under Task Scheduler - and only
// misbehave at runtime with no startup error. Explicitly forced on regardless of environment,
// so this whole class of bug throws at startup here too, not just under `dotnet run` in a dev
// terminal.
builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
{
    ValidateOnBuild = true
}));

var host = builder.Build();

var programLogger = host.Services.GetRequiredService<ILogger<Program>>();
if (sharedSecretsProbeResult.Status == SharedSecretsProbeStatus.FailedToLoad)
{
    programLogger.LogError(
        "[PROGRAM]: shared secrets file {SharedSecretsPath} EXISTS but FAILED TO LOAD - {Reason}. Continuing WITHOUT shared secrets (same as if the file were absent) - any credentials normally sourced from it will be missing; downstream services must already fail closed on that, not crash.",
        sharedSecretsPath,
        sharedSecretsProbeResult.FailureReason);
}
else
{
    programLogger.LogInformation(
        "[PROGRAM]: shared secrets file {SharedSecretsPath} {Status} (overrides user-secrets on matching keys when present)",
        sharedSecretsPath,
        sharedSecretsProbeResult.Status == SharedSecretsProbeStatus.Loadable ? "found and loaded" : "not found, skipped");
}

// Build-currency self-check (CLAUDE.md's "Build-Currency Self-Check" design decision,
// 2026-08-03) - detects exactly the failure class that let Task Scheduler run a build one
// merge behind the earnings-feeder reactivation on this same date, silently, with zero errors
// logged anywhere. Fail-open by construction (BuildCurrencyChecker's own doc comment): a check
// failure only logs a warning below and this process starts normally regardless - it must
// never delay or block startup.
var embeddedInformationalVersion = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
var embeddedCommitHash = BuildCurrencyChecker.ExtractCommitHash(embeddedInformationalVersion);
var buildCurrencyResult = await new BuildCurrencyChecker(new ProcessGitCommandRunner()).CheckAsync(
    embeddedCommitHash,
    builder.Environment.ContentRootPath,
    relevantPaths: ["Drps.Ingestion", "Drps.Shared"]);

switch (buildCurrencyResult.Status)
{
    case BuildCurrencyStatus.Stale:
        programLogger.LogError(
            "[PROGRAM]: STALE BUILD - this published binary is {StaleCommitCount} commit(s) behind main for its own relevant paths ({ChangedPaths}). Re-run publish.ps1.",
            buildCurrencyResult.StaleCommitCount,
            string.Join(", ", buildCurrencyResult.ChangedPaths ?? []));

        // Optional alert (design decision item 5) - a raw HttpClient POST via
        // BuildCurrencyAlerter, deliberately not Drps.Execution's PushoverNotificationService
        // (no cross-project dependency). Never throws - a Pushover outage must never affect
        // startup.
        var appToken = builder.Configuration["Pushover:AppToken"];
        var userKey = builder.Configuration["Pushover:UserKey"];
        using (var pushoverClient = new HttpClient())
        {
            var alertResult = await BuildCurrencyAlerter.SendStaleBuildAlertAsync(
                pushoverClient, appToken, userKey, "Drps.Ingestion", buildCurrencyResult.StaleCommitCount, CancellationToken.None);
            if (alertResult.Outcome is BuildCurrencyAlertOutcome.HttpFailure or BuildCurrencyAlertOutcome.Exception)
            {
                programLogger.LogWarning(
                    "[PROGRAM]: build-currency Pushover alert did not send - {Detail}", alertResult.Detail);
            }
        }
        break;

    case BuildCurrencyStatus.Unverifiable:
        programLogger.LogWarning(
            "[PROGRAM]: could not verify build currency against main - {Detail}", buildCurrencyResult.Detail);
        break;

    case BuildCurrencyStatus.Current:
    default:
        break;
}

// Opt-in, one-time seed for GateParameters' first real row - deliberately NOT run on every
// startup (GateParametersSeeder's own doc comment). Only fires when explicitly requested via
// `dotnet run -- --seed-gate-parameters`; the normal host loop below never runs in that case.
if (args.Contains("--seed-gate-parameters"))
{
    using var seedScope = host.Services.CreateScope();
    var seedDbContext = seedScope.ServiceProvider.GetRequiredService<DrpsDbContext>();
    var seedLogger = seedScope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        await GateParametersSeeder.SeedAsync(seedDbContext, DateTime.UtcNow.Date, CancellationToken.None);
        seedLogger.LogInformation("[PROGRAM]: GateParameters seed complete - one active row inserted");
    }
    catch (Exception ex)
    {
        seedLogger.LogError(ex, "[PROGRAM]: GateParameters seed failed");
    }

    return;
}

// Opt-in, one-time seed retiring NVDA's stale seeder GateScore (Id 2, ScanDate 7/16/2026) from
// OrchestrationWorker's open-candidate consideration - deliberately NOT run on every startup
// (ExcludedTickerSeeder's own doc comment). Only fires when explicitly requested via
// `dotnet run -- --seed-excluded-ticker-nvda`; the normal host loop below never runs in that case.
if (args.Contains("--seed-excluded-ticker-nvda"))
{
    using var seedScope = host.Services.CreateScope();
    var seedDbContext = seedScope.ServiceProvider.GetRequiredService<DrpsDbContext>();
    var seedLogger = seedScope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        var inserted = await ExcludedTickerSeeder.SeedNvdaAsync(seedDbContext, DateTime.UtcNow, CancellationToken.None);
        seedLogger.LogInformation(
            inserted
                ? "[PROGRAM]: ExcludedTicker seed complete - NVDA row inserted"
                : "[PROGRAM]: ExcludedTicker seed skipped - NVDA already excluded");
    }
    catch (Exception ex)
    {
        seedLogger.LogError(ex, "[PROGRAM]: ExcludedTicker NVDA seed failed");
    }

    return;
}

host.Run();
