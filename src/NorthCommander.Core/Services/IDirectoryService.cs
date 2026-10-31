using NorthCommander.Core.Models;

namespace NorthCommander.Core.Services;

public interface IDirectoryService
{
    /// <summary>
    /// Reads the contents of <paramref name="path"/> on a background thread.
    /// Directories are listed first, then files, each sorted by name.
    /// A ".." entry is prepended when the directory has a parent.
    /// </summary>
    Task<DirectoryListing> GetDirectoryListingAsync(string path, CancellationToken cancellationToken = default);
}
