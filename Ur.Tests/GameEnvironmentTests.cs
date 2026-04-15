using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Ur;

namespace Ur.Tests
{
    public class GameEnvironmentTests
    {
        // ── Reset ────────────────────────────────────────────────────────────────

        [Fact]
        public void Reset_ReturnsStateOfCorrectSize()
        {
            var env = new GameEnvironment(seed: 42);
            float[] state = env.Reset();
            Assert.Equal(GameEnvironment.StateSize, state.Length);
        }

        [Fact]
        public void Reset_AllPiecesInHand_ProgressIsZero()
        {
            var env = new GameEnvironment(seed: 42);
            float[] state = env.Reset();

            // Pieces in hand have movementCounter = -1 → normalized = (-1 + 1)/15 = 0
            for (int i = 0; i < 7; i++)
                Assert.Equal(0f, state[i]); // Player 1 pieces

            for (int i = 7; i < 14; i++)
                Assert.Equal(0f, state[i]); // Player 2 pieces
        }

        [Fact]
        public void Reset_HandAndGoalCountsCorrect()
        {
            var env = new GameEnvironment(seed: 42);
            float[] state = env.Reset();

            // Player 1: 7 in hand, 0 in goal
            Assert.Equal(1f, state[14]); // piecesInHand / 7
            Assert.Equal(0f, state[15]); // piecesInGoal / 7

            // Player 2: 7 in hand, 0 in goal
            Assert.Equal(1f, state[16]);
            Assert.Equal(0f, state[17]);
        }

        [Fact]
        public void Reset_RollIsBetween0And4()
        {
            var env = new GameEnvironment(seed: 42);
            float[] state = env.Reset();

            float normalizedRoll = state[18];
            Assert.InRange(normalizedRoll, 0f, 1f);
            // Actual roll = normalizedRoll * 4, should be 0–4
            float roll = normalizedRoll * 4f;
            Assert.InRange(roll, 0f, 4f);
        }

        [Fact]
        public void Reset_StateIsNormalized()
        {
            var env = new GameEnvironment(seed: 42);
            float[] state = env.Reset();

            for (int i = 0; i < GameEnvironment.StateSize; i++)
                Assert.InRange(state[i], 0f, 1f);
        }

        // ── GetValidActions ──────────────────────────────────────────────────────

        [Fact]
        public void GetValidActions_ReturnsCorrectLength()
        {
            var env = new GameEnvironment(seed: 42);
            env.Reset();
            bool[] actions = env.GetValidActions();
            Assert.Equal(GameEnvironment.ActionCount, actions.Length);
        }

        [Fact]
        public void GetValidActions_AtStart_OnlyPlaceFromHandIsValid()
        {
            // At the start, all pieces are in hand. The only valid move is to place from hand.
            // (Unless roll is 0, but we skip no-move turns in Reset)
            var env = new GameEnvironment(seed: 42);
            env.Reset();
            bool[] actions = env.GetValidActions();

            // Actions 0–6 should all be false (no pieces on board)
            for (int i = 0; i < 7; i++)
                Assert.False(actions[i]);

            // Action 7 (place from hand) should be true
            Assert.True(actions[7]);
        }

        [Fact]
        public void GetValidActions_AfterGameOver_AllFalse()
        {
            var env = new GameEnvironment(seed: 42);
            env.Reset();

            // Play until game is over
            bool done = false;
            int maxSteps = 10000;
            int steps = 0;
            while (!done && steps < maxSteps)
            {
                bool[] valid = env.GetValidActions();
                // Pick first valid action
                int action = -1;
                for (int i = 0; i < valid.Length; i++)
                {
                    if (valid[i]) { action = i; break; }
                }
                if (action == -1) break;
                var (state, reward, d, info) = env.Step(action);
                done = d;
                steps++;
            }

            // After game is over, all actions should be invalid
            bool[] postGameActions = env.GetValidActions();
            Assert.All(postGameActions, a => Assert.False(a));
        }

        // ── Step ─────────────────────────────────────────────────────────────────

        [Fact]
        public void Step_InvalidAction_ReturnsNegativeReward()
        {
            var env = new GameEnvironment(seed: 42);
            env.Reset();

            // Action 0 should be invalid at start (no on-board pieces)
            var (state, reward, done, info) = env.Step(0);
            Assert.Equal(-1f, reward);
            Assert.False(done);
            Assert.True(info.ContainsKey("error"));
        }

        [Fact]
        public void Step_OutOfRangeAction_ReturnsNegativeReward()
        {
            var env = new GameEnvironment(seed: 42);
            env.Reset();
            var (state, reward, done, info) = env.Step(99);
            Assert.Equal(-1f, reward);
            Assert.True(info.ContainsKey("error"));
        }

        [Fact]
        public void Step_NegativeAction_ReturnsNegativeReward()
        {
            var env = new GameEnvironment(seed: 42);
            env.Reset();
            var (state, reward, done, info) = env.Step(-1);
            Assert.Equal(-1f, reward);
            Assert.True(info.ContainsKey("error"));
        }

        [Fact]
        public void Step_ValidAction_ReturnsZeroReward()
        {
            var env = new GameEnvironment(seed: 42);
            env.Reset();

            // Action 7 = place from hand, should be valid at start
            var (state, reward, done, info) = env.Step(7);

            // Mid-game reward should be 0 (no shaping)
            if (!done)
                Assert.Equal(0f, reward);
        }

        [Fact]
        public void Step_ReturnsStateOfCorrectSize()
        {
            var env = new GameEnvironment(seed: 42);
            env.Reset();
            var (state, reward, done, info) = env.Step(7);
            Assert.Equal(GameEnvironment.StateSize, state.Length);
        }

        [Fact]
        public void Step_AfterGameOver_ReturnsError()
        {
            var env = new GameEnvironment(seed: 42);
            env.Reset();

            // Play to completion
            bool gameDone = false;
            int maxSteps = 10000;
            int steps = 0;
            while (!gameDone && steps < maxSteps)
            {
                bool[] valid = env.GetValidActions();
                int action = -1;
                for (int i = 0; i < valid.Length; i++)
                {
                    if (valid[i]) { action = i; break; }
                }
                if (action == -1) break;
                var (s, r, d, info) = env.Step(action);
                gameDone = d;
                steps++;
            }

            // Now try stepping again
            var (state, reward, done, info2) = env.Step(7);
            Assert.True(done);
            Assert.True(info2.ContainsKey("error"));
        }

        // ── Full game ────────────────────────────────────────────────────────────

        [Fact]
        public void FullGame_EventuallyTerminates()
        {
            var env = new GameEnvironment(seed: 123);
            env.Reset();

            bool done = false;
            int steps = 0;
            int maxSteps = 10000;

            while (!done && steps < maxSteps)
            {
                bool[] valid = env.GetValidActions();
                int action = -1;
                for (int i = 0; i < valid.Length; i++)
                {
                    if (valid[i]) { action = i; break; }
                }
                if (action == -1) break;

                var (state, reward, d, info) = env.Step(action);
                done = d;
                steps++;

                // State should always be normalized
                for (int i = 0; i < state.Length; i++)
                    Assert.InRange(state[i], 0f, 1f);
            }

            Assert.True(done, $"Game did not terminate within {maxSteps} steps");
        }

        [Fact]
        public void FullGame_WinnerGetsCorrectReward()
        {
            var env = new GameEnvironment(seed: 456);
            env.Reset();

            float finalReward = 0f;
            bool done = false;
            int steps = 0;
            Dictionary<string, object>? finalInfo = null;

            while (!done && steps < 10000)
            {
                bool[] valid = env.GetValidActions();
                int action = -1;
                for (int i = 0; i < valid.Length; i++)
                {
                    if (valid[i]) { action = i; break; }
                }
                if (action == -1) break;

                var (state, reward, d, info) = env.Step(action);
                done = d;
                finalReward = reward;
                finalInfo = info;
                steps++;
            }

            Assert.True(done);
            // Final reward should be +1 (agent won) or -1 (opponent won)
            Assert.True(finalReward == 1f || finalReward == -1f,
                $"Expected +1 or -1 final reward, got {finalReward}");
            Assert.True(finalInfo!.ContainsKey("winner"));
        }

        [Fact]
        public void MultipleGames_CanResetAndPlay()
        {
            var env = new GameEnvironment(seed: 789);

            for (int game = 0; game < 5; game++)
            {
                env.Reset();
                bool done = false;
                int steps = 0;

                while (!done && steps < 10000)
                {
                    bool[] valid = env.GetValidActions();
                    int action = -1;
                    for (int i = 0; i < valid.Length; i++)
                    {
                        if (valid[i]) { action = i; break; }
                    }
                    if (action == -1) break;

                    var (state, reward, d, info) = env.Step(action);
                    done = d;
                    steps++;
                }
                Assert.True(done, $"Game {game} did not terminate");
            }
        }

        // ── State vector consistency ─────────────────────────────────────────────

        [Fact]
        public void StateVector_ActionMaskMatchesGetValidActions()
        {
            var env = new GameEnvironment(seed: 42);
            float[] state = env.Reset();
            bool[] valid = env.GetValidActions();

            for (int i = 0; i < GameEnvironment.ActionCount; i++)
            {
                float expected = valid[i] ? 1f : 0f;
                Assert.Equal(expected, state[19 + i]);
            }
        }

        [Fact]
        public void StateVector_PieceCountsAddUpToSeven()
        {
            var env = new GameEnvironment(seed: 42);
            env.Reset();

            // Play a few moves and check consistency
            for (int step = 0; step < 20; step++)
            {
                float[] state = env.GetState();
                bool[] valid = env.GetValidActions();
                int action = -1;
                for (int i = 0; i < valid.Length; i++)
                {
                    if (valid[i]) { action = i; break; }
                }
                if (action == -1) break;

                // Check that P1 pieces in hand + on board + in goal = 7
                float p1Hand = state[14] * 7f;
                float p1Goal = state[15] * 7f;
                // Count on-board pieces (progress > 0 and < 1 in normalized form)
                int p1OnBoard = 0;
                for (int i = 0; i < 7; i++)
                {
                    if (state[i] > 0f && state[i] < 1f)
                        p1OnBoard++;
                }
                float totalP1 = p1Hand + p1Goal + p1OnBoard;
                Assert.InRange(totalP1, 6.5f, 7.5f); // allow floating point

                var (s, r, d, info) = env.Step(action);
                if (d) break;
            }
        }

        // ── Seeded determinism ───────────────────────────────────────────────────

        [Fact]
        public void SameSeed_ProducesSameInitialState()
        {
            var env1 = new GameEnvironment(seed: 999);
            var env2 = new GameEnvironment(seed: 999);

            float[] state1 = env1.Reset();
            float[] state2 = env2.Reset();

            Assert.Equal(state1, state2);
        }

        [Fact]
        public void SameSeed_SameActionSequence_ProducesSameResults()
        {
            var env1 = new GameEnvironment(seed: 999);
            var env2 = new GameEnvironment(seed: 999);

            env1.Reset();
            env2.Reset();

            for (int step = 0; step < 50; step++)
            {
                bool[] valid1 = env1.GetValidActions();
                bool[] valid2 = env2.GetValidActions();
                Assert.Equal(valid1, valid2);

                int action = -1;
                for (int i = 0; i < valid1.Length; i++)
                {
                    if (valid1[i]) { action = i; break; }
                }
                if (action == -1) break;

                var (s1, r1, d1, _) = env1.Step(action);
                var (s2, r2, d2, _) = env2.Step(action);

                Assert.Equal(s1, s2);
                Assert.Equal(r1, r2);
                Assert.Equal(d1, d2);

                if (d1) break;
            }
        }
    }
}
