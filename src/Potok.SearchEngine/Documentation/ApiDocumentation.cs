using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace Potok.SearchEngine.Documentation;

/// <summary>Static API descriptions and examples. This module never reads application configuration or the database.</summary>
public static class ApiDocumentation
{
    private static readonly Assembly Assembly = typeof(ApiDocumentation).Assembly;
    private static readonly JsonObject Operations = Read("operations.json");
    private static readonly JsonObject Schemas = Read("schemas.json");
    private static readonly Dictionary<string, Type> Types = new[] { Assembly, typeof(Potok.SearchEngine.Core.Models.TorrentSearchRequest).Assembly }
        .SelectMany(assembly => assembly.GetTypes())
        .Append(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails))
        .Append(typeof(Microsoft.AspNetCore.Mvc.ValidationProblemDetails))
        .Where(type => type.IsPublic || type.IsNestedPublic)
        .GroupBy(type => type.Name)
        .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

    public static IServiceCollection AddApiDocumentation(this IServiceCollection services)
    {
        services.AddOpenApi("v1", options =>
        {
            options.AddSchemaTransformer((schema, context, cancellationToken) =>
            {
                if (Schemas[context.JsonTypeInfo.Type.Name] is JsonObject docs)
                {
                    schema.Description = docs["description"]?.GetValue<string>();
                    if (schema.Properties is not null && docs["properties"] is JsonObject properties)
                        foreach (var pair in properties)
                            if (schema.Properties.TryGetValue(pair.Key, out var property) && property is OpenApiSchema concrete)
                            {
                                concrete.Description = pair.Value?["description"]?.GetValue<string>();
                                concrete.Deprecated = pair.Value?["deprecated"]?.GetValue<bool>() ?? false;
                            }
                }
                return Task.CompletedTask;
            });
            options.AddOperationTransformer(DescribeOperation);
            options.AddDocumentTransformer((document, context, cancellationToken) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "Potok SearchEngine API",
                    Version = "v1",
                    Description = "Поиск торрентов, потоковые результаты, переопределения и продолжение просмотра. JSON: camelCase; у списка подписок поля tmdb_id и last_refresh_time. Сервис не проверяет Bearer: uid является ключом данных, не авторизацией. Инструкции и примеры — в docs/API.md."
                };
                document.Tags = Operations.Select(pair => pair.Value?["tag"]?.GetValue<string>())
                    .Where(tag => tag is not null).Distinct()
                    .Select(tag => new OpenApiTag { Name = tag! }).ToHashSet();

                return Task.CompletedTask;
            });
        });
        return services;
    }

    public static IEndpointRouteBuilder MapApiDocumentation(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapOpenApi().AllowAnonymous();
        endpoints.MapScalarApiReference("/scalar", options =>
        {
            options.WithTitle("Potok SearchEngine API")
                .WithOpenApiRoutePattern("/openapi/{documentName}.json")
                .DisableDefaultFonts()
                .DisableAgent();
            options.ProxyUrl = null;
            options.PersistentAuthentication = false;
        }).AllowAnonymous();

        return endpoints;
    }

    private static async Task DescribeOperation(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.Description.ActionDescriptor is not ControllerActionDescriptor action)
        {
            if (context.Description.RelativePath == "health")
            {
                operation.OperationId = "Health";
                operation.Summary = "Проверить доступность сервиса";
                operation.Tags = new HashSet<OpenApiTagReference> { new("Сервис", context.Document) };
                operation.Description = "200 с пустым телом. Проверяет HTTP-процесс, не соединения с внешними провайдерами.";
            }
            return;
        }
        var key = action.ControllerName + "." + action.ActionName;
        if (Operations[key] is not JsonObject docs)
            throw new InvalidOperationException($"API documentation missing for {key}.");
        operation.OperationId = key.Replace(".", "_") + "_" + context.Description.HttpMethod;
        operation.Summary = docs["summary"]!.GetValue<string>();
        operation.Deprecated = docs["deprecated"]?.GetValue<bool>() ?? false;
        operation.Description = docs["description"]?.GetValue<string>();
        operation.Tags = new HashSet<OpenApiTagReference> { new(docs["tag"]!.GetValue<string>(), context.Document) };

        if (operation.Parameters is not null)
            foreach (var item in operation.Parameters)
                if (item is OpenApiParameter parameter && docs["parameters"]?[parameter.Name!] is JsonObject paramDocs)
                {
                    parameter.Description = paramDocs["description"]?.GetValue<string>();
                    parameter.Example = paramDocs["example"]?.DeepClone();
                }
        if (operation.RequestBody is OpenApiRequestBody body && docs["requestExamples"] is JsonObject requestExamples)
            foreach (var media in (body.Content ?? new Dictionary<string, OpenApiMediaType>()).Values)
                media.Examples = Examples(requestExamples);

        if (docs["rawRequestBody"]?.GetValue<bool>() == true && context.Description.HttpMethod is not ("GET" or "HEAD"))
            operation.RequestBody = new OpenApiRequestBody { Required = false, Description = "Необязательное исходное тело upstream запроса.", Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = new OpenApiSchema() } } };
        operation.Responses ??= new OpenApiResponses();
        if (context.Description.ParameterDescriptions.Count > 0)
            operation.Responses.TryAdd("400", new OpenApiResponse
            {
                Description = "Неверный JSON или параметры: ValidationProblemDetails.",
                Content = new Dictionary<string, OpenApiMediaType> { ["application/problem+json"] = new() { Schema = await context.GetOrCreateSchemaAsync(typeof(Microsoft.AspNetCore.Mvc.ValidationProblemDetails), null, cancellationToken) } }
            });
        if (docs["responses"] is JsonObject responses)
            foreach (var pair in responses)
            {
                var responseDocs = pair.Value!.AsObject();
                if (!operation.Responses.TryGetValue(pair.Key, out var current) || current is not OpenApiResponse)
                    operation.Responses[pair.Key] = new OpenApiResponse();
                var response = (OpenApiResponse)operation.Responses[pair.Key];
                response.Description = responseDocs["description"]!.GetValue<string>();
                if (responseDocs["type"] is JsonValue typeName)
                {
                    var name = typeName.GetValue<string>();
                    var type = name == "string" ? typeof(string) : Types[name.TrimEnd('[', ']')];
                    if (name.EndsWith("[]", StringComparison.Ordinal)) type = type.MakeArrayType();
                    var schema = await context.GetOrCreateSchemaAsync(type, null, cancellationToken);
                    response.Content = new Dictionary<string, OpenApiMediaType>
                    {
                        [responseDocs["contentType"]?.GetValue<string>() ?? "application/json"] = new() { Schema = schema }
                    };
                }
                if (responseDocs["contentType"] is JsonValue contentType && response.Content is null)
                    response.Content = new Dictionary<string, OpenApiMediaType> { [contentType.GetValue<string>()] = new() };
                if (responseDocs["examples"] is JsonObject examples && response.Content is not null)
                    foreach (var media in response.Content.Values) media.Examples = Examples(examples);
                if (pair.Key == "400")
                {
                    response.Content ??= new Dictionary<string, OpenApiMediaType>();
                    response.Content.TryAdd("application/problem+json", new OpenApiMediaType { Schema = await context.GetOrCreateSchemaAsync(typeof(Microsoft.AspNetCore.Mvc.ValidationProblemDetails), null, cancellationToken) });
                }
                if (responseDocs["headers"] is JsonObject headers)
                {
                    response.Headers ??= new Dictionary<string, IOpenApiHeader>();
                    foreach (var header in headers)
                        response.Headers[header.Key] = new OpenApiHeader { Description = header.Value!.GetValue<string>(), Schema = new OpenApiSchema { Type = JsonSchemaType.String } };
                }
            }


    }

    private static Dictionary<string, IOpenApiExample> Examples(JsonObject examples) => examples.ToDictionary(
        pair => pair.Key,
        pair => (IOpenApiExample)new OpenApiExample
        {
            Summary = pair.Value!["summary"]!.GetValue<string>(),
            Description = pair.Value["description"]?.GetValue<string>(),
            Value = pair.Value["file"] is JsonValue file
                ? Select(Read(file.GetValue<string>()), pair.Value["pointer"]!.GetValue<string>())
                : pair.Value["value"]?.DeepClone()
        });

    private static JsonNode? Select(JsonNode root, string pointer)
    {
        JsonNode? node = root;
        foreach (var key in pointer.Split('/')) node = node?[key];
        return node?.DeepClone();
    }

    private static JsonObject Read(string filename)
    {
        var resource = Assembly.GetManifestResourceNames().Single(name => name.EndsWith("." + filename, StringComparison.Ordinal));
        using var stream = Assembly.GetManifestResourceStream(resource)!;
        return JsonNode.Parse(stream)!.AsObject();
    }
}
