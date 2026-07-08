using GitKit.Core.Data;
using Xunit;

namespace GitKit.Core.Tests;

public sealed class AppDatabaseTests : IDisposable
{
    private readonly string _path;
    private readonly AppDatabase _db;

    public AppDatabaseTests()
    {
        _path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"gitkit-db-{Guid.NewGuid():N}.db");
        _db = new AppDatabase(_path);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch { }
    }

    [Fact]
    public void Settings_roundtrip_and_overwrite()
    {
        Assert.Null(_db.GetSetting("devops.org"));

        _db.SetSetting("devops.org", "https://dev.azure.com/acme");
        Assert.Equal("https://dev.azure.com/acme", _db.GetSetting("devops.org"));

        _db.SetSetting("devops.org", "https://dev.azure.com/outra");
        Assert.Equal("https://dev.azure.com/outra", _db.GetSetting("devops.org"));
    }

    [Fact]
    public void Plans_roundtrip_and_overwrite()
    {
        Assert.Null(_db.GetPlan(1234));

        _db.SavePlan(1234, "Plano inicial\ncom linhas.");
        Assert.Equal("Plano inicial\ncom linhas.", _db.GetPlan(1234));

        _db.SavePlan(1234, "Plano revisado");
        Assert.Equal("Plano revisado", _db.GetPlan(1234));
    }

    [Fact]
    public void Sessions_and_messages_roundtrip_in_order()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        _db.CreateSession(sessionId, 42, "https://github.com/a/b", "us/42", @"C:\gtk\9", "Running");
        _db.UpdateSessionStatus(sessionId, "WaitingForInput");

        _db.AppendMessage(sessionId, "user", "primeiro prompt");
        _db.AppendMessage(sessionId, "agent", "resposta \"com aspas\" e\nlinhas");

        var messages = _db.GetMessages(sessionId);
        Assert.Equal(2, messages.Count);
        Assert.Equal("user", messages[0].Role);
        Assert.Equal("primeiro prompt", messages[0].Text);
        Assert.Equal("agent", messages[1].Role);
        Assert.Contains("aspas", messages[1].Text);
    }
}
