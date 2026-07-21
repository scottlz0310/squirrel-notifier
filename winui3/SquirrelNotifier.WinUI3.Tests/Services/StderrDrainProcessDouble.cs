// <copyright file="StderrDrainProcessDouble.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

/// <summary>
/// stderr を読み切るまで終了しない子プロセスの test double（#201）。
/// 実プロセスではパイプバッファ（既定 4KB 程度）が埋まると子プロセスが書き込みでブロックし、
/// 呼び出し側が stderr を読み進めない限り WaitForExitAsync が返らない。この挙動を、
/// stderr が EOF まで読まれたときだけ WaitForExitAsync を完了させることで再現する.
/// </summary>
internal static class StderrDrainProcessDouble
{
    /// <summary>パイプバッファを超える stderr 出力量.</summary>
    public const int LargeStderrLength = 64 * 1024;

    /// <summary>
    /// stderr が読み切られるまで終了しないプロセスのモックを生成する.
    /// </summary>
    /// <param name="stdout">stdout の内容.</param>
    /// <returns>プロセスのモック.</returns>
    public static Mock<IProcessInstance> CreateBlockingOnStderr(string stdout)
    {
        var stderrDrained = new TaskCompletionSource();

        var mockProcess = new Mock<IProcessInstance>();
        mockProcess.SetupGet(p => p.ExitCode).Returns(0);
        mockProcess.SetupGet(p => p.StandardOutput)
            .Returns(new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(stdout))));
        mockProcess.SetupGet(p => p.StandardError)
            .Returns(new StreamReader(new EofSignalingStream(new string('x', LargeStderrLength), stderrDrained)));
        mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => stderrDrained.Task.WaitAsync(ct));

        return mockProcess;
    }

    private sealed class EofSignalingStream : MemoryStream
    {
        private readonly TaskCompletionSource _eofReached;

        public EofSignalingStream(string content, TaskCompletionSource eofReached)
            : base(Encoding.UTF8.GetBytes(content))
        {
            _eofReached = eofReached;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                _ = _eofReached.TrySetResult();
            }

            return read;
        }
    }
}
