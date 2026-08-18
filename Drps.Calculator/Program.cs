using System.Reflection;
using Drps.Calculator;
using Drps.Calculator.Atr;
using Drps.Calculator.Calendar;
using Drps.Calculator.Dma;
using Drps.Calculator.Dma.RollingDma;
using Drps.Calculator.Logging;
using Drps.Calculator.Persistence;
using Drps.Calculator.Rsi;
using Drps.Calculator.Rvol;
using Drps.Calculator.Verification;
using Drps.Shared.BuildCurrency;
using Drps.Shared.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

// Scheduling resilience audit (this session) - see Drps.Ingestion/Program.cs's own comment for
// the full "why"/no-op-in-dev explanation, identical here. Genuinely a no-op under `dotnet run`.
builder.Services.AddWindowsService(options => options.ServiceName = "DRPS Calculator");

// Additive alongside the default console provider (already registered by
// Host.CreateApplicationBuilder), same pattern as Drps.Ingestion/Program.cs.
builder.Logging.AddProvider(
    new FileLoggerProvider(Path.Combine(builder.Environment.ContentRootPath, "Logs")));

// Added last so it overrides user-secrets on any matching key; anything only
// present in user-secrets (not yet in the shared file) still falls through untouched.
// Shared across machines/products (e.g. also read by Drps.Ingestion and CapitalFill) but
// never committed to source control.
//
// Fail-open fix (CLAUDE.md, 2026-08-04) - see Drps.Ingestion/Program.cs's own comment for the
// full "why". SharedSecretsProbe.Probe reads/parses the file eagerly, right here, so a
// malformed or locked file is caught at this specific point instead of inside the much broader
// builder.Build() call below, where it used to throw uncaught and crash this whole process.
var sharedSecretsPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".APIKeys", "shared-APIKeys.json");
var sharedSecretsProbeResult = SharedSecretsProbe.Probe(sharedSecretsPath);
if (sharedSecretsProbeResult.Status != SharedSecretsProbeStatus.FailedToLoad)
{
    builder.Configuration.AddJsonFile(sharedSecretsPath, optional: true, reloadOnChange: false);
}

// Shared with Drps.Ingestion so the two engines can never silently drift out of sync
// (previously each carried its own independent copy in its own appsettings.json - see
// CLAUDE.md's watchlist-duplication note). Not a secret, so - unlike the shared secrets file
// above - this lives in the repo root and is checked into source control. optional: false -
// a missing watchlist is a fail-closed startup error, not a silent empty-array default.
// NOTE: nothing in Drps.Calculator binds WatchlistOptions from this section anymore
// (CLAUDE.md's "Universe-Wide Dynamic Discovery - Watchlist File Retired from Production
// Path", 2026-08-05 - Worker.cs's compute scope is now driven entirely by
// RollingDmaStateService.GetDma5AlignedTickersAsync) - the dead Configure<WatchlistOptions>
// call that used to sit here was removed 2026-08-06, confirmed zero remaining consumers. The
// file load itself is left in place: Drps.Ingestion still reads this same file independently.
var sharedWatchlistPath = Path.Combine(
    builder.Environment.ContentRootPath, "..", "shared-watchlist.appsettings.json");
builder.Configuration.AddJsonFile(sharedWatchlistPath, optional: false, reloadOnChange: false);

builder.Services.Configure<CalculatorSettings>(builder.Configuration.GetSection("Calculator"));
builder.Services.AddDbContext<CalculatorDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Drps")
        ?? @"Server=(localdb)\mssqllocaldb;Database=Drps;Trusted_Connection=True;"));

// DrpsDbContext, alongside CalculatorDbContext above - not a replacement, not used for any
// indicator computation. Solely for the idempotency guard's WorkerRunRecord bookkeeping (this
// session's scheduling-resilience audit, Fix 3) - same connection string, same physical "Drps"
// database, same pattern Drps.Gate already uses for reaching across both DbContexts.
builder.Services.AddDbContext<Drps.Ingestion.Persistence.DrpsDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Drps")
        ?? @"Server=(localdb)\mssqllocaldb;Database=Drps;Trusted_Connection=True;"));

builder.Services.Configure<AlpacaOptions>(builder.Configuration.GetSection("Alpaca"));
builder.Services.AddHttpClient(AlpacaTradingCalendarService.HttpClientName, (sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<AlpacaOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
    client.DefaultRequestHeaders.Add("APCA-API-KEY-ID", options.KeyId);
    client.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", options.SecretKey);
});
builder.Services.AddScoped<ITradingCalendarService, AlpacaTradingCalendarService>();

builder.Services.AddScoped<DmaComputationService>();
builder.Services.AddScoped<RsiComputationService>();
builder.Services.AddScoped<RvolComputationService>();
builder.Services.AddScoped<AtrComputationService>();

// Watch-only per CLAUDE.md's "RsiSlope / RsiConcavity: Design Direction Locked" (2026-07-31) -
// computed and persisted, not wired into any Adjuster/Execution consumer yet. Must run after
// RsiComputationService (RsiSlope) and RsiSlopeComputationService (RsiConcavity) in the same
// cycle - see Worker.cs's per-symbol loop ordering.
builder.Services.AddScoped<RsiSlopeComputationService>();
builder.Services.AddScoped<RsiConcavityComputationService>();

// Full-universe cheap pre-filter (CLAUDE.md's "Rolling DMA State Machine") - structurally
// separate from DmaComputationService above, which now reads its per-symbol compute scope
// from this same service's own GetDma5AlignedTickersAsync (CLAUDE.md's "Universe-Wide Dynamic
// Discovery - Watchlist File Retired from Production Path", 2026-08-05) rather than a fixed
// watchlist - see Worker.cs's own discovery block for the real call site.
builder.Services.AddScoped<RollingDmaStateService>();

// Not yet called by anything in this codebase - registered so it's ready for a future
// consumer (the eventual Gate) to resolve. See DmaVerificationJoinService/
// RsiVerificationJoinService/RvolVerificationJoinService/AtrVerificationJoinService's own
// doc comments for why these are live, read-time checks rather than a cached column on the
// indicator entities.
builder.Services.AddScoped<DmaVerificationJoinService>();
builder.Services.AddScoped<RsiVerificationJoinService>();
builder.Services.AddScoped<RvolVerificationJoinService>();
builder.Services.AddScoped<AtrVerificationJoinService>();

// Dual-source (Alpaca+Tiingo) bar verification for an arbitrary, dynamically-supplied
// candidate ticker list - reuses Drps.Ingestion's own AlpacaFeeder/TiingoFeeder/
// IngestionRunner/BarReconciliationService directly via the existing Drps.Ingestion.csproj
// ProjectReference (see the .csproj's own comment) rather than duplicating any of that logic
// here. Wired into Worker.cs's nightly loop (CandidateBarVerificationService's own doc
// comment has the full "why" and caller shape). IngestionJob is registered below but no
// longer used by CandidateBarVerificationService as of the 2026-08-06 Alpaca-skip fix -
// IngestionRunner is now constructed directly, per ticker, with only the feeder(s) that call
// actually needs (see CandidateBarVerificationService's own doc comment for why the DI-shared
// IngestionRunner/IngestionJob instance can't serve that per-call need). Types below are
// fully qualified rather than `using`'d in, deliberately -
// this project's own AlpacaOptions (configured above, for AlpacaTradingCalendarService)
// would otherwise collide with Drps.Ingestion's distinct AlpacaOptions type; both bind from
// the same "Alpaca" config section without conflict since they're different types.
builder.Services.Configure<Drps.Ingestion.AlpacaOptions>(builder.Configuration.GetSection("Alpaca"));
builder.Services.AddHttpClient(Drps.Ingestion.Feeders.AlpacaFeeder.HttpClientName, (sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<Drps.Ingestion.AlpacaOptions>>().Value;
    client.BaseAddress = new Uri("https://data.alpaca.markets");
    client.DefaultRequestHeaders.Add("APCA-API-KEY-ID", options.KeyId);
    client.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", options.SecretKey);
});
builder.Services.AddScoped<Drps.Ingestion.Feeders.IMarketDataFeeder, Drps.Ingestion.Feeders.AlpacaFeeder>();

builder.Services.Configure<Drps.Ingestion.Feeders.TiingoOptions>(builder.Configuration.GetSection("Tiingo"));
builder.Services.AddHttpClient(Drps.Ingestion.Feeders.TiingoFeeder.HttpClientName, client =>
{
    client.BaseAddress = new Uri("https://api.tiingo.com");
});
builder.Services.AddScoped<Drps.Ingestion.Feeders.IMarketDataFeeder, Drps.Ingestion.Feeders.TiingoFeeder>();

builder.Services.AddScoped<Drps.Ingestion.Orchestration.SourceStatusTracker>();
builder.Services.AddScoped<Drps.Ingestion.Orchestration.IngestionRunner>();
builder.Services.AddScoped<Drps.Ingestion.Orchestration.BarReconciliationService>();
builder.Services.AddScoped<Drps.Ingestion.Orchestration.IngestionJob>();
builder.Services.AddScoped<CandidateBarVerificationService>();

// On-demand manual trigger (CLAUDE.md's "Execution Layer: On-Demand Manual Trigger for
// Ingestion/Calculator/Gate Workers", 2026-07-24) - registers the real process args so
// Worker's DI-resolved constructor can check for "--run-now" once at startup. Same convention
// as Drps.Ingestion's equivalent registration: nothing else in this project registers
// string[], so this is unambiguous, and Worker's tests (which construct it directly, not via
// DI) are unaffected since the args parameter simply defaults to null there.
builder.Services.AddSingleton(args);
builder.Services.AddHostedService<Worker>();

// DI-lifetime-violation fix (this session, see Drps.Ingestion/Program.cs for the full
// reasoning): ValidateOnBuild defaults to true only when EnvironmentName == Development, so a
// captive-dependency bug would silently pass builder.Build() in Production - where this worker
// actually runs under Task Scheduler - and only misbehave at runtime with no startup error.
// Forced on regardless of environment.
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
// 2026-08-03) - see Drps.Ingestion/Program.cs's own comment for the full "why". Path scope here
// additionally includes Drps.Ingestion, since Drps.Calculator.csproj carries a ProjectReference
// to it (WorkerRunGuard reuse) - a commit touching Drps.Ingestion also changes what's compiled
// into this published binary.
var embeddedInformationalVersion = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
var embeddedCommitHash = BuildCurrencyChecker.ExtractCommitHash(embeddedInformationalVersion);
var buildCurrencyResult = await new BuildCurrencyChecker(new ProcessGitCommandRunner()).CheckAsync(
    embeddedCommitHash,
    builder.Environment.ContentRootPath,
    relevantPaths: ["Drps.Calculator", "Drps.Ingestion", "Drps.Shared"]);

switch (buildCurrencyResult.Status)
{
    case BuildCurrencyStatus.Stale:
        programLogger.LogError(
            "[PROGRAM]: STALE BUILD - this published binary is {StaleCommitCount} commit(s) behind main for its own relevant paths ({ChangedPaths}). Re-run publish.ps1.",
            buildCurrencyResult.StaleCommitCount,
            string.Join(", ", buildCurrencyResult.ChangedPaths ?? []));

        var appToken = builder.Configuration["Pushover:AppToken"];
        var userKey = builder.Configuration["Pushover:UserKey"];
        using (var pushoverClient = new HttpClient())
        {
            var alertResult = await BuildCurrencyAlerter.SendStaleBuildAlertAsync(
                pushoverClient, appToken, userKey, "Drps.Calculator", buildCurrencyResult.StaleCommitCount, CancellationToken.None);
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

host.Run();
