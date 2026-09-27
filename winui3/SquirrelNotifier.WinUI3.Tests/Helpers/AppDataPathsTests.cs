// <copyright file="AppDataPathsTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public sealed class AppDataPathsTests
{
    private const string _localAppData = @"C:\Users\someone\AppData\Local";
    private const string _baseKey = "SquirrelNotifier-SingleInstance-TEST";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveRoot_ShouldUseLocalAppDataWhenOverrideIsUnset(string? overrideRoot)
    {
        string root = AppDataPaths.ResolveRoot(overrideRoot, _localAppData);

        root.Should().Be(@"C:\Users\someone\AppData\Local\SquirrelNotifier");
    }

    [Theory]
    [InlineData(@"D:\e2e\data", @"D:\e2e\data")]
    [InlineData(@"D:\e2e\run\..\data", @"D:\e2e\data")]
    [InlineData(@"D:/e2e/data", @"D:\e2e\data")]
    [InlineData(@"\\server\share\data", @"\\server\share\data")]
    public void ResolveRoot_ShouldUseNormalizedOverride(string overrideRoot, string expected)
    {
        string root = AppDataPaths.ResolveRoot(overrideRoot, _localAppData);

        root.Should().Be(expected);
    }

    [Theory]
    [InlineData("data")]
    [InlineData(@"..\data")]
    [InlineData(@"\data")]
    [InlineData("D:data")]
    public void ResolveRoot_ShouldRejectPathThatIsNotFullyQualified(string overrideRoot)
    {
        Action act = () => AppDataPaths.ResolveRoot(overrideRoot, _localAppData);

        act.Should().Throw<ArgumentException>()
            .WithMessage($"*{AppDataPaths.DataRootEnvironmentVariable}*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveSingleInstanceKey_ShouldKeepBaseKeyWhenOverrideIsUnset(string? overrideRoot)
    {
        string key = AppDataPaths.ResolveSingleInstanceKey(_baseKey, overrideRoot);

        key.Should().Be(_baseKey);
    }

    [Theory]
    [InlineData(@"D:\e2e\data", @"d:\E2E\Data")]
    [InlineData(@"D:\e2e\data", @"D:\e2e\data\")]
    [InlineData(@"D:\e2e\data", @"D:\e2e\run\..\data")]
    public void ResolveSingleInstanceKey_ShouldMatchForSameRoot(string first, string second)
    {
        string firstKey = AppDataPaths.ResolveSingleInstanceKey(_baseKey, first);
        string secondKey = AppDataPaths.ResolveSingleInstanceKey(_baseKey, second);

        firstKey.Should().Be(secondKey);
        firstKey.Should().MatchRegex($"^{_baseKey}-[0-9a-f]{{16}}$");
    }

    [Fact]
    public void ResolveSingleInstanceKey_ShouldDifferForDifferentRoots()
    {
        string firstKey = AppDataPaths.ResolveSingleInstanceKey(_baseKey, @"D:\e2e\data-1");
        string secondKey = AppDataPaths.ResolveSingleInstanceKey(_baseKey, @"D:\e2e\data-2");

        firstKey.Should().NotBe(secondKey);
    }

    // 環境変数はプロセス共有のため書き換えず、実行環境の値から期待値を導出して配線だけを確認する
    [Fact]
    public void Properties_ShouldResolveFromCurrentEnvironment()
    {
        string? overrideRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable);
        string expectedRoot = AppDataPaths.ResolveRoot(
            overrideRoot,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

        AppDataPaths.Root.Should().Be(expectedRoot);
        AppDataPaths.LogDirectory.Should().Be(Path.Combine(expectedRoot, "logs"));
        AppDataPaths.SingleInstanceKey(_baseKey).Should().Be(AppDataPaths.ResolveSingleInstanceKey(_baseKey, overrideRoot));
    }

    [Fact]
    public void ResolveSingleInstanceKey_ShouldRejectRelativeOverride()
    {
        Action act = () => AppDataPaths.ResolveSingleInstanceKey(_baseKey, "data");

        act.Should().Throw<ArgumentException>();
    }
}
