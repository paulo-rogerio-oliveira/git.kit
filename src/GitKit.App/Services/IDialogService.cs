using GitKit.App.ViewModels;

namespace GitKit.App.Services;

/// <summary>
/// Abstrai diálogos de UI para manter os ViewModels livres de WPF.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Exibe (modalmente) o formulário de resolução de conflitos.
    /// Retorna true se a replicação foi concluída dentro do formulário.
    /// </summary>
    bool ShowConflicts(ConflictsViewModel viewModel);

    /// <summary>
    /// Abre um seletor de arquivo. Retorna o caminho escolhido ou null se cancelado.
    /// </summary>
    string? PickFile(string title, string filter);

    /// <summary>Exibe uma mensagem informativa.</summary>
    void ShowInfo(string title, string message);

    /// <summary>Exibe uma mensagem de erro.</summary>
    void ShowError(string title, string message);

    /// <summary>Exibe uma confirmação Sim/Não. Retorna true para "Sim".</summary>
    bool Confirm(string title, string message);
}
