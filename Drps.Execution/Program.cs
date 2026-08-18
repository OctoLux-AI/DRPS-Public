using Drps.Adjuster.Configuration;
using Drps.Adjuster.Sentiment;
using Drps.Execution;
using Drps.Execution.Alpaca;
using Drps.Execution.Candidates;
using Drps.Execution.Firing;
using Drps.Execution.Logging;
using Drps.Execution.Notifications;
using Drps.Execution.PreFire;
using Drps.Execution.Reconciliation;
using Drps.Ingestion;
using Drps.Ingestion.Feeders;
using Drps.Ingestion.Persistence;
using Drps.Ledger;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

// Additive alongside the default console provider (already registered by
// Host.CreateApplicationBuilder), same pattern as Drps.Ingestion/Program.cs and
// Drps.Calculator/Program.cs. Log-level filtering is handled upstream by the generic
// host's logging pipeline using this same appsettings.json "Logging" section - no
// separate filtering logic here.
builder.Logging.AddProvider(
    new FileLoggerProvider(Path.Combine(builder.Environment.ContentRootPath, "Logs")));

// Same shared-secrets convention as every other project's Program.cs - Execution needs the
// same Alpaca:KeyId/Alpaca:SecretKey pair AlpacaFeeder/AlpacaAccountFeeder already read, this
// time for the trading API (account/clock/assets/positions/orders) rather than market data.
var sharedSecretsPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".APIKeys", "shared-APIKeys.json");
builder.Configuration.AddJsonFile(sharedSecretsPath, optional: true, reloadOnChange: false);

// Reuses Drps.Ingestion's existing AlpacaOptions - same "Alpaca" config section
// AlpacaFeeder/AlpacaAccountFeeder already bind, no second, duplicate options class.
// AlpacaOptions.BaseUrl already defaults to the paper-trading endpoint
// (https://paper-api.alpaca.markets) - the same base every trading-API call in this project
// must use; this must never be pointed at a live-trading endpoint.
builder.Services.Configure<AlpacaOptions>(builder.Configuration.GetSection("Alpaca"));
builder.Services.AddHttpClient(AlpacaTradingClient.HttpClientName, (sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<AlpacaOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
    client.DefaultRequestHeaders.Add("APCA-API-KEY-ID", options.KeyId);
    client.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", options.SecretKey);
});
builder.Services.AddScoped<IAlpacaTradingClient, AlpacaTradingClient>();

// Pushover push-notification wiring (CLAUDE.md's Execution Layer: Ninth Design Decision) -
// three trigger points: OrderFiringService (a genuine fire succeeds), ReconciliationService
// (orphan/phantom detection), PreFireGateService (kill-switch trip). Registered Scoped, matching
// IAlpacaTradingClient's own lifetime immediately above - same "HTTP-wrapper service consumed by
// scoped call sites" shape, even though this service itself has no scoped dependencies. A
// missing/placeholder Pushover:AppToken/Pushover:UserKey never crashes anything -
// PushoverNotificationService logs and skips internally rather than throwing.
builder.Services.Configure<PushoverOptions>(builder.Configuration.GetSection("Pushover"));
builder.Services.AddHttpClient(PushoverNotificationService.HttpClientName, client =>
{
    client.BaseAddress = new Uri("https://api.pushover.net");
});
builder.Services.AddScoped<IPushoverNotificationService, PushoverNotificationService>();

// Reused directly from Drps.Ingestion - same pattern as Drps.Gate/Drps.Adjuster/Drps.Ledger,
// no second DbContext or duplicate EF configuration. First real consumer of this registration
// in Drps.Execution: PreFireGateService's kill-switch counter and AdjusterParameters read.
builder.Services.AddDbContext<DrpsDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Drps")
        ?? @"Server=(localdb)\mssqllocaldb;Database=Drps;Trusted_Connection=True;"));

// The ordered, short-circuiting pre-fire check pipeline (CLAUDE.md's Execution Layer: First
// Design Decision). Actively called from OrderFiringService - EvaluateOpenAsync on the open
// path (FireAsync) and EvaluateCloseAsync on the close path (FireCloseAsync).
builder.Services.Configure<PreFireGateSettings>(builder.Configuration.GetSection("PreFireGate"));
builder.Services.AddScoped<KillSwitchTracker>();
builder.Services.AddScoped<PreFireGateService>();

// Sentiment Adjuster Multiplier Decision (CLAUDE.md 2026-07-24) - fire-time, bidirectional
// sizing adjustment, called from OrderFiringService.FireAsync only (never FireCloseAsync - see
// that method's own comment). Same shared-secrets convention as every other API-key-backed
// client in this codebase - Marketaux:ApiKey already lives in shared-APIKeys.json alongside
// Alpaca/Finnhub/Tiingo. MarketauxSentimentClient/SentimentMultiplierService are both stateless
// aside from their own scoped HttpClient/DbContext-free dependencies, matching
// AlpacaAccountFeeder/InsiderLookupService's own Scoped registration shape.
builder.Services.Configure<MarketauxOptions>(builder.Configuration.GetSection("Marketaux"));
builder.Services.AddHttpClient(MarketauxSentimentClient.HttpClientName);
builder.Services.AddScoped<MarketauxSentimentClient>();
builder.Services.Configure<SentimentMultiplierOptions>(builder.Configuration.GetSection("SentimentMultiplier"));
builder.Services.AddScoped<SentimentMultiplierService>();

// Adjuster: Multi-Signal Multiplier Combination - Weighted-Deviation-Sum Model (CLAUDE.md
// 2026-07-26) - per-signal weights (insider, sentiment) OrderFiringService combines via
// MultiSignalMultiplierCombiner at fire time. Day-one defaults (1.0/1.0) live on
// MultiSignalWeightOptions itself; this section is optional in configuration until real
// Monolith/AAR data justifies retuning them.
builder.Services.Configure<MultiSignalWeightOptions>(builder.Configuration.GetSection("MultiSignalWeights"));

// Turns an approved GateScore + AdjusterAllocation pair into a real Alpaca order.
builder.Services.AddScoped<OrderFiringService>();

// The three read-only candidate finders plus the class that composes them into one
// "what should happen right now" result - OrchestrationWorker's own discovery step.
builder.Services.AddScoped<OpenCandidateQuery>();
builder.Services.AddScoped<CloseCandidateQuery>();
builder.Services.AddScoped<PlateauDeactivationCandidateQuery>();
builder.Services.AddScoped<CandidateOrchestrator>();

// The shared, audited Position write path (CLAUDE.md's Execution Layer: Fourth Design
// Decision) and the fill-confirmation polling that calls it after a real fire.
builder.Services.AddScoped<LedgerPositionWriter>();
builder.Services.AddScoped<FillConfirmationService>();

// Singleton, shared in-flight ticker tracker (CLAUDE.md's Execution Layer: Sixth Design
// Decision) - registered once here so OrchestrationWorker and the not-yet-built ATR ratchet
// monitor worker both resolve the SAME instance, not separate ones each with their own state.
builder.Services.AddSingleton<IInFlightPositionTracker, InFlightPositionTracker>();

// OrchestrationWorker - wires CandidateOrchestrator's output to real order firing and fill
// confirmation on a flat polling interval (CLAUDE.md's Execution Layer: Sixth Design Decision).
// IsDryRunEnabled defaults to true (see OrchestrationSettings) - this must be deliberately
// flipped to false in configuration before this Worker will ever place a real order.
builder.Services.Configure<OrchestrationSettings>(builder.Configuration.GetSection("Orchestration"));
builder.Services.AddHostedService<OrchestrationWorker>();

// AtrRatchetMonitorWorker - live ATR ratchet stop monitoring on already-held positions, on its
// own, separate polling interval (CLAUDE.md's Execution Layer: Third Design Decision). Resolves
// the SAME IInFlightPositionTracker singleton registered above as OrchestrationWorker, so
// neither worker ever races the other to close the same position independently.
// IsDryRunEnabled defaults to true (see AtrMonitorSettings) - this must be deliberately flipped
// to false in configuration before a real ATR stop breach will ever fire a real close order.
builder.Services.Configure<AtrMonitorSettings>(builder.Configuration.GetSection("AtrMonitor"));
builder.Services.AddHostedService<AtrRatchetMonitorWorker>();

// ReconciliationService - the two-way Ledger/Alpaca position diff (CLAUDE.md's Execution
// Layer: Fifth Design Decision). Reuses the same LedgerPositionWriter registered above for
// FillConfirmationService - one shared, audited write path, not a second copy.
builder.Services.AddScoped<ReconciliationService>();

// ReconciliationWorker - runs ReconciliationService on its own, genuinely slower cadence
// (PollIntervalSeconds default 300s) than OrchestrationWorker/AtrRatchetMonitorWorker above -
// the last piece of the Execution Layer scope opened this session (Execution Layer: Addendum -
// Worker SDK chosen specifically to host several BackgroundServices at once).
builder.Services.Configure<ReconciliationSettings>(builder.Configuration.GetSection("Reconciliation"));
builder.Services.AddHostedService<ReconciliationWorker>();

var host = builder.Build();
host.Run();
