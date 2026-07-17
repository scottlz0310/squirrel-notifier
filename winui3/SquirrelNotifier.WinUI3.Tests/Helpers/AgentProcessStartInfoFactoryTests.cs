// <copyright file="AgentProcessStartInfoFactoryTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class AgentProcessStartInfoFactoryTests
{
    [Fact]
    public void Create_ShouldUseArgumentListDirectly_ForNativeExecutable()
    {
        ProcessStartInfo psi = AgentProcessStartInfoFactory.Create(
            @"C:\tools\claude.exe",
            ["-p", "レビュー 50% & echo safe"]);

        psi.FileName.Should().Be(@"C:\tools\claude.exe");
        psi.ArgumentList.Should().Equal("-p", "レビュー 50% & echo safe");
        psi.Arguments.Should().BeEmpty();
        psi.UseShellExecute.Should().BeFalse();
        psi.CreateNoWindow.Should().BeTrue();
        psi.RedirectStandardInput.Should().BeTrue();
        psi.RedirectStandardOutput.Should().BeTrue();
        psi.RedirectStandardError.Should().BeTrue();
    }

    [Fact]
    public void Create_ShouldUseArgumentListDirectly_ForUnresolvedBareCommandName()
    {
        // 未解決のコマンド名（拡張子なし）は CreateProcessW の .exe 暗黙補完に任せ、
        // 起動失敗は Win32 エラーとして呼び出し元で処理される
        ProcessStartInfo psi = AgentProcessStartInfoFactory.Create("claude", ["--version"]);

        psi.FileName.Should().Be("claude");
        psi.ArgumentList.Should().Equal("--version");
    }

    [Theory]
    [InlineData(@"C:\fake & tools\codex.cmd")]
    [InlineData(@"C:\fake %TEMP% tools\codex.bat")]
    [InlineData(@"C:\tools\CODEX.CMD")]
    public void Create_ShouldWrapWithCmdExe_ForShellScriptShim(string resolvedPath)
    {
        ProcessStartInfo psi = AgentProcessStartInfoFactory.Create(
            resolvedPath,
            ["exec", "thread-owl MCP で owner/repo#52 を 50% & echo レビューしてください"]);

        psi.FileName.Should().EndWith("cmd.exe", "cmd.exe（ComSpec）へ委譲すること");
        psi.Arguments.Should().Be(
            "/d /s /v:off /c \"\"%SQUIRREL_NOTIFIER_LAUNCHER_COMMAND%\" \"%SQUIRREL_NOTIFIER_LAUNCHER_ARG_0%\" \"%SQUIRREL_NOTIFIER_LAUNCHER_ARG_1%\"\"");
        psi.Environment["SQUIRREL_NOTIFIER_LAUNCHER_COMMAND"].Should().Be(resolvedPath);
        psi.Environment["SQUIRREL_NOTIFIER_LAUNCHER_ARG_0"].Should().Be("exec");
        psi.Environment["SQUIRREL_NOTIFIER_LAUNCHER_ARG_1"].Should().Be(
            "thread-owl MCP で owner/repo#52 を 50% & echo レビューしてください",
            "メタ文字を含む引数も加工せず環境変数へ格納されること");
        psi.ArgumentList.Should().BeEmpty();
    }

    [Fact]
    public void Create_ShouldPassEmptyArgumentAsLiteralQuotes_ForShellScriptShim()
    {
        // cmd.exe は空の環境変数を保持できないため、空引数はリテラル "" で渡す
        ProcessStartInfo psi = AgentProcessStartInfoFactory.Create(@"C:\tools\tool.cmd", ["a", string.Empty, "b"]);

        psi.Arguments.Should().Be(
            "/d /s /v:off /c \"\"%SQUIRREL_NOTIFIER_LAUNCHER_COMMAND%\" \"%SQUIRREL_NOTIFIER_LAUNCHER_ARG_0%\" \"\" \"%SQUIRREL_NOTIFIER_LAUNCHER_ARG_2%\"\"");
        psi.Environment.Should().NotContainKey("SQUIRREL_NOTIFIER_LAUNCHER_ARG_1");
        psi.Environment["SQUIRREL_NOTIFIER_LAUNCHER_ARG_2"].Should().Be("b");
    }

    [Theory]
    [InlineData(@"C:\dir\", @"C:\dir\\")]
    [InlineData(@"C:\dir\\", @"C:\dir\\\\")]
    [InlineData(@"C:\di\r", @"C:\di\r")]
    public void Create_ShouldDoubleTrailingBackslashes_ForShellScriptShim(string argument, string expectedStored)
    {
        // 展開後は引用符で囲まれるため、閉じ引用符直前のバックスラッシュ列を二重化して
        // CommandLineToArgvW が元の値どおりに復元できるようにする
        ProcessStartInfo psi = AgentProcessStartInfoFactory.Create(@"C:\tools\tool.cmd", [argument]);

        psi.Environment["SQUIRREL_NOTIFIER_LAUNCHER_ARG_0"].Should().Be(expectedStored);
    }

    [Theory]
    [InlineData("contains \" quote")]
    [InlineData("contains \r carriage-return")]
    [InlineData("contains \n line-feed")]
    public void Create_ShouldRejectUnsafeArguments_ForShellScriptShim(string argument)
    {
        // 引用符・改行は環境変数展開方式でも cmd.exe の引用状態を破壊しうるため明示エラー
        Action act = () => AgentProcessStartInfoFactory.Create(@"C:\tools\tool.cmd", ["safe", argument]);

        act.Should().Throw<ArgumentException>().WithMessage("*#1*cannot be passed safely*");
    }

    [Fact]
    public void Create_ShouldNotRejectUnsafeCharacters_ForNativeExecutable()
    {
        // ネイティブ実行形式は ArgumentList の標準引用で安全に渡せるため制限しない
        ProcessStartInfo psi = AgentProcessStartInfoFactory.Create(@"C:\tools\claude.exe", ["say \"hi\"\nplease"]);

        psi.ArgumentList.Should().Equal("say \"hi\"\nplease");
    }
}
