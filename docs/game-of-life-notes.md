# Building the Game of Life loop — mistakes & corrections

Notes from writing `src/life.blsp` (toroidal 60×40 Conway's Life). Recorded so
the next person — or the next Claude — doesn't re-walk the same five potholes.
Split into **mistakes I made** and **places the skill/guide steered me wrong**.

## Mistakes I made (and the fix)

### 1. `(:require display)` in `defmodule` loads but doesn't refer names

I opened the module with `(defmodule life … (:require display))` and called
`frame`/`clear`/`text` bare. Load failed:

```
unbound symbol: frame   (and clear, text)
```

`:require` *loads* a module but leaves its exports qualified (`display/frame`).
To pull names into scope **bare**, use `:use` — exactly what the scaffolded
`main` does with `hello`:

```lisp
(defmodule life "…" (:use display))   ; not (:require display)
```

### 2. `defn` names are module-qualified from outside; `def` globals are not

Testing through MCP `eval` (which runs in the REPL's global scope, *outside* the
module), bare calls failed:

```
unbound symbol: cells          ; it's life/cells out here
```

`apropos "life/"` showed every `defn` qualified — `life/step`, `life/cells`,
`life/seed`. But `*glider*`, `*width*`, `*offsets*` (the `def` vars) are **not**
qualified — `apropos "glider"` → `*glider*`. So from the REPL:

```lisp
(life/cells *glider*)          ; defn qualified, def var bare
```

*Inside* the module file the distinction vanishes — sibling `defn`s call each
other bare (`seed` calls `cells`/`place` with no prefix), which is why the file
loads clean. The qualification only matters when you reach in from outside.

### 3. `:main` in `project.blsp` takes the entry list **unquoted**

I wrote `:main '(life run-life)`. `nest run` rejected it:

```
project: :main must be a module symbol or '(module fn), got (quote (life run-life))
```

`project.blsp` is read as **data, not evaluated**, so the quote isn't stripped —
it's read literally as `(quote (life run-life))`. The correct forms:

```lisp
:main life               ; runs life/main
:main (life run-life)    ; runs life/run-life  ← what I needed
```

No quote, no `"module/fn"` string. (See §A below — the guides actively misled
me here.)

### 4. `nest format` takes no file argument — it's whole-tree

`nest format src/life.blsp` →

```
error: unexpected argument 'src/life.blsp' found
```

`nest format` (no args) considers and rewrites the whole tree. Just run it bare
and let `git diff` show what changed.

### 5. Verifying a `term-enter` TUI needs a real TTY (a pty), not a pipe

The skill's verification recipe pipes stdout:

```
nest run --for 600ms 2>&1 | cat -v | grep -oE '\^\[\[[0-9;]*[A-Za-z]'
```

For this full-screen TUI that died instantly:

```
runtime error: terminal: No such device or address (os error 6)
```

`term-enter` (alternate screen + raw mode) requires a controlling terminal; a
pipe has none. Allocate a pseudo-terminal with `script`:

```bash
script -qec "nest run --for 800ms" /dev/null 2>&1 | cat -v \
  | grep -oE '\^\[\[[0-9;]*[A-Za-z]' | sort | uniq -c    # cursor moves per frame

script -qec "nest run --for 400ms" /dev/null 2>&1 | tr -cd $'\xe2\x96\x88' | wc -c
                                                          # count █ glyphs rendered
```

That confirmed ~8 frames of cursor-positioning escapes and live `█` cells.

## A. Where the skill / guides gave wrong or incomplete information

1. **`:main` quoting — actively wrong.** `CLAUDE.md` (the upstream project
   guidance) documents `:main 'app` and `:main '(app start)` *with* a leading
   quote, and `docs/brood-for-claude.md` doesn't cover `:main` at all. The
   running `nest` rejects the quoted form and wants it **unquoted**
   (`(life run-life)`). Either the doc is stale or the runtime changed; right now
   they disagree, and the doc loses. **Fix the CLAUDE.md examples to drop the
   quote.**

2. **`:require` vs `:use` as `defmodule` clauses — undocumented.** Neither the
   `writing-brood` skill nor `docs/brood-for-claude.md` explains the difference.
   The doc's module skeleton shows top-level `(require 'hello)` then a bare
   `(greeting)` "just working," which implies `require` refers names — but as a
   **`defmodule` clause**, `:require` only loads while `:use` refers. The skill's
   ANSI/display advice ("`(require 'display)` then call `(frame …)`") reinforces
   the wrong intuition for module code. Worth a one-liner: *inside `defmodule`,
   `:use` to call names bare; `:require` only loads.*

3. **TUI verification recipe assumes a pipe works.** The skill's "inspect the raw
   bytes for escapes" command (`nest run … | cat -v | grep …`) is fine for an
   `ansi`-string loop that just `print`s escapes, but it **fails for any
   `term-enter` TUI** — those need a TTY. The skill should note: wrap the run in
   `script -qec "…" /dev/null` when the loop uses `term-enter`/`term-draw`.

4. **Module-qualified `defn` names vs the "flat namespace" framing.** The skill
   and the `flat-namespace-one-main` memory describe one flat global table. Post
   the module system, `defn` names are actually exposed **qualified**
   (`life/step`) outside their module while `def` vars stay bare — a wrinkle that
   bit me in §2 and isn't spelled out anywhere. The "one `main` per project"
   warning still holds, but "there is exactly one of every global name" reads as
   too strong against what `apropos` actually shows.

## What the guides got right

- The `frequencies`/`mapcat` neighbour-counting idiom (skill §8) is exactly the
  right shape for `step` — no nested scan, no mutable tally.
- "Lists for code, vectors for data," tail-recursion for the loop, and the
  truthiness/`:else` rules all held with no surprises.
- The MCP `eval`/`load`/`apropos`/`lookup` loop caught every one of the mistakes
  above at write-time rather than at `nest test` — using it is the single biggest
  reason this came together quickly.

## B. Iterating on the spec — what each follow-up cost

The board went through a rapid string of edits: `60×40 → 40×60 → 90×40 →
120×40`, "make it much more concise," "< 40 lines of code," "add random known
shapes," and "consider the board size." Notes on how that went and what would
make it smoother:

- **Resizes were cheap because the dimensions were already named.** `*w*`/`*h*`
  are referenced everywhere (wrap, seed range, render, tests), so each resize was
  a two-line `def` change plus three test-bound edits. *Improvement:* the test
  bounds duplicate the `*w*`/`*h*` literals — they should import the module's
  `*w*`/`*h*` instead of hard-coding `120`/`40`, so a resize needs no test edit.
  (Didn't, only because `def` vars cross the module boundary unqualified and I
  wanted the test obvious.)

- **"Consider the board size" fell out for free** once the shape count was
  `(max 4 (quot (* *w* *h*) 400))` and placement used `(rand-int s *w*)` /
  `(rand-int s *h*)` — density and spread now track any resize automatically.

- **The "< 40 lines" target fought the docstring convention.** Brood wants a
  docstring on every public `defn`, but a docstring is always its own line, so
  seven public fns = seven forced lines. I hit the budget by converting them to
  one-line `;;` comments above each form. That's a real tension the guides don't
  acknowledge: *you cannot have both "docstring on every public fn" and a tight
  line budget* — flag the trade rather than silently dropping docs.

- **The formatter mangles inline comments inside data literals.** Writing
  `*shapes*` as one pattern per line with a trailing `; block` / `; glider`
  comment, `nest format` collapsed the first two patterns onto one line and
  shuffled a comment onto the wrong row (see the diff history of this file). The
  fix was to pull the names into a single block comment *above* the vector and
  leave the data comment-free. *Lesson:* keep `;` comments out of vector/map
  literals you intend to `nest format`; annotate above the form instead.

- **MCP image staleness bit twice.** After editing `life.blsp` on disk I ran
  `nest format`/`nest test` (which read the file) but the MCP image still held the
  *old* defs, so `eval` reported `unbound symbol: *w*` until I `load`ed the file
  again. *Reflex to keep:* after any on-disk edit, `load` the file into the image
  before `eval`-testing against it. (Matches the existing `verify-brood-fixes`
  memory — the image is a separate world from the files.)

## C. The frame-pacing bug — and why it was NOT the clock or the GC

**Symptom (user-reported):** watching the animation, it "speeds up and slows down
now and again," suspected to be "something off with the clock, or the GC making
the clock inconsistent."

**Real cause — my loop, not the runtime.** The old loop paced with a *fixed*
wait taken **after** variable-time work:

```lisp
(term-draw (render live gen))     ; cost ~constant (full screen)
(term-poll 90)                    ; always wait 90ms MORE
(life--loop (step live) …)        ; step cost ∝ live-cell count — VARIABLE
```

So the real frame period was `render + step + 90ms`. As the population swelled
and collapsed (gliders colliding, shapes settling), `step` time swung, and the
period swung with it — exactly the "speeds up and slows down" the user saw. A GC
pause adds the occasional extra hitch on top, but it is **not** the driver, and
the clock is fine: `now-ns` has sub-microsecond resolution (measured ~640ns
between back-to-back calls) and is monotonic enough to pace on.

**Fix — fixed-timestep against a wall-clock deadline.** Carry a `due` timestamp;
each frame, poll only for the time *remaining* until `due`, then advance `due` by
one frame period. Work that finishes early waits longer; work that finishes late
waits the 1ms floor — the *period* stays constant either way:

```lisp
(defn life--loop (live gen due)
  (do (term-draw (render live gen))
      (if (includes? ["q" :escape :ctrl-c]
            (term-poll (max 1 (quot (- due (now-ns)) 1000000))))   ; ns→ms remaining
        nil
        (let (next (+ due *frame-ns*))
          (life--loop (step live) (+ gen 1)
            (if (< next (now-ns)) (+ (now-ns) *frame-ns*) next)))))) ; resync if behind
```

The `(< next (now-ns))` resync matters: after a long GC pause the deadline would
otherwise be far in the past, and the next several frames would poll the 1ms
floor and *race* to catch up — a visible speed burst. Snapping `due` back to
"now + one frame" turns that into a single dropped frame instead of a sprint.

**Lessons for the guides:**

1. **Don't pace a render loop with a fixed sleep/poll after the work** — that
   couples frame rate to per-frame compute. The `writing-brood` skill shows
   `(my-loop (+ n 1))` after `(sleep 1000)` as the loop idiom; fine for a logger,
   **wrong for an animation**. The skill should show the fixed-timestep
   (`now-ns` deadline) pattern for any TUI whose per-frame cost varies.
2. **Blaming "the GC" or "the clock" for timing jitter is the easy wrong
   answer.** Measure before attributing: `now-ns` was sub-µs and monotonic, and
   the jitter scaled with population, not with allocation — both point at loop
   structure, not the runtime. Reach for a measurement, not a culprit.
