using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App.Services;

/// <summary>Periodically retries queued feedback packages once both the network and the feedback server are reachable.</summary>
public sealed class FeedbackQueueWorker : BackgroundService
{
    private readonly IFeedbackService _feedback;
    private readonly ILogger<FeedbackQueueWorker> _logger;

    public FeedbackQueueWorker(IFeedbackService feedback, ILogger<FeedbackQueueWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        ArgumentNullException.ThrowIfNull(logger);
        _feedback = feedback;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int uploaded = await _feedback.FlushQueueAsync(stoppingToken).ConfigureAwait(false);
                if (uploaded > 0)
                {
                    _logger.LogInformation("Flushed {Count} queued feedback package(s).", uploaded);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Flushing the feedback queue failed; it will be retried later.");
            }

            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false);
        }
    }
}
