using Xunit;
using BrainApp.Core.Models;
using BrainApp.Core.Services;
using Microsoft.Extensions.Options;
using BrainApp.Core.Config;

namespace BrainApp.Tests;

public class ProfileRepositoryStateTests
{
    private static ProfileRepository CreateRepo()
    {
        var tempDbFolder = Path.Combine(Path.GetTempPath(), "brainapp_state_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDbFolder);
        return new ProfileRepository(Options.Create(new StorageSettings { AppDataFolder = tempDbFolder }));
    }

    [Fact]
    public void AppState_RoundTrips_LastProfileAndActiveSession()
    {
        var repo = CreateRepo();
        repo.SaveProfile(new Profile { Id = "p1", Name = "One" });

        repo.SetLastSelectedProfileId("p1");
        repo.SetActiveSessionId("p1", "sess-abc");

        Assert.Equal("p1", repo.GetLastSelectedProfileId());
        Assert.Equal("sess-abc", repo.GetActiveSessionId("p1"));
    }

    [Fact]
    public void GetLatestSessionWithMessages_SkipsEmptyNewChatSession()
    {
        var repo = CreateRepo();
        const string profileId = "prof1";
        repo.SaveProfile(new Profile { Id = profileId, Name = "Test" });

        var empty = repo.CreateSession(profileId, "New chat");
        var withMessages = repo.CreateSession(profileId, "Real chat");
        repo.SaveMessages(withMessages.Id, new[]
        {
            new ChatMessage
            {
                Id = "m1",
                SessionId = withMessages.Id,
                Role = MessageRole.User,
                Content = "hello",
                CreatedAt = DateTime.UtcNow
            }
        });

        var latest = repo.GetLatestSessionWithMessages(profileId);

        Assert.NotNull(latest);
        Assert.Equal(withMessages.Id, latest!.Id);
        Assert.NotEqual(empty.Id, latest.Id);
    }

    [Fact]
    public void DeleteEmptySessions_RemovesOrphanSessions()
    {
        var repo = CreateRepo();
        const string profileId = "prof2";
        repo.SaveProfile(new Profile { Id = profileId, Name = "Test" });

        var orphan = repo.CreateSession(profileId, "New chat");
        var kept = repo.CreateSession(profileId, "Kept");
        repo.SaveMessages(kept.Id, new[]
        {
            new ChatMessage
            {
                Id = "m1",
                SessionId = kept.Id,
                Role = MessageRole.User,
                Content = "x",
                CreatedAt = DateTime.UtcNow
            }
        });

        repo.DeleteEmptySessions(profileId);

        Assert.Null(repo.GetSession(orphan.Id));
        Assert.NotNull(repo.GetSession(kept.Id));
    }
}
