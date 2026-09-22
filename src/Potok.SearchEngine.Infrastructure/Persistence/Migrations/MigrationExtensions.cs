using FluentMigrator.Runner;
using FluentMigrator.Runner.Initialization;
using FluentMigrator.Runner.VersionTableInfo;
using Microsoft.Extensions.DependencyInjection;

namespace Potok.SearchEngine.Infrastructure.Persistence.Migrations;

public static class MigrationExtensions
{
    public static IServiceCollection AddSearchEngineMigrations(this IServiceCollection services, string connectionString)
    {
        services
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(InitialSearchEngineSchema).Assembly).For.Migrations())
            .AddLogging(lb => lb.AddFluentMigratorConsole())
            .Configure<TypeFilterOptions>(opt =>
            {
                opt.Namespace = "Potok.SearchEngine.Infrastructure.Persistence.Migrations";
                opt.NestedNamespaces = true;
            })
            .AddScoped<IVersionTableMetaData, SearchEngineVersionTable>();

        return services;
    }

    public static void RunSearchEngineMigrations(this IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        runner.MigrateUp();
    }
}
