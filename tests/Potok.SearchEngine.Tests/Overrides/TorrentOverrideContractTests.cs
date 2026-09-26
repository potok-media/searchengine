using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Potok.SearchEngine.Controllers;
using Potok.SearchEngine.Core.Interfaces;
using Potok.SearchEngine.Core.Models;
using Xunit;

namespace Potok.SearchEngine.Tests.Overrides;

public class TorrentOverrideContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly ArmEpisodeOverrideTarget Target = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void JsonbContractReadsOldNumericOverridesAndPreservesNewCanonicalTargets()
    {
        const string oldJson = "{\"file-0\":{\"season\":0,\"episode\":1,\"mode\":\"pin\"}}";
        var map = JsonSerializer.Deserialize<Dictionary<string, FileOverrideEntry>>(oldJson, JsonOptions)!;
        Assert.Equal(0, map["file-0"].Season);
        Assert.Null(map["file-0"].ArmTarget);
        map["file-1"] = new(null, null, "anchor", Target, ["file-1", "file-2"]);
        var json = JsonSerializer.Serialize(map, JsonOptions);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(Target.EpisodeId, document.RootElement.GetProperty("file-1").GetProperty("armTarget").GetProperty("episodeId").GetGuid());
        Assert.Equal(Target.EntryId, document.RootElement.GetProperty("file-1").GetProperty("armTarget").GetProperty("entryId").GetGuid());
        var restored = JsonSerializer.Deserialize<Dictionary<string, FileOverrideEntry>>(json, JsonOptions)!;
        Assert.Equal(Target, restored["file-1"].ArmTarget);
        Assert.Equal(new[] { "file-1", "file-2" }, restored["file-1"].ScopeFileIds);
        Assert.Equal(1, restored["file-0"].Episode);
    }

    [Fact]
    public async Task PostFilePersistsCanonicalScopeWithoutInventingNumericCoordinates()
    {
        var repository = new CapturingRepository();
        var controller = new OverridesController(repository);
        var result = await controller.UpsertFile("HASH", new("first", Mode: "anchor", ArmTarget: Target, ScopeFileIds: ["first", "second"]));
        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("hash", repository.Hash);
        Assert.Equal("first", repository.FileId);
        Assert.Equal(Target, repository.Entry!.ArmTarget);
        Assert.Null(repository.Entry.Season);
        Assert.Null(repository.Entry.Episode);
        Assert.Equal(new[] { "first", "second" }, repository.Entry.ScopeFileIds);
    }

    [Theory]
    [InlineData("invalid-mode")]
    [InlineData("empty-guid")]
    [InlineData("empty-scope")]
    [InlineData("duplicate-scope")]
    [InlineData("missing-anchor")]
    [InlineData("scoped-pin")]
    [InlineData("no-target")]
    public async Task PostFileRejectsMalformedCanonicalOverridesBeforePersistence(string error)
    {
        var request = new UpsertFileOverrideRequest("first", Mode: "anchor", ArmTarget: Target, ScopeFileIds: ["first", "second"]);
        request = error switch
        {
            "invalid-mode" => request with { Mode = "foo" },
            "empty-guid" => request with { ArmTarget = Target with { EntryId = Guid.Empty } },
            "empty-scope" => request with { ScopeFileIds = [] },
            "duplicate-scope" => request with { ScopeFileIds = ["first", "first"] },
            "missing-anchor" => request with { ScopeFileIds = ["second"] },
            "scoped-pin" => request with { Mode = "pin" },
            _ => request with { ArmTarget = null }
        };
        var repository = new CapturingRepository();
        Assert.IsType<BadRequestObjectResult>(await new OverridesController(repository).UpsertFile("hash", request));
        Assert.Null(repository.Entry);
    }

    [Fact]
    public async Task PostFileRetainsLegacySeasonZeroAndFractionalEpisode()
    {
        var repository = new CapturingRepository();
        var result = await new OverridesController(repository).UpsertFile("hash", new("a", 0, 24.5m, "pin"));
        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(0, repository.Entry!.Season);
        Assert.Equal(24.5m, repository.Entry.Episode);
    }

    private sealed class CapturingRepository : ISeasonOverrideRepository
    {
        public string? Hash { get; private set; }
        public string? FileId { get; private set; }
        public FileOverrideEntry? Entry { get; private set; }
        public Task<Dictionary<string, FileOverrideEntry>> UpsertFileAsync(string hash, string fileId, FileOverrideEntry entry)
        {
            Hash = hash;
            FileId = fileId;
            Entry = entry;
            return Task.FromResult(new Dictionary<string, FileOverrideEntry> { [fileId] = entry });
        }
        public Task<Dictionary<string, SeasonOverrideEntry>> GetAsync(string hash) => throw new NotSupportedException();
        public Task<Dictionary<string, SeasonOverrideEntry>> UpsertSeasonAsync(string hash, string sourceKey, SeasonOverrideEntry entry) => throw new NotSupportedException();
        public Task<Dictionary<string, SeasonOverrideEntry>> RemoveSeasonAsync(string hash, string sourceKey) => throw new NotSupportedException();
        public Task ReplaceAsync(string hash, Dictionary<string, SeasonOverrideEntry> map) => throw new NotSupportedException();
        public Task<Dictionary<string, FileOverrideEntry>> GetFileMapAsync(string hash) => throw new NotSupportedException();
        public Task<Dictionary<string, FileOverrideEntry>> RemoveFileAsync(string hash, string fileId) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, TorrentOverrideSummary>> GetSummariesAsync(IReadOnlyCollection<string> hashes) => throw new NotSupportedException();
    }
}
