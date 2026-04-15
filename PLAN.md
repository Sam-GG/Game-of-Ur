# Game of Ur — Reinforcement Learning Implementation Plan

## Phase 1: Engine Cleanup & Correctness

- [x] Fix `getPossibleMoves` state leak: when `movePiece` returns 1 (illegal), the stack snapshot pushed by `updateStacks()` is never popped. Only successful moves call `undoMove()`. Every failed probe leaves a stale snapshot, corrupting undo history and leaking memory during training.
- [x] Fix `movePiece` mutation on failure: `piece.movementCounter += roll` happens before legality checks. On failure it rolls back with `-= roll`, but since `GamePiece` is a reference type the actual board piece is mutated during the probe. Fix by checking legality before mutating.
- [x] Fix `roll == 0` handling in `movePiece`: currently returns 0 (success) without doing anything, but also clears `hasDouble`. This is semantically wrong — a zero roll should skip the turn, not count as a successful "move."
- [x] Separate classes into individual files: `Game.cs`, `Player.cs`, `GamePiece.cs`, `GameBoard.cs`
- [x] Remove dead/unused `using` statements (e.g., `System.ComponentModel.DataAnnotations.Schema`, `System.Numerics`, `System.Runtime.InteropServices`, `System.Transactions`)
- [x] Upgrade target framework from `net5.0` to `net8.0`
- [x] Add a unit test project with tests covering: move legality, captures, rosette double-turns, goal scoring, roll=0 handling, overshoot, capture-on-rosette protection, `getPossibleMoves` correctness

## Phase 2: Training-Ready Game API

- [x] Build a `GameEnvironment` class with Gym-style interface: `Reset()`, `Step(action) → (state, reward, done, info)`, `GetValidActions()`
- [x] Design a proper fixed-size state vector (not string concatenation). Approximately 30 floats: 7 values for each piece's progress (0–14 or -1 for hand), 7 for opponent pieces, roll, pieces in hand/goal for both players. All normalized to [0, 1].
- [x] Design a fixed discrete action space with invalid-action masking. Actions map to "move piece N" where N is a logical piece index (0–6), plus "place new piece". Invalid actions are masked per turn.
- [x] Reward: start with only win/loss signal (+1/-1 at game end, 0 otherwise). Only add shaping if training fails to converge. The owner explicitly wants minimal shaping to let the agent explore freely.
- [x] Expose `GameEnvironment` over a clean IPC interface (gRPC or stdin/stdout protocol) so the Python training script can call `Reset`, `Step`, `GetValidActions` as RPCs.

## Phase 3: Python RL Training

- [ ] Set up Python project with Stable-Baselines3 and sb3-contrib
- [ ] Implement a Gym wrapper that communicates with the C# `GameEnvironment` over the IPC interface
- [ ] Use `MaskablePPO` from sb3-contrib (invalid action masking is critical for board games)
- [ ] Train agent vs random opponent first (~500K–1M steps, target >60% win rate)
- [ ] Logging with TensorBoard: win rate, avg game length, reward per episode, action distribution

## Phase 4: Self-Play & Analysis

- [ ] Once agent beats random, implement self-play training
- [ ] Freeze trained policy and evaluate against various opponents (random, greedy, defensive heuristic)
- [ ] Visualize policy decisions for key board states to extract learned strategies
- [ ] Compare findings to known Ur strategy research
