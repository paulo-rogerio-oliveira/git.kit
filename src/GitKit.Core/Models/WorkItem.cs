namespace GitKit.Core.Models;

/// <summary>
/// Um work item do Azure DevOps (User Story ou Task) exibido na aba User Stories.
/// </summary>
public sealed record WorkItem(int Id, string Type, string Title, string State, string AssignedTo, string Description)
{
    /// <summary>Texto curto exibido no card.</summary>
    public string Display => $"#{Id}  {Title}";

    /// <summary>Responsável ou marcador de "sem responsável".</summary>
    public string AssignedToDisplay => string.IsNullOrWhiteSpace(AssignedTo) ? "(sem responsável)" : AssignedTo;

    public override string ToString() => Display;
}

/// <summary>
/// Configurações de acesso ao Azure DevOps (REST + PAT), persistidas no banco local.
/// </summary>
public sealed record DevOpsSettings(string OrgUrl, string Project, string Pat, string UserEmail)
{
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(OrgUrl)
        && !string.IsNullOrWhiteSpace(Project)
        && !string.IsNullOrWhiteSpace(Pat)
        && !string.IsNullOrWhiteSpace(UserEmail);
}
