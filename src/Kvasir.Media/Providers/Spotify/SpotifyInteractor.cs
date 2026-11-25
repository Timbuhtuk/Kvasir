using Microsoft.Extensions.Configuration;
using SpotifyAPI.Web;

namespace Kvasir.Media;

/// <summary>
/// Struct for Spotify API operations (no DI needed)
/// </summary>
public struct SpotifyInteractor
{
    private readonly IConfiguration _configuration;
    private readonly bool _supportSpotify;
    private readonly SpotifyClientConfig? _clientConfig;

    public SpotifyInteractor(IConfiguration configuration) : this(configuration, null) { }

    internal SpotifyInteractor(IConfiguration configuration, SpotifyClientConfig? clientConfig)
    {
        _configuration = configuration;
        _supportSpotify = bool.Parse(_configuration["spotify_settings:SPOTIFY_ENABLED"] ?? "false");
        _clientConfig = clientConfig;
    }

    public bool IsEnabled => _supportSpotify;

    public async Task<SpotifyClient> GetClientAsync() {
        if (!_supportSpotify)
            throw new InvalidOperationException("Spotify is not enabled");

        string clientId = _configuration["spotify_settings:SPOTIFY_CLIENT_ID"]
            ?? throw new InvalidOperationException("Spotify client ID is not configured");
        string clientSecret = _configuration["spotify_settings:SPOTIFY_CLIENT_SECRET"]
            ?? throw new InvalidOperationException("Spotify client secret is not configured");

        SpotifyClientConfig config = _clientConfig ?? SpotifyClientConfig.CreateDefault();
        ClientCredentialsRequest request = new(clientId, clientSecret);
        ClientCredentialsTokenResponse response = await new OAuthClient(config).RequestToken(request);
        return new SpotifyClient(config.WithToken(response.AccessToken));
    }
}
