using System;
using System.Collections.Generic;
using System.Linq;

namespace Ur
{
    /// <summary>
    /// Gym-style environment wrapper for the Game of Ur.
    /// The agent always controls Player 1. Player 2 acts as a random opponent.
    /// 
    /// Action space: discrete, 8 actions.
    ///   Actions 0–6: move the piece with logical index N (sorted by movementCounter ascending).
    ///   Action 7: place a new piece from hand.
    /// Invalid actions are masked each turn via <see cref="GetValidActions"/>.
    /// 
    /// State vector: 30 floats, all normalized to [0, 1].
    ///   [0–6]   Player 1 piece progress (movementCounter / 14.0; -1 in hand → 0, scored → 1)
    ///   [7–13]  Player 2 piece progress
    ///   [14]    Player 1 pieces in hand / 7.0
    ///   [15]    Player 1 pieces in goal / 7.0
    ///   [16]    Player 2 pieces in hand / 7.0
    ///   [17]    Player 2 pieces in goal / 7.0
    ///   [18]    Current roll / 4.0
    ///   [19–26] Action mask (1.0 = valid, 0.0 = invalid) for actions 0–7
    ///   [27]    Has double turn (1.0 or 0.0)
    ///   [28]    Unused (reserved, always 0)
    ///   [29]    Unused (reserved, always 0)
    /// </summary>
    class GameEnvironment
    {
        public const int StateSize = 30;
        public const int ActionCount = 8;
        private const int PieceCount = 7;
        private const float MaxProgress = 14f;
        private const float MaxRoll = 4f;

        private InternalGame _game;
        private int _currentRoll;
        private bool _done;
        private Random _rng;

        // Mapping from logical piece index to board index (or -1 for hand pieces).
        // Rebuilt each turn so the agent sees a consistent ordering.
        private int[] _p1PieceMap;

        // Always access board/player state through the Game to stay in sync
        // after getPossibleMoves replaces references via undoMove().
        private GameBoard Board => _game.CurrentBoard;
        private Player Player1 => _game.CurrentPlayer1;
        private Player Player2 => _game.CurrentPlayer2;

        public GameEnvironment(int? seed = null)
        {
            _rng = seed.HasValue ? new Random(seed.Value) : new Random();
            _game = new InternalGame();
            _p1PieceMap = new int[PieceCount];
            Reset();
        }

        /// <summary>
        /// Resets the environment to a fresh game and rolls the first dice.
        /// Returns the initial state vector.
        /// </summary>
        public float[] Reset()
        {
            _game.Init(new Player(1), new Player(2), new GameBoard());
            _done = false;
            _currentRoll = RollDice();

            // If roll is 0, agent has no moves — skip to opponent until agent gets a non-empty turn
            SkipIfNoMoves();

            return GetState();
        }

        /// <summary>
        /// Returns a boolean array of length <see cref="ActionCount"/>.
        /// true = action is valid this turn, false = invalid.
        /// </summary>
        public bool[] GetValidActions()
        {
            var mask = new bool[ActionCount];
            if (_done) return mask;

            var possibleMoves = _game.getPossibleMoves(Player1, _currentRoll);
            BuildPieceMap();

            foreach (int boardIdx in possibleMoves)
            {
                int action = BoardIndexToAction(boardIdx);
                if (action >= 0 && action < ActionCount)
                    mask[action] = true;
            }
            return mask;
        }

        /// <summary>
        /// Executes the given action for the agent (Player 1).
        /// Returns (state, reward, done, info).
        /// </summary>
        public (float[] state, float reward, bool done, Dictionary<string, object> info) Step(int action)
        {
            var info = new Dictionary<string, object>();

            if (_done)
            {
                info["error"] = "Game is already over. Call Reset().";
                return (GetState(), 0f, true, info);
            }

            // Validate action
            var validActions = GetValidActions();
            if (action < 0 || action >= ActionCount || !validActions[action])
            {
                info["error"] = "Invalid action";
                // Return current state with small negative reward for illegal move
                return (GetState(), -1f, false, info);
            }

            // Convert action to board move
            int boardIdx = ActionToBoardIndex(action);
            ExecuteMove(Player1, Player2, boardIdx, _currentRoll);

            // Check if agent won
            if (Player1.piecesInGoal >= 7)
            {
                _done = true;
                info["winner"] = 1;
                return (GetState(), 1f, true, info);
            }

            // Handle double turn for agent
            if (Player1.hasDouble)
            {
                Player1.hasDouble = false;
                _currentRoll = RollDice();
                SkipIfNoMoves();
                return (GetState(), 0f, _done, info);
            }

            // Opponent's turn(s)
            PlayOpponentTurns();

            // Check if opponent won
            if (Player2.piecesInGoal >= 7)
            {
                _done = true;
                info["winner"] = 2;
                return (GetState(), -1f, true, info);
            }

            // Roll for agent's next turn
            _currentRoll = RollDice();
            SkipIfNoMoves();

            return (GetState(), 0f, _done, info);
        }

        /// <summary>
        /// Builds the 30-element normalized state vector.
        /// </summary>
        public float[] GetState()
        {
            var state = new float[StateSize];
            BuildPieceMap();

            // Player 1 piece progress [0–6]
            var p1Pieces = GetAllPieceProgress(Player1);
            for (int i = 0; i < PieceCount; i++)
                state[i] = NormalizeProgress(p1Pieces[i]);

            // Player 2 piece progress [7–13]
            var p2Pieces = GetAllPieceProgress(Player2);
            for (int i = 0; i < PieceCount; i++)
                state[7 + i] = NormalizeProgress(p2Pieces[i]);

            // Aggregate stats [14–18]
            state[14] = Player1.piecesInHand / 7f;
            state[15] = Player1.piecesInGoal / 7f;
            state[16] = Player2.piecesInHand / 7f;
            state[17] = Player2.piecesInGoal / 7f;
            state[18] = _currentRoll / MaxRoll;

            // Action mask [19–26]
            if (!_done)
            {
                var validActions = GetValidActions();
                for (int i = 0; i < ActionCount; i++)
                    state[19 + i] = validActions[i] ? 1f : 0f;
            }

            // Has double [27]
            state[27] = Player1.hasDouble ? 1f : 0f;

            // Reserved [28–29]
            state[28] = 0f;
            state[29] = 0f;

            return state;
        }

        // ── Internal helpers ──────────────────────────────────────────────────────

        private int RollDice()
        {
            int roll = 0;
            for (int i = 0; i < 4; i++)
                roll += _rng.Next(0, 2);
            return roll;
        }

        /// <summary>
        /// Builds a map of logical piece index (0–6) → board position index for Player 1.
        /// Pieces are sorted by movementCounter ascending: in-hand pieces first (-1),
        /// then on-board pieces by progress, then scored pieces (14).
        /// This gives the agent a stable ordering.
        /// </summary>
        private void BuildPieceMap()
        {
            var pieces = new List<(int movementCounter, int boardIdx)>();

            // Collect on-board pieces
            for (int i = 0; i < Board.gameBoard.Length; i++)
            {
                var piece = Board.gameBoard[i];
                if (piece != null && piece.player.playerNum == Player1.playerNum)
                    pieces.Add((piece.movementCounter, i));
            }

            // Add in-hand pieces (boardIdx = -1)
            for (int i = 0; i < Player1.piecesInHand; i++)
                pieces.Add((-1, -1));

            // Add scored pieces (movementCounter = 14, boardIdx = -2 as sentinel)
            for (int i = 0; i < Player1.piecesInGoal; i++)
                pieces.Add((14, -2));

            // Sort by movementCounter ascending
            pieces.Sort((a, b) => a.movementCounter.CompareTo(b.movementCounter));

            // Fill the map
            for (int i = 0; i < PieceCount && i < pieces.Count; i++)
                _p1PieceMap[i] = pieces[i].boardIdx;
        }

        /// <summary>
        /// Returns sorted movementCounter values for all 7 pieces of the given player.
        /// In-hand = -1, scored = 14.
        /// </summary>
        private int[] GetAllPieceProgress(Player player)
        {
            var progress = new List<int>();

            // On-board pieces
            for (int i = 0; i < Board.gameBoard.Length; i++)
            {
                var piece = Board.gameBoard[i];
                if (piece != null && piece.player.playerNum == player.playerNum)
                    progress.Add(piece.movementCounter);
            }

            // In-hand pieces
            for (int i = 0; i < player.piecesInHand; i++)
                progress.Add(-1);

            // Scored pieces
            for (int i = 0; i < player.piecesInGoal; i++)
                progress.Add(14);

            progress.Sort();
            return progress.ToArray();
        }

        private float NormalizeProgress(int movementCounter)
        {
            // -1 (in hand) → 0.0, 0 → 1/15, ..., 13 → 14/15, 14 (scored) → 1.0
            return (movementCounter + 1) / 15f;
        }

        /// <summary>
        /// Maps a board index (from getPossibleMoves) to an action index (0–7).
        /// -1 (place from hand) maps to action 7.
        /// On-board indices map to the logical piece index in _p1PieceMap.
        /// </summary>
        private int BoardIndexToAction(int boardIdx)
        {
            if (boardIdx == -1)
                return 7; // place from hand

            for (int i = 0; i < PieceCount; i++)
            {
                if (_p1PieceMap[i] == boardIdx)
                    return i;
            }
            return -1; // should not happen
        }

        /// <summary>
        /// Maps an action index (0–7) back to a board index for getPossibleMoves / movePiece.
        /// Action 7 → -1 (place from hand). Actions 0–6 → board index from _p1PieceMap.
        /// </summary>
        private int ActionToBoardIndex(int action)
        {
            if (action == 7)
                return -1;
            return _p1PieceMap[action];
        }

        private void ExecuteMove(Player player, Player opponent, int boardIdx, int roll)
        {
            if (boardIdx == -1)
            {
                // Place new piece from hand
                Board.movePiece(player, opponent, new GamePiece(player), roll);
            }
            else
            {
                Board.movePiece(player, opponent, Board.getPiece(boardIdx), roll);
            }
        }

        /// <summary>
        /// Plays the random opponent (Player 2) until it's the agent's turn again.
        /// Handles double turns for the opponent.
        /// </summary>
        private void PlayOpponentTurns()
        {
            while (true)
            {
                int opponentRoll = RollDice();
                var opponentMoves = _game.getPossibleMoves(Player2, opponentRoll);

                if (opponentMoves.Count > 0)
                {
                    int move = opponentMoves[_rng.Next(opponentMoves.Count)];
                    ExecuteMove(Player2, Player1, move, opponentRoll);
                }

                // Check if opponent won
                if (Player2.piecesInGoal >= 7)
                    return;

                // If opponent got a double, keep going
                if (Player2.hasDouble)
                {
                    Player2.hasDouble = false;
                    continue;
                }

                // Opponent's turn is over
                break;
            }
        }

        /// <summary>
        /// If the agent has no legal moves (roll 0, or all moves blocked), auto-skip
        /// to the opponent and keep going until the agent gets a playable turn
        /// or the game ends.
        /// </summary>
        private void SkipIfNoMoves()
        {
            while (!_done)
            {
                var moves = _game.getPossibleMoves(Player1, _currentRoll);
                if (moves.Count > 0)
                    break;

                // No moves for agent — play opponent
                PlayOpponentTurns();

                if (Player2.piecesInGoal >= 7)
                {
                    _done = true;
                    return;
                }

                _currentRoll = RollDice();
            }
        }

        /// <summary>
        /// Internal Game subclass to expose getPossibleMoves with injected state.
        /// Properties always return the current references (which may be replaced by undoMove).
        /// </summary>
        private class InternalGame : Game
        {
            public void Init(Player p1, Player p2, GameBoard board)
            {
                this.player1 = p1;
                this.player2 = p2;
                this.gameBoard = board;
            }

            public GameBoard CurrentBoard => gameBoard;
            public Player CurrentPlayer1 => player1;
            public Player CurrentPlayer2 => player2;
        }
    }
}
