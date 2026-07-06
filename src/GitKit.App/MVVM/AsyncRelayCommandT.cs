using System.Windows.Input;

namespace GitKit.App.MVVM;

/// <summary>
/// <see cref="ICommand"/> assíncrono com parâmetro tipado; impede reentrância
/// enquanto a tarefa está em execução.
/// </summary>
public sealed class AsyncRelayCommand<T> : ICommand
{
    private readonly Func<T?, Task> _execute;
    private readonly Func<T?, bool>? _canExecute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<T?, Task> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke(Cast(parameter)) ?? true);

    public void NotifyCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
            return;

        try
        {
            _isExecuting = true;
            CommandManager.InvalidateRequerySuggested();
            await _execute(Cast(parameter)).ConfigureAwait(true);
        }
        finally
        {
            _isExecuting = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private static T? Cast(object? parameter) => parameter is T value ? value : default;
}
