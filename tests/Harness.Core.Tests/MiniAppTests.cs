// Harness.WinUI — Licensed under the MIT License.

using Harness.Core.MiniApps;

namespace Harness.Core.Tests;

public class MiniAppTests
{
    private const string Page = """
        <html><head><title>Pomodoro &amp; focus</title>
        <script type="application/json" id="harness-app">
        { "name": "Pomodoro", "permissions": ["llm", "notify", "bogus", "llm"], "allowedHosts": ["https://cdn.example.com", "https://x.com; script-src *", "*"] }
        </script></head><body>hi</body></html>
        """;

    [Fact]
    public void Manifest_reads_name_permissions_and_only_plain_hosts()
    {
        var manifest = MiniAppManifest.Parse(Page);
        Assert.Equal("Pomodoro", manifest.Name);
        Assert.Equal(["llm", "notify"], manifest.Permissions);
        Assert.Equal(["bogus"], manifest.UnknownPermissions);
        Assert.Equal(["https://cdn.example.com"], manifest.AllowedHosts);
    }

    [Fact]
    public void Manifest_csp_opens_only_declared_hosts()
    {
        var csp = MiniAppManifest.Parse(Page).Csp();
        Assert.Contains("connect-src https://cdn.example.com", csp);
        Assert.DoesNotContain("script-src *", csp);
        Assert.Contains("default-src 'none'", csp);
    }

    [Theory]
    [InlineData("<title>Notes</title><p>x</p>", "Notes")]
    [InlineData("<script type=\"application/json\" id=\"harness-app\">{ broken</script><title>T</title>", "T")]
    [InlineData("<p>no title</p>", "App")]
    public void Manifest_falls_back_to_title_then_a_default(string html, string name)
    {
        var manifest = MiniAppManifest.Parse(html);
        Assert.Equal(name, manifest.Name);
        Assert.Empty(manifest.Permissions);
        Assert.Empty(manifest.AllowedHosts);
        Assert.Contains("connect-src 'none'", manifest.Csp());
    }

    [Fact]
    public void Permission_tiers()
    {
        Assert.Equal(MiniAppTier.Basic, MiniAppPermissions.TierOf("llm"));
        Assert.Equal(MiniAppTier.System, MiniAppPermissions.TierOf("tray"));
        Assert.Equal(MiniAppTier.Full, MiniAppPermissions.TierOf("proc"));
        Assert.Null(MiniAppPermissions.TierOf("root"));
    }

    [Fact]
    public void Hash_follows_content()
    {
        Assert.Equal(MiniAppStore.ComputeHash("a"), MiniAppStore.ComputeHash("a"));
        Assert.NotEqual(MiniAppStore.ComputeHash("a"), MiniAppStore.ComputeHash("b"));
    }

    [Fact]
    public void Install_persists_and_survives_a_new_store()
    {
        using var dir = new TempDir();
        var app = new MiniAppStore(dir.Path).Install(Page, "artifact-1");

        var reopened = new MiniAppStore(dir.Path);
        var found = Assert.Single(reopened.List());
        Assert.Equal(app.Id, found.Id);
        Assert.Equal("Pomodoro", found.Name);
        Assert.Equal("artifact-1", found.SourceArtifactId);
        Assert.Equal(Page, reopened.ReadIndex(app.Id));
    }

    [Fact]
    public void Same_artifact_id_installed_twice_makes_two_apps()
    {
        using var dir = new TempDir();
        var store = new MiniAppStore(dir.Path);
        var first = store.Install("<title>One</title>", "same");
        var second = store.Install("<title>Two</title>", "same");
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("<title>One</title>", store.ReadIndex(first.Id));
        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void Update_adds_a_version_and_keeps_the_old_one()
    {
        using var dir = new TempDir();
        var store = new MiniAppStore(dir.Path);
        var v1 = store.Install("<title>A</title>", null);

        Assert.Equal(v1.VersionHash, store.Update(v1.Id, "<title>A</title>")!.VersionHash);

        var v2 = store.Update(v1.Id, "<title>A</title><p>new</p>")!;
        Assert.NotEqual(v1.VersionHash, v2.VersionHash);
        Assert.Equal(v1.InstalledAt, v2.InstalledAt);
        Assert.Equal("<title>A</title><p>new</p>", store.ReadIndex(v1.Id));
        Assert.Equal("<title>A</title>", store.ReadVersion(v1.Id, v1.VersionHash));
        Assert.Null(store.Update(new string('0', 32), "x"));
    }

    [Fact]
    public void Ids_that_are_not_ours_are_refused()
    {
        using var dir = new TempDir();
        var store = new MiniAppStore(dir.Path);
        var app = store.Install("<title>A</title>", null);
        Assert.Null(store.Get(@"..\..\x"));
        Assert.Null(store.ReadVersion(app.Id, @"..\..\app"));
        Assert.False(store.Remove(".."));
    }

    [Fact]
    public void Remove_deletes_the_app()
    {
        using var dir = new TempDir();
        var store = new MiniAppStore(dir.Path);
        var app = store.Install("<title>A</title>", null);
        Assert.True(store.Remove(app.Id));
        Assert.Empty(store.List());
        Assert.Null(store.ReadIndex(app.Id));
    }
}
