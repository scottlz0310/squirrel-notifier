// <copyright file="CommandPathResolverTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class CommandPathResolverTests : IDisposable
{
    private readonly string _tempDir;

    public CommandPathResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CommandPathResolverTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private string CreateFile(string relativePath)
    {
        string fullPath = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "@echo off");
        return fullPath;
    }

    [Fact]
    public void Resolve_ShouldReturnFullPath_WhenCommandIsExistingFilePath()
    {
        string file = CreateFile("direct-tool.cmd");

        string? result = CommandPathResolver.Resolve(file, pathVariable: string.Empty);

        result.Should().Be(Path.GetFullPath(file));
    }

    [Theory]
    [InlineData(".exe")]
    [InlineData(".cmd")]
    [InlineData(".bat")]
    public void Resolve_ShouldFindCommandViaPathExt(string extension)
    {
        string file = CreateFile($"bin{Path.DirectorySeparatorChar}my-tool{extension}");

        string? result = CommandPathResolver.Resolve(
            "my-tool",
            pathVariable: Path.GetDirectoryName(file),
            pathExtVariable: ".COM;.EXE;.BAT;.CMD");

        result.Should().Be(Path.GetFullPath(file));
    }

    [Fact]
    public void Resolve_ShouldRespectPathExtPriorityOrder()
    {
        // PATHEXT の並び順（シェルの優先順）どおり .EXE が .CMD より先に選ばれる
        string exe = CreateFile($"bin{Path.DirectorySeparatorChar}my-tool.exe");
        CreateFile($"bin{Path.DirectorySeparatorChar}my-tool.cmd");

        string? result = CommandPathResolver.Resolve(
            "my-tool",
            pathVariable: Path.GetDirectoryName(exe),
            pathExtVariable: ".EXE;.CMD");

        result.Should().Be(Path.GetFullPath(exe));
    }

    [Fact]
    public void Resolve_ShouldFindExactFileName_WhenCommandAlreadyHasExtension()
    {
        string file = CreateFile($"bin{Path.DirectorySeparatorChar}my-tool.cmd");

        string? result = CommandPathResolver.Resolve(
            "my-tool.cmd",
            pathVariable: Path.GetDirectoryName(file),
            pathExtVariable: ".COM;.EXE;.BAT;.CMD");

        result.Should().Be(Path.GetFullPath(file));
    }

    [Fact]
    public void Resolve_ShouldPreferPathDirectories_OverExtraSearchDirectory()
    {
        string pathFile = CreateFile($"path-bin{Path.DirectorySeparatorChar}my-tool.cmd");
        string extraFile = CreateFile($"extra-bin{Path.DirectorySeparatorChar}my-tool.cmd");

        string? result = CommandPathResolver.Resolve(
            "my-tool",
            extraSearchDirectory: Path.GetDirectoryName(extraFile),
            pathVariable: Path.GetDirectoryName(pathFile),
            pathExtVariable: ".CMD");

        result.Should().Be(Path.GetFullPath(pathFile));
    }

    [Fact]
    public void Resolve_ShouldFallBackToExtraSearchDirectory_WhenPathMisses()
    {
        string extraFile = CreateFile($"extra-bin{Path.DirectorySeparatorChar}my-tool.cmd");

        string? result = CommandPathResolver.Resolve(
            "my-tool",
            extraSearchDirectory: Path.GetDirectoryName(extraFile),
            pathVariable: string.Empty,
            pathExtVariable: ".CMD");

        result.Should().Be(Path.GetFullPath(extraFile));
    }

    [Theory]
    [InlineData("nonexistent-tool-xyz")]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_ShouldReturnNull_WhenCommandCannotBeResolved(string command)
    {
        string? result = CommandPathResolver.Resolve(command, pathVariable: string.Empty);

        result.Should().BeNull();
    }
}
