// <copyright file="ProcessOutputDecoderTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class ProcessOutputDecoderTests
{
    private static readonly Encoding _cp932 = CreateCp932();

    private static Encoding CreateCp932()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }

    // 実際の読み取り経路（Latin1 で開いた StreamReader）と同じ形へ変換する
    private static string AsLatin1Line(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    [Theory]
    [InlineData("")]
    [InlineData("plain ascii line")]
    [InlineData("日本語を含む UTF-8 の行")]
    [InlineData("絵文字 🐿️ とサロゲートペア")]
    [InlineData("{\"type\":\"assistant\",\"text\":\"レビューを開始します\"}")]
    public void Decode_ShouldRoundTripUtf8(string original)
    {
        string latin1Line = AsLatin1Line(Encoding.UTF8.GetBytes(original));

        ProcessOutputDecoder.Decode(latin1Line, _cp932).Should().Be(original);
    }

    [Theory]
    [InlineData("'this-command-does-not-exist-12345' は、内部コマンドまたは外部コマンド、")]
    [InlineData("操作可能なプログラムまたはバッチ ファイルとして認識されていません。")]
    [InlineData("指定されたパスが見つかりません。")]
    public void Decode_ShouldFallBackToOemForCp932Bytes(string original)
    {
        // cmd.exe が OEM コードページ（日本語環境では CP932）で書き出す失敗メッセージ
        string latin1Line = AsLatin1Line(_cp932.GetBytes(original));

        ProcessOutputDecoder.Decode(latin1Line, _cp932).Should().Be(original);
    }

    [Fact]
    public void Decode_ShouldPreferUtf8_WhenBytesAreValidUtf8()
    {
        // 同じ文字列でも UTF-8 で書かれていれば UTF-8 として解釈する（CP932 へ落とさない）
        const string original = "エージェントからの出力";
        string utf8Line = AsLatin1Line(Encoding.UTF8.GetBytes(original));
        string cp932Line = AsLatin1Line(_cp932.GetBytes(original));

        utf8Line.Should().NotBe(cp932Line, "前提: 2 つのエンコーディングでバイト列が異なること");
        ProcessOutputDecoder.Decode(utf8Line, _cp932).Should().Be(original);
        ProcessOutputDecoder.Decode(cp932Line, _cp932).Should().Be(original);
    }

    [Fact]
    public void Decode_ShouldNotThrow_ForArbitraryInvalidBytes()
    {
        // UTF-8 としても CP932 としても素直に解釈できないバイト列でも例外にしない
        string latin1Line = AsLatin1Line([0xFF, 0xFE, 0x80, 0x41]);

        FluentActions.Invoking(() => ProcessOutputDecoder.Decode(latin1Line, _cp932))
            .Should().NotThrow();
    }

    [Fact]
    public void Decode_ShouldUseAmbientOemEncoding_WhenNotSpecified()
    {
        // 引数なしオーバーロードは GetOEMCP() 由来のエンコーディングを使う。実行環境の
        // OEM コードページで書いた「UTF-8 としては不正」なバイト列が復元できることを確認する。
        // ASCII だと UTF-8 デコードが成功してしまい、フォールバック経路を通らない
        Encoding oem = ProcessOutputDecoder.OemEncoding;
        string? sample = OemEncodingSample.PickNonUtf8Sample(oem);

        sample.Should().NotBeNull(
            $"OEM コードページ {oem.CodePage} で往復し、かつ UTF-8 として不正になるサンプルが必要");

        ProcessOutputDecoder.Decode(AsLatin1Line(oem.GetBytes(sample!))).Should().Be(sample);
    }
}
