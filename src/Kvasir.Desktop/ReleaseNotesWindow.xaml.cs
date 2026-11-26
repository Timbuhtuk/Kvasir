using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Kvasir.Desktop.Services;

namespace Kvasir.Desktop;

public partial class ReleaseNotesWindow : Window
{
    internal ReleaseNotesWindow(ReleaseNotes notes, ReleaseNotesState state)
    {
        InitializeComponent();
        VersionBox.SelectionChanged += (_, _) => NotesScroll.ScrollToTop();
        VersionBox.ItemsSource = notes.Entries;
        VersionBox.SelectedIndex = 0;
        ContentRendered += (_, _) =>
        {
            if (ReleaseNotes.IsReleaseBuild && !state.MarkSeen(notes.Current.Version))
                SaveWarning.Visibility = Visibility.Visible;
        };
    }

    internal static ReleaseNotes? LoadCatalog()
    {
        try { return ReleaseNotes.Bundled; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            Trace.WriteLine(ex);
            return null;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
}
