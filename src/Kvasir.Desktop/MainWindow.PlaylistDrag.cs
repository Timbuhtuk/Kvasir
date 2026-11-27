using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kvasir.Control;
using Kvasir.Desktop.ViewModels;

namespace Kvasir.Desktop;

public partial class MainWindow
{
    private const string PlaylistTrackDragFormat = "Kvasir.PlaylistTrack";
    private sealed record PlaylistTrackDrag(ServerWorkspace Server, int PlaylistId, int TrackId, Point Origin, bool PlayOnRelease);
    private sealed record PlaylistDropSite(FrameworkElement Element, int Id, DragDropEffects Effect, bool After = false);
    private PlaylistTrackDrag? _pendingPlaylistDrag, _activePlaylistDrag;
    private PlaylistDropSite? _playlistDropSite;
    private PlaylistDropAdorner? _playlistDropAdorner;
    private AdornerLayer? _playlistAdornerLayer;
    private DispatcherTimer? _playlistDragScrollTimer;
    private ItemsControl? _playlistDragScrollTarget;
    private ScrollViewer? _playlistDragScrollViewer;
    private Point _playlistDragScrollPoint;
    private int _playlistDragScrollDirection;

    private void PlaylistDrag_Start(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _pendingPlaylistDrag = null;
        if (!_viewModel.GuildEnabled || _viewModel.SelectedTab is not { } server ||
            _viewModel.SelectedPlaylist is not { } playlist || FindRow(e.OriginalSource as DependencyObject)?.Item is not TrackItem track)
            return;

        _pendingPlaylistDrag = new(server, playlist.Id, track.Id, e.GetPosition(PlaylistTrackGrid), e.ClickCount == 2);
    }

    private void PlaylistDrag_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _pendingPlaylistDrag = null; return; }
        if (_activePlaylistDrag != null || _pendingPlaylistDrag is not { } drag || !IsCurrentPlaylistDrag(drag)) return;

        // Keep DataGrid's drag-selection from taking over while the pointer crosses the drag threshold.
        e.Handled = true;
        if (!HasPlaylistDragDistance(drag.Origin, e.GetPosition(PlaylistTrackGrid))) return;

        _pendingPlaylistDrag = null;
        _activePlaylistDrag = drag;
        try
        {
            if (PlaylistTrackGrid.IsMouseCaptured) PlaylistTrackGrid.ReleaseMouseCapture();
            DragDrop.DoDragDrop(PlaylistTrackGrid, new DataObject(PlaylistTrackDragFormat, ""),
                DragDropEffects.Move | DragDropEffects.Copy);
        }
        finally
        {
            _activePlaylistDrag = null;
            ClearPlaylistDrop();
        }
    }

    private void PlaylistDrag_Release(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var drag = _pendingPlaylistDrag;
        _pendingPlaylistDrag = null;
        if (drag is not { PlayOnRelease: true } || !IsCurrentPlaylistDrag(drag)) return;

        Point position = e.GetPosition(PlaylistTrackGrid);
        if (!HasPlaylistDragDistance(drag.Origin, position) &&
            FindRow(PlaylistTrackGrid.InputHitTest(position) as DependencyObject)?.Item is TrackItem track && track.Id == drag.TrackId &&
            _viewModel.SelectedSong?.Id == drag.TrackId)
            _viewModel.ActionCommand.Execute("playlist-song-play");
    }

    private static bool HasPlaylistDragDistance(Point origin, Point position) =>
        Math.Abs(position.X - origin.X) >= SystemParameters.MinimumHorizontalDragDistance ||
        Math.Abs(position.Y - origin.Y) >= SystemParameters.MinimumVerticalDragDistance;

    private bool IsCurrentPlaylistDrag(PlaylistTrackDrag drag) =>
        _viewModel.GuildEnabled && ReferenceEquals(drag.Server, _viewModel.SelectedTab) && drag.PlaylistId == _viewModel.SelectedPlaylist?.Id;

    private PlaylistTrackDrag? GetPlaylistDrag(DragEventArgs e) =>
        _activePlaylistDrag is { } drag && IsCurrentPlaylistDrag(drag) && e.Data.GetDataPresent(PlaylistTrackDragFormat, false) ? drag : null;

    private static PlaylistDropSite? FindPlaylistDrop(ItemsControl target, PlaylistTrackDrag drag, Point point)
    {
        if (target.InputHitTest(point) is not DependencyObject source) return null;
        var container = ItemsControl.ContainerFromElement(target, source);
        if (target is ListBox)
            return container is ListBoxItem { Content: PlaylistItem playlist } item && playlist.Id != drag.PlaylistId
                ? new(item, playlist.Id, DragDropEffects.Copy) : null;

        if (container is DataGridRow { Item: TrackItem track } row && track.Id != drag.TrackId)
        {
            bool after = target.TranslatePoint(point, row).Y >= row.ActualHeight / 2;
            return new(row, track.Id, DragDropEffects.Move, after);
        }
        if (container == null && target.ItemContainerGenerator.ContainerFromIndex(target.Items.Count - 1)
            is DataGridRow { Item: TrackItem last } lastRow && last.Id != drag.TrackId)
        {
            Point relative = target.TranslatePoint(point, lastRow);
            if (relative.Y >= lastRow.ActualHeight && relative.X >= 0 && relative.X < lastRow.ActualWidth)
                return new(lastRow, last.Id, DragDropEffects.Move, After: true);
        }
        return null;
    }

    private void PlaylistDrag_Over(object sender, DragEventArgs e)
    {
        var target = (ItemsControl)sender;
        Point point = e.GetPosition(target);
        var drag = GetPlaylistDrag(e);
        var site = drag == null ? null : FindPlaylistDrop(target, drag, point);
        if (site != null && (e.AllowedEffects & site.Effect) == 0) site = null;
        if (drag == null) ClearPlaylistDrop();
        else
        {
            ShowPlaylistDrop(site);
            UpdatePlaylistDragScroll(target, point);
        }
        e.Effects = site?.Effect ?? DragDropEffects.None;
        e.Handled = true;
    }

    private void PlaylistDrag_Leave(object sender, DragEventArgs e) => ClearPlaylistDrop();

    private async void PlaylistDrag_Drop(object sender, DragEventArgs e)
    {
        ClearPlaylistDrop();
        var target = (ItemsControl)sender;
        var drag = GetPlaylistDrag(e);
        var site = drag == null ? null : FindPlaylistDrop(target, drag, e.GetPosition(target));
        e.Effects = site != null && (e.AllowedEffects & site.Effect) != 0 ? site.Effect : DragDropEffects.None;
        e.Handled = true;
        if (drag == null || site == null || e.Effects == DragDropEffects.None) return;

        if (site.Effect == DragDropEffects.Copy)
            await _viewModel.CopyPlaylistTrackAsync(drag.TrackId, site.Id);
        else
        {
            // A column sort would hide the newly saved playlist order.
            PlaylistTrackGrid.Items.SortDescriptions.Clear();
            foreach (var column in PlaylistTrackGrid.Columns) column.SortDirection = null;
            await _viewModel.MovePlaylistTrackAsync(drag.TrackId, site.Id, site.After);
        }
    }

    private void ShowPlaylistDrop(PlaylistDropSite? site)
    {
        if (Equals(_playlistDropSite, site)) return;
        if (_playlistDropAdorner != null) _playlistAdornerLayer?.Remove(_playlistDropAdorner);
        _playlistDropSite = site;
        _playlistDropAdorner = null;
        _playlistAdornerLayer = site == null ? null : AdornerLayer.GetAdornerLayer(site.Element);
        if (site == null || _playlistAdornerLayer == null) return;
        _playlistDropAdorner = new(site, (Brush)FindResource("Ts.Accent")) { IsHitTestVisible = false };
        _playlistAdornerLayer.Add(_playlistDropAdorner);
    }

    private void UpdatePlaylistDragScroll(ItemsControl target, Point point)
    {
        if (_playlistDragScrollTarget != target)
        {
            _playlistDragScrollTarget = target;
            _playlistDragScrollViewer = FindPlaylistScrollViewer(target);
        }
        _playlistDragScrollPoint = point;
        if (_playlistDragScrollViewer is not { } viewer) return;
        Point position = target.TranslatePoint(point, viewer);
        double top = target is DataGrid grid ? grid.ColumnHeaderHeight : 0;
        const double edge = 24;
        _playlistDragScrollDirection = position.Y < top + edge ? -1 : position.Y > viewer.ActualHeight - edge ? 1 : 0;
        if (_playlistDragScrollDirection == 0)
        {
            _playlistDragScrollTimer?.Stop();
            return;
        }
        _playlistDragScrollTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(110), DispatcherPriority.Input, PlaylistDrag_Scroll, Dispatcher);
        _playlistDragScrollTimer.Start();
    }

    private void PlaylistDrag_Scroll(object? sender, EventArgs e)
    {
        if (_activePlaylistDrag is not { } drag || !IsCurrentPlaylistDrag(drag) ||
            _playlistDragScrollTarget is not { } target || _playlistDragScrollViewer is not { } viewer)
        {
            ClearPlaylistDrop();
            return;
        }
        if (_playlistDragScrollDirection < 0 && viewer.VerticalOffset <= 0 ||
            _playlistDragScrollDirection > 0 && viewer.VerticalOffset >= viewer.ScrollableHeight)
        {
            _playlistDragScrollTimer?.Stop();
            return;
        }
        if (_playlistDragScrollDirection < 0) viewer.LineUp();
        else viewer.LineDown();
        target.UpdateLayout();
        ShowPlaylistDrop(FindPlaylistDrop(target, drag, _playlistDragScrollPoint));
    }

    private void ClearPlaylistDrop()
    {
        _playlistDragScrollTimer?.Stop();
        _playlistDragScrollTarget = null;
        _playlistDragScrollViewer = null;
        ShowPlaylistDrop(null);
    }

    private static ScrollViewer? FindPlaylistScrollViewer(DependencyObject element)
    {
        if (element is ScrollViewer viewer) return viewer;
        for (int q = 0; q < VisualTreeHelper.GetChildrenCount(element); q++)
            if (FindPlaylistScrollViewer(VisualTreeHelper.GetChild(element, q)) is { } child) return child;
        return null;
    }

    private sealed class PlaylistDropAdorner(PlaylistDropSite site, Brush brush) : Adorner(site.Element)
    {
        private readonly Pen _pen = new(brush, 2);

        protected override void OnRender(DrawingContext drawingContext)
        {
            Size size = AdornedElement.RenderSize;
            if (size.Width <= 2 || size.Height <= 2) return;
            if (site.Effect == DragDropEffects.Copy)
                drawingContext.DrawRectangle(null, _pen, new Rect(1, 1, size.Width - 2, size.Height - 2));
            else
            {
                double y = site.After ? size.Height - 1 : 1;
                drawingContext.DrawLine(_pen, new Point(0, y), new Point(size.Width, y));
            }
        }
    }
}
