// <copyright file="ProcessOutputDecoder.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Runtime.InteropServices;
using System.Text;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// 子プロセスの出力行を、UTF-8 と OEM コードページが混在しうるストリームから復元する（#231）.
/// </summary>
/// <remarks>
/// <para>
/// <c>.cmd</c> / <c>.bat</c> シムを <c>cmd.exe</c> 経由で起動すると、1 本のハンドルに
/// 2 種類のエンコーディングが混ざる。cmd.exe 自身のメッセージ（「内部コマンドまたは外部コマンド…」
/// 等）は OEM コードページで書き出され、その先で起動される実エージェント（Node 製 CLI 等）は
/// UTF-8 を書き出す。<see cref="ProcessStartInfo.StandardOutputEncoding"/> にどちらを指定しても
/// 一方が必ず化けるため、単一のエンコーディング指定では解決できない.
/// </para>
/// <para>
/// そこで読み取り側は <see cref="Encoding.Latin1"/> でストリームを開き、バイト値を 1 文字 1 バイトで
/// 保存したまま行に分割する（行区切りの CR / LF は ASCII であり、UTF-8 の多バイト列を分断しない）。
/// 各行はここで UTF-8 として厳密にデコードし、不正バイト列だった行のみ OEM コードページで
/// 再デコードする。妥当な UTF-8 は往復して元の文字列に戻るため、UTF-8 のみを出力する
/// ネイティブ実行形式の経路は影響を受けない.
/// </para>
/// </remarks>
internal static class ProcessOutputDecoder
{
    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Encoding _oemEncoding = ResolveOemEncoding();

    // フォールバック先として使う OEM コードページのエンコーディング。cmd.exe が出力に使うものと同じ
    internal static Encoding OemEncoding => _oemEncoding;

    /// <summary>
    /// <see cref="Encoding.Latin1"/> で読み取った 1 行を、実際のエンコーディングへ復元する.
    /// </summary>
    /// <param name="latin1Line">
    /// <see cref="Encoding.Latin1"/> で開いた <see cref="StreamReader"/> から読み取った行。
    /// 各文字はバイト値そのものを表す（U+0000〜U+00FF）.
    /// </param>
    /// <returns>復元した行.</returns>
    public static string Decode(string latin1Line) => Decode(latin1Line, _oemEncoding);

    /// <summary>
    /// OEM コードページを明示してデコードする（テスト用）.
    /// </summary>
    /// <param name="latin1Line"><see cref="Encoding.Latin1"/> で読み取った行.</param>
    /// <param name="oemEncoding">UTF-8 として不正だった場合に使うエンコーディング.</param>
    /// <returns>復元した行.</returns>
    internal static string Decode(string latin1Line, Encoding oemEncoding)
    {
        ArgumentNullException.ThrowIfNull(latin1Line);
        ArgumentNullException.ThrowIfNull(oemEncoding);

        if (latin1Line.Length == 0)
        {
            return latin1Line;
        }

        byte[] bytes = Encoding.Latin1.GetBytes(latin1Line);
        try
        {
            return _strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return oemEncoding.GetString(bytes);
        }
    }

    // cmd.exe が使うのはプロセスの CurrentCulture ではなくシステムの OEM コードページであるため、
    // GetOEMCP() を直接引く。日本語環境で英語ロケールのアプリを動かす構成でも正しい値になる.
    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    private static Encoding ResolveOemEncoding()
    {
        // .NET Core 以降は Windows コードページを組み込みで持たないため provider の登録が必要
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding((int)GetOEMCP());
    }
}
