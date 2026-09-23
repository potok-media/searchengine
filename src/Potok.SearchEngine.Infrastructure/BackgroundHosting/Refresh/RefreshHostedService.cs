using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ILogger = Serilog.ILogger;

namespace Potok.SearchEngine.Infrastructure.BackgroundHosting.Refresh;

public class RefreshHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Config _config;
    private readonly ILogger _logger;

    public RefreshHostedService(IServiceScopeFactory scopeFactory, IOptions<Config> config, ILogger logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Refresh.Enable)
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_config.Refresh.TimeOut));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var coordinator = scope.ServiceProvider.GetRequiredService<QueryRefreshCoordinator>();
                await coordinator.RefreshStaleAsync(
                    TimeSpan.FromMinutes(_config.Refresh.OlderThanMin),
                    _config.Refresh.Limit,
                    stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "RefreshHostedService failed");
            }
        }
    }
}
