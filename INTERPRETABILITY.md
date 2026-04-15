# Game of Ur — Model Interpretability Guide

A reference for peering into the trained MaskablePPO agent to surface learned strategies and heuristics.
Each technique is ordered from cheapest/easiest to most involved.

---

## State Vector Quick Reference

All techniques below operate on the 30-float normalized state vector defined in `ARCHITECTURE.md`.

| Index  | Meaning                          | Notes                              |
|--------|----------------------------------|------------------------------------|
| 0–6    | P1 piece progress                | `(movementCounter + 1) / 15`       |
| 7–13   | P2 piece progress                |                                    |
| 14     | P1 pieces in hand / 7            |                                    |
| 15     | P1 pieces in goal / 7            |                                    |
| 16     | P2 pieces in hand / 7            |                                    |
| 17     | P2 pieces in goal / 7            |                                    |
| 18     | Current roll / 4                 |                                    |
| 19–26  | Action mask                      | 1 = valid, 0 = invalid             |
| 27     | Has double turn flag             |                                    |
| 28–29  | Reserved                         |                                    |

Rosette board indices (for probe construction): `3, 4, 9, 17, 18`. Index `9` is the safe rosette (no captures).

---

## Technique 1 — Action Distribution Analysis

**What it tells you:** The agent's dominant behavioral tendencies across thousands of positions.
**Cost:** Very low — just rollout collection and aggregation.

### Approach

Collect `(state, action_probs)` pairs from many episodes and aggregate by action index.

```python
import numpy as np
from sb3_contrib import MaskablePPO

def get_action_probs(model, obs):
    obs_tensor = model.policy.obs_to_tensor(obs)[0]
    dist = model.policy.get_distribution(obs_tensor)
    return dist.distribution.probs.detach().numpy().flatten()

def collect_action_distributions(model, env, n_episodes=500):
    all_probs = []
    for _ in range(n_episodes):
        obs, _ = env.reset()
        done = False
        while not done:
            probs = get_action_probs(model, obs)
            all_probs.append(probs)
            action = np.argmax(probs * env.action_masks())
            obs, _, done, _, _ = env.step(action)
    return np.array(all_probs)  # shape: (n_steps, 8)
```

### What to look for

- Does action 7 (place from hand) dominate early game? Does it fall off in mid/late game?
- Is there a strong preference for advancing the furthest piece (high logical index) vs. the safest?
- Are certain actions almost never chosen even when valid? That may signal a learned avoidance heuristic.

---

## Technique 2 — Value Function Landscape

**What it tells you:** How the agent assesses board positions — its internal sense of "who is winning."
**Cost:** Low — single forward passes, no gradients needed.

### Approach

Query `predict_values()` while sweeping two state dimensions and holding everything else fixed.

```python
import torch
import matplotlib.pyplot as plt
import numpy as np

def get_value(model, obs):
    obs_tensor = model.policy.obs_to_tensor(obs)[0]
    return model.policy.predict_values(obs_tensor).item()

def value_heatmap(model, base_state, dim_x, dim_y, resolution=20):
    """
    Sweep two state dimensions and plot the value function.
    dim_x, dim_y: indices into the 30-float state vector.
    """
    xs = np.linspace(0, 1, resolution)
    ys = np.linspace(0, 1, resolution)
    grid = np.zeros((resolution, resolution))

    for i, x in enumerate(xs):
        for j, y in enumerate(ys):
            state = base_state.copy()
            state[dim_x] = x
            state[dim_y] = y
            grid[j, i] = get_value(model, state[np.newaxis])

    plt.figure(figsize=(7, 6))
    plt.imshow(grid, origin='lower', aspect='auto',
               extent=[0, 1, 0, 1], cmap='RdYlGn')
    plt.colorbar(label='V(s)')
    plt.xlabel(f'state[{dim_x}]')
    plt.ylabel(f'state[{dim_y}]')
    plt.title('Value Function Landscape')
    plt.tight_layout()
    plt.show()

# Example: P1 goals scored (idx 15) vs P2 goals scored (idx 17)
value_heatmap(model, base_state, dim_x=15, dim_y=17)
```

### Suggested sweeps

| dim_x | dim_y | Question being asked |
|-------|-------|----------------------|
| 15 (P1 goals) | 17 (P2 goals) | How sharp is the endgame value cliff? |
| 0 (P1 lead piece) | 7 (P2 lead piece) | Does the agent value the race or react to the opponent? |
| 18 (roll) | 15 (P1 goals) | Does a high roll matter more in certain game stages? |

---

## Technique 3 — Saliency / Input Gradients

**What it tells you:** Which state features the agent is most sensitive to when making a decision.
**Cost:** Low — one backward pass per state.

### Approach

Compute the gradient of the chosen action's log-probability with respect to the input.

```python
import torch
import numpy as np

def saliency(model, obs, action):
    obs_tensor = model.policy.obs_to_tensor(obs)[0].requires_grad_(True)
    dist = model.policy.get_distribution(obs_tensor)
    log_prob = dist.log_prob(torch.tensor([action], dtype=torch.long))
    log_prob.backward()
    return obs_tensor.grad.abs().detach().numpy().flatten()

def mean_saliency_over_rollout(model, env, n_episodes=200):
    saliencies = []
    for _ in range(n_episodes):
        obs, _ = env.reset()
        done = False
        while not done:
            action, _ = model.predict(obs, deterministic=True,
                                      action_masks=env.action_masks())
            sal = saliency(model, obs[np.newaxis], int(action))
            saliencies.append(sal)
            obs, _, done, _, _ = env.step(action)
    return np.array(saliencies).mean(axis=0)  # shape: (30,)
```

### Interpreting results

Map the 30-element mean saliency back to feature names and plot a bar chart:

```python
feature_names = (
    [f'P1_piece_{i}' for i in range(7)] +
    [f'P2_piece_{i}' for i in range(7)] +
    ['P1_in_hand', 'P1_in_goal', 'P2_in_hand', 'P2_in_goal', 'roll'] +
    [f'action_mask_{i}' for i in range(8)] +
    ['has_double', 'reserved_0', 'reserved_1']
)

plt.figure(figsize=(14, 4))
plt.bar(feature_names, mean_sal)
plt.xticks(rotation=90)
plt.title('Mean Saliency Across Rollout')
plt.tight_layout()
plt.show()
```

### What to look for

- If `P2_piece_*` features are consistently high → agent is actively tracking threats.
- If `roll` dominates → agent is mostly reactive to dice, not position.
- If `P1_in_goal` / `P2_in_goal` spike → agent is endgame-aware.

---

## Technique 4 — Targeted Probes (Ur-Specific Hypotheses)

**What it tells you:** Whether the agent has internalized specific Ur strategies.
**Cost:** Low — construct synthetic states and query the policy directly.

### Probe A — Rosette Landing Preference

Does the agent prefer a move that lands on a rosette over an equivalent non-rosette move?

```python
def probe_rosette_preference(model, base_state, rosette_action, alt_action):
    """
    Vary P2's goal progress and measure the agent's preference for a rosette-landing
    action over an alternative. Positive result = rosette preferred.
    """
    deltas = []
    for p2_goal in range(7):
        state = base_state.copy()
        state[17] = p2_goal / 7.0
        probs = get_action_probs(model, state[np.newaxis])
        deltas.append(probs[rosette_action] - probs[alt_action])
    return deltas
```

### Probe B — Capture Sensitivity

Does the agent's probability of an aggressive (capture) move increase when a capture is available?

```python
def probe_capture_urgency(model, base_state, capture_action, safe_action, opponent_progress_idx):
    """
    Sweep an opponent piece's progress and measure shift toward the capture action.
    opponent_progress_idx: which of state[7–13] tracks the piece in question.
    """
    results = []
    for progress in np.linspace(0, 1, 15):
        state = base_state.copy()
        state[opponent_progress_idx] = progress
        probs = get_action_probs(model, state[np.newaxis])
        results.append(probs[capture_action] - probs[safe_action])
    return results
```

### Probe C — Endgame Urgency (Race vs. Aggression)

Does the agent shift from aggressive to pure-racing behavior as either player approaches 7 goals?

```python
def probe_endgame_shift(model, base_state, race_action, aggressive_action):
    results = {}
    for p2_goal in range(7):
        row = []
        for p1_goal in range(7):
            state = base_state.copy()
            state[15] = p1_goal / 7.0
            state[17] = p2_goal / 7.0
            probs = get_action_probs(model, state[np.newaxis])
            row.append(probs[race_action] - probs[aggressive_action])
        results[p2_goal] = row
    return results  # plot as heatmap: x=P1 goals, y=P2 goals
```

### Probe D — Safe Square Valuation (Index 9)

Does the agent treat the safe center rosette as a destination preference, even when it is not the furthest advance?

Construct two states that are identical except in one the available move lands on index 9, and in the other it advances further. Compare action probabilities directly.

---

## Technique 5 — Counterfactual Decision Boundary Search

**What it tells you:** Crisp, human-readable rules — "the agent switches decision when feature X crosses threshold T."
**Cost:** Medium — binary search over each feature dimension per decision pair.

### Approach

```python
def find_decision_boundary(model, base_state, action_a, action_b, feature_idx,
                           lo=0.0, hi=1.0, n_iter=20):
    """
    Binary search for the value of state[feature_idx] at which the agent's
    preferred action switches from action_a to action_b.
    Returns the threshold value, or None if no switch occurs in [lo, hi].
    """
    state = base_state.copy()

    state[feature_idx] = lo
    probs_lo = get_action_probs(model, state[np.newaxis])
    if np.argmax(probs_lo) != action_a:
        return None  # action_a not preferred at lo end

    state[feature_idx] = hi
    probs_hi = get_action_probs(model, state[np.newaxis])
    if np.argmax(probs_hi) != action_b:
        return None  # action_b not preferred at hi end

    for _ in range(n_iter):
        mid = (lo + hi) / 2
        state[feature_idx] = mid
        probs = get_action_probs(model, state[np.newaxis])
        if np.argmax(probs) == action_a:
            lo = mid
        else:
            hi = mid

    return (lo + hi) / 2

# Example: at what opponent goal count does the agent switch from aggressive to racing?
threshold = find_decision_boundary(
    model, base_state,
    action_a=AGGRESSIVE_ACTION,
    action_b=RACE_ACTION,
    feature_idx=17  # P2 pieces in goal / 7
)
print(f"Agent switches strategy when P2 goals / 7 ≈ {threshold:.2f}  →  ~{threshold * 7:.1f} pieces scored")
```

### Sweeping all features

Run `find_decision_boundary` across all 30 feature dimensions for a given decision pair and rank by which features produce a threshold in the interior of `[0, 1]`. Those are the features actively driving that decision.

---

## Technique 6 — Behavioral Clustering with UMAP

**What it tells you:** Whether the agent has multiple distinct strategic modes, and what board states trigger each.
**Cost:** Higher — requires rollout collection, UMAP fit, and manual cluster inspection.

### Approach

```python
import umap
import matplotlib.pyplot as plt

def collect_rollout_data(model, env, n_episodes=1000):
    states, probs_list = [], []
    for _ in range(n_episodes):
        obs, _ = env.reset()
        done = False
        while not done:
            probs = get_action_probs(model, obs[np.newaxis])
            states.append(obs.copy())
            probs_list.append(probs)
            action = np.argmax(probs * env.action_masks())
            obs, _, done, _, _ = env.step(action)
    return np.array(states), np.array(probs_list)

states, probs = collect_rollout_data(model, env, n_episodes=1000)

# Embed into 2D
reducer = umap.UMAP(n_components=2, random_state=42)
embedding = reducer.fit_transform(np.hstack([states, probs]))

# Color by dominant action
dominant_action = np.argmax(probs, axis=1)
plt.figure(figsize=(9, 7))
scatter = plt.scatter(embedding[:, 0], embedding[:, 1],
                      c=dominant_action, cmap='tab10', s=2, alpha=0.5)
plt.colorbar(scatter, label='Dominant action')
plt.title('UMAP of (state, policy) space — colored by dominant action')
plt.tight_layout()
plt.show()
```

### Interpreting clusters

- **Tight clusters with a single dominant action:** The agent has a strong, consistent heuristic for that region of state space.
- **Mixed clusters:** The agent is uncertain or context-sensitive in those states — worth investigating with probes (Technique 4).
- **Isolated outlier clusters:** Rare game states (e.g., very late game with many pieces scored) — check if the agent behaves sensibly there or has poor training coverage.

You can also re-color by other features (e.g., `states[:, 17]` = opponent progress) to see if clusters correspond to game phases.

---

## Recommended Exploration Order

| Step | Technique | Goal |
|------|-----------|------|
| 1 | Action Distribution Analysis | Get the lay of the land |
| 2 | Value Function Landscape | Understand what positions the agent considers winning |
| 3 | Saliency / Input Gradients | Identify which features drive decisions globally |
| 4 | Targeted Probes | Test specific Ur strategy hypotheses |
| 5 | Counterfactual Boundary Search | Turn probe observations into crisp rules |
| 6 | UMAP Clustering | Check for multiple strategic modes (most expensive — run last) |

---

## Dependencies

```
stable-baselines3
sb3-contrib          # MaskablePPO
torch
numpy
matplotlib
umap-learn
```