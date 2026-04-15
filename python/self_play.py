#!/usr/bin/env python3
"""
Self-play training for the Royal Game of Ur.

Iteratively trains agents against frozen copies of themselves:
  1. Load the best model so far (or start from scratch / a pre-trained model).
  2. Train a new model where the opponent is the frozen model.
  3. Evaluate the new model against the frozen model.
  4. If the new model is better, it becomes the new best model.
  5. Repeat.

Usage:
    python self_play.py
    python self_play.py --base-model ./models/ur_ppo_final.zip --iterations 5
    python self_play.py --total-timesteps 500000 --iterations 10
"""

import argparse
import random as pyrandom
import sys
from pathlib import Path

import numpy as np
from sb3_contrib import MaskablePPO
from sb3_contrib.common.maskable.utils import get_action_masks
from sb3_contrib.common.wrappers import ActionMasker
from stable_baselines3.common.callbacks import CheckpointCallback

sys.path.insert(0, str(Path(__file__).resolve().parent))

from callbacks import UrMetricsCallback
from ur_env import UrEnv


def make_opponent_callback(model: MaskablePPO, obs_holder: list):
    """Create a callback that uses a frozen model to pick opponent moves.

    The callback receives the bridge info dict (with ``opponent_valid_moves``
    and ``opponent_roll``) and returns a board-index action.

    ``obs_holder`` is a mutable single-element list that the training loop
    updates with the latest observation so the callback can use it.
    """

    def callback(info: dict) -> int:
        valid_moves = info.get("opponent_valid_moves", [])
        if not valid_moves:
            return -1
        if len(valid_moves) == 1:
            return valid_moves[0]

        if model is None:
            return pyrandom.choice(valid_moves)

        obs_arr = obs_holder[0] if obs_holder[0] is not None else None
        if obs_arr is None:
            return pyrandom.choice(valid_moves)

        # Use the model's policy on the current observation to get action probs.
        # The observation is from P1's perspective, but the model's learned
        # preferences (e.g., prefer scoring, captures, rosettes) still provide
        # useful signal for selecting among the opponent's valid moves.
        try:
            import torch
            obs_tensor = torch.as_tensor(obs_arr).float().unsqueeze(0)
            with torch.no_grad():
                dist = model.policy.get_distribution(obs_tensor)
                probs = dist.distribution.probs.squeeze().numpy()

            # Map model action preferences to valid board-index moves.
            # Action 7 = place from hand (-1 board index).
            # Actions 0-6 = move piece by logical index.
            move_weights = []
            for move in valid_moves:
                if move == -1:
                    # Place from hand → model action 7
                    move_weights.append(probs[7] if len(probs) > 7 else 1.0)
                else:
                    # On-board move → average of model actions 0-6
                    move_weights.append(float(np.mean(probs[:7])))

            weights = np.array(move_weights, dtype=np.float64)
            weights = np.maximum(weights, 1e-8)
            weights /= weights.sum()
            idx = np.random.choice(len(valid_moves), p=weights)
            return valid_moves[idx]

        except Exception:
            return pyrandom.choice(valid_moves)

    return callback


def make_self_play_env(
    csproj_dir: str,
    opponent_model: MaskablePPO | None,
    seed: int | None = None,
    obs_holder: list | None = None,
) -> ActionMasker:
    """Create an env where the opponent is a frozen model (or random if None).

    ``obs_holder`` is a mutable single-element list that the training loop
    updates with the latest observation, used by the opponent callback.
    """
    if opponent_model is not None:
        if obs_holder is None:
            obs_holder = [None]
        callback = make_opponent_callback(opponent_model, obs_holder)
        env = UrEnv(
            csproj_dir=csproj_dir,
            seed=seed,
            opponent="external",
            opponent_callback=callback,
        )
    else:
        env = UrEnv(csproj_dir=csproj_dir, seed=seed)

    def mask_fn(e):
        return e.action_masks()

    return ActionMasker(env, mask_fn)


def evaluate_model(
    model: MaskablePPO,
    csproj_dir: str,
    opponent: str = "random",
    n_episodes: int = 100,
    seed: int | None = None,
) -> float:
    """Evaluate model win rate against a given opponent type."""
    env = UrEnv(csproj_dir=csproj_dir, seed=seed, opponent=opponent)
    wins = 0
    for ep in range(n_episodes):
        obs, _ = env.reset()
        done = False
        ep_reward = 0.0
        while not done:
            action_masks = get_action_masks(env)
            action, _ = model.predict(obs, deterministic=True, action_masks=action_masks)
            obs, reward, done, truncated, info = env.step(int(action))
            ep_reward += reward
            done = done or truncated
        if ep_reward > 0:
            wins += 1
    env.close()
    return wins / n_episodes


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description="Self-play training for Game of Ur")
    p.add_argument(
        "--base-model",
        type=str,
        default=None,
        help="Path to a pre-trained model to start from (optional)",
    )
    p.add_argument(
        "--iterations",
        type=int,
        default=5,
        help="Number of self-play iterations (default: 5)",
    )
    p.add_argument(
        "--total-timesteps",
        type=int,
        default=500_000,
        help="Timesteps per iteration (default: 500,000)",
    )
    p.add_argument(
        "--eval-episodes",
        type=int,
        default=100,
        help="Evaluation episodes per iteration (default: 100)",
    )
    p.add_argument(
        "--seed",
        type=int,
        default=None,
        help="Random seed",
    )
    p.add_argument(
        "--save-dir",
        type=str,
        default="./models/self_play",
        help="Directory to save self-play models (default: ./models/self_play)",
    )
    p.add_argument(
        "--tb-log",
        type=str,
        default="./tb_logs/self_play",
        help="TensorBoard log directory (default: ./tb_logs/self_play)",
    )
    p.add_argument(
        "--csproj-dir",
        type=str,
        default=str(Path(__file__).resolve().parent.parent),
        help="Path to the C# project directory",
    )
    p.add_argument(
        "--learning-rate",
        type=float,
        default=3e-4,
        help="Learning rate (default: 3e-4)",
    )
    return p.parse_args()


def main():
    args = parse_args()
    save_dir = Path(args.save_dir)
    save_dir.mkdir(parents=True, exist_ok=True)

    print("=== Game of Ur — Self-Play Training ===")
    print(f"  Iterations      : {args.iterations}")
    print(f"  Steps/iteration : {args.total_timesteps:,}")
    print(f"  Eval episodes   : {args.eval_episodes}")
    print(f"  Base model      : {args.base_model or '(none — train from scratch)'}")
    print()

    # Load or initialize the best model
    best_model = None
    if args.base_model:
        best_model = MaskablePPO.load(args.base_model)
        win_rate = evaluate_model(
            best_model, args.csproj_dir, opponent="random",
            n_episodes=args.eval_episodes, seed=args.seed,
        )
        print(f"Base model win rate vs random: {win_rate:.2%}")
    else:
        print("No base model — iteration 0 will train against random opponent.")

    best_win_rate = 0.0

    for iteration in range(args.iterations):
        print(f"\n{'='*60}")
        print(f"  Self-Play Iteration {iteration + 1}/{args.iterations}")
        print(f"{'='*60}")

        # Create environment with frozen opponent
        env = make_self_play_env(
            csproj_dir=args.csproj_dir,
            opponent_model=best_model,
            seed=args.seed,
        )

        # Train new model
        model = MaskablePPO(
            "MlpPolicy",
            env,
            learning_rate=args.learning_rate,
            n_steps=2048,
            batch_size=64,
            n_epochs=10,
            gamma=0.99,
            verbose=1,
            tensorboard_log=args.tb_log,
            seed=args.seed,
        )

        metrics_cb = UrMetricsCallback(window_size=100, log_interval=50, verbose=1)

        print(f"  Training for {args.total_timesteps:,} steps...")
        model.learn(
            total_timesteps=args.total_timesteps,
            callback=[metrics_cb],
            progress_bar=True,
        )

        # Save iteration model
        iter_path = str(save_dir / f"self_play_iter_{iteration + 1}")
        model.save(iter_path)
        print(f"  Model saved to {iter_path}")

        # Evaluate against random
        win_rate_random = evaluate_model(
            model, args.csproj_dir, opponent="random",
            n_episodes=args.eval_episodes, seed=args.seed,
        )
        print(f"  Win rate vs random: {win_rate_random:.2%}")

        # Evaluate against greedy
        win_rate_greedy = evaluate_model(
            model, args.csproj_dir, opponent="greedy",
            n_episodes=args.eval_episodes, seed=args.seed,
        )
        print(f"  Win rate vs greedy: {win_rate_greedy:.2%}")

        # Use random win rate as the primary metric for promotion
        if win_rate_random > best_win_rate:
            best_win_rate = win_rate_random
            best_model = model
            best_path = str(save_dir / "self_play_best")
            model.save(best_path)
            print(f"  ★ New best model! Saved to {best_path}")
        else:
            print(f"  No improvement (best: {best_win_rate:.2%})")

        env.close()

    print(f"\n=== Self-Play Complete ===")
    print(f"  Best win rate vs random: {best_win_rate:.2%}")
    print(f"  Best model: {save_dir / 'self_play_best'}")


if __name__ == "__main__":
    main()
