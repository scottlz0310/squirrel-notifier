// <copyright file="ClaudeStreamJsonEventExtractorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Linq;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class ClaudeStreamJsonEventExtractorTests
{
    // Claude Code CLI（-p --verbose --output-format stream-json）の実イベント構造を模した fixture（#187）
    private const string _toolResultArrayContentEvent =
        """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_01","content":[{"type":"text","text":"@squirrel-progress {\"schemaVersion\":1,\"phaseIndex\":3,\"totalPhases\":8,\"phaseLabel\":\"修正\",\"message\":\"accept 2件\"}"}]}]},"session_id":"s1"}""";

    private const string _toolResultStringContentEvent =
        """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_02","content":"@squirrel-progress {\"schemaVersion\":1,\"phaseIndex\":0,\"totalPhases\":8,\"phaseLabel\":\"準備\"}"}]},"session_id":"s1"}""";

    private const string _toolResultMixedLinesEvent =
        """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_03","content":[{"type":"text","text":"通常のツール出力 1 行目\r\n@squirrel-progress {\"schemaVersion\":1,\"phaseIndex\":5,\"totalPhases\":8,\"phaseLabel\":\"返信\"}\n通常のツール出力 3 行目"}]}]},"session_id":"s1"}""";

    private const string _assistantTextEvent =
        """{"type":"assistant","message":{"id":"msg_01","role":"assistant","content":[{"type":"text","text":"レビューを開始します。\n\nまず PR の差分を確認します。"}]},"session_id":"s1"}""";

    private const string _assistantToolUseEvent =
        """{"type":"assistant","message":{"id":"msg_02","role":"assistant","content":[{"type":"tool_use","id":"toolu_01","name":"Bash","input":{"command":"echo '@squirrel-progress {\"schemaVersion\":1,\"phaseIndex\":1,\"totalPhases\":8,\"phaseLabel\":\"x\"}'"}}]},"session_id":"s1"}""";

    [Fact]
    public void TryExtract_ShouldExtractProgressMarker_FromToolResultArrayContent()
    {
        bool extracted = ClaudeStreamJsonEventExtractor.TryExtract(_toolResultArrayContentEvent, out ClaudeStreamJsonExtraction? extraction);

        extracted.Should().BeTrue();
        extraction!.ProgressEvents.Should().HaveCount(1);
        AgentProgressEvent progress = extraction.ProgressEvents[0];
        progress.PhaseIndex.Should().Be(3);
        progress.TotalPhases.Should().Be(8);
        progress.PhaseLabel.Should().Be("修正");
        progress.Message.Should().Be("accept 2件");
        extraction.LogLines.Should().BeEmpty();
    }

    [Fact]
    public void TryExtract_ShouldExtractProgressMarker_FromToolResultStringContent()
    {
        bool extracted = ClaudeStreamJsonEventExtractor.TryExtract(_toolResultStringContentEvent, out ClaudeStreamJsonExtraction? extraction);

        extracted.Should().BeTrue();
        extraction!.ProgressEvents.Should().ContainSingle(p => p.PhaseIndex == 0 && p.PhaseLabel == "準備");
    }

    [Fact]
    public void TryExtract_ShouldExtractOnlyMarkerLines_AndSuppressOtherToolOutput()
    {
        // マーカー以外のツール出力（Read のファイル全文等）はライブログへ流さない（#187 の設計判断）
        bool extracted = ClaudeStreamJsonEventExtractor.TryExtract(_toolResultMixedLinesEvent, out ClaudeStreamJsonExtraction? extraction);

        extracted.Should().BeTrue();
        extraction!.ProgressEvents.Should().ContainSingle(p => p.PhaseIndex == 5 && p.PhaseLabel == "返信");
        extraction.LogLines.Should().BeEmpty();
    }

    [Fact]
    public void TryExtract_ShouldPublishAssistantText_AsLogLines()
    {
        bool extracted = ClaudeStreamJsonEventExtractor.TryExtract(_assistantTextEvent, out ClaudeStreamJsonExtraction? extraction);

        extracted.Should().BeTrue();
        extraction!.ProgressEvents.Should().BeEmpty();
        extraction.LogLines.Should().Equal("レビューを開始します。", "まず PR の差分を確認します。");
    }

    [Fact]
    public void TryExtract_ShouldSuppressToolUseBlocks_EvenIfInputContainsMarker()
    {
        // tool_use の入力（echo コマンド文字列）内のマーカーは実行前の引数であり progress として扱わない
        bool extracted = ClaudeStreamJsonEventExtractor.TryExtract(_assistantToolUseEvent, out ClaudeStreamJsonExtraction? extraction);

        extracted.Should().BeTrue();
        extraction!.ProgressEvents.Should().BeEmpty();
        extraction.LogLines.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"type":"system","subtype":"init","cwd":"C:\\work","session_id":"s1","tools":["Bash"]}""")]
    [InlineData("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"部分"}},"session_id":"s1"}""")]
    [InlineData("""{"type":"result","subtype":"success","is_error":false,"duration_ms":1200,"result":"最終応答テキスト","session_id":"s1"}""")]
    public void TryExtract_ShouldSuppressEventsWithoutDisplayContent(string line)
    {
        // system / stream_event、および成功 result（最終応答は assistant イベントで配信済み）は抑制する
        bool extracted = ClaudeStreamJsonEventExtractor.TryExtract(line, out ClaudeStreamJsonExtraction? extraction);

        extracted.Should().BeTrue();
        extraction!.ProgressEvents.Should().BeEmpty();
        extraction.LogLines.Should().BeEmpty();
    }

    [Fact]
    public void TryExtract_ShouldPublishErrorResultText()
    {
        const string line = """{"type":"result","subtype":"error_during_execution","is_error":true,"result":"実行中にエラーが発生しました","session_id":"s1"}""";

        bool extracted = ClaudeStreamJsonEventExtractor.TryExtract(line, out ClaudeStreamJsonExtraction? extraction);

        extracted.Should().BeTrue();
        extraction!.LogLines.Should().Equal("実行中にエラーが発生しました");
    }

    [Fact]
    public void TryExtract_ShouldPublishErrorResultSubtype_WhenResultTextIsMissing()
    {
        const string line = """{"type":"result","subtype":"error_max_turns","is_error":true,"session_id":"s1"}""";

        bool extracted = ClaudeStreamJsonEventExtractor.TryExtract(line, out ClaudeStreamJsonExtraction? extraction);

        extracted.Should().BeTrue();
        extraction!.LogLines.Should().Equal("claude stream-json result: error_max_turns");
    }

    [Fact]
    public void TryExtract_ShouldIgnoreMalformedMarkerInsideToolResult()
    {
        // tool_result 内の検証に通らないマーカー行（未知 schemaVersion）は progress にも log にもならない
        const string line =
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_04","content":"@squirrel-progress {\"schemaVersion\":99,\"phaseIndex\":0,\"totalPhases\":1,\"phaseLabel\":\"x\"}"}]},"session_id":"s1"}""";

        bool extracted = ClaudeStreamJsonEventExtractor.TryExtract(line, out ClaudeStreamJsonExtraction? extraction);

        extracted.Should().BeTrue();
        extraction!.ProgressEvents.Should().BeEmpty();
        extraction.LogLines.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("通常のログ行です")]
    [InlineData("{broken json")]
    [InlineData("""[{"type":"assistant"}]""")]
    [InlineData("""{"noType":true}""")]
    [InlineData("""{"type":123}""")]
    [InlineData("""{"type":"unknown_future_event","data":"x"}""")]
    public void TryExtract_ShouldRejectNonStreamJsonLines(string? line)
    {
        // 非 JSON・malformed JSON・未知 type は抽出対象外（false）とし、
        // 呼び出し側が生の行を通常ログとして流す（#187 AC: レビュー実行を失敗させない）
        bool extracted = ClaudeStreamJsonEventExtractor.TryExtract(line, out ClaudeStreamJsonExtraction? extraction);

        extracted.Should().BeFalse();
        extraction.Should().BeNull();
    }
}
