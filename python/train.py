#!/usr/bin/env python3
"""
Train a MaskablePPO agent on the Royal Game of Ur.

Usage:
    python train.py                          # default 1M steps
    python train.py --total-timesteps 500000
    python train.py --seed 42 --tb-log ./tb_logs

The agent learns to play as Player 1 against a random opponent
implemented in the C# engine.  Invalid-action masking (via sb3-contrib
MaskablePPO) prevents the agent from selecting illegal moves.

Logs are written to TensorBoard under ``--tb-log`` (default: ``./tb_logs``).
Trained models are saved to ``--save-dir`` (default: ``./models``).
"""

import argparse
import sys
from pathlib import Path

import numpy as np
from sb3_contrib import MaskablePPO
from sb3_contrib.common.wrappers import ActionMasker
from stable_baselines3.common.callbacks import CheckpointCallback

# Ensure the python package is importable
sys.path.insert(0, str(Path(__file__).resolve().parent))

from callbacks import UrMetricsCallback
from ur_env import UrEnv


def mask_fn(env: UrEnv) -> np.ndarray:
    """Return the current valid-action mask for MaskablePPO."""
    return env.action_masks()


def make_env(csproj_dir: str, seed: int | None = None) -> ActionMasker:
    """Create a masked Game-of-Ur environment."""
    env = UrEnv(csproj_dir=csproj_dir, seed=seed)
    return ActionMasker(env, mask_fn)


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description="Train MaskablePPO on Game of Ur")
    p.add_argument(
        "--total-timesteps",
        type=int,
        default=1_000_000,
        help="Total training timesteps (default: 1_000_000)",
    )
    p.add_argument(
        "--seed",
        type=int,
        default=None,
        help="Random seed for reproducibility",
    )
    p.add_argument(
        "--tb-log",
        type=str,
        default="./tb_logs",
        help="TensorBoard log directory (default: ./tb_logs)",
    )
    p.add_argument(
        "--save-dir",
        type=str,
        default="./models",
        help="Directory to save trained models (default: ./models)",
    )
    p.add_argument(
        "--csproj-dir",
        type=str,
        default=str(Path(__file__).resolve().parent.parent),
        help="Path to the C# project directory containing Ur.csproj",
    )
    p.add_argument(
        "--checkpoint-freq",
        type=int,
        default=50_000,
        help="Save a checkpoint every N timesteps (default: 50_000)",
    )
    p.add_argument(
        "--learning-rate",
        type=float,
        default=3e-4,
        help="Learning rate for PPO (default: 3e-4)",
    )
    p.add_argument(
        "--n-steps",
        type=int,
        default=2048,
        help="Steps per rollout (default: 2048)",
    )
    p.add_argument(
        "--batch-size",
        type=int,
        default=64,
        help="Mini-batch size (default: 64)",
    )
    p.add_argument(
        "--n-epochs",
        type=int,
        default=10,
        help="PPO epochs per update (default: 10)",
    )
    p.add_argument(
        "--gamma",
        type=float,
        default=0.99,
        help="Discount factor (default: 0.99)",
    )
    return p.parse_args()


def main():
    args = parse_args()

    save_dir = Path(args.save_dir)
    save_dir.mkdir(parents=True, exist_ok=True)

    print(f"=== Game of Ur — MaskablePPO Training ===")
    print(f"  Total timesteps : {args.total_timesteps:,}")
    print(f"  Seed            : {args.seed}")
    print(f"  TensorBoard log : {args.tb_log}")
    print(f"  Model save dir  : {args.save_dir}")
    print(f"  C# project dir  : {args.csproj_dir}")
    print()

    env = make_env(csproj_dir=args.csproj_dir, seed=args.seed)

    model = MaskablePPO(
        "MlpPolicy",
        env,
        learning_rate=args.learning_rate,
        n_steps=args.n_steps,
        batch_size=args.batch_size,
        n_epochs=args.n_epochs,
        gamma=args.gamma,
        verbose=1,
        tensorboard_log=args.tb_log,
        seed=args.seed,
    )

    # Callbacks
    checkpoint_cb = CheckpointCallback(
        save_freq=args.checkpoint_freq,
        save_path=str(save_dir / "checkpoints"),
        name_prefix="ur_ppo",
    )
    metrics_cb = UrMetricsCallback(
        window_size=100,
        log_interval=50,
        verbose=1,
    )

    print("Starting training...")
    model.learn(
        total_timesteps=args.total_timesteps,
        callback=[checkpoint_cb, metrics_cb],
        progress_bar=True,
    )

    final_path = str(save_dir / "ur_ppo_final")
    model.save(final_path)
    print(f"\nTraining complete. Model saved to {final_path}")


if __name__ == "__main__":
    main()
