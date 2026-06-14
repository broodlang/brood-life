# life — guidance for Claude

Conway's Game of Life on a wrapping torus, animated in a native GUI window.
Written in Brood (`.blsp`). The architecture is two processes (ADR-058): a
**SIM** (owns the model, steps it, recolours it, BUILDS the frame's render ops,
and paces itself with a self-resetting `(after)` timer that input preempts — the
ADR-101 timer mechanism), and the **RENDERER** (the root process, which owns the
window and just BLITS the SIM's ops — op-building lives on the SIM because the
root process is ~2× slower at compute; the renderer re-renders locally only for
instant input feedback). The sim's per-frame work is serial. See `README.md` for
the design notes and `src/` layout.

Conventions worth knowing here:

- The sim core is **pure** (`step`, `seed`, `shift`, `inject-pattern`) and is what
  the test suite covers; the frame loop owns a GUI window, so exercise it with
  `nest run --for <ms>`, not `nest test`.
- Pattern **geometry** lives in `shapes`/`guns`; torus **placement** (the
  `*w*`/`*h*` globals and `shift`) stays in `life`.
- `*foo*` globals (board size, pacing, schedule) are the tuning knobs — each is
  documented inline at the top of `src/life.blsp`.

## Running

- `nest test`   — run the test suite (each test runs in its own green process).
- `nest run`    — invoke the entry point. Defaults to the `main` function in
  the `main` module; override in `project.blsp` with `:main`:
  `:main 'app` runs `app/main`; `:main '(app start)` runs `app/start`.
  Names are flat across the whole project (ADR-019) — there is exactly one
  of every global name, so don't define `main` in two modules (the runner
  warns if you do).
- `nest run --for 2s` — run a loop / full-screen TUI for a bounded time, then
  exit cleanly (`2s`, `500ms`, or a bare integer of ms). The way to exercise
  a never-returning program end-to-end or in CI.
- `nest format` — format Brood source.

## Writing Brood

`docs/brood-for-claude.md` is the language reference geared for AI assistants
— syntax, idioms, and the patterns that aren't shared with other Lisps. Read
it before generating Brood code. The `.claude/skills/writing-brood` skill
carries the short version and auto-loads when Claude Code edits `.blsp` files.

Brood ships randomness (`rand-int`/`rand-float`/`shuffle`/`sample` — pure and
seedable, thread the seed), bitwise ops (`bit-and`/`bit-or`/`bit-xor`/...),
and discovery (`apropos`, `all-globals`, `doc-search`) — use the last three to
find what exists instead of guessing names.

## MCP integration

`.mcp.json` points Claude Code at this project's `nest mcp` server, so `cd life && claude`
auto-attaches an agent that can `eval`, `load`, `lookup`, `macroexpand`, `format`,
and discover the image with `apropos` / `all-globals` / `doc-search`, against the
live image (ADR-036, `docs/mcp.md` upstream).
