"""
Gymnasium environment wrapper for the Game of Ur C# engine.

Communicates with the C# GameEnvironment via stdin/stdout JSON-line IPC.
The C# bridge is started as a subprocess with ``dotnet run -- --bridge``.
"""

import json
import subprocess
import sys
from pathlib import Path
from typing import Any, Optional

import gymnasium as gym
import numpy as np
from gymnasium import spaces

# Default path to the C# project (one level up from this file)
_DEFAULT_CSPROJ_DIR = str(Path(__file__).resolve().parent.parent)

STATE_SIZE = 30
ACTION_COUNT = 8


class UrEnv(gym.Env):
    """Gymnasium environment for the Royal Game of Ur.

    The agent controls Player 1.  Player 2 is a random opponent
    implemented inside the C# engine.

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
    ):
        super().__init__()
        self._csproj_dir = csproj_dir or _DEFAULT_CSPROJ_DIR
        self._seed = seed
        self._dotnet_exe = dotnet_exe
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
        obs = np.array(response["state"], dtype=np.float32)
        reward = float(response["reward"])
        done = bool(response["done"])
        self._update_mask(response)
        info = response.get("info", {})
        return obs, reward, done, False, info

    def action_masks(self) -> np.ndarray:
        """Return the current valid-action mask (required by MaskablePPO)."""
        return self._valid_action_mask.copy()

    def close(self):
        self._stop_bridge()

    # ── IPC helpers ────────────────────────────────────────────────────────

    def _start_bridge(self):
        """Start (or restart) the C# bridge subprocess."""
        self._stop_bridge()

        cmd = [self._dotnet_exe, "run", "--project", self._csproj_dir, "--", "--bridge"]
        if self._seed is not None:
            cmd += ["--seed", str(self._seed)]

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
