using Drps.Adjuster.Configuration;
using Drps.Adjuster.Scoring;
using Microsoft.Extensions.Options;

namespace Drps.Adjuster;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AdjusterSettings _settings;
    private readonly Func<DateTime> _nowProvider;

    public Worker(
        ILogger<Worker> logger,
        IServiceScopeFactory scopeFactory,
        IOptions<AdjusterSettings> settings,
        Func<DateTime>? nowProvider = null)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _settings = settings.Value;
        // Clock-injection seam, same precedent as Drps.Gate's Worker - lets Drps.Monolith
        // later replay this loop's logic against a historical "as of" date. Live runs use
        // the default real-clock provider.
        _nowProvider = nowProvider ?? (() => DateTime.Now);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runNumber = 0;

        // Same recurring shape as Drps.Gate/Drps.Calculator's Worker: ends only via
        // stoppingToken cancellation (Ctrl+C, host shutdown) - never on its own.
        while (!stoppingToken.IsCancellationRequested)
        {
            runNumber++;
            var asOf = _nowProvider();
            _logger.LogInformation(
                "[ADJUSTER-WORKER]: starting scheduled run {RunNumber} as of {AsOf}", runNumber, asOf);

            // Fresh DI scope per scan (Anti-Spaghetti flush rule), same shape as
            // Drps.Gate's Worker.
            using (var scope = _scopeFactory.CreateScope())
            {
                var scanService = scope.ServiceProvider.GetRequiredService<AdjusterScanService>();

                try
                {
                    await scanService.RunScanAsync(asOf, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[ADJUSTER-WORKER]: scheduled run {RunNumber} failed", runNumber);
                }
            }

            _logger.LogInformation(
                "[ADJUSTER-WORKER]: scheduled run {RunNumber} complete, next run in {IntervalMinutes} minute(s)",
                runNumber, _settings.IntervalMinutes);

            await Task.Delay(TimeSpan.FromMinutes(_settings.IntervalMinutes), stoppingToken);
        }
    }
}
