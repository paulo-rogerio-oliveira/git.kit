using GitKit.Core.Models;

namespace GitKit.Core.Services;

/// <summary>
/// Acesso ao Azure DevOps (Boards) via REST + PAT: consulta de User Stories e
/// atribuição (US + Task filha ativa em nome do desenvolvedor).
/// </summary>
public interface IAzureDevOpsService
{
    /// <summary>Define as credenciais/organização usadas nas próximas chamadas.</summary>
    void Configure(DevOpsSettings settings);

    /// <summary>True quando as configurações mínimas (org/projeto/PAT/e-mail) estão presentes.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// User Stories em que o usuário autenticado possui Tasks filhas (WIQL de links,
    /// <c>[Target.AssignedTo] = @Me</c>).
    /// </summary>
    Task<IReadOnlyList<WorkItem>> GetMyTaskUserStoriesAsync(CancellationToken ct = default);

    /// <summary>User Stories sem responsável (AssignedTo vazio), em estados ativos.</summary>
    Task<IReadOnlyList<WorkItem>> GetUnassignedUserStoriesAsync(CancellationToken ct = default);

    /// <summary>
    /// Atribui a US ao desenvolvedor configurado, cria uma Task filha em nome dele e a
    /// coloca em Active. Retorna o id da task criada.
    /// </summary>
    Task<int> AssignToMeAsync(int userStoryId, string taskTitle, CancellationToken ct = default);
}
