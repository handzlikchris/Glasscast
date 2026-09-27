using System.Net.WebSockets;
using GlassesRemote.Server.Alerts;
using GlassesRemote.Server.Desktop;
using GlassesRemote.Server.Hosting;
using GlassesRemote.Server.Media;
using GlassesRemote.Server.Pairing;
using GlassesRemote.Server.Protocol;
using Microsoft.Extensions.Options;

namespace GlassesRemote.Server.Sessions;

/// <summary>Everything a control session needs, resolved once from DI.</summary>
public sealed record SessionServices(
    IOptions<ControlSessionOptions> Options,
    IScreen Screen,
    RegionStore RegionStore,
    IInputInjector Input,
    IKeepAwake KeepAwake,
    IMediaPeerFactory Peers,
    IFrameEncoderFactory Encoders,
    FramePump Pump,
    CastArea CastArea,
    IWindowSwitcher Windows,
    IOptions<AppShortcutOptions> Apps,
    AlertLog Alerts,
    StatsLog Stats,
    TimeProvider Time,
    ILogger<ControlSession> Logger);

/// <summary>
/// One authenticated session: WebRTC signalling, video, and input, until the
/// glasses disconnect, the PC terminates it, or a limit is hit.
/// </summary>
public sealed class ControlSession
{
    private readonly SocketIO _io;
    private readonly SessionLease _lease;
    private readonly SessionServices _s;
    private readonly ControlSessionOptions _options;
    private long _lastInputTimestamp;

    public ControlSession(SocketIO io, SessionLease lease, SessionServices services)
    {
        _io = io;
        _lease = lease;
        _s = services;
        _options = services.Options.Value;
    }

    public async Task RunAsync(CancellationToken requestAborted)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, _lease.Ended);
        cts.CancelAfter(_options.MaxDuration);
        var ct = cts.Token;

        using var awake = _s.KeepAwake.Acquire();
        var controller = new InputController(_s.Input, _s.RegionStore, _s.Screen.PrimarySize, _s.RegionStore.Load());
        _s.CastArea.Set(controller.Region);
        var apps = _s.Apps.Value.Usable;
        using var encoder = _s.Encoders.Create();
        using var peer = _s.Peers.Create(encoder.Codec);
        peer.Closed += () => SafeCancel(cts);

        var closeStatus = WebSocketCloseStatus.NormalClosure;
        var closeReason = "session ended";
        try
        {
            await _io.SendAsync(new
            {
                type = "hello",
                monitor = new { width = controller.Monitor.Width, height = controller.Monitor.Height },
                region = controller.Region,
                mode = ControlProtocol.ModeName(controller.Mode),
                apps = apps.Select(a => a.Name.Trim()).ToArray(),
                codec = encoder.Codec,
            }, ct);

            await _io.SendAsync(new { type = "rtcOffer", sdp = await peer.CreateOfferAsync() }, ct);

            _s.Stats.Write(_lease.Id, "event", new Dictionary<string, object?>
            {
                ["event"] = "start",
                ["codec"] = encoder.Codec,
                ["mode"] = ControlProtocol.ModeName(controller.Mode),
                ["region"] = $"{controller.Region.Width}x{controller.Region.Height}",
                ["resumed"] = _lease.Resumed,
            });

            _lastInputTimestamp = _s.Time.GetTimestamp();
            var receiving = ReceiveLoopAsync(controller, peer, apps, ct);
            var streaming = _s.Pump.RunAsync(peer, encoder, () => controller.CurrentSource, stats => SendStats(stats, ct), ct);
            var watching = IdleWatchAsync(ct);

            var finished = await Task.WhenAny(receiving, streaming, watching);
            if (finished == receiving && await ResultOrNull(receiving) is { } violation)
            {
                closeStatus = WebSocketCloseStatus.PolicyViolation;
                closeReason = violation;
                // Rule-breaking glasses don't get to come back without an approval.
                _lease.ForgetDevice(violation);
            }
            else if (finished == watching && !ct.IsCancellationRequested)
            {
                closeReason = "idle";
            }

            SafeCancel(cts);
            await Task.WhenAll(Swallow(receiving), Swallow(streaming), Swallow(watching));
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // Disconnect, terminate or limit: fall through to close.
        }
        finally
        {
            if (_lease.Ended.IsCancellationRequested)
            {
                // Replaced: the same glasses reconnected. Terminated: ended on the PC.
                closeReason = _lease.Superseded ? "replaced" : "terminated";
            }
            _s.CastArea.Set(null);
            _s.Stats.Write(_lease.Id, "event", new Dictionary<string, object?> { ["event"] = "end", ["reason"] = closeReason });
            await _io.CloseQuietlyAsync(closeStatus, closeReason);
            _s.Logger.LogInformation("Session {Session} closed: {Reason}", _lease.Id, closeReason);
        }
    }

    /// <summary>Returns null when the client goes away, or a reason when it breaks the rules.</summary>
    private async Task<string?> ReceiveLoopAsync(InputController controller, IMediaPeer peer,
        IReadOnlyList<AppShortcut> apps, CancellationToken ct)
    {
        var rate = Math.Max(1, _options.MaxMessagesPerSecond);
        var bucket = new TokenBucket(_s.Time, rate, rate * 2);

        while (!ct.IsCancellationRequested)
        {
            ReadOnlyMemory<byte>? raw;
            try
            {
                raw = await _io.ReceiveAsync(ct);
            }
            catch (InvalidClientMessageException ex)
            {
                _s.Alerts.Raise(AlertKind.ProtocolViolation, _lease.RemoteAddress, ex.Message);
                return "invalid message";
            }

            if (raw is null)
            {
                return null;
            }

            if (!bucket.TryTake())
            {
                _s.Alerts.Raise(AlertKind.MessageRateLimited, _lease.RemoteAddress, "Session exceeded the message rate");
                return "rate limit";
            }

            if (!ControlProtocol.TryParse(raw.Value.Span, out var message, out var error))
            {
                _s.Alerts.Raise(AlertKind.ProtocolViolation, _lease.RemoteAddress, $"Rejected message ({error})");
                return "invalid message";
            }

            switch (message)
            {
                case AuthenticateMessage:
                    _s.Alerts.Raise(AlertKind.ProtocolViolation, _lease.RemoteAddress, "Repeated authenticate");
                    return "invalid message";

                case RtcAnswerMessage answer:
                    if (!peer.ApplyAnswer(answer.Sdp))
                    {
                        return "bad answer";
                    }
                    break;

                case IceCandidateMessage candidate:
                    peer.AddRemoteCandidate(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);
                    break;

                case SwitchAppMessage switchApp:
                    Interlocked.Exchange(ref _lastInputTimestamp, _s.Time.GetTimestamp());
                    var result = switchApp.Slot <= apps.Count
                        ? _s.Windows.Switch(apps[switchApp.Slot - 1], controller.Region)
                        : AppSwitchResult.Failed;
                    _s.Logger.LogInformation("Session {Session} switched to app {Slot}: {Result}", _lease.Id, switchApp.Slot, result);
                    _s.Stats.Write(_lease.Id, "event", new Dictionary<string, object?>
                    {
                        ["event"] = "switchApp",
                        ["slot"] = switchApp.Slot,
                        ["result"] = ResultName(result),
                    });
                    await _io.SendAsync(new { type = "appSwitch", slot = switchApp.Slot, result = ResultName(result) }, ct);
                    break;

                case ClientStatsMessage stats:
                    // Measurements, not input: they don't keep an idle session alive.
                    _s.Stats.Write(_lease.Id, "glasses", stats.Values.Select(v => new KeyValuePair<string, object?>(v.Key, v.Value)));
                    break;

                case PingMessage ping:
                    await _io.SendAsync(new { type = "pong", t = ping.T, serverTime = _s.Time.GetUtcNow().ToUnixTimeMilliseconds() }, ct);
                    break;

                default:
                    Interlocked.Exchange(ref _lastInputTimestamp, _s.Time.GetTimestamp());
                    if (message is SetModeMessage setMode)
                    {
                        _s.Stats.Write(_lease.Id, "event", new Dictionary<string, object?>
                        {
                            ["event"] = "setMode",
                            ["mode"] = ControlProtocol.ModeName(setMode.Mode),
                        });
                    }
                    if (controller.Handle(message!) == HandleResult.RegionChanged)
                    {
                        _s.CastArea.Set(controller.Region);
                        await _io.SendAsync(new { type = "region", region = controller.Region }, ct);
                    }
                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// Media stats for the glasses' stats panel. Fire and forget: the frame pump must never wait
    /// on the control socket, and a stats message that fails to send is simply lost.
    /// </summary>
    private void SendStats(MediaStats stats, CancellationToken ct)
    {
        var message = new
        {
            type = "mediaStats",
            fps = Math.Round(stats.Fps, 1),
            captureMs = Math.Round(stats.CaptureMs, 1),
            captureMaxMs = Math.Round(stats.CaptureMaxMs, 1),
            encodeMs = Math.Round(stats.EncodeMs, 1),
            encodeMaxMs = Math.Round(stats.EncodeMaxMs, 1),
            sendMs = Math.Round(stats.SendMs, 1),
            sendMaxMs = Math.Round(stats.SendMaxMs, 1),
            nacked = stats.Nacked,
            resent = stats.Resent,
            kbps = Math.Round(stats.Kbps),
            keyframes = stats.Keyframes,
            keyframeRequests = stats.KeyframeRequests,
            // [RTP timestamp, capture start in Unix ms (server clock), bytes] per frame sent.
            frames = stats.Frames.Select(f => new long[] { f.Rtp, f.CapturedAtUnixMs, f.Bytes }).ToArray(),
        };
        _ = Swallow(_io.SendAsync(message, ct));

        var sizes = stats.Frames.Select(f => f.Bytes / 1024.0).DefaultIfEmpty(0).ToArray();
        _s.Stats.Write(_lease.Id, "pc", new Dictionary<string, object?>
        {
            ["fps"] = message.fps,
            ["captureMs"] = message.captureMs,
            ["captureMaxMs"] = message.captureMaxMs,
            ["encodeMs"] = message.encodeMs,
            ["encodeMaxMs"] = message.encodeMaxMs,
            ["sendMs"] = message.sendMs,
            ["sendMaxMs"] = message.sendMaxMs,
            ["nacked"] = message.nacked,
            ["resent"] = message.resent,
            ["kbps"] = message.kbps,
            ["keyframes"] = message.keyframes,
            ["keyframeRequests"] = message.keyframeRequests,
            ["frameKb"] = Math.Round(sizes.Average(), 1),
            ["frameMaxKb"] = Math.Round(sizes.Max(), 1),
        });
    }

    private static string ResultName(AppSwitchResult result) => result switch
    {
        AppSwitchResult.Switched => "switched",
        AppSwitchResult.NotRunning => "notRunning",
        _ => "failed",
    };

    private async Task IdleWatchAsync(CancellationToken ct)
    {
        var check = TimeSpan.FromSeconds(Math.Min(30, Math.Max(1, _options.IdleTimeout.TotalSeconds / 4)));
        while (true)
        {
            await Task.Delay(check, _s.Time, ct);
            var idleFor = _s.Time.GetElapsedTime(Interlocked.Read(ref _lastInputTimestamp));
            if (idleFor >= _options.IdleTimeout)
            {
                _s.Logger.LogInformation("Session {Session} idle for {Idle}; closing", _lease.Id, idleFor);
                return;
            }
        }
    }

    private static void SafeCancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task<string?> ResultOrNull(Task<string?> task)
    {
        try
        {
            return await task;
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            return null;
        }
    }

    private static async Task Swallow(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
        }
    }
}
