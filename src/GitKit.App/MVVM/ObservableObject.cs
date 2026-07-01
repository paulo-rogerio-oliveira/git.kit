using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GitKit.App.MVVM;

/// <summary>
/// Base para objetos que notificam alterações de propriedade (INotifyPropertyChanged).
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// Atribui <paramref name="value"/> a <paramref name="field"/> e dispara a
    /// notificação somente se o valor mudou. Retorna true quando houve mudança.
    /// </summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
