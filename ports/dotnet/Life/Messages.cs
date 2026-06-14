namespace Life;

// ── RENDERER → SIM ───────────────────────────────────────────────────────────
// The renderer only ever forwards input; the SIM folds it into the next generation.
public abstract record InputMsg;
public sealed record Press(bool Gun, int Col, int Row) : InputMsg;  // left=shape, right=gun
public sealed record Drag(bool Gun, int Col, int Row) : InputMsg;
public sealed record Release : InputMsg;
public sealed record Resize(int W, int H) : InputMsg;               // window resize / zoom
public sealed record FpsDelta(int Step) : InputMsg;                 // -/= : retune the cap
public sealed record SpawnDelta(int Step) : InputMsg;              // [/] : retune auto-spawn
public sealed record SetFps(int Cap) : InputMsg;                    // fps button: absolute cap
public sealed record SetSpawn(int Every) : InputMsg;               // spawn button: absolute interval
public sealed record Clear : InputMsg;                              // clear button: empty the board
public sealed record Drawn : InputMsg;                              // renderer's per-frame ack
public sealed record Quit : InputMsg;

// ── SIM → RENDERER ───────────────────────────────────────────────────────────
// One render op per live cell (cell coords + colour); the renderer blits, scaling to
// pixels. The SIM does ALL the model + colour + op-building work, like the Brood SIM.
public readonly record struct RenderOp(int X, int Y, byte R, byte G, byte B);

public sealed record Frame(RenderOp[] Ops, int W, int H, string Status);
