namespace LocalExile.Client.Sdk.Engine;

/// <summary>
/// Optional capability interface implemented by <see cref="IFileOverlayService"/> providers
/// that can read un-overlaid stock files from the running 3.29 client's GGPK/bundle archive.
/// </summary>
public interface IStockFileProvider
{
    /// <summary>
    /// Reads a stock game file directly from the underlying 3.29 game archive, bypassing overlays.
    /// Returns <c>null</c> if unavailable or when not running in a live client.
    /// </summary>
    /// <param name="virtualPath">Virtual game path (relative paths containing <c>..</c> are rejected).</param>
    byte[]? ReadStockGameFile(string virtualPath);
}

/// <summary>
/// SDK <c>0.2.0</c> extension helpers for <see cref="IFileOverlayService"/>.
/// </summary>
public static class FileOverlayServiceExtensions
{
    /// <summary>
    /// Reads a stock game file directly from the underlying 3.29 client archive via <see cref="IStockFileProvider"/>.
    /// </summary>
    /// <param name="files">The file overlay service.</param>
    /// <param name="virtualPath">Virtual game path to read.</param>
    /// <returns>The stock file bytes, or <c>null</c> if absent or unsupported.</returns>
    public static byte[]? ReadStockGameFile(this IFileOverlayService files, string virtualPath)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);
        return files is IStockFileProvider provider ? provider.ReadStockGameFile(virtualPath) : null;
    }

    /// <summary>
    /// Invalidates any cached engine representation of <paramref name="virtualPath"/> (for hot-reloading assets).
    /// </summary>
    /// <param name="files">The file overlay service.</param>
    /// <param name="virtualPath">Virtual game path to invalidate.</param>
    public static void InvalidateCachedAsset(this IFileOverlayService files, string virtualPath)
    {
        ArgumentNullException.ThrowIfNull(files);
        files.Invalidate(virtualPath);
    }
}
