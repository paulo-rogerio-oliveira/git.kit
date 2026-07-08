using Microsoft.Data.Sqlite;

namespace GitKit.Core.Data;

/// <summary>Mensagem persistida de uma sessão do agente (transcript).</summary>
public sealed record AgentMessageRecord(long Id, string SessionId, string Role, string Text, DateTimeOffset At);

/// <summary>
/// Banco SQL embutido (SQLite) do git.kit: settings (DevOps/agente), planejamentos
/// técnicos por User Story e sessões/transcripts do agente. Arquivo único em
/// <c>%LOCALAPPDATA%\git.kit\gitkit.db</c> (ou caminho informado nos testes).
/// Conexões são curtas (uma por operação); o SQLite cuida do pooling/locking.
/// </summary>
public sealed class AppDatabase
{
    private readonly string _connectionString;

    public AppDatabase(string databasePath)
    {
        DatabasePath = databasePath;
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        Initialize();
    }

    public string DatabasePath { get; }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS TechnicalPlans (
                WorkItemId INTEGER PRIMARY KEY,
                Plan TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS AgentSessions (
                Id TEXT PRIMARY KEY,
                WorkItemId INTEGER NOT NULL,
                RepoUrl TEXT NOT NULL,
                Branch TEXT NOT NULL,
                WorkingDir TEXT NOT NULL,
                Status TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS AgentMessages (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId TEXT NOT NULL,
                Role TEXT NOT NULL,
                Text TEXT NOT NULL,
                At TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_AgentMessages_Session ON AgentMessages(SessionId);
            """;
        command.ExecuteNonQuery();
    }

    // ----- Settings -----

    public string? GetSetting(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Settings WHERE Key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Settings (Key, Value) VALUES ($key, $value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    // ----- Planejamentos técnicos -----

    public string? GetPlan(int workItemId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Plan FROM TechnicalPlans WHERE WorkItemId = $id";
        command.Parameters.AddWithValue("$id", workItemId);
        return command.ExecuteScalar() as string;
    }

    public void SavePlan(int workItemId, string plan)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO TechnicalPlans (WorkItemId, Plan, UpdatedAt) VALUES ($id, $plan, $at)
            ON CONFLICT(WorkItemId) DO UPDATE SET Plan = excluded.Plan, UpdatedAt = excluded.UpdatedAt
            """;
        command.Parameters.AddWithValue("$id", workItemId);
        command.Parameters.AddWithValue("$plan", plan);
        command.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
    }

    // ----- Sessões do agente -----

    public void CreateSession(string sessionId, int workItemId, string repoUrl, string branch, string workingDir, string status)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AgentSessions (Id, WorkItemId, RepoUrl, Branch, WorkingDir, Status, CreatedAt)
            VALUES ($id, $wi, $repo, $branch, $dir, $status, $at)
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$wi", workItemId);
        command.Parameters.AddWithValue("$repo", repoUrl);
        command.Parameters.AddWithValue("$branch", branch);
        command.Parameters.AddWithValue("$dir", workingDir);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void UpdateSessionStatus(string sessionId, string status)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE AgentSessions SET Status = $status WHERE Id = $id";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$id", sessionId);
        command.ExecuteNonQuery();
    }

    public void AppendMessage(string sessionId, string role, string text)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AgentMessages (SessionId, Role, Text, At) VALUES ($session, $role, $text, $at)
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$role", role);
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<AgentMessageRecord> GetMessages(string sessionId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, SessionId, Role, Text, At FROM AgentMessages
            WHERE SessionId = $session ORDER BY Id
            """;
        command.Parameters.AddWithValue("$session", sessionId);

        var messages = new List<AgentMessageRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var at = DateTimeOffset.TryParse(reader.GetString(4), out var parsed) ? parsed : DateTimeOffset.MinValue;
            messages.Add(new AgentMessageRecord(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), at));
        }

        return messages;
    }
}
