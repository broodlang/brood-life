using System.Threading.Channels;
using Life;
using Raylib_cs;

// ── tuning knobs (the Brood `*globals*`) ──────────────────────────────────────
int cellPx = 6;            // board cell size in pixels (at view zoom 1)
int footerPx = 72;         // status-bar height (status line + a row of clickable buttons)
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

var sim = new Sim(toSim.Reader, toRenderer.Writer, Seed(), spawnEvery);
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
Raylib.SetTargetFPS(0);   // uncapped display — the SIM runs flat out, bounded only by the ack

// Draw a string in a FIXED-PITCH grid (cell = widest glyph + 1px), so the status line never
// reflows as the gen/fps/cells/MB digit widths change — Brood renders its footer in a
// monospace grid for exactly this reason. Spaces are skipped (they just advance the cursor).
int monoPitch = Raylib.MeasureText("M", 24) + 1;
void DrawMono(string s, int x, int y, int size, Color c)
{
    for (int i = 0; i < s.Length; i++)
        if (s[i] != ' ') Raylib.DrawText(s[i].ToString(), x + i * monoPitch, y, size, c);
}

Frame? latest = null;
bool leftDown = false, rightDown = false;
var clock = System.Diagnostics.Stopwatch.StartNew();

// VIEW (camera) zoom — like Brood: the scroll wheel zooms onto the mouse, the BOARD is
// unchanged (no refit). z=1 shows the whole board; higher magnifies a panned window.
int viewZoom = 1, viewOx = 0, viewOy = 0;
const int zoomMin = 1, zoomMax = 10, zoomStep = 1;

int[] spawnPresets = { 0, 60, 300, 1800 };   // off → frequent → … (generations, the .NET spawn unit)

// footer button rects, recomputed each frame from the window size.
Rectangle spawnBtn = default, clearBtn = default;

while (!Raylib.WindowShouldClose())
{
    if (forMs is long limit && clock.ElapsedMilliseconds >= limit) break;

    // live resize → tell the SIM to refit the board (disabled when the board is pinned)
    int w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight();
    if (!fixedBoard && (w != windowW || h != windowH))
    {
        windowW = w; windowH = h;
        toSim.Writer.TryWrite(new Resize(BoardW(), BoardH()));
        ClampView();
    }

    var mp = Raylib.GetMousePosition();

    // scroll-wheel → VIEW zoom (camera onto the mouse); the board is UNCHANGED, like Brood.
    float wheel = Raylib.GetMouseWheelMove();
    if (wheel != 0) ViewZoom(wheel > 0 ? zoomStep : -zoomStep, (int)mp.X, (int)mp.Y);

    // map the screen pixel THROUGH the view (camera offset + zoom) to a BOARD cell
    int cw = cellPx * viewZoom;
    int col = viewOx + (int)(mp.X / cw), row = viewOy + (int)(mp.Y / cw);
    bool overFooter = mp.Y >= windowH - footerPx;
    bool inBoard = !overFooter && row >= 0 && row < BoardH() && col >= 0 && col < BoardW();

    // ── clickable footer buttons (like Brood): spawn (cycles the interval), clear (empties) ──
    int fyTop = windowH - footerPx;
    spawnBtn = new Rectangle(14, fyTop + 40, 150, 26);
    clearBtn = new Rectangle(windowW - 100, fyTop + 40, 86, 26);
    if (Raylib.IsMouseButtonPressed(MouseButton.Left))
    {
        if (PointIn(spawnBtn, mp)) { spawnEvery = NextSpawn(spawnEvery); toSim.Writer.TryWrite(new SetSpawn(spawnEvery)); }
        else if (PointIn(clearBtn, mp)) { toSim.Writer.TryWrite(new Clear()); }
    }

    // board paint: press/drag/release forwarded to the SIM (left = shape, right = gun)
    HandleButton(MouseButton.Left, gun: false, ref leftDown, col, row, inBoard);
    HandleButton(MouseButton.Right, gun: true, ref rightDown, col, row, inBoard);

    // keyboard knobs
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
        // blit each live cell THROUGH the view: offset by the pan, scaled by the zoom, and
        // clipped to the board viewport above the footer (the footer is painted over the top).
        int cwR = cellPx * viewZoom;
        int boardBottom = windowH - footerPx;
        foreach (var op in frame.Ops)
        {
            int rx = (op.X - viewOx) * cwR, ry = (op.Y - viewOy) * cwR;
            if (rx < 0 || ry < 0 || rx >= windowW || ry >= boardBottom) continue;
            Raylib.DrawRectangle(rx, ry, cwR, cwR, new Color(op.R, op.G, op.B, (byte)255));
        }

        int fy = windowH - footerPx;
        Raylib.DrawRectangle(0, fy, windowW, footerPx, new Color(24, 24, 30, 255));
        DrawMono(frame.Status, 14, fy + 8, 24, new Color(220, 220, 230, 255));
        DrawButton(spawnBtn, $"spawn {(spawnEvery == 0 ? "off" : spawnEvery + "g")}", false);
        DrawButton(clearBtn, "clear", false);
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

// VIEW zoom by `dir` (+in), clamped to [zoomMin, zoomMax]; keep the board cell under the
// mouse fixed by panning, then clamp the pan so the magnified window stays in-board. Port of
// Brood's `view-zoom!` (cells are square here, so no *cell-aspect* factor).
void ViewZoom(int dir, int mx, int my)
{
    int z = viewZoom, z2 = Math.Clamp(z + dir, zoomMin, zoomMax);
    int bx = viewOx + mx / (cellPx * z), by = viewOy + my / (cellPx * z);   // board cell under mouse
    int ox = bx - mx / (cellPx * z2), oy = by - my / (cellPx * z2);          // keep it there at z2
    int bw = BoardW(), bh = BoardH();
    viewZoom = z2;
    viewOx = Math.Clamp(ox, 0, Math.Max(0, bw - bw / z2));
    viewOy = Math.Clamp(oy, 0, Math.Max(0, bh - bh / z2));
}

// Re-clamp the pan to the (possibly resized) board, keeping the zoom.
void ClampView()
{
    int bw = BoardW(), bh = BoardH();
    viewOx = Math.Clamp(viewOx, 0, Math.Max(0, bw - bw / viewZoom));
    viewOy = Math.Clamp(viewOy, 0, Math.Max(0, bh - bh / viewZoom));
}

static bool PointIn(Rectangle r, System.Numerics.Vector2 p) =>
    p.X >= r.X && p.X < r.X + r.Width && p.Y >= r.Y && p.Y < r.Y + r.Height;

// spawn button: cycle the auto-spawn interval through the presets, wrapping (off → … → off).
int NextSpawn(int every)
{
    int i = Array.IndexOf(spawnPresets, every);
    return spawnPresets[(i < 0 ? 0 : i + 1) % spawnPresets.Length];
}

// A light raised footer key with dark text — Brood's *btn-face* {:bg :white :fg :black}.
void DrawButton(Rectangle r, string label, bool active)
{
    Raylib.DrawRectangleRec(r, active ? new Color(238, 238, 238, 255) : new Color(200, 200, 206, 255));
    Raylib.DrawRectangleLinesEx(r, 1, new Color(90, 90, 100, 255));
    Raylib.DrawText(label, (int)r.X + 8, (int)r.Y + 5, 16, new Color(18, 18, 22, 255));
}

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
