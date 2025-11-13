using Application.Services;

namespace Application.Interfaces;

/// <summary>
/// Factory interface for creating MusicViewService instances
/// </summary>
public interface IMusicViewFactory
{
    MusicViewService Create(MusicClientService musicClient, ulong guildDiscordId);
}

