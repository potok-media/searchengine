using Potok.SearchEngine.Core.Utils;

namespace Potok.SearchEngine.Infrastructure.Search;

internal static class TorrentIdentityResolver
{
    public static bool TryResolve(
        TorrentDetails item,
        out string normalizedHash,
        out string? errorCode,
        out string? errorMessage)
    {
        normalizedHash = string.Empty;
        errorCode = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(item.Magnet))
            return Fail("missing_magnet", "A valid magnet link is required.", out errorCode, out errorMessage);

        var magnetHash = MagnetBuilder.HashFromMagnet(item.Magnet);
        if (magnetHash is null)
            return Fail("invalid_magnet", "The magnet does not contain a valid BitTorrent hash.", out errorCode, out errorMessage);

        if (!string.IsNullOrWhiteSpace(item.InfoHash))
        {
            var explicitHash = MagnetBuilder.NormalizeHash(item.InfoHash);
            if (explicitHash is null)
                return Fail("invalid_hash", "The explicit info hash is invalid.", out errorCode, out errorMessage);
            if (!string.Equals(explicitHash, magnetHash, StringComparison.Ordinal))
                return Fail("hash_mismatch", "The explicit and magnet-derived hashes differ.", out errorCode, out errorMessage);
        }

        normalizedHash = magnetHash;
        return true;
    }

    private static bool Fail(
        string code,
        string message,
        out string? errorCode,
        out string? errorMessage)
    {
        errorCode = code;
        errorMessage = message;
        return false;
    }
}
