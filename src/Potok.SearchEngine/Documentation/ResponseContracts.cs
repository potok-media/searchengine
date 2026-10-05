using Potok.SearchEngine.Core.Models;
namespace Potok.SearchEngine.Documentation;

// Documentation types for anonymous objects returned by the existing controllers.
public sealed record ApiSuccess(bool Success);
public sealed record SubscriptionResult(bool Result);
public sealed record SeasonOverrideResult(bool Success, Dictionary<string, SeasonOverrideEntry> SeasonMap);
public sealed record FileOverrideResult(bool Success, Dictionary<string, FileOverrideEntry> FileMap);
public sealed record ApiError(string Error, string Message);
