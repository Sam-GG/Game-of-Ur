#!/usr/bin/env python3
"""
Play against a trained Game of Ur model interactively in the terminal.

The human plays as Player 2 (opponent) and the trained model plays
as Player 1 (agent). The C# engine runs with ``--opponent external``
so that opponent turns are delegated to Python, where the human enters
their move via the console.

Usage:
    python play.py --model ./models/ur_ppo_final.zip
    python play.py --model ./models/self_play/self_play_best.zip --seed 42
"""

import argparse
import sys
from pathlib import Path

import numpy as np
from sb3_contrib import MaskablePPO
from sb3_contrib.common.maskable.utils import get_action_masks

sys.path.insert(0, str(Path(__file__).resolve().parent))

from ur_env import UrEnv, state_to_board, P1_PATTERN, P2_PATTERN

# ── Constants ─────────────────────────────────────────────────────────────

ROSETTES = {3, 4, 9, 17, 18}
TOTAL_PIECES = 7


# ── Board rendering ──────────────────────────────────────────────────────

def _cell(board: list[int], idx: int) -> str:
    """Return a 4-char cell label for the given board position."""
    occ = board[idx]
    ros = idx in ROSETTES
    if occ == 1:
        return "AI* " if ros else " AI "
    elif occ == 2:
        return "You*" if ros else "You "
    else:
        return " *  " if ros else "    "


def render_board(board: list[int]) -> str:
    """Build a text-art board showing piece positions.

    board: list of 20 ints (0=empty, 1=Player 1/AI, 2=Player 2/You).
    """
    c = lambda i: _cell(board, i)
    lines = [
        "    Your Home Lane          Your Exit",
        "  ┌────┬────┬────┬────┐        ┌────┬────┐",
        f"  │{c(14)}│{c(15)}│{c(16)}│{c(17)}│        │{c(18)}│{c(19)}│",
        "  ├────┼────┼────┼────┼────┬────┼────┼────┤",
        f"  │{c(6)}│{c(7)}│{c(8)}│{c(9)}│{c(10)}│{c(11)}│{c(12)}│{c(13)}│  ← shared",
        "  ├────┼────┼────┼────┼────┴────┼────┼────┤",
        f"  │{c(0)}│{c(1)}│{c(2)}│{c(3)}│        │{c(4)}│{c(5)}│",
        "  └────┴────┴────┴────┘        └────┴────┘",
        "     AI Home Lane           AI Exit",
    ]
    return "\n".join(lines)


def _read_status(state_vec: np.ndarray) -> dict:
    """Extract human-readable status values from the state vector."""
    return {
        "p1_hand": round(state_vec[14] * TOTAL_PIECES),
        "p1_goal": round(state_vec[15] * TOTAL_PIECES),
        "p2_hand": round(state_vec[16] * TOTAL_PIECES),
        "p2_goal": round(state_vec[17] * TOTAL_PIECES),
        "roll": round(state_vec[18] * 4),
    }


def display(board: list[int], state_vec: np.ndarray, *, show_roll: bool = True):
    """Print the full game display: board + status."""
    s = _read_status(state_vec)
    print()
    print(render_board(board))
    print()
    print(
        f"  AI : {s['p1_goal']} scored, {s['p1_hand']} in hand    |    "
        f"You: {s['p2_goal']} scored, {s['p2_hand']} in hand"
    )
    if show_roll:
        print(f"  Roll: {s['roll']}")
    print()


def _move_destination(pattern: list[int], current_mc: int, roll: int) -> str:
    """Describe the destination of a move along *pattern* from *current_mc*.

    Returns a suffix like ``" → square 8"`` or ``" → GOAL!"``.
    """
    new_mc = current_mc + roll
    if new_mc == 14:
        return " → GOAL!"
    if 0 <= new_mc <= 13:
        to_pos = pattern[new_mc]
        ros = " * rosette!" if to_pos in ROSETTES else ""
        return f" → square {to_pos}{ros}"
    return ""


def describe_ai_action(
    action: int, roll: int, board: list[int], state_vec: np.ndarray
) -> str:
    """Return a human-readable description of the AI's chosen action."""
    if action == 7:
        dest = _move_destination(P1_PATTERN, -1, roll)
        return f"Place new piece{dest}"

    # action 0–6: move an existing piece.  Reconstruct the piece map the
    # same way C# does: collect (movementCounter, boardIdx) for ALL P1
    # pieces — including in-hand (mc=-1, boardIdx=-1) and scored (mc=14,
    # boardIdx=-2) — then sort by mc ascending.
    p1_hand = round(state_vec[14] * TOTAL_PIECES)
    p1_goal = round(state_vec[15] * TOTAL_PIECES)

    pieces: list[tuple[int, int]] = []  # (movementCounter, boardIdx)

    # In-hand pieces
    for _ in range(p1_hand):
        pieces.append((-1, -1))

    # On-board pieces
    for i in range(20):
        if board[i] == 1:
            for mc_val, pos in enumerate(P1_PATTERN):
                if pos == i:
                    pieces.append((mc_val, i))
                    break

    # Scored pieces
    for _ in range(p1_goal):
        pieces.append((14, -2))

    pieces.sort()

    if action < len(pieces):
        mc_val, from_pos = pieces[action]
        if from_pos < 0:
            # Shouldn't happen for actions 0–6 (those should be on-board)
            return f"Move piece (action {action})"
        dest = _move_destination(P1_PATTERN, mc_val, roll)
        return f"Move piece at square {from_pos}{dest}"
    return f"Move piece (action {action})"


# ── Human opponent callback ──────────────────────────────────────────────

def human_opponent_callback(info: dict) -> int:
    """Prompt the human for their move when it's the opponent's turn."""
    board = info.get("board")
    state = info.get("state")
    valid_moves = info.get("opponent_valid_moves", [])
    roll = info.get("opponent_roll", 0)

    # Show the board so the player can see the current position
    if board is not None and state is not None:
        state_arr = (
            np.array(state, dtype=np.float32)
            if not isinstance(state, np.ndarray)
            else state
        )
        display(board, state_arr, show_roll=False)

    print("  ═══ YOUR TURN ═══")
    print(f"  Your roll: {roll}")

    if not valid_moves:
        print("  No valid moves — turn skipped.")
        return -1

    print("  Valid moves:")
    for i, move in enumerate(valid_moves):
        if move == -1:
            dest = _move_destination(P2_PATTERN, -1, roll)
            print(f"    [{i}] Place new piece{dest}")
        else:
            # Find movement counter from the board position
            dest = ""
            for mc_val, pos in enumerate(P2_PATTERN):
                if pos == move:
                    dest = _move_destination(P2_PATTERN, mc_val, roll)
                    break
            print(f"    [{i}] Move piece at square {move}{dest}")

    while True:
        try:
            choice = input(f"\n  Enter choice (0-{len(valid_moves)-1}): ").strip()
            idx = int(choice)
            if 0 <= idx < len(valid_moves):
                return valid_moves[idx]
            print(f"  Please enter 0-{len(valid_moves)-1}.")
        except (ValueError, EOFError):
            print("  Invalid input. Enter a number.")


# ── CLI ──────────────────────────────────────────────────────────────────

def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description="Play against a trained Game of Ur agent")
    p.add_argument(
        "--model",
        type=str,
        required=True,
        help="Path to saved MaskablePPO model (.zip)",
    )
    p.add_argument(
        "--seed",
        type=int,
        default=None,
        help="Random seed",
    )
    p.add_argument(
        "--csproj-dir",
        type=str,
        default=str(Path(__file__).resolve().parent.parent),
        help="Path to the C# project directory",
    )
    p.add_argument(
        "--deterministic",
        action="store_true",
        default=False,
        help="Use deterministic (greedy) policy for the AI (default: stochastic)",
    )
    return p.parse_args()


# ── Main ─────────────────────────────────────────────────────────────────

def main():
    args = parse_args()

    print("╔════════════════════════════════════════════╗")
    print("║    Royal Game of Ur — Play vs Trained AI   ║")
    print("╠════════════════════════════════════════════╣")
    print("║  You are Player 2.  The AI is Player 1.   ║")
    print("║  Score 7 pieces to win!                    ║")
    print("║                                            ║")
    print("║  Board key:  AI = AI piece   You = yours   ║")
    print("║              *  = rosette (double turn)    ║")
    print("╚════════════════════════════════════════════╝")
    print()
    print(f"  Loading model: {args.model}")

    model = MaskablePPO.load(args.model)

    env = UrEnv(
        csproj_dir=args.csproj_dir,
        seed=args.seed,
        opponent="external",
        opponent_callback=human_opponent_callback,
    )

    games_played = 0
    human_wins = 0
    ai_wins = 0

    try:
        while True:
            games_played += 1
            print(f"\n{'='*50}")
            print(f"  Game {games_played}")
            print(f"{'='*50}")

            obs, _ = env.reset()
            done = False

            while not done:
                # Show the board and current state
                board = state_to_board(obs)
                status = _read_status(obs)
                roll = status["roll"]
                display(board, obs)

                # AI's turn
                action_masks = get_action_masks(env)

                if not np.any(action_masks):
                    print("  (AI has no valid moves — skipping)")
                    obs, _ = env.reset()
                    continue

                action, _ = model.predict(
                    obs, deterministic=args.deterministic, action_masks=action_masks
                )
                action = int(action)

                print(f"  AI rolled {roll}: {describe_ai_action(action, roll, board, obs)}")

                obs, reward, done, truncated, info = env.step(action)
                done = done or truncated

                # Explain special events that happened during the step
                if not done:
                    if info.get("agent_extra_turn") == "rosette":
                        print("  ✦ AI landed on a rosette — gets another turn!")
                    if info.get("opponent_skipped_roll") is not None:
                        skip_roll = info["opponent_skipped_roll"]
                        print(
                            f"  (You rolled {skip_roll} — no valid moves."
                            f" Turn skipped.)"
                        )

            # Game over — show final board
            board = state_to_board(obs)
            display(board, obs, show_roll=False)

            if reward > 0:
                ai_wins += 1
                print("  ╔═══════════════════════════╗")
                print("  ║   AI WINS! Better luck    ║")
                print("  ║      next time.           ║")
                print("  ╚═══════════════════════════╝")
            else:
                human_wins += 1
                print("  ╔═══════════════════════════╗")
                print("  ║  YOU WIN! Congratulations! ║")
                print("  ╚═══════════════════════════╝")

            print(
                f"\n  Score: AI {ai_wins} — You {human_wins}"
                f" (of {games_played} games)"
            )

            again = input("\n  Play again? (y/n): ").strip().lower()
            if again != "y":
                break

    except KeyboardInterrupt:
        print("\n\n  Game interrupted.")
    finally:
        env.close()

    print(
        f"\n  Final score: AI {ai_wins} — You {human_wins}"
        f" (of {games_played} games)"
    )
    print("  Thanks for playing!")


if __name__ == "__main__":
    main()
