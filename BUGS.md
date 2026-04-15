# Game of Ur — Known Bugs & Fixes

## Bug 1: `getPossibleMoves` State Leak

**Status:** - [x] Fixed

**Description:** `updateStacks()` is called before each probe in the `foreach` loop and the "place new piece" check, but `undoMove()` is only called when `result == 0`. When `movePiece` returns 1 (illegal move), the pushed state is never popped.

**Location:** `Game.cs` (master) → `Game.getPossibleMoves()`, lines 67–78

**Root Cause:** The probe pattern pushes a snapshot with `updateStacks()` then checks legality by actually executing the move. On success it adds the move to the list and calls `undoMove()`. On failure it does neither — the stale snapshot stays on all three stacks forever.

**Impact:** During normal gameplay this causes minor state corruption. During a training loop running thousands of games it is catastrophic: the undo stacks grow without bound (memory leak), and any subsequent call to `undoMove()` restores the wrong game state, injecting completely invalid board positions into training data.

**Fix:** Call `undoMove()` unconditionally after every probe, regardless of the result:
```csharp
updateStacks();
int result = gameBoard.movePiece(player, getOppositePlayer(player), gameBoard.getPiece(idx), roll);
if (result == 0)
{
    possibleMoves.Add(idx);
}
undoMove(); // Always restore state after probing
```

---

## Bug 2: `movePiece` Mutates Piece on Failure

**Status:** - [x] Fixed

**Description:** `piece.movementCounter += roll` at the top of the movement logic mutates the piece object before any legality checks. If the move is later found to be illegal (blocked by a friendly piece, overshoots the goal, or would capture on the safe rosette), the code does `piece.movementCounter -= roll` to undo — but by then the piece has already been mutated and the undo is racing against the stack restore.

**Location:** `Game.cs` (master) → `GameBoard.movePiece()`, line 394

**Root Cause:** `GamePiece` is a reference type. When `getPossibleMoves` probes a move, it passes a reference to the *actual board piece* (via `gameBoard.getPiece(idx)`). Mutating `movementCounter` before confirming legality means the real piece on the board is transiently in an invalid state during the probe.

**Impact:** Subtle race-like state corruption during `getPossibleMoves`. The roll-back (`-= roll`) usually corrects this for single-threaded use, but it relies on the exact inverse being applied before any snapshot is taken, which is fragile and confusing. Combined with Bug 1 (stale snapshots), the restored state may contain a piece with the wrong counter.

**Fix:** Calculate the prospective new counter first, perform all legality checks, and only write back to `piece.movementCounter` once the move is confirmed:
```csharp
int newCounter = piece.movementCounter + roll;
if (newCounter == 14) { /* score */ }
else if (newCounter > 14) { return 1; }
int destinationIdx = player.movementPattern[newCounter];
// ... collision checks using newCounter ...
piece.movementCounter = newCounter; // only write on confirmed legal move
```

---

## Bug 3: `roll == 0` Returns Success in `movePiece`

**Status:** - [x] Fixed

**Description:** When `roll == 0`, `movePiece` clears `hasDouble` and immediately returns `0` (success). A zero roll means no movement is possible and the turn should be skipped — treating it as a successful move gives the caller incorrect feedback.

**Location:** `Game.cs` (master) → `GameBoard.movePiece()`, lines 389–392

**Root Cause:** The early return for `roll == 0` was likely added to avoid division-by-zero or array-bounds issues, but the return code was incorrectly set to 0. Additionally, `hasDouble` is cleared by the `roll == 0` guard at line 385–388, which has its own semantics problem: a zero roll when `hasDouble` is true should not consume the double.

**Impact:** The `getPossibleMoves` method correctly returns an empty list for roll 0, so normal gameplay usually skips turns correctly. However, if `movePiece` is ever called directly with roll=0 (e.g., from the RL training loop or a test), it will report success with no board change, injecting a spurious "successful no-op" into the action history.

**Fix:** Return `1` for `roll == 0`:
```csharp
if (roll == 0)
{
    return 1;
}
```

---

## Bug 4: State Representation Is Ambiguous (RL Branch)

**Status:** - [x] Fixed in Phase 2

**Description:** The state string sent to Python in the `ReinforcementLearning` branch is a raw concatenation: board as 20 digits + roll + p1Goal + p1Hand + p2Goal + p2Hand. There are no delimiters.

**Location:** `ReinforcementLearning` branch → `Game.cs` (RL version), state serialization code

**Root Cause:** Multi-digit values (e.g., `piecesInGoal = 10`) shift all subsequent fields. More critically, the board only encodes *which player occupies a space*, not *how far along each piece is*. Two board states with pieces in the same positions but different movement progress are indistinguishable.

**Impact:** This was almost certainly a primary cause of the agent failing to learn meaningful strategies. The network receives ambiguous observations that map multiple distinct game states to the same input vector.

**Fix (Phase 2):** Replace with a fixed-size float array: 7 values for each piece's `movementCounter` (-1 in hand, 0–13 on board, 14 scored), normalized to [0, 1], plus roll, pieces-in-hand/goal for both players. Approximately 30 floats total with no ambiguity.

---

## Bug 5: `captured()` Doesn't Update `piecesInHand`

**Status:** - [ ] Documented (low priority, workaround in place)

**Description:** The `captured()` method on `GamePiece` resets the piece's state but has `player.piecesInHand += 1` commented out. The caller (`movePiece`) instead does `opponent.piecesInHand++` directly.

**Location:** `Game.cs` (master) → `GamePiece.captured()`, line 286

**Root Cause:** The increment was commented out, likely to avoid double-counting since the caller also increments. The workaround in `movePiece` is correct for the current code paths.

**Impact:** Currently harmless since `captured()` is only called from `capturePiece()` which is only called from `movePiece()`. However, if `captured()` is ever called from any other context (e.g., a future `GameEnvironment.Reset()` that recycles pieces), `piecesInHand` will not be updated and the hand count will be wrong.

**Fix (Low Priority):** Move the `piecesInHand++` into `captured()` and remove it from `movePiece`, or leave as-is but add a comment explaining the intentional design.
