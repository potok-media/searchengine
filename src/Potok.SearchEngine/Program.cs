using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.HttpOverrides;
using Potok.SearchEngine;
using Potok.SearchEngine.Infrastructure.Configuration;
using Potok.SearchEngine.Infrastructure.Persistence.Migrations;
using Potok.SearchEngine.Logging;
using Potok.SearchEngine.Middlewares;
using Scalar.AspNetCore;
using Serilog;

Log.Logger = SerilogSetup.CreateLogger();

var builder = WebApplication.CreateBuilder(args);

var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
{
    builder.WebHost.UseUrls($"http://*:{port}");
}

builder.Logging.ClearProviders();
builder.Host.UseSerilog(Log.Logger, dispose: true);

// Tracker config (config.yml mounted as config.local.yml in Docker)
builder.Configuration.AddYamlFile("config.local.yml", false, true);

// User secrets — явно, для любого окружения: CreateBuilder добавляет их только в
// Development, а локально хост запускается собранным бинарём (Production).
builder.Configuration.AddUserSecrets(System.Reflection.Assembly.GetExecutingAssembly(), optional: true, reloadOnChange: true);


// --- Глобальные настройки ---
CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// Dapper-маппинг snake_case -> PascalCase; от этого зависит ContinueWatchingRepository.
DefaultTypeMap.MatchNamesWithUnderscores = true;

// --- Регистрация зависимостей ---
builder.Services.AddSearchEngineServices(builder.Configuration);
builder.Services.AddSearchEngineInfrastructure(builder.Configuration);
builder.Services.AddOpenApi();

var app = builder.Build();

// --- Middleware ---
app.MapOpenApi();
app.MapScalarApiReference();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsync(new
        {
            error = "Internal server error",
            message = "An unexpected error occurred. Please try again later."
        }.ToJson());
    });
});

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.UseRouting();
app.UseResponseCompression();

app.UseModHeaders();

app.UseSerilogRequestLogging(loggingOptions =>
{
    loggingOptions.MessageTemplate = SerilogSetup.RequestMessageTemplate;
    loggingOptions.GetLevel = SerilogSetup.RequestLogLevel;
    loggingOptions.EnrichDiagnosticContext = SerilogSetup.EnrichRequest;
});

app.MapControllers();

// --- Миграция БД ---
app.Services.RunSearchEngineMigrations();

// --- Запуск приложения ---
await app.RunAsync();

// --- Вспомогательные методы ---
namespace Potok.SearchEngine
{
    internal static class Extensions
    {
        public static string ToJson(this object obj)
        {
            return JsonSerializer.Serialize(obj, new JsonSerializerOptions
            {
                PropertyNamingPolicy = null
            });
        }
    }
}
