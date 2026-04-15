# Game of Ur — Engine Architecture

## Board Representation

The board is a flat 20-element array (`GamePiece[] gameBoard`). Each element is either `null` (empty) or a `GamePiece` reference.

```
████████████████        ████████
█0 ██1 ██2 ██3 █        █4 ██5 █
████████████████        ████████
████████████████████████████████
█6 ██7 ██8 ██9 ██10██11██12██13█
████████████████████████████████
████████████████        ████████
█14██15██16██17█        █18██19█
████████████████        ████████
```

Indices 0–5 are Player 1's private lane. Indices 14–19 are Player 2's private lane. Indices 6–13 are the shared central lane where captures can occur (except index 9, the safe rosette).

## Movement Pattern System

Each player has a `movementPattern` array that maps a piece's logical *progress counter* (0–13) to a physical board index. Progress counter 14 means the piece has scored (left the board).

- **Player 1:** `{0, 1, 2, 3, 6, 7, 8, 9, 10, 11, 12, 13, 5, 4}`
- **Player 2:** `{14, 15, 16, 17, 6, 7, 8, 9, 10, 11, 12, 13, 19, 18}`

Both players share board indices 6–13 (the middle row). The paths diverge at the end: Player 1 exits through 5→4, Player 2 exits through 19→18.

## Piece Tracking

`GamePiece.movementCounter` encodes a piece's current progress along its player's path:

| Value | Meaning                              |
|-------|--------------------------------------|
| -1    | In hand (not yet placed on board)    |
| 0–13  | On the board at `movementPattern[n]` |
| 14    | Scored (in goal, off the board)      |

## Rosette Squares

Landing on a rosette grants the player an extra turn (`player.hasDouble = true`).

| Board Index | Location            | Special Rule         |
|-------------|---------------------|----------------------|
| 3           | Player 1 start lane | Double turn          |
| 17          | Player 2 start lane | Double turn          |
| 9           | Shared center lane  | Double turn + safe   |
| 4           | Player 1 exit lane  | Double turn          |
| 18          | Player 2 exit lane  | Double turn          |

Index 9 is the *safe rosette*: a piece on it cannot be captured.

## Game Flow

1. **Roll** — sum of 4 binary dice (0–4)
2. **`getPossibleMoves`** — probe each on-board piece and the hand using `movePiece` + `undoMove`
3. **Select move** — human input or AI (random in the current engine)
4. **`movePiece`** — execute the chosen move; handles placement, movement, capture, and goal scoring
5. **Check double** — if `player.hasDouble`, the same player goes again; otherwise `changeTurns()`
6. **Win check** — game ends when either player reaches `piecesInGoal == 7`

## State Management (Undo System)

The engine uses three parallel stacks to snapshot and restore state:

- `Stack<GameBoard> gameStates`
- `Stack<Player> player1States`
- `Stack<Player> player2States`

`updateStacks()` deep-copies the current board and both players onto their stacks. `undoMove()` pops all three stacks and restores the previous state. This is used by `getPossibleMoves` to probe candidate moves without permanently changing the game state.

> **Note:** A bug in the original code (fixed in Phase 1) meant `undoMove()` was only called on a successful probe, leaving stale snapshots on the stack after failed probes. See `BUGS.md` Bug 1.

## Class Structure (post-Phase 2)

| File                  | Class              | Responsibility                                                      |
|-----------------------|--------------------|---------------------------------------------------------------------|
| `Game.cs`             | `Game`             | Game loop, undo system, human/AI move selection                     |
| `Player.cs`           | `Player`           | Player state: piece counts, movement pattern, double turn           |
| `GamePiece.cs`        | `GamePiece`        | Piece state: owner, progress counter, in-hand flag                  |
| `GameBoard.cs`        | `GameBoard`        | Board array, move execution, collision detection                    |
| `GameEnvironment.cs`  | `GameEnvironment`  | Gym-style RL API: Reset, Step, GetValidActions, state vector        |
| `EnvironmentBridge.cs`| `EnvironmentBridge` | Stdin/stdout JSON-line IPC for Python training                     |

## GameEnvironment (Phase 2)

### State Vector (30 floats, normalized [0, 1])

| Index  | Meaning                                                |
|--------|--------------------------------------------------------|
| 0–6    | Player 1 piece progress: `(movementCounter + 1) / 15` |
| 7–13   | Player 2 piece progress                               |
| 14     | Player 1 pieces in hand / 7                           |
| 15     | Player 1 pieces in goal / 7                           |
| 16     | Player 2 pieces in hand / 7                           |
| 17     | Player 2 pieces in goal / 7                           |
| 18     | Current roll / 4                                      |
| 19–26  | Action mask (1 = valid, 0 = invalid)                  |
| 27     | Has double turn flag                                  |
| 28–29  | Reserved                                              |

### Action Space (8 discrete actions)

| Action | Meaning                                    |
|--------|--------------------------------------------|
| 0–6    | Move piece at logical index N (sorted by progress) |
| 7      | Place new piece from hand                  |

### IPC Protocol (stdin/stdout JSON lines)

Run with `dotnet run -- --bridge [--seed N]`. Each line is a JSON object:

```json
{"method": "reset"}
{"method": "step", "action": 7}
{"method": "get_valid_actions"}
{"method": "get_state"}
{"method": "close"}
```

Response format:
```json
{"state": [...], "reward": 0, "done": false, "valid_actions": [...], "info": {}}
```
