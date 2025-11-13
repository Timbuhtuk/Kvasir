using Application.Interfaces;
using Microsoft.Extensions.Configuration;
using SpotifyAPI.Web;

namespace Application;

/// <summary>
/// Struct for Spotify API operations (no DI needed)
/// </summary>
public struct SpotifyInteractor
{
    private readonly IConfiguration _configuration;
    private readonly bool _supportSpotify;

    public SpotifyInteractor(IConfiguration configuration)
    {
        _configuration = configuration;
        _supportSpotify = bool.Parse(_configuration["spotify_settings:SPOTIFY_ENABLED"] ?? "false");
    }

    public bool IsEnabled => _supportSpotify;

    public async Task<SpotifyClient> GetClientAsync()
    {
        if (!_supportSpotify)
            throw new InvalidOperationException("Spotify is not enabled");

        SpotifyClientConfig config = SpotifyClientConfig.CreateDefault();
        ClientCredentialsRequest request = new(
            _configuration["spotify_settings:SPOTIFY_CLIENT_ID"],
            _configuration["spotify_settings:SPOTIFY_CLIENT_SECRET"]);
        ClientCredentialsTokenResponse response = await new OAuthClient(config).RequestToken(request);
        return new SpotifyClient(config.WithToken(response.AccessToken));
    }
}

