using GitKit.Core.Services;
using Xunit;

namespace GitKit.Core.Tests;

public sealed class RecentRepositoriesTests
{
    private static (RecentRepositories recent, string file) New()
    {
        var dir = Path.Combine(Path.GetTempPath(), "git.kit-recent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return (new RecentRepositories(Path.Combine(dir, "recent.json")), dir);
    }

    [Fact]
    public void Add_puts_most_recent_first_and_persists()
    {
        var (recent, dir) = New();
        try
        {
            recent.Add("https://example.com/a.git");
            recent.Add(@"C:\repos\local");
            recent.Add("git@host:b.git");

            var all = recent.GetAll();
            Assert.Equal(new[] { "git@host:b.git", @"C:\repos\local", "https://example.com/a.git" }, all);

            // Persistiu: uma nova instância lê o mesmo histórico.
            var reopened = new RecentRepositories(Path.Combine(dir, "recent.json"));
            Assert.Equal(all, reopened.GetAll());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Add_promotes_existing_without_duplicating()
    {
        var (recent, dir) = New();
        try
        {
            recent.Add("https://example.com/a.git");
            recent.Add("https://example.com/b.git");
            // Reusar 'a' deve promovê-lo ao topo, sem duplicar (case-insensitive).
            recent.Add("HTTPS://EXAMPLE.COM/A.GIT");

            var all = recent.GetAll();
            Assert.Equal(2, all.Count);
            Assert.Equal("HTTPS://EXAMPLE.COM/A.GIT", all[0]);
            Assert.Equal("https://example.com/b.git", all[1]);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
