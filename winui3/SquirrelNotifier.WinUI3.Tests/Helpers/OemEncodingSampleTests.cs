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
    [InlineData(932)]  // ja-JP（開発環境・Shift_JIS）
    [InlineData(437)]  // en-US（GitHub Actions windows ランナー・米国 OEM）
    [InlineData(850)]  // 西欧 OEM
    [InlineData(1252)] // ANSI 西欧（単一バイト系の代表）
    [InlineData(866)]  // キリル OEM（#252 レビュー指摘）
    [InlineData(737)]  // ギリシャ OEM（#252 レビュー指摘）
    [InlineData(874)]  // タイ OEM（#252 レビュー指摘）
    [InlineData(950)]  // 繁体字中国語 Big5（#252 レビュー指摘）
    [InlineData(936)]  // 簡体字中国語 GBK
    [InlineData(949)]  // 韓国語 OEM
    [InlineData(864)]  // アラビア語 OEM（動的探索フォールバック）
    public void PickNonUtf8Sample_ShouldFindSample_ForKnownCodePages(int codePage)
    {
        // 実行マシンのロケールに関わらず、CI ランナーのコードページでもサンプルが選べること。
        // これが崩れると OEM 依存のテストが特定ロケールでしか通らなくなる（#231, #252 のレビュー指摘）
        Encoding encoding = Encoding.GetEncoding(codePage);

        string? sample = OemEncodingSample.PickNonUtf8Sample(encoding);

        sample.Should().NotBeNull($"コードページ {codePage} で使えるサンプルが必要");
        encoding.GetString(encoding.GetBytes(sample!)).Should().Be(sample, "OEM として往復すること");
        OemEncodingSample.IsValidUtf8(encoding.GetBytes(sample!)).Should().BeFalse(
            "UTF-8 として不正であること（フォールバック経路を通すため）");
    }

    [Theory]
    [InlineData(932)]
    [InlineData(437)]
    [InlineData(866)]
    [InlineData(737)]
    [InlineData(874)]
    [InlineData(950)]
    public void FindDynamicNonUtf8Sample_ShouldGenerateValidSample_WithoutPredefinedCandidates(int codePage)
    {
        // 定義済み候補リストに頼らず、動的探索のみでも往復可能かつ UTF-8 不正なサンプルを生成できること
        Encoding encoding = Encoding.GetEncoding(codePage);

        string? sample = OemEncodingSample.FindDynamicNonUtf8Sample(encoding);

        sample.Should().NotBeNull($"コードページ {codePage} で動的にサンプルが生成できること");
        encoding.GetString(encoding.GetBytes(sample!)).Should().Be(sample, "OEM として往復すること");
        OemEncodingSample.IsValidUtf8(encoding.GetBytes(sample!)).Should().BeFalse(
            "UTF-8 として不正であること（フォールバック経路を通すため）");
    }

    [Theory]
    [InlineData(65001)] // Windows UTF-8 システムロケール（#252 レビュー指摘）
    [InlineData(20127)] // US-ASCII
    public void PickNonUtf8Sample_ShouldReturnNull_WhenNoNonUtf8CandidateCanExist(int codePage)
    {
        // UTF-8 では往復するバイト列がすべて UTF-8 として妥当になるため、非 UTF-8 サンプルは存在しない。
        // ASCII では非 ASCII 候補がすべて '?' へ落ちるため、往復する非 UTF-8 サンプルは存在しない。
        Encoding encoding = Encoding.GetEncoding(codePage);
        OemEncodingSample.PickNonUtf8Sample(encoding).Should().BeNull();
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
