using System.Windows.Input;

namespace GitKit.App.MVVM;

/// <summary>
/// <see cref="ICommand"/> síncrono com parâmetro tipado (ex.: um item de linha do grid).
/// </summary>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(Cast(parameter)) ?? true;

    public void NotifyCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();

    public void Execute(object? parameter) => _execute(Cast(parameter));

    private static T? Cast(object? parameter) => parameter is T value ? value : default;
}
