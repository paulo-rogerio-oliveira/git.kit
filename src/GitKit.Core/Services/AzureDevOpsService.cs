using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GitKit.Core.Models;

namespace GitKit.Core.Services;

/// <summary>
/// Implementação de <see cref="IAzureDevOpsService"/> sobre a API REST do Azure
/// DevOps (api-version 7.0), autenticada por PAT (Basic). As consultas usam WIQL;
/// os detalhes vêm em lote via <c>workitemsbatch</c>.
/// </summary>
public sealed class AzureDevOpsService : IAzureDevOpsService, IDisposable
{
    private const string ApiVersion = "api-version=7.0";
    private const int MaxBatch = 200; // limite do endpoint workitemsbatch

    private static readonly string[] DetailFields =
    {
        "System.Id", "System.WorkItemType", "System.Title",
        "System.State", "System.AssignedTo", "System.Description",
    };

    private readonly HttpClient _http;
    private DevOpsSettings _settings = new(string.Empty, string.Empty, string.Empty, string.Empty);

    public AzureDevOpsService(HttpClient? http = null) => _http = http ?? new HttpClient();

    public bool IsConfigured => _settings.IsComplete;

    public void Configure(DevOpsSettings settings) => _settings = settings;

    public void Dispose() => _http.Dispose();

    // ----- Consultas -----

    public async Task<IReadOnlyList<WorkItem>> GetMyTaskUserStoriesAsync(CancellationToken ct = default)
    {
        // WIQL de LINKS: US (source) → Task (target) atribuída a mim.
        var wiql = """
            SELECT [System.Id] FROM WorkItemLinks
            WHERE [Source.System.TeamProject] = @project
              AND [Source.System.WorkItemType] = 'User Story'
              AND [System.Links.LinkType] = 'System.LinkTypes.Hierarchy-Forward'
              AND [Target.System.WorkItemType] = 'Task'
              AND [Target.System.AssignedTo] = @Me
            MODE (MustContain)
            """;

        using var doc = await QueryWiqlAsync(wiql, ct).ConfigureAwait(false);

        // Coleta os ids das US (source) das relações retornadas.
        var ids = new List<int>();
        if (doc.RootElement.TryGetProperty("workItemRelations", out var relations))
        {
            foreach (var relation in relations.EnumerateArray())
            {
                if (relation.TryGetProperty("source", out var source)
                    && source.ValueKind == JsonValueKind.Object
                    && source.TryGetProperty("id", out var idEl))
                {
                    var id = idEl.GetInt32();
                    if (!ids.Contains(id))
                        ids.Add(id);
                }
            }
        }

        return await GetDetailsAsync(ids, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorkItem>> GetUnassignedUserStoriesAsync(CancellationToken ct = default)
    {
        var wiql = """
            SELECT [System.Id] FROM WorkItems
            WHERE [System.TeamProject] = @project
              AND [System.WorkItemType] = 'User Story'
              AND [System.AssignedTo] = ''
              AND [System.State] NOT IN ('Closed', 'Removed', 'Done')
            ORDER BY [System.ChangedDate] DESC
            """;

        using var doc = await QueryWiqlAsync(wiql, ct).ConfigureAwait(false);

        var ids = new List<int>();
        if (doc.RootElement.TryGetProperty("workItems", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var idEl))
                    ids.Add(idEl.GetInt32());
            }
        }

        return await GetDetailsAsync(ids, ct).ConfigureAwait(false);
    }

    // ----- Atribuição -----

    public async Task<int> AssignToMeAsync(int userStoryId, string taskTitle, CancellationToken ct = default)
    {
        EnsureConfigured();

        // 1) Atribui a US ao desenvolvedor.
        await PatchWorkItemAsync(userStoryId, new object[]
        {
            new { op = "add", path = "/fields/System.AssignedTo", value = _settings.UserEmail },
        }, ct).ConfigureAwait(false);

        // 2) Cria a Task filha atribuída a ele (nasce 'New' pelas regras do processo).
        var parentUrl = $"{OrgUrl()}/_apis/wit/workItems/{userStoryId}";
        var createBody = new object[]
        {
            new { op = "add", path = "/fields/System.Title", value = taskTitle },
            new { op = "add", path = "/fields/System.AssignedTo", value = _settings.UserEmail },
            new
            {
                op = "add",
                path = "/relations/-",
                value = new { rel = "System.LinkTypes.Hierarchy-Reverse", url = parentUrl },
            },
        };

        var createUrl = $"{OrgUrl()}/{Uri.EscapeDataString(_settings.Project)}/_apis/wit/workitems/$Task?{ApiVersion}";
        using var created = await SendJsonPatchAsync(HttpMethod.Post, createUrl, createBody, ct).ConfigureAwait(false);
        var taskId = created.RootElement.GetProperty("id").GetInt32();

        // 3) Coloca a task em Active (transição New → Active).
        await PatchWorkItemAsync(taskId, new object[]
        {
            new { op = "add", path = "/fields/System.State", value = "Active" },
        }, ct).ConfigureAwait(false);

        return taskId;
    }

    // ----- Infra REST -----

    private void EnsureConfigured()
    {
        if (!IsConfigured)
            throw new InvalidOperationException(
                "Configurações do Azure DevOps incompletas: informe organização, projeto, PAT e e-mail.");
    }

    private string OrgUrl() => _settings.OrgUrl.TrimEnd('/');

    private AuthenticationHeaderValue AuthHeader()
        => new("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{_settings.Pat}")));

    private async Task<JsonDocument> QueryWiqlAsync(string wiql, CancellationToken ct)
    {
        EnsureConfigured();
        var url = $"{OrgUrl()}/{Uri.EscapeDataString(_settings.Project)}/_apis/wit/wiql?{ApiVersion}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { query = wiql }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = AuthHeader();

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Consulta WIQL falhou ({(int)response.StatusCode}): {Excerpt(body)}");

        return JsonDocument.Parse(body);
    }

    private async Task<IReadOnlyList<WorkItem>> GetDetailsAsync(IReadOnlyList<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return Array.Empty<WorkItem>();

        var url = $"{OrgUrl()}/_apis/wit/workitemsbatch?{ApiVersion}";
        var payload = new { ids = ids.Take(MaxBatch).ToArray(), fields = DetailFields };
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = AuthHeader();

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Busca de work items falhou ({(int)response.StatusCode}): {Excerpt(body)}");

        using var doc = JsonDocument.Parse(body);
        var items = new List<WorkItem>();
        foreach (var element in doc.RootElement.GetProperty("value").EnumerateArray())
            items.Add(ParseWorkItem(element));

        // Mantém a ordem da consulta original.
        var order = ids.Select((id, index) => (id, index)).ToDictionary(p => p.id, p => p.index);
        return items.OrderBy(i => order.TryGetValue(i.Id, out var index) ? index : int.MaxValue).ToArray();
    }

    private async Task PatchWorkItemAsync(int id, object[] operations, CancellationToken ct)
    {
        var url = $"{OrgUrl()}/_apis/wit/workitems/{id}?{ApiVersion}";
        using var doc = await SendJsonPatchAsync(HttpMethod.Patch, url, operations, ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendJsonPatchAsync(HttpMethod method, string url, object[] operations, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(operations), Encoding.UTF8, "application/json-patch+json"),
        };
        request.Headers.Authorization = AuthHeader();

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Operação no work item falhou ({(int)response.StatusCode}): {Excerpt(body)}");

        return JsonDocument.Parse(body);
    }

    private static WorkItem ParseWorkItem(JsonElement element)
    {
        var id = element.GetProperty("id").GetInt32();
        var fields = element.GetProperty("fields");

        string Text(string name)
            => fields.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString() ?? string.Empty
                : string.Empty;

        // System.AssignedTo é um objeto identidade ({displayName, uniqueName, ...}).
        var assigned = string.Empty;
        if (fields.TryGetProperty("System.AssignedTo", out var who) && who.ValueKind == JsonValueKind.Object)
        {
            assigned = who.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? string.Empty : string.Empty;
        }

        return new WorkItem(
            id,
            Text("System.WorkItemType"),
            Text("System.Title"),
            Text("System.State"),
            assigned,
            StripHtml(Text("System.Description")));
    }

    // A descrição vem em HTML; remove as tags para exibição/prompt.
    private static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var text = Regex.Replace(html, "<br ?/?>|</p>|</div>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", string.Empty);
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }

    private static string Excerpt(string body)
        => body.Length <= 500 ? body : body[..500] + "…";
}
