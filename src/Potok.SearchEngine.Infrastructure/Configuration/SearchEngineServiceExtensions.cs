using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Potok.SearchEngine.Infrastructure.BackgroundHosting.Cloudflare;
using Potok.SearchEngine.Infrastructure.BackgroundHosting.Refresh;
using Potok.SearchEngine.Infrastructure.Cache;
using Potok.SearchEngine.Infrastructure.Http;
using Potok.SearchEngine.Infrastructure.Http.FlareSolverr;
using Potok.SearchEngine.Infrastructure.Persistence.Migrations;
using Potok.SearchEngine.Infrastructure.Persistence.Repositories;
using Potok.SearchEngine.Infrastructure.Search;
using Potok.SearchEngine.Infrastructure.Services;
using Potok.SearchEngine.Infrastructure.Trackers.Aniliberty;
using Potok.SearchEngine.Infrastructure.Trackers.AnimeLayer;
using Potok.SearchEngine.Infrastructure.Trackers.Kinozal;
using Potok.SearchEngine.Infrastructure.Trackers.MegaPeer;
using Potok.SearchEngine.Infrastructure.Trackers.NNMClub;
using Potok.SearchEngine.Infrastructure.Trackers.RuTor;
using Potok.SearchEngine.Infrastructure.Trackers.RuTracker;

namespace Potok.SearchEngine.Infrastructure.Configuration;

public static class SearchEngineServiceExtensions
{
    // CacheService stamps every entry with Size = 150 MB; the cache must allow several of those.
    private const long MemoryCacheSizeLimit = 4L * 1024 * 1024 * 1024;

    public static IServiceCollection AddSearchEngineServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<Config>(configuration);

        var connectionString = configuration.GetConnectionString("DefaultConnection")
                               ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        services.AddSingleton(connectionString);
        services.AddMemoryCache(options => options.SizeLimit = MemoryCacheSizeLimit);

        // Persistence
        services
            .AddScoped<ITorrentObservationStore>(sp => new TorrentObservationStore(
                sp.GetRequiredService<string>(),
                TimeSpan.FromMinutes(sp.GetRequiredService<IOptions<Config>>().Value.Refresh.PeerFreshnessMin),
                sp.GetRequiredService<Serilog.ILogger>()))
            .AddScoped<ITorrentCatalogStore>(sp => new TorrentCatalogStore(sp.GetRequiredService<string>()))
            .AddScoped<IQueriesRepository, QueriesRepository>()
            .AddScoped<ISubscriptionRepository, SubscriptionRepository>()
            .AddScoped<ISeasonOverrideRepository, SeasonOverrideRepository>()
            .AddScoped<IContinueWatchingRepository, ContinueWatchingRepository>();

        // Search
        services
            .AddScoped<ITrackerIngestion, TrackerIngestion>()
            .AddScoped<QueryRefreshCoordinator>()
            .AddScoped<ILocalSearchService, LocalSearchService>()
            .AddScoped<ISearchService, SearchService>()
            .AddScoped<ITorrentMergerService, TorrentMergerService>()
            .AddScoped<IMediaResolverService, MediaResolverService>()
            .AddScoped<ISubscribeService, SubscribeService>()
            .AddScoped<ITrackerSearch, RuTrackerSearch>()
            .AddScoped<ITrackerSearch, AnilibertySearch>()
            .AddScoped<ITrackerSearch, RuTorSearch>()
            .AddScoped<ITrackerSearch, AnimeLayerSearch>()
            .AddScoped<ITrackerSearch, NNMClubSearch>()
            .AddScoped<ITrackerSearch, KinozalSearch>()
            .AddScoped<ITrackerSearch, MegaPeerSearch>();

        services.AddSingleton<ICacheService, CacheService>();
        services.AddSingleton<TrackerProxyPool>();
        services.AddSingleton<CloudflareGuard>();
        services.AddSingleton<IFlareSolverrClient, FlareSolverrClient>();
        services.AddScoped<TrackerHttpClient>();
        services.AddSearchEngineHttpClients();

        services.AddSearchEngineMigrations(connectionString);

        return services;
    }

    public static IServiceCollection AddSearchEngineInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddEndpointsApiExplorer();
        services.AddControllers();

        services.Configure<ApiBehaviorOptions>(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
            {
                var errors = context.ModelState
                    .Where(e => e.Value?.Errors.Count > 0)
                    .SelectMany(e => e.Value!.Errors)
                    .Select(e => e.ErrorMessage)
                    .Distinct()
                    .ToArray();

                return new BadRequestObjectResult(new
                {
                    error = "Validation failed",
                    details = errors
                });
            };
        });

        services.AddResponseCompression(options =>
        {
            options.MimeTypes = ResponseCompressionDefaults.MimeTypes
                .Concat(["application/vnd.apple.mpegurl", "image/svg+xml"]);
        });

        services.AddRouting(options => options.LowercaseUrls = true);

        services.AddHostedService<RefreshHostedService>();
        services.AddHostedService<CloudflareWarmupHostedService>();

        return services;
    }
}
