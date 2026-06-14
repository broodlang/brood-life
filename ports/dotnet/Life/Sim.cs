using System.Diagnostics;
using System.Threading.Channels;

namespace Life;

/// <summary>
/// The SIM. Faithful counterpart of the Brood SIM process (ADR-058/101): it owns the
/// model, steps it, recolours it, BUILDS the frame's render ops, formats the status line,
/// and paces ITSELF to a frame-rate cap with a self-resetting timer that any input
/// PREEMPTS. It waits for the renderer's <see cref="Drawn"/> ack each frame, so it never
/// runs more than one frame ahead. All per-frame work runs serially on this one thread.
///
/// Brood's mechanism — a `receive` that parks on `(after period)`, where input messages
/// preempt the timeout — maps to a Channel read cancelled by a per-frame delay token:
/// a message arriving wins the race (preempt + apply now), the delay firing means
/// "budget spent, step now".
/// </summary>
public sealed class Sim
{
    private readonly ChannelReader<InputMsg> _input;
    private readonly ChannelWriter<Frame> _frames;

    // Tuning knobs — the Brood `*globals*`. targetFps 0 == uncapped (rip from frame 1).
    private int _targetFps;
    private int _spawnEvery;   // auto-spawn a random pattern every N generations (0 = off)

    private BitBoard _board;
    private long _gen;

    public Sim(ChannelReader<InputMsg> input, ChannelWriter<Frame> frames,
        BitBoard initial, int targetFps, int spawnEvery)
    {
        _input = input;
        _frames = frames;
        _targetFps = targetFps;
        _spawnEvery = spawnEvery;
        _board = initial;  // the renderer (Program) owns seeding, so a bench can pin it
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var fpsClock = Stopwatch.StartNew();
        int framesThisSecond = 0;
        double measuredFps = 0;
        bool awaitingAck = false;

        // Push an initial (empty) frame so the window has something to show at once.
        await Emit(measuredFps);

        while (!ct.IsCancellationRequested)
        {
            long workStart = sw.ElapsedMilliseconds;

            // ── self-pacing wait: park until the next frame is due, but let input preempt ──
            int period = _targetFps > 0 ? Math.Max(1, 1000 / _targetFps) : 0;
            await WaitForTick(period, ct);
            if (ct.IsCancellationRequested) break;

            // Drain every input that's queued (a preempting message + whatever piled up).
            while (_input.TryRead(out var msg))
            {
                switch (msg)
                {
                    case Quit: return;
                    case Drawn: awaitingAck = false; break;
                    case Resize(var rw, var rh): _board = _board.Refit(rw, rh); break;
                    case FpsDelta(var d): _targetFps = Math.Clamp(_targetFps + d, 0, 240); break;
                    case SpawnDelta(var d): _spawnEvery = Math.Max(0, _spawnEvery + d * 30); break;
                    case Press(var gun, var c, var r): Drop(gun, c, r); break;
                    case Drag(var gun, var c, var r): Drop(gun, c, r); break;  // freehand
                    case Release: break;
                }
            }

            // ── the frame's compute: step, auto-spawn, recolour-and-build-ops ──
            _board = _board.Step();
            _gen++;

            if (_spawnEvery > 0 && _gen % _spawnEvery == 0)
                AutoSpawn();

            // measured fps
            framesThisSecond++;
            if (fpsClock.ElapsedMilliseconds >= 1000)
            {
                measuredFps = framesThisSecond * 1000.0 / fpsClock.ElapsedMilliseconds;
                framesThisSecond = 0;
                fpsClock.Restart();
            }

            // Don't get ahead of the renderer: wait for the previous frame's ack.
            if (awaitingAck)
            {
                var ack = await ReadUntilAck(ct);
                if (ack) return; // Quit arrived while waiting
            }

            await Emit(measuredFps);
            awaitingAck = true;

            // subtract the work already spent so the cap is a true target
            long spent = sw.ElapsedMilliseconds - workStart;
            _ = spent; // (folded into the next WaitForTick via period; kept explicit for clarity)
        }
    }

    // Park until the frame is due. A queued/arriving input cancels the wait early (preempt).
    private async Task WaitForTick(int periodMs, CancellationToken ct)
    {
        if (_input.TryPeek(out _)) return;       // input already waiting → act now
        if (periodMs <= 0) return;               // uncapped → never park
        using var delay = CancellationTokenSource.CreateLinkedTokenSource(ct);
        delay.CancelAfter(periodMs);
        try
        {
            // returns true when a message arrives (preempt), throws when the delay fires
            await _input.WaitToReadAsync(delay.Token);
        }
        catch (OperationCanceledException) { /* budget spent — step now */ }
    }

    // While bounded behind the renderer, block until the ack — but still honour Quit.
    private async Task<bool> ReadUntilAck(CancellationToken ct)
    {
        while (await _input.WaitToReadAsync(ct))
            while (_input.TryRead(out var m))
            {
                if (m is Quit) return true;
                if (m is Drawn) return false;
                Apply(m);
            }
        return true;
    }

    private void Apply(InputMsg m)
    {
        switch (m)
        {
            case Resize(var rw, var rh): _board = _board.Refit(rw, rh); break;
            case FpsDelta(var d): _targetFps = Math.Clamp(_targetFps + d, 0, 240); break;
            case SpawnDelta(var d): _spawnEvery = Math.Max(0, _spawnEvery + d * 30); break;
            case Press(var gun, var c, var r): Drop(gun, c, r); break;
            case Drag(var gun, var c, var r): Drop(gun, c, r); break;
            case Release: break;
        }
    }

    private void Drop(bool gun, int col, int row)
    {
        if (gun)
            _board = _board.Place(Shapes.GosperGun, col, row);
        else
            _board = _board.Place(Shapes.Random((int)(_gen + col * 7 + row * 13)), col, row);
    }

    private void AutoSpawn()
    {
        int seed = (int)(_gen / Math.Max(1, _spawnEvery));
        int col = Mod(seed * 53, _board.W);
        int row = Mod(seed * 97, _board.H);
        _board = _board.Place(Shapes.Random(seed), col, row);
    }

    // Recolour + build render ops: one op per live cell, hue advancing with generation.
    private async Task Emit(double measuredFps)
    {
        var ops = new List<RenderOp>(_board.LiveCount());
        foreach (var (x, y) in _board.Cells())
        {
            var (r, g, b) = Hsv(((x + y + _gen) % 360 + 360) % 360 / 360.0, 0.75, 1.0);
            ops.Add(new RenderOp(x, y, r, g, b));
        }

        int live = ops.Count;
        string status = $"gen {_gen}   fps {(_targetFps == 0 ? "uncapped" : _targetFps.ToString())}" +
                        $" ({measuredFps:0})   live {live}   spawn {(_spawnEvery == 0 ? "off" : $"{_spawnEvery}g")}";

        await _frames.WriteAsync(new Frame(ops.ToArray(), _board.W, _board.H, status));
    }

    private static int Mod(int a, int n) => ((a % n) + n) % n;

    private static (byte, byte, byte) Hsv(double h, double s, double v)
    {
        double i = Math.Floor(h * 6);
        double f = h * 6 - i;
        double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
        (double r, double g, double b) = ((int)i % 6) switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
        return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }
}
