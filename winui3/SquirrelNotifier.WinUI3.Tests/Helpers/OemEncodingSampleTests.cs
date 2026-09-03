// <copyright file="OemEncodingSampleTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text;
using FluentAssertions;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class OemEncodingSampleTests
{
    public OemEncodingSampleTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [Theory]
    [InlineData(932)]  // ja-JP（開発環境）
    [InlineData(437)]  // en-US（GitHub Actions windows ランナー）
    [InlineData(850)]  // 西欧 OEM
    [InlineData(1252)] // ANSI 西欧（OEM ではないが単一バイト系の代表として）
    public void PickNonUtf8Sample_ShouldFindSample_ForKnownCodePages(int codePage)
    {
        // 実行マシンのロケールに関わらず、CI ランナーのコードページでもサンプルが選べること。
        // これが崩れると OEM 依存のテストが特定ロケールでしか通らなくなる（#231 のレビュー指摘）
        Encoding encoding = Encoding.GetEncoding(codePage);

        string? sample = OemEncodingSample.PickNonUtf8Sample(encoding);

        sample.Should().NotBeNull($"コードページ {codePage} で使えるサンプルが必要");
        encoding.GetString(encoding.GetBytes(sample!)).Should().Be(sample, "OEM として往復すること");
        OemEncodingSample.IsValidUtf8(encoding.GetBytes(sample!)).Should().BeFalse(
            "UTF-8 として不正であること（フォールバック経路を通すため）");
    }

    [Fact]
    public void PickNonUtf8Sample_ShouldReturnNull_WhenNoCandidateSurvives()
    {
        // ASCII では候補がすべて '?' へ落ちるため往復せず、選べるサンプルがない
        OemEncodingSample.PickNonUtf8Sample(Encoding.ASCII).Should().BeNull();
    }

    [Theory]
    [InlineData(new byte[] { 0x41, 0x42, 0x43 }, true)]
    [InlineData(new byte[] { 0xE6, 0x97, 0xA5 }, true)] // UTF-8 の「日」
    [InlineData(new byte[] { 0x82 }, false)]            // 単独の継続バイト
    [InlineData(new byte[] { 0xFF, 0xFE }, false)]
    public void IsValidUtf8_ShouldClassifyBytes(byte[] bytes, bool expected)
    {
        OemEncodingSample.IsValidUtf8(bytes).Should().Be(expected);
    }
}
