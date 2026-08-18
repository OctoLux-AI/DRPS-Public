using System.Reflection;
using Drps.Calculator.Persistence;
using Drps.Calculator.Verification;
using Drps.Gate;
using Drps.Gate.Configuration;
using Drps.Gate.Scoring;
using Drps.Ingestion.Persistence;
using Drps.Ingestion.Verification;
using Drps.Ledger;
using Drps.Shared.BuildCurrency;
using Drps.Shared.Configuration;
using Drps.Shared.Notifications;
using Drps.Shared.Positioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = Host.CreateApplicationBuilder(args);

// Scheduling resilience audit (this session) - see Drps.Ingestion/Program.cs's own comment for
// the full "why"/no-op-in-dev explanation, identical here. Genuinely a no-op under `dotnet run`.
builder.Services.AddWindowsService(options => options.ServiceName = "DRPS Gate");

// Added here for the first time (build-currency self-check below, item 5's optional Pushover
// alert) - Drps.Gate previously had no shared-secrets loading at all, since it makes no HTTP
// calls of its own. Same file, same optional:true/no-file-is-fine posture as
// Drps.Ingestion/Drps.Calculator's identical block - purely additive, doesn't change any
// existing Gate configuration binding.
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

builder.Services.Configure<GateSettings>(builder.Configuration.GetSection("Gate"));

// Reused directly from Drps.Ingestion - confirmed decision, no second DbContext or
// duplicate EF configuration. Same connection string config key as Drps.Ingestion so both
// projects point at the same database.
builder.Services.AddDbContext<DrpsDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Drps")
        ?? @"Server=(localdb)\mssqllocaldb;Database=Drps;Trusted_Connection=True;"));

// Reused directly from Drps.Calculator, same reasoning as DrpsDbContext above - Gate reads
// the indicator tables Calculator owns, it does not duplicate their EF configuration. Same
// connection string, so both projects point at the same physical database.
builder.Services.AddDbContext<CalculatorDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Drps")
        ?? @"Server=(localdb)\mssqllocaldb;Database=Drps;Trusted_Connection=True;"));

builder.Services.AddScoped<DmaVerificationJoinService>();
builder.Services.AddScoped<RsiVerificationJoinService>();
// Sleeper Bucket (CLAUDE.md 2026-08-04) - GateScanService's first real consumer of this
// service, built the immediately preceding task alongside its own DI-free unit tests.
builder.Services.AddScoped<RsiSlopeVerificationJoinService>();
builder.Services.AddScoped<RvolVerificationJoinService>();
builder.Services.AddScoped<AtrVerificationJoinService>();
builder.Services.AddScoped<SectorLookupService>();
builder.Services.AddScoped<EarningsLookupService>();

// Real, Drps.Ledger-backed implementation (LedgerPositionStateProvider's own doc comment) -
// Scoped, not Singleton, since it depends on the scoped DrpsDbContext registered above.
// StubPositionStateProvider (Drps.Gate.Positioning) is left in place as a reference/fallback,
// just no longer the active registration.
builder.Services.AddScoped<IPositionStateProvider, LedgerPositionStateProvider>();

// Structural notification seam (see ILifecycleNotificationService's own doc comment) - no-op
// today, does not advance the "Pushover parked until 30 auto-completed trades" decision.
builder.Services.AddScoped<ILifecycleNotificationService, NoOpLifecycleNotificationService>();

// Write path for Gate's composite-degradation lifecycle stamps (LowGradeDate/
// CoolDownStartDate) onto Drps.Ledger's Position rows - CLAUDE.md's 2026-07-18 decision
// block, point 5. Scoped, not Singleton, since it depends on the scoped DrpsDbContext.
builder.Services.AddScoped<LedgerLifecycleStampService>();

// No clock dependency here - GateScanService.RunScanAsync takes the as-of timestamp as a
// parameter rather than a constructor-injected Func<DateTime>, so it's fully DI-resolvable
// like every other service above. See GateScanService's own doc comment for the pattern.
builder.Services.AddScoped<GateScanService>();

// On-demand manual trigger (CLAUDE.md's "Execution Layer: On-Demand Manual Trigger for
// Ingestion/Calculator/Gate Workers", 2026-07-24) - registers the real process args so
// Worker's DI-resolved constructor can check for "--run-now" once at startup. Same convention
// as Drps.Ingestion's/Drps.Calculator's equivalent registrations: nothing else in this project
// registers string[], so this is unambiguous, and Worker's tests (which construct it directly,
// not via DI) are unaffected since the args parameter simply defaults to null there.
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
// covers Drps.Gate's own directory plus every ProjectReference that flows into its published
// output (Drps.Calculator, Drps.Ingestion, Drps.Ledger, Drps.Shared).
var embeddedInformationalVersion = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
var embeddedCommitHash = BuildCurrencyChecker.ExtractCommitHash(embeddedInformationalVersion);
var buildCurrencyResult = await new BuildCurrencyChecker(new ProcessGitCommandRunner()).CheckAsync(
    embeddedCommitHash,
    builder.Environment.ContentRootPath,
    relevantPaths: ["Drps.Gate", "Drps.Calculator", "Drps.Ingestion", "Drps.Ledger", "Drps.Shared"]);

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
                pushoverClient, appToken, userKey, "Drps.Gate", buildCurrencyResult.StaleCommitCount, CancellationToken.None);
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
