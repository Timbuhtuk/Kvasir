using System.Windows;
using System.Windows.Controls;
using Kvasir.Desktop.ViewModels;

namespace Kvasir.Desktop;

public partial class JournalWindow : Window
{
    public JournalWindow() => InitializeComponent();
    private void Clear_Click(object sender, RoutedEventArgs e) => ((ControlViewModel)DataContext).ClearLog();
    private void Log_TextChanged(object sender, TextChangedEventArgs e) => ((TextBox)sender).ScrollToEnd();

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
