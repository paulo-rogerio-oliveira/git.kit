using System.Windows;
using GitKit.App.ViewModels;

namespace GitKit.App.Views;

public partial class ConflictsWindow : Window
{
    private ConflictsViewModel? _viewModel;

    public ConflictsWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Activated += OnActivated;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.RequestClose -= OnRequestClose;

        _viewModel = e.NewValue as ConflictsViewModel;

        if (_viewModel is not null)
            _viewModel.RequestClose += OnRequestClose;
    }

    // Ao voltar o foco para a janela (ex.: após resolver no TortoiseGitMerge),
    // reavalia o status dos conflitos automaticamente.
    private async void OnActivated(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
            await _viewModel.RefreshStatusAsync();
    }

    private void OnRequestClose(object? sender, EventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
