using System.Diagnostics;
using System.Numerics;
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

    // ── spawn-colour layer — a faithful port of life.blsp's `recolor`/`color-spawn`/
    // `birth-blend`. Keyed by bit index (x + y*W): each SPAWN (click/drag/auto-inject)
    // takes one fresh hue; a SURVIVOR keeps its colour; a NEWBORN is the channel-average
    // of its coloured torus neighbours; the DEAD are dropped. Cells with no entry (the
    // initial seed) render WHITE — exactly Brood's uncoloured `:white` (0xe5).
    private readonly Dictionary<int, (byte r, byte g, byte b)> _colors = new();
    private int _nextId;  // next spawn's colour id; hue = id * *spawn-hue-step* (137°) on the wheel
    private const int SpawnHueStep = 137;   // *spawn-hue-step* — ≈ the golden angle
    private const double SpawnSat = 0.85;   // *spawn-sat*
    private const double SpawnVal = 0.95;   // *spawn-val*
    private static readonly (byte r, byte g, byte b) White = (0xe5, 0xe5, 0xe5);  // :white

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
        long lastFrameMs = 0;     // wall time of the previous emitted frame — for the instantaneous fps
        bool awaitingAck = false;

        // Push an initial (empty) frame so the window has something to show at once.
        await Emit(0);

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
                    case Resize(var rw, var rh): { int ow = _board.W; _board = _board.Refit(rw, rh); ColorsRefit(ow, rw, rh); break; }
                    case FpsDelta(var d): _targetFps = Math.Clamp(_targetFps + d, 0, 240); break;
                    case SpawnDelta(var d): _spawnEvery = Math.Max(0, _spawnEvery + d * 30); break;
                    case Press(var gun, var c, var r): Drop(gun, c, r); break;
                    case Drag(var gun, var c, var r): Drop(gun, c, r); break;  // freehand
                    case Release: break;
                }
            }

            // ── the frame's compute: step, recolour, auto-spawn, build-ops ──
            var preStep = _board.Bits;
            _board = _board.Step();
            _gen++;
            Recolor(preStep, _board.Bits);   // carry the colour layer across the step

            if (_spawnEvery > 0 && _gen % _spawnEvery == 0)
                AutoSpawn();                  // a fresh spawn colour on the added cells

            // Don't get ahead of the renderer: wait for the previous frame's ack.
            if (awaitingAck)
            {
                var ack = await ReadUntilAck(ct);
                if (ack) return; // Quit arrived while waiting
            }

            long nowMs = sw.ElapsedMilliseconds;
            long perMs = nowMs - lastFrameMs;   // ms since the previous frame — Brood's `per`
            lastFrameMs = nowMs;
            await Emit(perMs);
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
            case Resize(var rw, var rh): { int ow = _board.W; _board = _board.Refit(rw, rh); ColorsRefit(ow, rw, rh); break; }
            case FpsDelta(var d): _targetFps = Math.Clamp(_targetFps + d, 0, 240); break;
            case SpawnDelta(var d): _spawnEvery = Math.Max(0, _spawnEvery + d * 30); break;
            case Press(var gun, var c, var r): Drop(gun, c, r); break;
            case Drag(var gun, var c, var r): Drop(gun, c, r); break;
            case Release: break;
        }
    }

    private void Drop(bool gun, int col, int row)
    {
        var old = _board.Bits;
        if (gun)
            _board = _board.Place(Shapes.GosperGun, col, row);
        else
            _board = _board.Place(Shapes.Random((int)(_gen + col * 7 + row * 13)), col, row);
        ColorSpawn(old, _board.Bits);
    }

    private void AutoSpawn()
    {
        int seed = (int)(_gen / Math.Max(1, _spawnEvery));
        int col = Mod(seed * 53, _board.W);
        int row = Mod(seed * 97, _board.H);
        var old = _board.Bits;
        _board = _board.Place(Shapes.Random(seed), col, row);
        ColorSpawn(old, _board.Bits);
    }

    // Give every cell a placement turned ON (in `newBits`, not `oldBits`) the next spawn's
    // fresh colour, so each spawn is its own hue. A placement only OR-s bits in, so the added
    // cells are exactly new XOR old. (life.blsp `color-spawn`.)
    private void ColorSpawn(BigInteger oldBits, BigInteger newBits)
    {
        var added = newBits ^ oldBits;
        if (added.IsZero) return;
        var rgb = SpawnRgb(_nextId++);
        foreach (int i in Positions(added)) _colors[i] = rgb;
    }

    // Carry the colour layer across ONE generation (life.blsp `recolor`): a SURVIVOR (live
    // both gens) keeps its colour; a DEAD cell (old & ~new) is dropped; a NEWBORN (new & ~old)
    // takes the blend of its parents. Births read the PRE-STEP colours — the dead were live
    // last gen, so they're valid parents — and don't see each other, so blend ALL births
    // first, THEN drop the dead, THEN write the births.
    private void Recolor(BigInteger oldBits, BigInteger newBits)
    {
        var surv = oldBits & newBits;
        var bornBits = newBits ^ surv;   // new & ~old
        var deadBits = oldBits ^ surv;   // old & ~new
        if (bornBits.IsZero && deadBits.IsZero) return;

        var births = new List<(int i, (byte, byte, byte) rgb)>();
        foreach (int i in Positions(bornBits))
            if (BirthBlend(i) is { } rgb) births.Add((i, rgb));
        foreach (int i in Positions(deadBits)) _colors.Remove(i);
        foreach (var (i, rgb) in births) _colors[i] = rgb;
    }

    // The colour a NEWBORN at index `i` takes: the channel-average (integer division, like
    // Brood's `quot`) of its 8 torus neighbours that carry a colour — its live parents. null
    // if none is coloured, so a colourless cell stays absent. (life.blsp `birth-blend`.)
    private (byte, byte, byte)? BirthBlend(int i)
    {
        int w = _board.W, h = _board.H;
        int x = i % w, y = i / w;
        int rn = Mod(y - 1, h) * w, rc = y * w, rs = Mod(y + 1, h) * w;
        int cl = Mod(x - 1, w), cr = Mod(x + 1, w);
        int sr = 0, sg = 0, sb = 0, n = 0;
        void Add(int j) { if (_colors.TryGetValue(j, out var c)) { sr += c.r; sg += c.g; sb += c.b; n++; } }
        Add(rn + cl); Add(rn + x); Add(rn + cr);
        Add(rc + cl); /*    self */ Add(rc + cr);
        Add(rs + cl); Add(rs + x); Add(rs + cr);
        if (n == 0) return null;
        return ((byte)(sr / n), (byte)(sg / n), (byte)(sb / n));
    }

    // Rekey the colour layer when the board is refit to a new width/height, dropping cells
    // that fall outside — the colour twin of BitBoard.Refit. (life.blsp `colors-refit`.)
    private void ColorsRefit(int oldW, int newW, int newH)
    {
        if (_colors.Count == 0) return;
        var rekeyed = new Dictionary<int, (byte, byte, byte)>(_colors.Count);
        foreach (var (i, rgb) in _colors)
        {
            int x = i % oldW, y = i / oldW;
            if (x < newW && y < newH) rekeyed[x + y * newW] = rgb;
        }
        _colors.Clear();
        foreach (var (k, v) in rekeyed) _colors[k] = v;
    }

    // The vivid [r g b] for spawn `id`: hue walks the wheel by SpawnHueStep° per spawn, so
    // consecutive spawns land far apart — a big range of distinct colours. (life.blsp `spawn-rgb`.)
    private static (byte r, byte g, byte b) SpawnRgb(int id) =>
        HsvRound(Mod(id * SpawnHueStep, 360) / 360.0, SpawnSat, SpawnVal);

    // The set-bit INDICES of a bitfield, by a single byte scan (cf. BitBoard.Cells). The board
    // fields are non-negative, so ToByteArray gives a clean little-endian magnitude.
    private static IEnumerable<int> Positions(BigInteger bits)
    {
        byte[] bytes = bits.ToByteArray();
        for (int bi = 0; bi < bytes.Length; bi++)
        {
            int by = bytes[bi];
            if (by == 0) continue;
            int baseBit = bi * 8;
            while (by != 0)
            {
                yield return baseBit + System.Numerics.BitOperations.TrailingZeroCount(by);
                by &= by - 1;
            }
        }
    }

    // Build render ops: one op per live cell, each cell its OWN spawn-blend colour from the
    // colour layer (uncoloured cells render white). The recolour itself happened in `Recolor`.
    private async Task Emit(long perMs)
    {
        var ops = new List<RenderOp>(_board.LiveCount());
        int w = _board.W;
        foreach (var (x, y) in _board.Cells())
        {
            var (r, g, b) = _colors.TryGetValue(x + y * w, out var c) ? c : White;
            ops.Add(new RenderOp(x, y, r, g, b));
        }

        // EXACTLY Brood's `status` info line: "gen N · F fps · C cells · M MB".
        // F = instantaneous fps from the last frame's delta (Brood `(quot 1000 (max 1 ms))`);
        // M = bytes currently allocated / 1 MB — Brood's `mem-bytes` is its allocator's live
        // bytes, whose managed-runtime analogue is GC.GetTotalMemory(false).
        long fps = 1000 / Math.Max(1, perMs);
        long mb = GC.GetTotalMemory(false) / 1048576;
        string status = $"gen {_gen} · {fps} fps · {ops.Count} cells · {mb} MB";

        await _frames.WriteAsync(new Frame(ops.ToArray(), _board.W, _board.H, status));
    }

    private static int Mod(int a, int n) => ((a % n) + n) % n;

    // HSV (h 0–1, s/v 0–1) → [r g b] bytes. A faithful port of life.blsp `hsv->rgb`: the same
    // sextant conversion, and Brood's `round` (half AWAY from zero), NOT a truncating cast — so
    // a spawn's colour is bit-identical on both engines.
    private static (byte, byte, byte) HsvRound(double h, double s, double v)
    {
        double hp = h * 6;
        int i = (int)Math.Floor(hp);
        double f = hp - i;
        double p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        (double r, double g, double b) = (i % 6) switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
        static byte B(double c) => (byte)Math.Round(c * 255, MidpointRounding.AwayFromZero);
        return (B(r), B(g), B(b));
    }
}
