# Game of Ur — Python RL Training

This directory contains the reinforcement learning training pipeline for the
Royal Game of Ur. It uses [Stable-Baselines3](https://stable-baselines3.readthedocs.io/)
with the [sb3-contrib](https://sb3-contrib.readthedocs.io/) `MaskablePPO` algorithm
to train an agent that learns to play as Player 1 against various opponents.

## Prerequisites

- **.NET 8 SDK** — required to build and run the C# game engine
- **Python 3.10+** — tested with 3.12

## Quick Start

```bash
# 1. Install Python dependencies
cd python
pip install -r requirements.txt

# 2. Train the agent (default: 1M steps vs random opponent)
python train.py

# 3. Monitor training in TensorBoard
tensorboard --logdir ./tb_logs

# 4. Evaluate the trained agent
python evaluate.py --model ./models/ur_ppo_final.zip --episodes 200

# 5. Evaluate against different opponents
python evaluate.py --model ./models/ur_ppo_final.zip --opponent greedy
python evaluate.py --model ./models/ur_ppo_final.zip --opponent defensive

# 6. Run self-play training (iterative improvement)
python self_play.py --base-model ./models/ur_ppo_final.zip --iterations 5

# 7. Analyze the agent's strategy
python analyze.py --model ./models/ur_ppo_final.zip

# 8. Play against the trained model interactively
python play.py --model ./models/ur_ppo_final.zip
```

## Architecture

```
python/
├── ur_env.py        # Gymnasium environment wrapper (IPC with C# bridge)
├── callbacks.py     # TensorBoard callback for win rate, game length, etc.
├── train.py         # MaskablePPO training script (vs random)
├── self_play.py     # Self-play training with iterative frozen opponents
├── evaluate.py      # Evaluate a saved model against various opponents
├── analyze.py       # Strategy analysis, visualization, and comparison
├── play.py          # Interactive human vs trained model play
├── requirements.txt # Python dependencies
└── README.md        # This file
```

### How It Works

1. **`ur_env.py`** — A Gymnasium-compatible environment that spawns the C#
   engine as a subprocess (`dotnet run -- --bridge`).  Communication uses
   newline-delimited JSON over stdin/stdout.  The environment exposes
   `action_masks()` for invalid-action masking. Supports multiple opponent
   types: `random`, `greedy`, `defensive`, and `external` (for self-play
   and human play).

2. **`train.py`** — Trains a `MaskablePPO` agent.  The agent receives a
   30-float observation vector (piece positions, roll, action mask, etc.)
   and selects from 8 discrete actions (move piece 0–6, or place new piece).
   Invalid actions are masked out so the agent never attempts illegal moves.

3. **`self_play.py`** — Iterative self-play training. Trains an agent against
   a frozen copy of the previous best model. Each iteration:
   - Train new model vs frozen opponent
   - Evaluate against random and greedy opponents
   - Promote to best model if improved

4. **`evaluate.py`** — Loads a trained model and plays evaluation games
   against a chosen opponent type (random, greedy, or defensive),
   reporting win rate and statistics.

5. **`analyze.py`** — Comprehensive strategy analysis:
   - Win rates against all opponent types
   - Action preferences by game phase (early/mid/late)
   - Comparison to known Ur strategy principles
   - Visualization plots (if matplotlib is installed)

6. **`play.py`** — Interactive terminal-based play against a trained model.
   The AI plays as Player 1 and the human plays as Player 2.

7. **`callbacks.py`** — Logs rolling metrics to TensorBoard every 50 episodes:
   win rate, loss rate, average game length, average reward, and per-action
   usage distribution.

## Opponent Types

The C# engine supports four opponent strategies for Player 2:

| Opponent     | Description                                                    |
|--------------|----------------------------------------------------------------|
| `random`     | Uniform random selection from valid moves                      |
| `greedy`     | Prioritizes: score > capture > rosette > advance furthest      |
| `defensive`  | Prioritizes: score > rosette > escape danger > place new       |
| `external`   | Opponent turns delegated to Python (for self-play & human play)|

## Training Parameters

| Parameter         | Default   | Description                          |
|-------------------|-----------|--------------------------------------|
| `--total-timesteps` | 1,000,000 | Total environment steps            |
| `--learning-rate`   | 3e-4      | PPO learning rate                  |
| `--n-steps`         | 2,048     | Steps per rollout buffer           |
| `--batch-size`      | 64        | Mini-batch size                    |
| `--n-epochs`        | 10        | PPO update epochs                  |
| `--gamma`           | 0.99      | Discount factor                    |
| `--checkpoint-freq`  | 50,000   | Save checkpoint every N steps      |
| `--seed`             | None     | Random seed for reproducibility    |
| `--opponent`         | random   | Opponent type for evaluation       |

## TensorBoard Metrics

After training, launch TensorBoard to view:

- **`ur/win_rate`** — rolling win rate over last 100 episodes
- **`ur/avg_game_length`** — mean steps per episode
- **`ur/avg_reward`** — mean reward per episode
- **`ur/action_dist/a0`–`a7`** — action usage distribution
- Standard SB3 metrics (policy loss, value loss, entropy, etc.)
