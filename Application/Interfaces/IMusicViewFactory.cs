using CS_Discord_Bot.music_parts;

namespace Application.Interfaces;

/// <summary>
/// Factory interface for creating MusicView instances
/// </summary>
public interface IMusicViewFactory
{
    MusicView Create(MusicClient musicClient, ulong guildDiscordId);
}

