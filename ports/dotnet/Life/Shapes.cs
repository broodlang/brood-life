namespace Life;

/// <summary>
/// A small pattern catalog. The Brood version fans ~110 patterns by rotation/reflection
/// (src/shapes.blsp); here we keep a representative handful — enough to make the board
/// come alive on click and auto-spawn. Each pattern is a list of (dx,dy) live cells.
/// </summary>
public static class Shapes
{
    public static readonly (int dx, int dy)[][] All =
    {
        // glider (spaceship)
        new[] { (1, 0), (2, 1), (0, 2), (1, 2), (2, 2) },
        // blinker (oscillator)
        new[] { (0, 0), (1, 0), (2, 0) },
        // block (still life)
        new[] { (0, 0), (1, 0), (0, 1), (1, 1) },
        // beacon (oscillator)
        new[] { (0, 0), (1, 0), (0, 1), (3, 2), (2, 3), (3, 3) },
        // lightweight spaceship (LWSS)
        new[] { (1, 0), (4, 0), (0, 1), (0, 2), (4, 2), (0, 3), (1, 3), (2, 3), (3, 3) },
        // r-pentomino (methuselah — chaos for ~1100 gens)
        new[] { (1, 0), (2, 0), (0, 1), (1, 1), (1, 2) },
    };

    /// <summary>The Gosper glider gun — emits a glider every 30 generations.</summary>
    public static readonly (int dx, int dy)[] GosperGun =
    {
        (0, 4), (0, 5), (1, 4), (1, 5),
        (10, 4), (10, 5), (10, 6), (11, 3), (11, 7), (12, 2), (12, 8), (13, 2), (13, 8),
        (14, 5), (15, 3), (15, 7), (16, 4), (16, 5), (16, 6), (17, 5),
        (20, 2), (20, 3), (20, 4), (21, 2), (21, 3), (21, 4), (22, 1), (22, 5),
        (24, 0), (24, 1), (24, 5), (24, 6),
        (34, 2), (34, 3), (35, 2), (35, 3),
    };

    public static (int dx, int dy)[] Random(int seed) => All[((seed % All.Length) + All.Length) % All.Length];
}
