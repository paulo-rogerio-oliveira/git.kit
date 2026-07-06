using GitKit.Core.Models;
using Xunit;

namespace GitKit.Core.Tests;

public sealed class GitHubRepoTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo.git", "github.com", "owner", "repo")]
    [InlineData("https://github.com/owner/repo", "github.com", "owner", "repo")]
    [InlineData("http://github.com/owner/repo/", "github.com", "owner", "repo")]
    [InlineData("git@github.com:owner/repo.git", "github.com", "owner", "repo")]
    [InlineData("git@github.com:owner/repo", "github.com", "owner", "repo")]
    [InlineData("ssh://git@github.com/owner/repo.git", "github.com", "owner", "repo")]
    [InlineData("https://github.example.com/team/sub/repo.git", "github.example.com", "sub", "repo")]
    public void TryParse_extracts_owner_and_repo(string url, string host, string owner, string name)
    {
        Assert.True(GitHubRepo.TryParse(url, out var repo));
        Assert.Equal(host, repo!.Host);
        Assert.Equal(owner, repo.Owner);
        Assert.Equal(name, repo.Name);
        Assert.Equal($"{owner}/{name}", repo.Slug);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("https://github.com/owner")]
    public void TryParse_rejects_invalid_input(string? url)
    {
        Assert.False(GitHubRepo.TryParse(url, out var repo));
        Assert.Null(repo);
    }
}
