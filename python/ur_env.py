"""
Gymnasium environment wrapper for the Game of Ur C# engine.

Communicates with the C# GameEnvironment via stdin/stdout JSON-line IPC.
The C# bridge is started as a subprocess with ``dotnet run -- --bridge``.

Supports multiple opponent types:
  - ``random``     — uniform random moves (default)
  - ``greedy``     — heuristic: score > capture > rosette > advance furthest
  - ``defensive``  — heuristic: score > rosette > escape danger > place new
  - ``external``   — opponent actions delegated to a Python callback
"""

import json
import subprocess
import sys
from pathlib import Path
from typing import Any, Callable, Optional

import gymnasium as gym
import numpy as np
from gymnasium import spaces

# Default path to the C# project (one level up from this file)
_DEFAULT_CSPROJ_DIR = str(Path(__file__).resolve().parent.parent)

STATE_SIZE = 30
ACTION_COUNT = 8

# Movement patterns (must match Player.cs)
P1_PATTERN = [0, 1, 2, 3, 6, 7, 8, 9, 10, 11, 12, 13, 5, 4]
P2_PATTERN = [14, 15, 16, 17, 6, 7, 8, 9, 10, 11, 12, 13, 19, 18]


def state_to_board(state_vec) -> list[int]:
    """Reconstruct the 20-cell board array from the 30-element state vector.

    The state vector encodes each piece's progress as
    ``(movementCounter + 1) / 15``:

    * ``state_vec[0..6]``  — Player 1's 7 pieces (0.0 = in hand, 1.0 = scored)
    * ``state_vec[7..13]`` — Player 2's 7 pieces

    We reverse the encoding to ``movementCounter = round(val * 15) - 1``
    and map on-board counters (0–13) to board positions via the
    movement-pattern arrays.

    Returns a list of 20 ints: 0 = empty, 1 = Player 1, 2 = Player 2.
    """
    board = [0] * 20
    for i in range(7):
        # Player 1 pieces: state[0..6]
        mc = round(state_vec[i] * 15) - 1
        if 0 <= mc <= 13:
            board[P1_PATTERN[mc]] = 1

        # Player 2 pieces: state[7..13]
        mc = round(state_vec[7 + i] * 15) - 1
        if 0 <= mc <= 13:
            board[P2_PATTERN[mc]] = 2

    return board


class UrEnv(gym.Env):
    """Gymnasium environment for the Royal Game of Ur.

    The agent controls Player 1.  Player 2's strategy is set by
    ``opponent`` (default: ``"random"``).

    When ``opponent="external"``, the caller must provide an
    ``opponent_callback`` that receives the bridge response dict
    (containing ``opponent_valid_moves`` and ``opponent_roll``) and
    returns a board-index action for the opponent.

    Observation space:
        Box(0, 1, shape=(30,), dtype=float32)
        See ARCHITECTURE.md for the full state vector layout.

    Action space:
        Discrete(8) with invalid-action masking.
        Actions 0-6 move a piece by logical index; action 7 places a new piece.
    """

    metadata = {"render_modes": []}

    def __init__(
        self,
        csproj_dir: Optional[str] = None,
        seed: Optional[int] = None,
        dotnet_exe: str = "dotnet",
        opponent: str = "random",
        opponent_callback: Optional[Callable[[dict], int]] = None,
    ):
        super().__init__()
        self._csproj_dir = csproj_dir or _DEFAULT_CSPROJ_DIR
        self._seed = seed
        self._dotnet_exe = dotnet_exe
        self._opponent = opponent
        self._opponent_callback = opponent_callback
        self._process: Optional[subprocess.Popen] = None

        self.observation_space = spaces.Box(
            low=0.0, high=1.0, shape=(STATE_SIZE,), dtype=np.float32
        )
        self.action_space = spaces.Discrete(ACTION_COUNT)

        # Cache for the latest valid-action mask (bool array length 8).
        self._valid_action_mask = np.zeros(ACTION_COUNT, dtype=np.bool_)

    # ── Gym API ────────────────────────────────────────────────────────────

    def reset(
        self, *, seed: Optional[int] = None, options: Optional[dict] = None
    ) -> tuple[np.ndarray, dict]:
        super().reset(seed=seed)

        # (Re)start the C# bridge process for each episode to guarantee
        # a clean game state and avoid any long-running process issues.
        self._start_bridge()

        response = self._send({"method": "reset"})
        obs = np.array(response["state"], dtype=np.float32)
        self._update_mask(response)
        return obs, {}

    def step(self, action: int) -> tuple[np.ndarray, float, bool, bool, dict]:
        response = self._send({"method": "step", "action": int(action)})

        # Save display-relevant info before opponent handling may replace
        # the response (opponent_step returns a fresh info dict).
        initial_info = dict(response.get("info", {}))

        # Handle external opponent turns transparently
        response = self._handle_opponent_turns(response)

        obs = np.array(response["state"], dtype=np.float32)
        reward = float(response["reward"])
        done = bool(response["done"])
        self._update_mask(response)
        info = response.get("info", {})

        # Preserve display-relevant fields from the initial step response
        # that may have been lost when _handle_opponent_turns replaced it.
        for key in ("agent_extra_turn", "opponent_skipped_roll"):
            if key in initial_info and key not in info:
                info[key] = initial_info[key]

        return obs, reward, done, False, info

    def action_masks(self) -> np.ndarray:
        """Return the current valid-action mask (required by MaskablePPO)."""
        return self._valid_action_mask.copy()

    def close(self):
        self._stop_bridge()

    # ── External opponent handling ─────────────────────────────────────────

    def _handle_opponent_turns(self, response: dict) -> dict:
        """If the response signals an opponent turn (external mode), call the
        opponent callback and loop until control returns to the agent."""
        while (
            not response.get("done", False)
            and response.get("info", {}).get("opponent_turn")
        ):
            if self._opponent_callback is None:
                raise RuntimeError(
                    "opponent='external' requires an opponent_callback"
                )

            # Enrich callback info with the reconstructed board so that
            # interactive callers (e.g. play.py) can display it.
            cb_info = dict(response.get("info", {}))
            state_arr = np.array(response["state"], dtype=np.float32)
            cb_info["board"] = state_to_board(state_arr)
            cb_info["state"] = state_arr

            opp_action = self._opponent_callback(cb_info)
            response = self._send(
                {"method": "opponent_step", "action": int(opp_action)}
            )
        return response

    # ── IPC helpers ────────────────────────────────────────────────────────

    def _start_bridge(self):
        """Start (or restart) the C# bridge subprocess."""
        self._stop_bridge()

        cmd = [self._dotnet_exe, "run", "--project", self._csproj_dir, "--", "--bridge"]
        if self._seed is not None:
            cmd += ["--seed", str(self._seed)]
        cmd += ["--opponent", self._opponent]

        self._process = subprocess.Popen(
            cmd,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
            bufsize=1,  # line-buffered
        )

    def _stop_bridge(self):
        if self._process is not None:
            try:
                self._process.stdin.close()
            except Exception:
                pass
            try:
                self._process.terminate()
                self._process.wait(timeout=5)
            except Exception:
                self._process.kill()
                self._process.wait(timeout=5)
            self._process = None

    def _send(self, request: dict) -> dict:
        """Send a JSON-line request and read the JSON-line response."""
        if self._process is None or self._process.poll() is not None:
            raise RuntimeError("Bridge process is not running")

        line = json.dumps(request) + "\n"
        self._process.stdin.write(line)
        self._process.stdin.flush()

        response_line = self._process.stdout.readline()
        if not response_line:
            raise RuntimeError("Bridge process closed unexpectedly")

        return json.loads(response_line)

    def _update_mask(self, response: dict):
        va = response.get("valid_actions")
        if va is not None:
            self._valid_action_mask = np.array(va, dtype=np.bool_)
        else:
            self._valid_action_mask = np.zeros(ACTION_COUNT, dtype=np.bool_)
