using System.Numerics;

namespace Life;

/// <summary>
/// Conway's Game of Life board as a WHOLE-BOARD bitboard: ONE arbitrary-precision
/// integer for the entire grid, bit (y*w + x) = cell (x,y) alive. This is a faithful
/// port of src/bitboard.blsp — same bit-plane full-adder torus step, same masks. .NET's
/// <see cref="BigInteger"/> stands in for Brood's native bignums + unrestricted shifts.
///
/// The board is immutable (a struct of readonly fields); each op returns a new board, so
/// the SIM thread owns the model the same way the Brood SIM process does.
/// </summary>
public readonly struct BitBoard
{
    public readonly int W;
    public readonly int H;
    public readonly BigInteger Bits;   // the grid integer
    private readonly BigInteger _mask;  // one row: w low bits (2^w - 1)
    private readonly BigInteger _board; // the whole grid: w*h bits (2^(w*h) - 1)
    private readonly BigInteger _col0;  // the x=0 bit of every row
    private readonly BigInteger _high;  // the x=w-1 bit of every row

    private BitBoard(int w, int h, BigInteger bits, BigInteger mask, BigInteger board,
        BigInteger col0, BigInteger high)
    {
        W = w; H = h; Bits = bits;
        _mask = mask; _board = board; _col0 = col0; _high = high;
    }

    /// <summary>An empty w×h board, with the torus edge-column masks precomputed once.</summary>
    public static BitBoard Make(int w, int h)
    {
        BigInteger mask = (BigInteger.One << w) - 1;          // one row
        BigInteger board = (BigInteger.One << (w * h)) - 1;   // the whole grid
        BigInteger col0 = board / mask;                        // each row's low bit (geometric series)
        BigInteger high = col0 << (w - 1);                     // each row's top bit
        return new BitBoard(w, h, BigInteger.Zero, mask, board, col0, high);
    }

    /// <summary>Set cell (x,y) live; returns the new board. Caller wraps x/y into range.</summary>
    public BitBoard Set(int x, int y) =>
        WithBits(Bits | (BigInteger.One << (x + y * W)));

    /// <summary>Set every cell of a pattern (a list of (dx,dy)) at origin (x,y), wrapped.</summary>
    public BitBoard Place(IEnumerable<(int dx, int dy)> pat, int x, int y)
    {
        var b = this;
        foreach (var (dx, dy) in pat)
            b = b.Set(Mod(x + dx, W), Mod(y + dy, H));
        return b;
    }

    /// <summary>Population: number of live cells.</summary>
    public int LiveCount() => (int)BigInteger.PopCount(Bits);

    /// <summary>
    /// Advance one generation. A fixed handful of whole-board big-int ops (a bit-plane
    /// full-adder over the eight torus-shifted neighbour fields), independent of population.
    /// A cell lives iff it has exactly 3 live neighbours, or 2 and it is already live.
    /// </summary>
    public BitBoard Step()
    {
        BigInteger b = Bits, brd = _board, mask = _mask, col0 = _col0, high = _high;
        int w = W, wm1 = w - 1, hm1w = (H - 1) * w;

        // west / east neighbour fields (torus-wrapped within each row)
        BigInteger l = ((b << 1) & (col0 ^ brd)) | ((b & high) >> wm1);
        BigInteger r = ((b >> 1) & (high ^ brd)) | ((b & col0) << wm1);

        // lift a field one row up / down, the off-board row wrapping round (torus)
        BigInteger Up(BigInteger f) => ((f << w) & brd) | (f >> hm1w);
        BigInteger Dn(BigInteger f) => (f >> w) | ((f & mask) << hm1w);

        // the eight neighbours: three columns (west/centre/east) × three rows
        BigInteger[] ns = { Up(l), Up(b), Up(r), l, r, Dn(l), Dn(b), Dn(r) };

        // full-adder: sum the eight 1-bit-per-cell fields into a per-cell count, low 3 bits
        BigInteger s0 = 0, s1 = 0, s2 = 0;
        foreach (var m in ns)
        {
            BigInteger c = s0 & m;
            s0 ^= m;
            BigInteger c2 = s1 & c;
            s1 ^= c;
            s2 ^= c2;
        }

        // survival: s1 & ~s2 & (s0 | cur)  — count of 2-with-cell or 3
        return WithBits(s1 & (s2 ^ brd) & (s0 | b));
    }

    /// <summary>
    /// The live cells as (x,y) pairs. Brood enumerates set bits with the native
    /// `bit-positions` builtin; here we snapshot the integer's bytes ONCE
    /// (<see cref="BigInteger.ToByteArray"/>, little-endian) and scan them — O(bytes)+O(live).
    /// NOTE: do NOT walk this by shifting the BigInteger one bit at a time — each shift copies
    /// the whole backing array, making enumeration O(bits²) and dominating the whole frame.
    /// </summary>
    public IEnumerable<(int x, int y)> Cells()
    {
        byte[] bytes = Bits.ToByteArray(); // little-endian two's complement; Bits >= 0
        for (int bi = 0; bi < bytes.Length; bi++)
        {
            int by = bytes[bi];
            if (by == 0) continue;
            int baseBit = bi * 8;
            while (by != 0)
            {
                int k = System.Numerics.BitOperations.TrailingZeroCount(by);
                int idx = baseBit + k;
                yield return (idx % W, idx / W);
                by &= by - 1; // clear the lowest set bit
            }
        }
    }

    /// <summary>Rebuild at new dims (resize/zoom), DROPPING cells that fall outside.</summary>
    public BitBoard Refit(int w, int h)
    {
        var nb = Make(w, h);
        foreach (var (x, y) in Cells())
            if (x < w && y < h) nb = nb.Set(x, y);
        return nb;
    }

    private BitBoard WithBits(BigInteger bits) =>
        new(W, H, bits, _mask, _board, _col0, _high);

    private static int Mod(int a, int n) => ((a % n) + n) % n;
}
