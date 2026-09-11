using Application.Interfaces;
using Microsoft.Extensions.Configuration;
using SpotifyAPI.Web;

namespace Connectors.Media;

/// <summary>
/// Struct for Spotify API operations (no DI needed)
/// </summary>
public struct SpotifyInteractor
{
    private readonly IConfiguration _configuration;
    private readonly bool _supportSpotify;

    public SpotifyInteractor(IConfiguration configuration) {
        _configuration = configuration;
        _supportSpotify = bool.Parse(_configuration["spotify_settings:SPOTIFY_ENABLED"] ?? "false");
    }

    public bool IsEnabled => _supportSpotify;

    public async Task<SpotifyClient> GetClientAsync() {
        if (!_supportSpotify)
            throw new InvalidOperationException("Spotify is not enabled");

        string clientId = _configuration["spotify_settings:SPOTIFY_CLIENT_ID"]
            ?? throw new InvalidOperationException("Spotify client ID is not configured");
        string clientSecret = _configuration["spotify_settings:SPOTIFY_CLIENT_SECRET"]
            ?? throw new InvalidOperationException("Spotify client secret is not configured");

        SpotifyClientConfig config = SpotifyClientConfig.CreateDefault();
        ClientCredentialsRequest request = new(clientId, clientSecret);
        ClientCredentialsTokenResponse response = await new OAuthClient(config).RequestToken(request);
        return new SpotifyClient(config.WithToken(response.AccessToken));
    }
}
