#!/usr/bin/env python3
"""
Evaluate a trained MaskablePPO agent on the Royal Game of Ur.

Usage:
    python evaluate.py --model ./models/ur_ppo_final.zip
    python evaluate.py --model ./models/ur_ppo_final.zip --episodes 500 --seed 0
    python evaluate.py --model ./models/ur_ppo_final.zip --opponent greedy
    python evaluate.py --model ./models/ur_ppo_final.zip --opponent defensive --deterministic

Reports win rate, loss rate, average game length, and average reward
over the requested number of evaluation episodes.
"""

import argparse
import sys
from pathlib import Path

import numpy as np
from sb3_contrib import MaskablePPO
from sb3_contrib.common.maskable.utils import get_action_masks

sys.path.insert(0, str(Path(__file__).resolve().parent))

from ur_env import UrEnv


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description="Evaluate a trained Game-of-Ur agent")
    p.add_argument(
        "--model",
        type=str,
        required=True,
        help="Path to saved MaskablePPO model (.zip)",
    )
    p.add_argument(
        "--episodes",
        type=int,
        default=200,
        help="Number of evaluation episodes (default: 200)",
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
        help="Path to the C# project directory containing Ur.csproj",
    )
    p.add_argument(
        "--deterministic",
        action="store_true",
        default=False,
        help="Use deterministic actions (greedy policy)",
    )
    p.add_argument(
        "--opponent",
        type=str,
        default="random",
        choices=["random", "greedy", "defensive"],
        help="Opponent strategy: random (default), greedy, or defensive",
    )
    return p.parse_args()


def evaluate(
    model: MaskablePPO,
    env: UrEnv,
    n_episodes: int,
    deterministic: bool = False,
) -> dict:
    """Run evaluation episodes and collect statistics."""
    wins = 0
    losses = 0
    total_rewards = []
    episode_lengths = []
    action_counts = np.zeros(8, dtype=np.int64)

    for ep in range(n_episodes):
        obs, _ = env.reset()
        done = False
        ep_reward = 0.0
        ep_length = 0

        while not done:
            action_masks = get_action_masks(env)
            action, _ = model.predict(
                obs, deterministic=deterministic, action_masks=action_masks
            )
            obs, reward, done, truncated, info = env.step(int(action))
            ep_reward += reward
            ep_length += 1
            action_counts[int(action)] += 1
            done = done or truncated

        total_rewards.append(ep_reward)
        episode_lengths.append(ep_length)

        if ep_reward > 0:
            wins += 1
        else:
            losses += 1

        if (ep + 1) % 50 == 0:
            print(f"  Episode {ep + 1}/{n_episodes} — running win rate: {wins / (ep + 1):.2%}")

    total = n_episodes
    win_rate = wins / total
    loss_rate = losses / total
    avg_reward = float(np.mean(total_rewards))
    avg_length = float(np.mean(episode_lengths))

    # Action distribution
    total_actions = action_counts.sum()
    action_dist = action_counts / total_actions if total_actions > 0 else action_counts

    return {
        "episodes": total,
        "wins": wins,
        "losses": losses,
        "win_rate": win_rate,
        "loss_rate": loss_rate,
        "avg_reward": avg_reward,
        "avg_game_length": avg_length,
        "action_distribution": {f"a{i}": float(action_dist[i]) for i in range(8)},
    }


def main():
    args = parse_args()

    print(f"=== Game of Ur — Agent Evaluation ===")
    print(f"  Model           : {args.model}")
    print(f"  Episodes        : {args.episodes}")
    print(f"  Deterministic   : {args.deterministic}")
    print(f"  Opponent        : {args.opponent}")
    print(f"  C# project dir  : {args.csproj_dir}")
    print()

    env = UrEnv(csproj_dir=args.csproj_dir, seed=args.seed, opponent=args.opponent)
    model = MaskablePPO.load(args.model)

    results = evaluate(
        model, env, n_episodes=args.episodes, deterministic=args.deterministic
    )
    env.close()

    print()
    print("=== Results ===")
    print(f"  Episodes        : {results['episodes']}")
    print(f"  Wins            : {results['wins']}")
    print(f"  Losses          : {results['losses']}")
    print(f"  Win Rate        : {results['win_rate']:.2%}")
    print(f"  Loss Rate       : {results['loss_rate']:.2%}")
    print(f"  Avg Reward      : {results['avg_reward']:.3f}")
    print(f"  Avg Game Length : {results['avg_game_length']:.1f} steps")
    print()
    print("  Action Distribution:")
    for action, pct in results["action_distribution"].items():
        label = f"move piece {action[1:]}" if action != "a7" else "place new"
        print(f"    {action} ({label:14s}): {pct:.2%}")


if __name__ == "__main__":
    main()
