#!/usr/bin/env python3
"""
Analyze a trained Game of Ur agent's strategy.

- Evaluates the model against random, greedy, and defensive opponents.
- Examines action preferences for key board scenarios.
- Compares findings to known Ur strategy research.

Usage:
    python analyze.py --model ./models/ur_ppo_final.zip
    python analyze.py --model ./models/self_play/self_play_best.zip --episodes 500
"""

import argparse
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))

try:
    import matplotlib

    matplotlib.use("Agg")
    import matplotlib.pyplot as plt

    HAS_MATPLOTLIB = True
except ImportError:
    HAS_MATPLOTLIB = False

from sb3_contrib import MaskablePPO
from sb3_contrib.common.maskable.utils import get_action_masks

from ur_env import UrEnv

# ── Known Ur Strategy Principles ──────────────────────────────────────────

KNOWN_STRATEGIES = """
Known Ur Strategy Principles (from academic research & expert play):

1. ROSETTE CONTROL: The center rosette (position 9) is the most valuable
   square — it grants a double turn and is safe from capture. Strong
   players prioritize landing on rosettes.

2. AGGRESSIVE CAPTURES: Capturing opponent pieces in the shared lane
   (positions 6-13) is highly valuable — it sends them back to hand,
   costing the opponent multiple turns of progress.

3. RACE EFFICIENCY: Scoring pieces quickly is paramount. Pieces near
   the end of the track should be advanced preferentially.

4. HAND MANAGEMENT: Introducing new pieces from hand at the right time
   balances board presence with vulnerability. Too many pieces on the
   board in the shared lane creates capture risk.

5. BLOCKING: Occupying key positions in the shared lane can impede
   opponent progress, especially the rosette at position 9.

6. RISK ASSESSMENT: Pieces in the shared lane (positions 6-13, except 9)
   are vulnerable to capture. Good strategy minimizes exposure time
   in the danger zone.
"""


def evaluate_vs_opponent(
    model: MaskablePPO,
    csproj_dir: str,
    opponent: str,
    n_episodes: int,
    seed: int | None = None,
) -> dict:
    """Evaluate model against a specific opponent type."""
    env = UrEnv(csproj_dir=csproj_dir, seed=seed, opponent=opponent)
    wins = 0
    total_rewards = []
    episode_lengths = []
    action_counts = np.zeros(8, dtype=np.int64)

    for ep in range(n_episodes):
        obs, _ = env.reset()
        done = False
        ep_reward = 0.0
        ep_length = 0

        while not done:
            masks = get_action_masks(env)
            action, _ = model.predict(obs, deterministic=True, action_masks=masks)
            obs, reward, done, truncated, info = env.step(int(action))
            ep_reward += reward
            ep_length += 1
            action_counts[int(action)] += 1
            done = done or truncated

        total_rewards.append(ep_reward)
        episode_lengths.append(ep_length)
        if ep_reward > 0:
            wins += 1

    env.close()

    total_actions = action_counts.sum()
    action_dist = action_counts / total_actions if total_actions > 0 else action_counts

    return {
        "opponent": opponent,
        "episodes": n_episodes,
        "wins": wins,
        "win_rate": wins / n_episodes,
        "avg_reward": float(np.mean(total_rewards)),
        "avg_length": float(np.mean(episode_lengths)),
        "action_dist": action_dist,
    }


def analyze_action_preferences(model: MaskablePPO, csproj_dir: str, seed: int | None = None):
    """Analyze what actions the model prefers in various game phases."""
    env = UrEnv(csproj_dir=csproj_dir, seed=seed)

    early_actions = np.zeros(8, dtype=np.int64)  # turns 1-10
    mid_actions = np.zeros(8, dtype=np.int64)  # turns 11-30
    late_actions = np.zeros(8, dtype=np.int64)  # turns 31+

    n_episodes = 200
    for _ in range(n_episodes):
        obs, _ = env.reset()
        done = False
        turn = 0

        while not done:
            turn += 1
            masks = get_action_masks(env)
            action, _ = model.predict(obs, deterministic=True, action_masks=masks)
            obs, reward, done, truncated, info = env.step(int(action))
            done = done or truncated

            a = int(action)
            if turn <= 10:
                early_actions[a] += 1
            elif turn <= 30:
                mid_actions[a] += 1
            else:
                late_actions[a] += 1

    env.close()

    def normalize(arr):
        s = arr.sum()
        return arr / s if s > 0 else arr

    return {
        "early": normalize(early_actions),
        "mid": normalize(mid_actions),
        "late": normalize(late_actions),
    }


def print_results(results: list[dict]):
    """Print evaluation results in a formatted table."""
    print("\n" + "=" * 65)
    print("  EVALUATION RESULTS")
    print("=" * 65)
    print(f"  {'Opponent':<12} {'Win Rate':>10} {'Avg Reward':>12} {'Avg Length':>12}")
    print("-" * 65)
    for r in results:
        print(
            f"  {r['opponent']:<12} {r['win_rate']:>9.1%} {r['avg_reward']:>12.3f} {r['avg_length']:>12.1f}"
        )
    print("=" * 65)


def print_action_preferences(phase_actions: dict):
    """Print action preferences by game phase."""
    action_labels = [f"move {i}" for i in range(7)] + ["place new"]

    print("\n" + "=" * 65)
    print("  ACTION PREFERENCES BY GAME PHASE")
    print("=" * 65)
    print(f"  {'Action':<12} {'Early':>8} {'Mid':>8} {'Late':>8}")
    print("-" * 65)
    for i in range(8):
        print(
            f"  {action_labels[i]:<12} {phase_actions['early'][i]:>7.1%} "
            f"{phase_actions['mid'][i]:>7.1%} {phase_actions['late'][i]:>7.1%}"
        )
    print("=" * 65)


def print_strategy_comparison(results: list[dict], phase_actions: dict):
    """Compare learned strategy to known Ur strategy principles."""
    print("\n" + "=" * 65)
    print("  STRATEGY ANALYSIS")
    print("=" * 65)
    print(KNOWN_STRATEGIES)

    print("Agent's Learned Behavior:")
    print("-" * 65)

    # Analyze place-new preference
    place_new_early = phase_actions["early"][7]
    place_new_late = phase_actions["late"][7]
    print(f"\n  1. HAND MANAGEMENT:")
    print(f"     Place-new in early game: {place_new_early:.1%}")
    print(f"     Place-new in late game:  {place_new_late:.1%}")
    if place_new_early > place_new_late:
        print("     → Agent introduces pieces early and focuses on advancing later. ✓")
    else:
        print("     → Agent is conservative about introducing new pieces.")

    # Analyze move preferences (higher piece indices = further along)
    advance_pref = sum(phase_actions["mid"][4:7])
    print(f"\n  2. RACE EFFICIENCY:")
    print(f"     Mid-game preference for advancing pieces 4-6: {advance_pref:.1%}")
    if advance_pref > 0.3:
        print("     → Agent prioritizes advancing pieces near the goal. ✓")
    else:
        print("     → Agent spreads moves across all pieces.")

    # Win rates comparison
    random_wr = next((r["win_rate"] for r in results if r["opponent"] == "random"), 0)
    greedy_wr = next((r["win_rate"] for r in results if r["opponent"] == "greedy"), 0)
    defensive_wr = next(
        (r["win_rate"] for r in results if r["opponent"] == "defensive"), 0
    )

    print(f"\n  3. OVERALL STRENGTH:")
    print(f"     vs Random    : {random_wr:.1%}")
    print(f"     vs Greedy    : {greedy_wr:.1%}")
    print(f"     vs Defensive : {defensive_wr:.1%}")

    if random_wr > 0.6:
        print("     → Agent has learned to beat random play consistently. ✓")
    if greedy_wr > 0.5:
        print("     → Agent outperforms greedy heuristic. ✓")
    if defensive_wr > 0.5:
        print("     → Agent outperforms defensive heuristic. ✓")

    print("=" * 65)


def save_plots(results: list[dict], phase_actions: dict, output_dir: Path):
    """Save analysis plots if matplotlib is available."""
    if not HAS_MATPLOTLIB:
        print("  (matplotlib not available — skipping plots)")
        return

    output_dir.mkdir(parents=True, exist_ok=True)

    # 1. Win rate bar chart
    fig, ax = plt.subplots(figsize=(8, 5))
    opponents = [r["opponent"] for r in results]
    win_rates = [r["win_rate"] for r in results]
    colors = ["#2ecc71" if wr > 0.5 else "#e74c3c" for wr in win_rates]
    bars = ax.bar(opponents, win_rates, color=colors, edgecolor="black", linewidth=0.5)
    ax.axhline(y=0.5, color="gray", linestyle="--", alpha=0.7, label="50% baseline")
    ax.set_ylabel("Win Rate")
    ax.set_title("Agent Win Rate vs Different Opponents")
    ax.set_ylim(0, 1)
    ax.legend()
    for bar, wr in zip(bars, win_rates):
        ax.text(
            bar.get_x() + bar.get_width() / 2,
            bar.get_height() + 0.02,
            f"{wr:.1%}",
            ha="center",
            fontsize=10,
        )
    plt.tight_layout()
    plt.savefig(str(output_dir / "win_rates.png"), dpi=150)
    plt.close()
    print(f"  Saved: {output_dir / 'win_rates.png'}")

    # 2. Action distribution by game phase
    action_labels = [f"move {i}" for i in range(7)] + ["place"]
    x = np.arange(len(action_labels))
    width = 0.25

    fig, ax = plt.subplots(figsize=(10, 5))
    ax.bar(x - width, phase_actions["early"], width, label="Early", color="#3498db")
    ax.bar(x, phase_actions["mid"], width, label="Mid", color="#e67e22")
    ax.bar(x + width, phase_actions["late"], width, label="Late", color="#9b59b6")
    ax.set_xlabel("Action")
    ax.set_ylabel("Frequency")
    ax.set_title("Action Distribution by Game Phase")
    ax.set_xticks(x)
    ax.set_xticklabels(action_labels, rotation=45)
    ax.legend()
    plt.tight_layout()
    plt.savefig(str(output_dir / "action_phases.png"), dpi=150)
    plt.close()
    print(f"  Saved: {output_dir / 'action_phases.png'}")

    # 3. Per-opponent action distribution
    if len(results) >= 1:
        fig, axes = plt.subplots(1, len(results), figsize=(5 * len(results), 5), sharey=True)
        if len(results) == 1:
            axes = [axes]
        for ax, r in zip(axes, results):
            ax.bar(action_labels, r["action_dist"], color="#1abc9c", edgecolor="black", linewidth=0.5)
            ax.set_title(f"vs {r['opponent']}\n(win: {r['win_rate']:.0%})")
            ax.tick_params(axis="x", rotation=45)
        axes[0].set_ylabel("Action Frequency")
        plt.suptitle("Action Distribution by Opponent", fontsize=14)
        plt.tight_layout()
        plt.savefig(str(output_dir / "action_by_opponent.png"), dpi=150)
        plt.close()
        print(f"  Saved: {output_dir / 'action_by_opponent.png'}")


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description="Analyze a trained Game of Ur agent")
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
        help="Evaluation episodes per opponent (default: 200)",
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
        "--output-dir",
        type=str,
        default="./analysis",
        help="Directory for analysis output / plots (default: ./analysis)",
    )
    return p.parse_args()


def main():
    args = parse_args()

    print("=== Game of Ur — Agent Analysis ===")
    print(f"  Model     : {args.model}")
    print(f"  Episodes  : {args.episodes}")
    print()

    model = MaskablePPO.load(args.model)

    # 1. Evaluate against each opponent type
    print("Evaluating against opponents...")
    results = []
    for opp in ["random", "greedy", "defensive"]:
        print(f"  vs {opp}...", end=" ", flush=True)
        r = evaluate_vs_opponent(
            model, args.csproj_dir, opp, args.episodes, seed=args.seed
        )
        results.append(r)
        print(f"done ({r['win_rate']:.1%} win rate)")

    # 2. Analyze action preferences by game phase
    print("\nAnalyzing action preferences by game phase...")
    phase_actions = analyze_action_preferences(model, args.csproj_dir, seed=args.seed)

    # 3. Print results
    print_results(results)
    print_action_preferences(phase_actions)
    print_strategy_comparison(results, phase_actions)

    # 4. Save plots
    output_dir = Path(args.output_dir)
    print(f"\nSaving analysis plots to {output_dir}/...")
    save_plots(results, phase_actions, output_dir)

    print("\nAnalysis complete.")


if __name__ == "__main__":
    main()
