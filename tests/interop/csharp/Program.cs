// Interop peer (C# side). Usage: listen <port> | dial <host> <port>
using BHDisplay.Core;

var me = ShareIdentity.Ephemeral("csharp-peer");
var done = new TaskCompletionSource<int>();
int got = 0;
void Wire(ShareSession s)
{
    s.Ready += x =>
    {
        Console.WriteLine($"CODE {x.PairCode}"); Console.WriteLine($"PEER {x.Peer?.Name}");
        x.Send(new ShareMsg.Enter(4, 0.25f)); x.Send(new ShareMsg.SwitchRequest(0x12)); x.Send(new ShareMsg.Clipboard("ঢাকা ✓ csharp"));
    };
    s.Message += (x, m) =>
    {
        if (m is ShareMsg.Ping or ShareMsg.Pong) return;
        Console.WriteLine($"GOT {m}");
        if (Interlocked.Increment(ref got) == 3) _ = Task.Delay(300).ContinueWith(_ => done.TrySetResult(0));
    };
    s.Closed += (_, why) => { Console.WriteLine($"CLOSED {why}"); if (got < 3) done.TrySetResult(3); };
    s.Start();
}
var listener = new ShareListener();
if (args is ["listen", var port])
{
    listener.Session += Wire;
    listener.Error += e => { Console.WriteLine($"ERROR {e}"); done.TrySetResult(4); };
    listener.Start(me, int.Parse(port));
    Console.WriteLine("LISTENING");
}
else if (args is ["dial", var host, var p]) Wire(await ShareSession.DialAsync(host, int.Parse(p), me));
else { Console.WriteLine("usage"); return 1; }
var finished = await Task.WhenAny(done.Task, Task.Delay(15000));
if (finished != done.Task) { Console.WriteLine("TIMEOUT"); return 2; }
return await done.Task;
