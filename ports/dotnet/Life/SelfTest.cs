namespace Life;

/// <summary>
/// A no-window check of the pure bitboard core — the counterpart of tests/life_test.blsp.
/// The frame loop owns a window (exercise it with `--for`); the core is pure and testable.
/// </summary>
public static class SelfTest
{
    public static int Run()
    {
        int failures = 0;

        void Check(string name, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {name}");
            if (!ok) failures++;
        }

        bool SameCells(BitBoard b, params (int, int)[] expected)
        {
            var got = new HashSet<(int, int)>(b.Cells());
            return got.SetEquals(expected);
        }

        // blinker oscillates with period 2 (horizontal ↔ vertical), on a torus
        var blinker = BitBoard.Make(10, 10).Place(new[] { (0, 0), (1, 0), (2, 0) }, 3, 3);
        var b1 = blinker.Step();
        Check("blinker → vertical", SameCells(b1, (4, 2), (4, 3), (4, 4)));
        Check("blinker period 2", SameCells(b1.Step(), blinker.Cells().ToArray()));

        // block is a still life
        var block = BitBoard.Make(10, 10).Place(new[] { (0, 0), (1, 0), (0, 1), (1, 1) }, 2, 2);
        Check("block is still", SameCells(block.Step(), block.Cells().ToArray()));

        // glider translates by (1,1) every 4 generations
        var glider = BitBoard.Make(20, 20).Place(Shapes.All[0], 1, 1);
        var moved = glider.Step().Step().Step().Step();
        var shifted = new HashSet<(int, int)>(glider.Cells().Select(c => (c.x + 1, c.y + 1)));
        Check("glider moves (1,1)/4gen", new HashSet<(int, int)>(moved.Cells()).SetEquals(shifted));

        // torus wrap: a blinker at the right edge wraps its neighbour count around
        var edge = BitBoard.Make(6, 6).Place(new[] { (0, 0), (1, 0), (2, 0) }, 5, 3);
        Check("torus wrap survives", edge.Step().LiveCount() == 3);

        // population count
        Check("live-count", glider.LiveCount() == 5);

        Console.WriteLine(failures == 0 ? "all passed" : $"{failures} FAILED");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Time step + cell-enumeration on the README's 250×140 board.</summary>
    public static int Bench()
    {
        const int W = 250, H = 140, gens = 2000;
        var board = BitBoard.Make(W, H)
            .Place(Shapes.GosperGun, 5, 5)
            .Place(Shapes.All[5], W / 2, H / 2);

        // warm up the JIT
        for (int i = 0; i < 100; i++) { board = board.Step(); foreach (var _ in board.Cells()) { } }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long cells = 0;
        for (int i = 0; i < gens; i++)
        {
            board = board.Step();
            foreach (var _ in board.Cells()) cells++;
        }
        sw.Stop();

        double perGen = sw.Elapsed.TotalMilliseconds / gens;
        Console.WriteLine($"{W}×{H}, {gens} gens: {sw.Elapsed.TotalMilliseconds:0} ms total, " +
                          $"{perGen:0.000} ms/gen (step+cells), live~{board.LiveCount()}");
        return 0;
    }
}
