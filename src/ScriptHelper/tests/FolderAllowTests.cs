using AiDevHelpers.Common;
using AiDevHelpers.Common.Contracts;
using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class FolderAllowTests : IDisposable
{
    private const string Glob = @"F:\**\**";

    private readonly string _base = Path.Combine(Path.GetTempPath(), "sh-folderallow-" + Guid.NewGuid());
    private readonly FakeElevation _elevation = new(true);
    private readonly FakeSigner _signer = new();
    private readonly ScriptHelperConfig _config;

    // Answers yes to the first N confirmations, then no.
    private sealed class CountingConfirmer(int yes) : IConfirmer
    {
        public List<string> Prompts { get; } = [];

        public bool Confirm(string prompt)
        {
            Prompts.Add(prompt);
            return Prompts.Count <= yes;
        }
    }

    public FolderAllowTests()
    {
        _config = new ScriptHelperConfig(Path.Combine(_base, "data"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true);
    }

    private VersionCounter Versions => new(_config.VersionsDirectory);

    private SetupCommands Commands(IConfirmer confirmer, IElevationCheck? elevation = null) => new(
        _config, elevation ?? _elevation, new FakeVolumes(), new FakeInspector(), new FakeInterpreter(), _signer, confirmer, @"C:\Users\tester",
        p => Directory.CreateDirectory(p));

    [Fact]
    public void NotElevated_WritesNothingAndAsksNothing()
    {
        var confirmer = new CountingConfirmer(2);

        var r = Commands(confirmer, new FakeElevation(false)).AllowFolder(Glob);

        Assert.False(r.Ok);
        Assert.Empty(confirmer.Prompts);
        Assert.False(Directory.Exists(_config.FolderAllowDirectory));
    }

    [Fact]
    public void DecliningTheFirstConfirmation_WritesNothing()
    {
        var confirmer = new CountingConfirmer(0);

        var r = Commands(confirmer).AllowFolder(Glob);

        Assert.False(r.Ok);
        Assert.Single(confirmer.Prompts);
        Assert.False(Directory.Exists(_config.FolderAllowDirectory));
    }

    [Fact]
    public void DecliningTheSecondConfirmation_WritesNothing()
    {
        var confirmer = new CountingConfirmer(1);

        var r = Commands(confirmer).AllowFolder(Glob);

        Assert.False(r.Ok);
        Assert.Equal(2, confirmer.Prompts.Count);
        Assert.False(Directory.Exists(_config.FolderAllowDirectory));
    }

    [Fact]
    public void BothPromptsNameTheExactGlobAndTheAdministratorRisk()
    {
        var confirmer = new CountingConfirmer(2);

        Commands(confirmer).AllowFolder(Glob);

        Assert.Equal(2, confirmer.Prompts.Count);
        Assert.All(confirmer.Prompts, p => Assert.Contains(Glob, p));
        Assert.All(confirmer.Prompts, p => Assert.Contains("administrator", p));
    }

    [Fact]
    public void BothConfirmations_GrantMatchesDeepPathButNotOtherDrive()
    {
        var r = Commands(new CountingConfirmer(2)).AllowFolder(Glob);

        Assert.True(r.Ok, r.Reason);
        Assert.True(FolderAllowStore.Allows(_config.FolderAllowDirectory, _signer, Versions, @"F:\src\tools\deep\run.ps1"));
        Assert.False(FolderAllowStore.Allows(_config.FolderAllowDirectory, _signer, Versions, @"G:\x.ps1"));
    }

    [Fact]
    public void OlderGrantFileCopiedBack_IsRefusedAsReplay()
    {
        Commands(new CountingConfirmer(2)).AllowFolder(Glob);
        var file = FolderAllowStore.PathFor(_config.FolderAllowDirectory, Glob);
        var first = File.ReadAllBytes(file);

        Commands(new CountingConfirmer(2)).AllowFolder(Glob);
        File.WriteAllBytes(file, first);

        Assert.False(FolderAllowStore.Allows(_config.FolderAllowDirectory, _signer, Versions, @"F:\src\tools\deep\run.ps1"));
    }

    [Fact]
    public void EditingTheStoredGlob_RevokesTheGrant()
    {
        Commands(new CountingConfirmer(2)).AllowFolder(Glob);
        var file = FolderAllowStore.PathFor(_config.FolderAllowDirectory, Glob);
        File.WriteAllText(file, File.ReadAllText(file).Replace("**", "*"));

        Assert.False(FolderAllowStore.Allows(_config.FolderAllowDirectory, _signer, Versions, @"F:\src\tools\deep\run.ps1"));
    }

    [Fact]
    public void GlobWithoutADrive_IsRefusedBeforeAnyPrompt()
    {
        var confirmer = new CountingConfirmer(2);

        var r = Commands(confirmer).AllowFolder("**");

        Assert.False(r.Ok);
        Assert.Empty(confirmer.Prompts);
    }
}
