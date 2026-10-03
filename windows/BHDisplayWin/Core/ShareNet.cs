// Keyboard & mouse sharing — TCP session (handshake + encrypted frames), listener, and UDP discovery.
// Events are raised on thread-pool threads; the app marshals them to its UI thread.
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Timer = System.Threading.Timer;

namespace BHDisplay.Core;

public enum Role { Dialer, Listener }

public sealed class ShareSession
{
    public Role Role { get; }
    public Hello? Peer { get; private set; }
    public string PairCode { get; private set; } = "";
    public string RemoteHost { get; }
    public byte[] PeerFingerprint => Peer is null ? Array.Empty<byte>() : Bytes.Sha256(Peer.IdentityKey);

    public event Action<ShareSession>? Ready;
    public event Action<ShareSession, ShareMsg>? Message;
    public event Action<ShareSession, string>? Closed;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly ShareIdentity _identity;
    private readonly ECDiffieHellman _ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private byte[] _myHello = Array.Empty<byte>();
    private byte[] _transcript = Array.Empty<byte>();
    private RecordCipher? _send, _recv;
    private bool _open;
    private int _closed;
    private long _lastReceive = Num.NowMs;
    private Timer? _timer;

    private ShareSession(TcpClient client, Role role, ShareIdentity identity)
    {
        _client = client; Role = role; _identity = identity;
        _client.NoDelay = true;
        _stream = client.GetStream();
        RemoteHost = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
    }

    public static ShareSession Accept(TcpClient c, ShareIdentity id) => new(c, Role.Listener, id);

    public static async Task<ShareSession> DialAsync(string host, int port, ShareIdentity id)
    {
        var c = new TcpClient { NoDelay = true };
        var connect = c.ConnectAsync(host, port);
        if (await Task.WhenAny(connect, Task.Delay(5000)) != connect) { c.Close(); throw new TimeoutException("connection timed out"); }
        await connect;                                         // rethrows a connection error
        return new ShareSession(c, Role.Dialer, id);
    }

    public void Start()
    {
        _timer = new Timer(_ => Tick(), null, 2000, 2000);
        _ = Task.Run(RunAsync);
    }

    public void Send(ShareMsg m) => _ = SendAsync(m);

    public async Task SendAsync(ShareMsg m)
    {
        if (!_open || _closed != 0) return;
        await _writeLock.WaitAsync();
        try
        {
            if (_send is null || _closed != 0) return;
            await WriteFrameAsync(_send.Seal(m.Encode()));     // seal under the lock: sequence order = write order
        }
        catch (Exception e) { Close($"send failed: {e.Message}"); }
        finally { _writeLock.Release(); }
    }

    public void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _timer?.Dispose();
        _cts.Cancel();
        try { _client.Close(); } catch { }
        _ephemeral.Dispose(); _send?.Dispose(); _recv?.Dispose();
        Closed?.Invoke(this, reason);
    }

    private void Tick()
    {
        var idle = Num.NowMs - Interlocked.Read(ref _lastReceive);
        if (_open) { if (idle > 6000) Close("peer stopped responding"); else Send(new ShareMsg.Ping()); }
        else if (idle > 5000) Close("handshake timed out");
    }

    private async Task WriteFrameAsync(byte[] body)
    {
        var buf = new byte[4 + body.Length];
        Bytes.PutU32(buf, 0, (uint)body.Length);
        Buffer.BlockCopy(body, 0, buf, 4, body.Length);
        await _stream.WriteAsync(buf, 0, buf.Length, _cts.Token);
    }

    private async Task ReadExactlyAsync(byte[] buf)
    {
        for (int got = 0; got < buf.Length;)
        {
            int n = await _stream.ReadAsync(buf, got, buf.Length - got, _cts.Token);
            if (n == 0) throw new EndOfStreamException();
            got += n;
        }
    }

    private async Task<byte[]> ReadFrameAsync()
    {
        var head = new byte[4];
        await ReadExactlyAsync(head);
        var n = Bytes.GetU32(head, 0);
        // Before authentication only small handshake frames are allowed (no large allocation for strangers).
        if (n < 1 || n > (_open ? Bhds.MaxFrame : 512)) throw new WireException("bad frame length");
        var body = new byte[n];
        await ReadExactlyAsync(body);
        Interlocked.Exchange(ref _lastReceive, Num.NowMs);
        return body;
    }

    private byte[] MakeHello() => new Hello(_identity.DeviceId, _identity.PublicKey,
        P256.Export(_ephemeral.ExportParameters(false)), Bytes.Random(32), _identity.Name).Encode();

    private async Task RunAsync()
    {
        try
        {
            // Listener commits to its HELLO first; the dialer sends its HELLO only after receiving that.
            byte[] peerCommit = Array.Empty<byte>();
            _myHello = MakeHello();
            if (Role == Role.Listener) await WriteFrameAsync(Handshake.Commitment(_myHello));
            else
            {
                peerCommit = await ReadFrameAsync();
                if (peerCommit.Length != 32) throw new WireException("bad commitment");
                await WriteFrameAsync(_myHello);
            }

            var peerHello = await ReadFrameAsync();
            var h = Hello.Decode(peerHello);
            if (Bytes.Equal(h.DeviceId, _identity.DeviceId)) throw new WireException("connected to itself");
            if (Role == Role.Dialer && !Bytes.FixedTimeEquals(Handshake.Commitment(peerHello), peerCommit))
                throw new WireException("peer's HELLO doesn't match its commitment");
            Peer = h;
            if (Role == Role.Listener) await WriteFrameAsync(_myHello);
            _transcript = Role == Role.Dialer ? Handshake.Transcript(_myHello, peerHello) : Handshake.Transcript(peerHello, _myHello);
            await WriteFrameAsync(Handshake.Sign(_identity, Role == Role.Dialer ? (byte)0 : (byte)1, _transcript));

            var sig = await ReadFrameAsync();
            if (!Handshake.Verify(sig, h.IdentityKey, Role == Role.Dialer ? (byte)1 : (byte)0, _transcript))
                throw new WireException("peer failed authentication");
            var keys = Handshake.DeriveKeys(_ephemeral, h.EphemeralKey, _transcript, Role == Role.Dialer);
            _send = new RecordCipher(keys.Send); _recv = new RecordCipher(keys.Receive);
            PairCode = keys.PairCode;
            _open = true;
            Ready?.Invoke(this);

            while (_closed == 0)
            {
                var msg = ShareMsg.Decode(_recv.Open(await ReadFrameAsync()));
                if (msg is ShareMsg.Ping) Send(new ShareMsg.Pong());
                Message?.Invoke(this, msg);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { Close("connection closed"); }
        catch (EndOfStreamException) { Close("peer closed"); }
        catch (IOException) { Close(_closed != 0 ? "connection closed" : "peer closed"); }
        catch (Exception e) { Close(e.Message); }
    }
}

public sealed class ShareListener
{
    private TcpListener? _l;
    public event Action<ShareSession>? Session;
    public event Action<string>? Error;

    public void Start(ShareIdentity id, int port = Bhds.TcpPort)
    {
        try
        {
            _l = new TcpListener(IPAddress.Any, port);
            _l.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _l.Start();
        }
        catch (Exception e) { Error?.Invoke($"can't listen on port {port}: {e.Message}"); return; }
        _ = Task.Run(async () =>
        {
            while (_l is not null)
            {
                try { var c = await _l.AcceptTcpClientAsync(); Session?.Invoke(ShareSession.Accept(c, id)); }
                catch (Exception) when (_l is null) { return; }
                catch (Exception) { await Task.Delay(500); }
            }
        });
    }

    public void Stop() { var l = _l; _l = null; l?.Stop(); }
}

public sealed record Beacon(byte[] DeviceId, string Host, int Port, byte[] FingerprintPrefix, string Name);

/// UDP broadcast "here I am" beacons. Informational only — sessions authenticate everything.
public sealed class ShareDiscovery
{
    private UdpClient? _udp;
    private Timer? _timer;
    private byte[] _beacon = Array.Empty<byte>();
    private readonly Dictionary<string, long> _lastReply = new();
    public event Action<Beacon>? Found;

    public void Start(ShareIdentity id, int tcpPort = Bhds.TcpPort)
    {
        var u = new UdpClient { EnableBroadcast = true };
        u.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        u.Client.Bind(new IPEndPoint(IPAddress.Any, Bhds.UdpPort));
        _udp = u;
        var w = new WireWriter();
        w.Bytes(Bhds.BeaconMagic); w.Bytes(id.DeviceId); w.U16((ushort)tcpPort); w.Bytes(id.Fingerprint, 8);
        var n = Encoding.UTF8.GetBytes(id.Name); int nl = Math.Min(n.Length, 64);
        w.U8((byte)nl); w.Bytes(n, nl);
        _beacon = w.ToArray();
        _timer = new Timer(_ => { foreach (var t in BroadcastTargets()) Send(t); }, null, 0, 2000);
        _ = Task.Run(async () =>
        {
            long windowStart = 0; int windowCount = 0;
            while (_udp is { } c)
            {
                try
                {
                    var r = await c.ReceiveAsync();
                    // A flood of (spoofed) beacons must not reach the UI thread, where the input hooks run:
                    // a real peer sends one every 2 s, so 20 a second overall is generous.
                    var now = Num.NowMs;
                    if (now - windowStart >= 1000) { windowStart = now; windowCount = 0; }
                    if (++windowCount > 20) continue;
                    if (TryParse(r.Buffer, r.RemoteEndPoint.Address.ToString(), id.DeviceId) is { } b)
                    {
                        Reply(r.RemoteEndPoint.Address);
                        Found?.Invoke(b);
                    }
                }
                catch (Exception) when (_udp is null) { return; }
                catch (Exception) { await Task.Delay(200); }
            }
        });
    }

    private void Send(IPAddress to)
    {
        try { _udp?.Send(_beacon, _beacon.Length, new IPEndPoint(to, Bhds.UdpPort)); } catch { }
    }

    /// Unicast our beacon straight back to a device we heard, so discovery works even if broadcasts
    /// only get through in one direction.
    private void Reply(IPAddress host)
    {
        var key = host.ToString(); var now = Num.NowMs;
        lock (_lastReply)
        {
            if (_lastReply.Count > 256) _lastReply.Clear();         // bounded: senders can be spoofed
            if (_lastReply.TryGetValue(key, out var t) && now - t < 5000) return;
            _lastReply[key] = now;
        }
        Send(host);
    }

    /// 255.255.255.255 often leaves through the wrong adapter on PCs with several (VPN, Hyper-V, VirtualBox),
    /// so also send to each up, non-loopback IPv4 interface's own subnet broadcast address.
    private static IEnumerable<IPAddress> BroadcastTargets()
    {
        var set = new HashSet<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                    || ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                    var ip = ua.Address.GetAddressBytes(); var mask = ua.IPv4Mask.GetAddressBytes();
                    var b = new byte[4];
                    for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                    set.Add(new IPAddress(b));
                }
            }
        }
        catch { }
        return set;
    }

    public static Beacon? TryParse(byte[] buf, string host, byte[] ownId)
    {
        try
        {
            var r = new WireReader(buf);
            if (!r.Bytes(5).SequenceEqual(Bhds.BeaconMagic)) return null;
            var id = r.Bytes(16);
            if (Bytes.Equal(id, ownId)) return null;
            var port = r.U16(); var fp = r.Bytes(8);
            int nl = r.U8(); if (nl > 64) return null;
            return new Beacon(id, host, port, fp, Bhds.CleanName(Encoding.UTF8.GetString(r.Bytes(nl))));
        }
        catch (WireException) { return null; }
    }

    public void Stop() { _timer?.Dispose(); var u = _udp; _udp = null; u?.Close(); }
}
