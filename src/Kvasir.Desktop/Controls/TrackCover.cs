using System.Windows;
using System.Windows.Controls;
using Kvasir.Desktop.Services;

namespace Kvasir.Desktop.Controls;

public sealed class TrackCover : Image
{
    public static readonly DependencyProperty LinkProperty = DependencyProperty.Register(
        nameof(Link), typeof(string), typeof(TrackCover), new PropertyMetadata(null, OnLinkChanged));

    private string? _videoId;
    private CancellationTokenSource? _request;

    public string? Link { get => (string?)GetValue(LinkProperty); set => SetValue(LinkProperty, value); }

    public TrackCover()
    {
        Loaded += (_, _) => UpdateCover();
        Unloaded += (_, _) => CancelRequest();
        IsVisibleChanged += (_, _) => UpdateCover();
    }

    private static void OnLinkChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var cover = (TrackCover)sender;
        string? videoId = YouTubeCoverCache.GetVideoId((string?)e.NewValue);
        if (cover._videoId == videoId) return;

        cover.CancelRequest();
        cover._videoId = videoId;
        cover.Source = null;
        cover.UpdateCover();
    }

    private void UpdateCover()
    {
        if (!IsLoaded || !IsVisible)
        {
            CancelRequest();
            return;
        }
        if (_videoId == null || _request != null || Source != null) return;

        _request = new CancellationTokenSource();
        _ = LoadAsync(_videoId, _request);
    }

    private async Task LoadAsync(string videoId, CancellationTokenSource request)
    {
        CancellationToken token = request.Token;
        try
        {
            var image = await Task.Run(() => YouTubeCoverCache.Shared.GetAsync(videoId, token), token);
            if (ReferenceEquals(_request, request)) Source = image;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_request, request)) _request = null;
            request.Dispose();
        }
    }

    private void CancelRequest()
    {
        _request?.Cancel();
        _request = null;
    }
}
