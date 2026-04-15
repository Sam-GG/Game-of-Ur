#!/usr/bin/env python3
"""
Play against a trained Game of Ur model interactively in the terminal.

The human plays as the opponent (Player 2) and the trained model plays
as Player 1 (the agent). The C# engine runs with ``--opponent external``
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

from ur_env import UrEnv

# Board layout for display
BOARD_TEMPLATE = r"""
    Player 2 lane        Player 2 exit
  ┌────┬────┬────┬────┐        ┌────┬────┐
  │ {14:4s}│ {15:4s}│ {16:4s}│ {17:4s}│        │ {18:4s}│ {19:4s}│
  ├────┼────┼────┼────┼────┬────┼────┼────┤
  │ {6:4s}│ {7:4s}│ {8:4s}│ {9:4s}│ {10:4s}│ {11:4s}│ {12:4s}│ {13:4s}│  ← shared lane
  ├────┼────┼────┼────┼────┴────┼────┼────┤
  │ {0:4s}│ {1:4s}│ {2:4s}│ {3:4s}│        │ {4:4s}│ {5:4s}│
  └────┴────┴────┴────┘        └────┴────┘
    Player 1 lane        Player 1 exit
"""

ROSETTES = {3, 4, 9, 17, 18}


def format_board(state_vec: np.ndarray) -> str:
    """Render the board from the state vector.

    Since the state vector encodes piece progress (not board positions),
    we reconstruct board occupancy. This is approximate — we show piece
    ownership per square.
    """
    # We can't perfectly reconstruct the board from the state vector alone,
    # so we'll just show a simplified board indicator.
    cells = {}
    for i in range(20):
        label = f"{i}"
        if i in ROSETTES:
            label = f"*{i}"
        cells[i] = label

    return BOARD_TEMPLATE.format(**cells)


def display_game_state(state_vec: np.ndarray):
    """Display the current game state to the terminal."""
    p1_hand = state_vec[14] * 7
    p1_goal = state_vec[15] * 7
    p2_hand = state_vec[16] * 7
    p2_goal = state_vec[17] * 7
    roll = state_vec[18] * 4

    print(format_board(state_vec))
    print(f"  Player 1 (AI) : {p1_goal:.0f} scored, {p1_hand:.0f} in hand")
    print(f"  Player 2 (You): {p2_goal:.0f} scored, {p2_hand:.0f} in hand")
    print(f"  Current roll  : {roll:.0f}")
    print()


def human_opponent_callback(info: dict) -> int:
    """Prompt the human for their move when it's the opponent's turn."""
    valid_moves = info.get("opponent_valid_moves", [])
    roll = info.get("opponent_roll", 0)

    print(f"\n  ═══ YOUR TURN (Player 2) ═══")
    print(f"  Your roll: {roll}")
    print(f"  Valid moves:")

    for i, move in enumerate(valid_moves):
        if move == -1:
            print(f"    [{i}] Place new piece from hand")
        else:
            print(f"    [{i}] Move piece at board position {move}")

    while True:
        try:
            choice = input(f"\n  Enter choice (0-{len(valid_moves)-1}): ").strip()
            idx = int(choice)
            if 0 <= idx < len(valid_moves):
                return valid_moves[idx]
            print(f"  Invalid choice. Enter 0-{len(valid_moves)-1}.")
        except (ValueError, EOFError):
            print("  Invalid input. Enter a number.")


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


def main():
    args = parse_args()

    print("╔════════════════════════════════════════════╗")
    print("║    Royal Game of Ur — Play vs Trained AI   ║")
    print("╠════════════════════════════════════════════╣")
    print("║  You are Player 2.  The AI is Player 1.   ║")
    print("║  Score 7 pieces to win!                    ║")
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
            turn = 0

            while not done:
                turn += 1
                display_game_state(obs)

                # AI's turn
                action_masks = get_action_masks(env)
                action, _ = model.predict(
                    obs, deterministic=args.deterministic, action_masks=action_masks
                )
                action = int(action)

                action_name = f"move piece (index {action})" if action < 7 else "place new piece"
                print(f"  AI plays: action {action} ({action_name})")

                obs, reward, done, truncated, info = env.step(action)
                done = done or truncated

            # Game over
            if reward > 0:
                ai_wins += 1
                print("\n  ╔═══════════════════════════╗")
                print("  ║   AI WINS! Better luck    ║")
                print("  ║      next time.           ║")
                print("  ╚═══════════════════════════╝")
            else:
                human_wins += 1
                print("\n  ╔═══════════════════════════╗")
                print("  ║  YOU WIN! Congratulations! ║")
                print("  ╚═══════════════════════════╝")

            print(f"\n  Score: AI {ai_wins} — You {human_wins} (of {games_played} games)")

            again = input("\n  Play again? (y/n): ").strip().lower()
            if again != "y":
                break

    except KeyboardInterrupt:
        print("\n\n  Game interrupted.")
    finally:
        env.close()

    print(f"\n  Final score: AI {ai_wins} — You {human_wins} (of {games_played} games)")
    print("  Thanks for playing!")


if __name__ == "__main__":
    main()
