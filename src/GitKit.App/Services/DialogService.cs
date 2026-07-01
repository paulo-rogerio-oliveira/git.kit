using System.Windows;
using GitKit.App.ViewModels;
using GitKit.App.Views;
using Microsoft.Win32;

namespace GitKit.App.Services;

/// <summary>
/// Implementação WPF de <see cref="IDialogService"/>.
/// </summary>
public sealed class DialogService : IDialogService
{
    public bool ShowConflicts(ConflictsViewModel viewModel)
    {
        var window = new ConflictsWindow
        {
            Owner = Application.Current?.MainWindow,
            DataContext = viewModel,
        };
        return window.ShowDialog() == true;
    }

    public string? PickFile(string title, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
            Multiselect = false,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public void ShowInfo(string title, string message)
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowError(string title, string message)
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public bool Confirm(string title, string message)
        => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}
