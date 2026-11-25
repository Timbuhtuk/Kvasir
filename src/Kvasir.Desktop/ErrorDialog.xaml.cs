using System.Windows;
using Kvasir.Desktop.Controls;
using Kvasir.Desktop.Services;

namespace Kvasir.Desktop;

public partial class ErrorDialog : ThemedDialogWindow
{
    public bool OpenJournal { get; private set; }
    public ErrorDialog(DesktopProblem problem)
    {
        InitializeComponent();
        Title = problem.Title; Heading.Text = problem.Title; Description.Text = problem.Cause;
        Recovery.Text = problem.Recovery; RemedyButton.Content = problem.ActionText;
        RemedyButton.Visibility = problem.Action == ProblemAction.None ? Visibility.Collapsed : Visibility.Visible;
    }
    private void Journal_Click(object sender, RoutedEventArgs e) { OpenJournal = true; DialogResult = false; }
    private void Remedy_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
