using Application.Services;

namespace Application.Interfaces;

/// <summary>
/// Factory interface for creating MusicViewService instances
/// </summary>
public interface IMusicViewFactory
{
    /// <summary>
    /// Creates a new MusicViewService instance
    /// </summary>
    /// <param name="musicClient">Music client service instance</param>
    /// <param name="guildDiscordId">Discord guild ID</param>
    /// <returns>New MusicViewService instance</returns>
    /// <exception cref="System.ArgumentNullException">Thrown when musicClient is null</exception>
    MusicViewService Create(MusicClientService musicClient, ulong guildDiscordId);
}

