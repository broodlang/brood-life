using System.Threading.Channels;
using Life;
using Raylib_cs;

// ── tuning knobs (the Brood `*globals*`) ──────────────────────────────────────
int cellPx = 2;            // board cell size in pixels; scroll wheel zooms it (ADR: footer fixed)
int footerPx = 60;         // status-bar height, kept fixed as the board scales
int targetFps = EnvInt("LIFE_FPS", 0);   // 0 == uncapped (rip from frame 1); -/= retune live
int spawnEvery = 600;      // auto-spawn a random pattern every N generations; [/] retune

// `--selftest` exercises the pure bitboard core (no window) — mirrors tests/life_test.blsp.
if (args.Contains("--selftest")) return SelfTest.Run();

// `--bench` times the hot loop (step + enumerate cells) on a 250×140 board — no window.
if (args.Contains("--bench")) return SelfTest.Bench();

// `--for <2s|500ms|N>` runs bounded then exits cleanly (the CI / headless path).
long? forMs = ParseFor(args);

// ── fair-comparison knobs (shared with the Brood version via env) ─────────────
// LIFE_BOARD=WxH pins the board (else derive from the window); LIFE_BLOCK=N seeds a
// centered solid N×N block and disables auto-spawn so both engines evolve IDENTICALLY.
var (fixedW, fixedH) = ParseBoard(Environment.GetEnvironmentVariable("LIFE_BOARD"));
bool fixedBoard = fixedW > 0;
int block = EnvInt("LIFE_BLOCK", 0);
if (block > 0) spawnEvery = 0;  // deterministic: just the block, same on both sides

int windowW = fixedBoard ? fixedW * cellPx : 1100;
int windowH = fixedBoard ? fixedH * cellPx + footerPx : 760;

// board dims derived from window + cell size (the renderer owns the window/zoom),
// unless pinned for a fair comparison.
int BoardW() => fixedBoard ? fixedW : Math.Max(8, windowW / cellPx);
int BoardH() => fixedBoard ? fixedH : Math.Max(8, (windowH - footerPx) / cellPx);

// seed: a centered solid N×N block, else the live-demo gun + methuselah.
BitBoard Seed()
{
    var b = BitBoard.Make(BoardW(), BoardH());
    if (block > 0)
    {
        int cx = BoardW() / 2 - block / 2, cy = BoardH() / 2 - block / 2;
        for (int dy = 0; dy < block; dy++)
            for (int dx = 0; dx < block; dx++)
                b = b.Set(cx + dx, cy + dy);
        return b;
    }
    return b.Place(Shapes.GosperGun, 4, 4).Place(Shapes.All[5], BoardW() / 2, BoardH() / 2);
}

// ── the two "processes" and the channels between them ─────────────────────────
// RENDERER → SIM: input (incl. the per-frame Drawn ack). SIM → RENDERER: frames.
var toSim = Channel.CreateUnbounded<InputMsg>(new() { SingleReader = true });
var toRenderer = Channel.CreateUnbounded<Frame>(new() { SingleReader = true });

var sim = new Sim(toSim.Reader, toRenderer.Writer, Seed(), targetFps, spawnEvery);
using var cts = new CancellationTokenSource();
var simTask = Task.Run(() => sim.RunAsync(cts.Token));

// ── the RENDERER (root): owns the window, blits the SIM's ops ──────────────────
// Open MAXIMIZED to match the Brood window — the resize handler below then refits the
// board to fill it (in window-derived mode). Resizable so the OS can un-maximize.
Raylib.SetConfigFlags(ConfigFlags.ResizableWindow | ConfigFlags.MaximizedWindow);
Raylib.InitWindow(windowW, windowH, "Life — .NET port (SIM/RENDERER split)");
Raylib.MaximizeWindow();
// the renderer's DISPLAY rate; 0 (uncapped sim) ⇒ no limiter, so it blits as fast as it can
// and the SIM (bounded only by the per-frame ack) runs flat out — the "faster is better" test.
Raylib.SetTargetFPS(targetFps > 0 ? Math.Max(targetFps, 120) : 0);

Frame? latest = null;
bool leftDown = false, rightDown = false;
var clock = System.Diagnostics.Stopwatch.StartNew();

while (!Raylib.WindowShouldClose())
{
    if (forMs is long limit && clock.ElapsedMilliseconds >= limit) break;

    // live resize → tell the SIM to refit the board (disabled when the board is pinned)
    int w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight();
    if (!fixedBoard && (w != windowW || h != windowH))
    {
        windowW = w; windowH = h;
        toSim.Writer.TryWrite(new Resize(BoardW(), BoardH()));
    }

    // scroll-wheel zoom (resize the board cell, refit) — also off when pinned
    float wheel = Raylib.GetMouseWheelMove();
    if (!fixedBoard && wheel != 0)
    {
        cellPx = Math.Clamp(cellPx + (wheel > 0 ? 1 : -1), 1, 40);
        toSim.Writer.TryWrite(new Resize(BoardW(), BoardH()));
    }

    // mouse → press/drag/release, forwarded to the SIM (left = shape, right = gun)
    var mp = Raylib.GetMousePosition();
    int col = (int)(mp.X / cellPx), row = (int)(mp.Y / cellPx);
    bool inBoard = row >= 0 && row < BoardH() && col >= 0 && col < BoardW();

    HandleButton(MouseButton.Left, gun: false, ref leftDown, col, row, inBoard);
    HandleButton(MouseButton.Right, gun: true, ref rightDown, col, row, inBoard);

    // keyboard knobs
    if (Raylib.IsKeyPressed(KeyboardKey.Minus)) toSim.Writer.TryWrite(new FpsDelta(-5));
    if (Raylib.IsKeyPressed(KeyboardKey.Equal)) toSim.Writer.TryWrite(new FpsDelta(+5));
    if (Raylib.IsKeyPressed(KeyboardKey.LeftBracket)) toSim.Writer.TryWrite(new SpawnDelta(+1));
    if (Raylib.IsKeyPressed(KeyboardKey.RightBracket)) toSim.Writer.TryWrite(new SpawnDelta(-1));
    if (Raylib.IsKeyPressed(KeyboardKey.Q)) break;

    // drain to the latest frame the SIM has built; ack it so the SIM may build the next
    bool gotNew = false;
    while (toRenderer.Reader.TryRead(out var f)) { latest = f; gotNew = true; }
    if (gotNew) toSim.Writer.TryWrite(new Drawn());

    // ── BLIT: the renderer's whole job — clear, draw each op, draw the footer ──
    Raylib.BeginDrawing();
    Raylib.ClearBackground(new Color(12, 12, 16, 255));
    if (latest is Frame frame)
    {
        foreach (var op in frame.Ops)
            Raylib.DrawRectangle(op.X * cellPx, op.Y * cellPx, cellPx, cellPx,
                new Color(op.R, op.G, op.B, (byte)255));

        int fy = windowH - footerPx;
        Raylib.DrawRectangle(0, fy, windowW, footerPx, new Color(24, 24, 30, 255));
        Raylib.DrawText(frame.Status, 14, fy + 12, 24, new Color(220, 220, 230, 255));
        Raylib.DrawText("L-drag: shapes   R-drag: guns   scroll: zoom   -/=: fps   [ ]: spawn   q: quit",
            14, fy + 38, 14, new Color(120, 120, 140, 255));
    }
    Raylib.EndDrawing();
}

// ── clean shutdown: stop the SIM, close the window ─────────────────────────────
toSim.Writer.TryWrite(new Quit());
cts.Cancel();
try { await simTask; } catch (OperationCanceledException) { }
Raylib.CloseWindow();
if (latest is Frame last) Console.WriteLine($"[.NET] {last.Status}");
return 0;

void HandleButton(MouseButton btn, bool gun, ref bool down, int col, int row, bool inBoard)
{
    if (Raylib.IsMouseButtonPressed(btn) && inBoard)
    {
        down = true;
        toSim.Writer.TryWrite(new Press(gun, col, row));
    }
    else if (Raylib.IsMouseButtonDown(btn) && down && inBoard)
    {
        toSim.Writer.TryWrite(new Drag(gun, col, row));
    }
    else if (Raylib.IsMouseButtonReleased(btn) && down)
    {
        down = false;
        toSim.Writer.TryWrite(new Release());
    }
}

static int EnvInt(string name, int dflt) =>
    int.TryParse(Environment.GetEnvironmentVariable(name), out var n) ? n : dflt;

static (int, int) ParseBoard(string? s)
{
    if (string.IsNullOrWhiteSpace(s)) return (0, 0);
    var p = s.ToLowerInvariant().Split('x');
    if (p.Length == 2 && int.TryParse(p[0], out var w) && int.TryParse(p[1], out var h)) return (w, h);
    if (int.TryParse(s, out var n)) return (n, n);
    return (0, 0);
}

static long? ParseFor(string[] argv)
{
    for (int i = 0; i < argv.Length - 1; i++)
    {
        if (argv[i] != "--for") continue;
        string s = argv[i + 1].Trim();
        if (s.EndsWith("ms")) return long.Parse(s[..^2]);
        if (s.EndsWith("s")) return (long)(double.Parse(s[..^1]) * 1000);
        return long.Parse(s);
    }
    return null;
}
