using System.IO;
using System.Text.Json;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class FileOperationsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fileops-" + Guid.NewGuid().ToString("N"));

    public FileOperationsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Project(string body)
    {
        var path = Path.Combine(_root, "P.csproj");
        File.WriteAllText(path, body);
        return path;
    }

    private static bool Ok(object reply) => JsonDocument.Parse(JsonSerializer.Serialize(reply)).RootElement.GetProperty("ok").GetBoolean();

    [Fact]
    public void ACreatedFileIsWrittenAndItsFolderMade()
    {
        var project = Project("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var file = Path.Combine(_root, "Sub", "New.cs");

        Assert.True(Ok(FileOperations.Create(file, "class New { }", project)));
        Assert.Equal("class New { }", File.ReadAllText(file));
    }

    [Fact]
    public void ADeletedFileIsGone()
    {
        var project = Project("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var file = Path.Combine(_root, "Old.cs");
        File.WriteAllText(file, "class Old { }");

        Assert.True(Ok(FileOperations.Delete(file, project)));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void AMoveCreatesTheNewFolderAndRemovesTheOld()
    {
        var project = Project("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var from = Path.Combine(_root, "A.cs");
        var to = Path.Combine(_root, "Other", "A.cs");
        File.WriteAllText(from, "class A { }");

        Assert.True(Ok(FileOperations.Move(from, to, project)));
        Assert.True(File.Exists(to));
        Assert.False(File.Exists(from));
    }

    [Fact]
    public void AnOldStyleProjectIsRefusedForEveryOperation()
    {
        var project = Project("<Project ToolsVersion=\"15.0\"></Project>");
        var file = Path.Combine(_root, "X.cs");
        File.WriteAllText(file, "class X { }");

        Assert.False(Ok(FileOperations.Create(Path.Combine(_root, "Y.cs"), "class Y { }", project)));
        Assert.False(Ok(FileOperations.Delete(file, project)));
        Assert.False(Ok(FileOperations.Move(file, Path.Combine(_root, "Z.cs"), project)));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void CreatingOverAnExistingFileIsRefused()
    {
        var project = Project("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var file = Path.Combine(_root, "Keep.cs");
        File.WriteAllText(file, "class Keep { }");

        Assert.False(Ok(FileOperations.Create(file, "class Other { }", project)));
        Assert.Equal("class Keep { }", File.ReadAllText(file));
    }
}
