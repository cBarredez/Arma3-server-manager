using System.Net;
using System.Net.Sockets;
using Arma3Manager.Api.Configuration;
using Arma3Manager.Api.Contracts;

namespace Arma3Manager.Api.Application;

/// <summary>
/// BattlEye RCon client talking to the Arma 3 process over loopback UDP. The game server always
/// runs as a child process inside this same container, so RCon never crosses the Podman network.
/// </summary>
public sealed class BattlEyeRconClient(AppConfig cfg) : IAsyncDisposable
{
    static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    readonly SemaphoreSlim gate = new(1, 1);
    readonly RconResponseAssembler assembler = new();
    readonly Dictionary<byte, TaskCompletionSource<string>> pending = [];
    UdpClient? socket;
    byte sequence;
    CancellationTokenSource? lifetime;
    TaskCompletionSource<bool>? loginResult;

    public bool IsConnected { get; private set; }
    public bool IsConfigured => !string.IsNullOrWhiteSpace(cfg.RconPassword);
    public event Action<string>? ServerMessageReceived;

    public async Task<string> SendCommandAsync(string command, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!IsConnected && !await ConnectLockedAsync(ct))
                throw new InvalidOperationException("Could not authenticate with the Arma 3 RCon listener");
            return await SendLockedAsync(command, ct);
        }
        finally { gate.Release(); }
    }

    public Task<string> SendSayAsync(string message, CancellationToken ct = default) => SendCommandAsync($"say -1 {message}", ct);

    public async Task<IReadOnlyList<RconPlayer>> GetPlayersAsync(CancellationToken ct = default) =>
        RconPlayerParser.Parse(await SendCommandAsync("players", ct));

    public Task<string> KickAsync(int playerId, string? reason, CancellationToken ct = default) =>
        SendCommandAsync(string.IsNullOrWhiteSpace(reason) ? $"kick {playerId}" : $"kick {playerId} {reason}", ct);

    public Task<string> BanAsync(int playerId, int minutes, string? reason, CancellationToken ct = default) =>
        SendCommandAsync(string.IsNullOrWhiteSpace(reason) ? $"ban {playerId} {minutes}" : $"ban {playerId} {minutes} {reason}", ct);

    public async Task DisconnectAsync()
    {
        await gate.WaitAsync();
        try { Reset(); }
        finally { gate.Release(); }
    }

    async Task<bool> ConnectLockedAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.RconPassword))
            throw new InvalidOperationException("RCON is not configured; set server.rcon_password in manager.secrets.toml");

        Reset();
        socket = new UdpClient();
        socket.Connect(IPAddress.Loopback, cfg.RconPort);
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => ReceiveLoopAsync(socket, lifetime.Token));

        loginResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await socket.SendAsync(BattlEyeProtocol.BuildLogin(cfg.RconPassword), ct);
        using var timeout = new CancellationTokenSource(CommandTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try { IsConnected = await loginResult.Task.WaitAsync(linked.Token); }
        catch (OperationCanceledException) { IsConnected = false; }

        if (IsConnected) _ = Task.Run(() => KeepAliveLoopAsync(lifetime.Token));
        else Reset();
        return IsConnected;
    }

    async Task<string> SendLockedAsync(string command, CancellationToken ct)
    {
        var seq = sequence++;
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[seq] = completion;
        try
        {
            await socket!.SendAsync(BattlEyeProtocol.BuildCommand(seq, command), ct);
            using var timeout = new CancellationTokenSource(CommandTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            return await completion.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            Reset();
            throw new TimeoutException("Timed out waiting for a response from the Arma 3 RCon listener");
        }
        finally { pending.Remove(seq); }
    }

    async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(KeepAliveInterval, ct);
                await gate.WaitAsync(ct);
                try
                {
                    if (socket is null) return;
                    await socket.SendAsync(BattlEyeProtocol.BuildCommand(sequence++, ""), ct);
                }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    async Task ReceiveLoopAsync(UdpClient client, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(ct);
                var packet = BattlEyeProtocol.TryParse(result.Buffer);
                switch (packet)
                {
                    case RconLoginResult login:
                        loginResult?.TrySetResult(login.Success);
                        break;
                    case RconCommandResponse response:
                        var text = assembler.Feed(response);
                        if (text is not null && pending.TryGetValue(response.Sequence, out var completion))
                            completion.TrySetResult(text);
                        break;
                    case RconServerMessage message:
                        _ = client.SendAsync(BattlEyeProtocol.BuildMessageAck(message.Sequence), ct);
                        ServerMessageReceived?.Invoke(message.Text);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    void Reset()
    {
        IsConnected = false;
        lifetime?.Cancel();
        lifetime?.Dispose();
        lifetime = null;
        socket?.Dispose();
        socket = null;
        foreach (var completion in pending.Values) completion.TrySetCanceled();
        pending.Clear();
        loginResult = null;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
