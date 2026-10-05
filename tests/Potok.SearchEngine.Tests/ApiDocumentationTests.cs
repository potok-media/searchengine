using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Potok.SearchEngine.Documentation;
using Xunit;

namespace Potok.SearchEngine.Tests;

public sealed class ApiDocumentationTests
{
    // Do not use WebApplicationFactory<Program>: normal startup runs migrations and background workers.
    private static async Task<WebApplication> StartHost()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            ApplicationName = typeof(ApiDocumentationTests).Assembly.FullName,
            ContentRootPath = AppContext.BaseDirectory,
            Args = []
        });
        builder.Configuration["hostBuilder:reloadConfigOnChange"] = "false";
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers().ConfigureApplicationPartManager(manager =>
        {
            manager.ApplicationParts.Clear();
            manager.ApplicationParts.Add(new AssemblyPart(typeof(ApiDocumentation).Assembly));
        });
        builder.Services.AddApiDocumentation();
        var app = builder.Build();
        app.MapApiDocumentation();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Production_document_covers_actions_parameters_and_public_response_types()
    {
        await using var host = await StartHost();
        var response = await host.GetTestClient().GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        var operations = document["paths"]!.AsObject().SelectMany(path => path.Value!.AsObject()
            .Where(method => new[] { "get", "post", "put", "patch", "delete", "options", "head" }.Contains(method.Key))
            .Select(method => (Path: path.Key, Method: method.Key, Operation: method.Value!.AsObject()))).ToArray();
        Assert.NotEmpty(operations);
        Assert.Equal(operations.Length, operations.Select(item => item.Operation["operationId"]!.GetValue<string>()).Distinct().Count());
        foreach (var item in operations)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Operation["summary"]?.GetValue<string>()));
            if (item.Operation["parameters"] is JsonArray parameters)
                foreach (var parameter in parameters)
                    Assert.False(string.IsNullOrWhiteSpace(parameter!["description"]?.GetValue<string>()), $"{item.Path}: {parameter["name"]}");
        }
        var actionKeys = typeof(ApiDocumentation).Assembly.GetTypes()
            .Where(type => type.Name.EndsWith("Controller", StringComparison.Ordinal))
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttributes().Any(attribute => attribute is Microsoft.AspNetCore.Mvc.Routing.IRouteTemplateProvider))
                .Select(method => type.Name.Replace("Controller", "") + "_" + method.Name + "_"))
            .ToArray();
        foreach (var action in actionKeys)
            Assert.Contains(operations, operation => operation.Operation["operationId"]!.GetValue<string>().StartsWith(action, StringComparison.Ordinal));
        var paths = document["paths"]!;
        Assert.NotNull(paths["/api/v1/torrents/search/stream"]!["post"]!["responses"]!["200"]!["content"]!["application/x-ndjson"]);
        Assert.Null(paths["/api/v1/torrents/search/stream"]!["post"]!["responses"]!["200"]!["content"]!["application/json"]);
        Assert.NotNull(paths["/api/v1/torrents/continue/{mediaType}/{tmdbId}"]!["get"]!["responses"]!["204"]);
        var removal = paths["/api/v1/torrents/overrides/{hash}/file/remove"]!["post"]!;
        Assert.Null(removal["requestBody"]);
        Assert.Contains(removal["parameters"]!.AsArray(), parameter => parameter!["name"]!.GetValue<string>() == "fileId" && parameter["in"]!.GetValue<string>() == "query");
        Assert.Null(document["components"]?["securitySchemes"]);
    }

    [Fact]
    public async Task Scalar_uses_local_assets_and_does_not_prefill_or_persist_authentication()
    {
        await using var host = await StartHost();
        var client = host.GetTestClient();
        var response = await client.GetAsync("/scalar/v1");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("openapi/v1.json", html);
        Assert.DoesNotContain("cdn.jsdelivr.net", html);
        Assert.DoesNotContain("fonts.scalar.com", html);
        Assert.DoesNotContain("clientSecret", html);
        Assert.DoesNotContain("bearerToken", html);
        Assert.Contains("\"persistAuth\":false", html);
        var asset = await client.GetAsync("/scalar/scalar.js");
        asset.EnsureSuccessStatusCode();
        Assert.Contains("javascript", asset.Content.Headers.ContentType!.MediaType);

    }

    [Fact]
    public async Task Document_has_no_dangling_references_and_examples_deserialize_as_their_declared_response_DTOs()
    {
        await using var host = await StartHost();
        var document = JsonNode.Parse(await host.GetTestClient().GetStringAsync("/openapi/v1.json"))!;
        void Inspect(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["$ref"] is JsonValue reference && reference.GetValue<string>().StartsWith("#/"))
                {
                    JsonNode? target = document;
                    foreach (var part in reference.GetValue<string>()[2..].Split('/')) target = target?[part.Replace("~1", "/").Replace("~0", "~")];
                    Assert.NotNull(target);
                }
                foreach (var child in obj) Inspect(child.Value);
            }
            else if (node is JsonArray array) foreach (var child in array) Inspect(child);
        }
        Inspect(document);
        var assembly = typeof(ApiDocumentation).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith(".operations.json"));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        var descriptions = JsonNode.Parse(stream)!.AsObject();
        var types = assembly.GetTypes().Concat(typeof(Potok.SearchEngine.Core.Models.TorrentSearchRequest).Assembly.GetTypes())
            .Append(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails)).Where(type => type.IsPublic || type.IsNestedPublic)
            .GroupBy(type => type.Name).ToDictionary(group => group.Key, group => group.First());
        foreach (var operation in descriptions)
            foreach (var response in operation.Value!["responses"]!.AsObject())
            {
                var name = response.Value!["type"]?.GetValue<string>();
                if (name is null || name == "string" || response.Value["examples"] is not JsonObject examples) continue;
                var type = types[name.TrimEnd('[', ']')];
                if (name.EndsWith("[]")) type = type.MakeArrayType();
                foreach (var example in examples)
                {
                    var value = example.Value!["value"];
                    if (example.Value["file"] is JsonValue filename)
                    {
                        using var fixture = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(resource => resource.EndsWith("." + filename.GetValue<string>())))!;
                        value = JsonNode.Parse(fixture);
                        foreach (var key in example.Value["pointer"]!.GetValue<string>().Split('/')) value = value![key];
                    }
                    Assert.NotNull(JsonSerializer.Deserialize(value!.ToJsonString(), type, new JsonSerializerOptions(JsonSerializerDefaults.Web) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow }));
                }
            }
    }
}
