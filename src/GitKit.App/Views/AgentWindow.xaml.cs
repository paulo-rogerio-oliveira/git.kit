using System.Windows;
using GitKit.App.ViewModels;

namespace GitKit.App.Views;

public partial class AgentWindow : Window
{
    private AgentSessionViewModel? _viewModel;

    public AgentWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        TranscriptBox.TextChanged += (_, _) => TranscriptBox.ScrollToEnd();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.RequestClose -= OnRequestClose;

        _viewModel = e.NewValue as AgentSessionViewModel;

        if (_viewModel is not null)
            _viewModel.RequestClose += OnRequestClose;

        TranscriptBox.ScrollToEnd();
    }

    private void OnRequestClose(object? sender, EventArgs e) => Close();
}
