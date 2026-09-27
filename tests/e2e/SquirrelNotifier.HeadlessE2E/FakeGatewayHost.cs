using System.Globalization;
using System.Runtime.CompilerServices;

namespace SquirrelNotifier.HeadlessE2E;

/// <summary>
/// 実デスクトップのランブック（#433）で、アプリの購読先にする fake Gateway を単体で起動する.
/// </summary>
internal static class FakeGatewayHost
{
    public const string Command = "serve-gateway";

    private static readonly TimeSpan _defaultLifetime = TimeSpan.FromMinutes(120);

    public static async Task<int> RunAsync(string[] args)
    {
        string? endpointFile = GetOption(args, "--endpoint-file");
        if (string.IsNullOrWhiteSpace(endpointFile))
        {
            await Console.Error.WriteLineAsync($"{Command}: --endpoint-file <path> を指定してください。").ConfigureAwait(false);
            return 2;
        }

        TimeSpan lifetime = _defaultLifetime;
        string? lifetimeMinutes = GetOption(args, "--lifetime-minutes");
        if (lifetimeMinutes is not null)
        {
            if (!int.TryParse(lifetimeMinutes, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) || minutes <= 0)
            {
                await Console.Error.WriteLineAsync($"{Command}: --lifetime-minutes には正の整数を指定してください。").ConfigureAwait(false);
                return 2;
            }

            lifetime = TimeSpan.FromMinutes(minutes);
        }

        // 停止し忘れても loopback の待ち受けが残り続けないよう、寿命を過ぎたら自分で終了する
        using var lifetimeCancellation = new CancellationTokenSource(lifetime);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            lifetimeCancellation.Cancel();
        };

        FakeGatewayServer server = await FakeGatewayServer.StartAsync(FakeGatewayMode.Success).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable serverScope = server.ConfigureAwait(false);
        string fullPath = Path.GetFullPath(endpointFile);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporaryPath = fullPath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, server.Endpoint.ToString()).ConfigureAwait(false);
        File.Move(temporaryPath, fullPath, overwrite: true);
        await Console.Out.WriteLineAsync($"fake-gateway-endpoint {server.Endpoint}").ConfigureAwait(false);

        try
        {
            await Task.Delay(Timeout.Infinite, lifetimeCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }

    private static string? GetOption(string[] args, string option)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], option, StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
