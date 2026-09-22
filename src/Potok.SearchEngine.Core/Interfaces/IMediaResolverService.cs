namespace Potok.SearchEngine.Core.Interfaces;

public interface IMediaResolverService
{
    Task<(string? search, string? altname)> ResolveKpImdb(string? search, string? altname);
}
