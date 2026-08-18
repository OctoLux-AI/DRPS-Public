namespace Drps.Ingestion.Orchestration;

public class IngestionJob
{
    private readonly IngestionRunner _ingestionRunner;
    private readonly BarReconciliationService _barReconciliationService;
    private readonly ILogger<IngestionJob> _logger;

    public IngestionJob(
        IngestionRunner ingestionRunner,
        BarReconciliationService barReconciliationService,
        ILogger<IngestionJob> logger)
    {
        _ingestionRunner = ingestionRunner;
        _barReconciliationService = barReconciliationService;
        _logger = logger;
    }

    public async Task RunOnceAsync(string symbol, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "[INGESTION-JOB]: starting run for {Symbol} {Start}-{End}", symbol, start, end);

        await _ingestionRunner.RunAsync(symbol, start, end, cancellationToken);
        await _barReconciliationService.ReconcileAsync(symbol, start, end, computationVersion: 1, cancellationToken);

        _logger.LogInformation(
            "[INGESTION-JOB]: completed run for {Symbol} {Start}-{End}", symbol, start, end);
    }
}
