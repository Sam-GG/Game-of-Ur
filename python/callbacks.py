"""
Custom Stable-Baselines3 callback for logging Game of Ur training metrics
to TensorBoard: win rate, average game length, reward per episode, and
action distribution.
"""

from collections import deque
from typing import Optional

import numpy as np
from stable_baselines3.common.callbacks import BaseCallback


class UrMetricsCallback(BaseCallback):
    """Tracks per-episode metrics and logs them to TensorBoard.

    Metrics logged every ``log_interval`` episodes:
        - ``ur/win_rate``         – fraction of recent episodes won by the agent
        - ``ur/loss_rate``        – fraction of recent episodes lost
        - ``ur/avg_game_length``  – mean steps per episode
        - ``ur/avg_reward``       – mean total reward per episode
        - ``ur/action_dist/a{i}`` – fraction of times each action was chosen

    Parameters
    ----------
    window_size : int
        Number of recent episodes to keep for rolling statistics.
    log_interval : int
        Log metrics every this many episodes.
    verbose : int
        Verbosity level.
    """

    def __init__(
        self,
        window_size: int = 100,
        log_interval: int = 50,
        verbose: int = 0,
    ):
        super().__init__(verbose)
        self._window_size = window_size
        self._log_interval = log_interval

        # Rolling buffers
        self._wins: deque[int] = deque(maxlen=window_size)
        self._losses: deque[int] = deque(maxlen=window_size)
        self._episode_lengths: deque[int] = deque(maxlen=window_size)
        self._episode_rewards: deque[float] = deque(maxlen=window_size)
        self._action_counts = np.zeros(8, dtype=np.int64)

        # Per-episode accumulators
        self._current_ep_reward = 0.0
        self._current_ep_length = 0
        self._episodes_total = 0

    def _on_step(self) -> bool:
        # Accumulate per-step info
        actions = self.locals.get("actions")
        if actions is not None:
            for a in actions.flatten():
                if 0 <= a < 8:
                    self._action_counts[a] += 1

        rewards = self.locals.get("rewards")
        dones = self.locals.get("dones")

        if rewards is None or dones is None:
            return True

        # Support vectorised envs: iterate over each sub-env
        for i in range(len(dones)):
            self._current_ep_reward += float(rewards[i])
            self._current_ep_length += 1

            if dones[i]:
                self._episode_rewards.append(self._current_ep_reward)
                self._episode_lengths.append(self._current_ep_length)

                # Determine outcome from reward:
                #   +1 → win, -1 → loss, 0 → draw (shouldn't happen in Ur)
                if self._current_ep_reward > 0:
                    self._wins.append(1)
                    self._losses.append(0)
                else:
                    self._wins.append(0)
                    self._losses.append(1)

                self._current_ep_reward = 0.0
                self._current_ep_length = 0
                self._episodes_total += 1

                if self._episodes_total % self._log_interval == 0:
                    self._log_metrics()

        return True

    def _log_metrics(self):
        if len(self._episode_rewards) == 0:
            return

        win_rate = np.mean(self._wins) if self._wins else 0.0
        loss_rate = np.mean(self._losses) if self._losses else 0.0
        avg_length = np.mean(self._episode_lengths)
        avg_reward = np.mean(self._episode_rewards)

        self.logger.record("ur/win_rate", float(win_rate))
        self.logger.record("ur/loss_rate", float(loss_rate))
        self.logger.record("ur/avg_game_length", float(avg_length))
        self.logger.record("ur/avg_reward", float(avg_reward))
        self.logger.record("ur/episodes_total", self._episodes_total)

        # Action distribution (normalised)
        total_actions = self._action_counts.sum()
        if total_actions > 0:
            dist = self._action_counts / total_actions
            for i in range(8):
                self.logger.record(f"ur/action_dist/a{i}", float(dist[i]))

        if self.verbose >= 1:
            print(
                f"[UrMetrics] ep={self._episodes_total}  "
                f"win_rate={win_rate:.2%}  avg_len={avg_length:.1f}  "
                f"avg_reward={avg_reward:.3f}"
            )
