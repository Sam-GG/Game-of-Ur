using System;
using System.Collections.Generic;
using System.Linq;

namespace Ur
{
    /// <summary>
    /// Gym-style environment wrapper for the Game of Ur.
    /// The agent always controls Player 1. Player 2 acts as an opponent
    /// whose strategy is determined by <see cref="OpponentType"/>.
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

        /// <summary>Opponent strategy for Player 2.</summary>
        public string OpponentType { get; }

        private InternalGame _game;
        private int _currentRoll;
        private bool _done;
        private Random _rng;

        // Mapping from logical piece index to board index (or -1 for hand pieces).
        // Rebuilt each turn so the agent sees a consistent ordering.
        private int[] _p1PieceMap;

        // For the "external" opponent mode: when true, Step() has paused
        // waiting for the caller to supply the opponent's action via OpponentStep().
        private bool _waitingForOpponent;
        private int _opponentRoll;

        // Always access board/player state through the Game to stay in sync
        // after getPossibleMoves replaces references via undoMove().
        private GameBoard Board => _game.CurrentBoard;
        private Player Player1 => _game.CurrentPlayer1;
        private Player Player2 => _game.CurrentPlayer2;

        /// <param name="seed">Optional RNG seed for reproducibility.</param>
        /// <param name="opponentType">One of: random, greedy, defensive, external.</param>
        public GameEnvironment(int? seed = null, string opponentType = "random")
        {
            OpponentType = (opponentType ?? "random").ToLowerInvariant();
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
            _waitingForOpponent = false;
            _currentRoll = RollDice();

            // If roll is 0, agent has no moves — skip to opponent until agent gets a non-empty turn.
            // For "external" opponent mode, we re-roll until non-zero because at game start
            // (all pieces in hand) neither player can move with roll 0, so no opponent
            // interaction is needed — it's equivalent to both sides skipping.
            if (OpponentType == "external")
            {
                while (_currentRoll == 0 || _game.getPossibleMoves(Player1, _currentRoll).Count == 0)
                    _currentRoll = RollDice();
            }
            else
            {
                SkipIfNoMoves();
            }

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
        /// Returns valid actions for the opponent (Player 2) as board indices.
        /// Used by the "external" opponent mode.
        /// </summary>
        public List<int> GetOpponentValidMoves()
        {
            return _game.getPossibleMoves(Player2, _opponentRoll);
        }

        /// <summary>
        /// Returns the current dice roll for the opponent. Only meaningful
        /// when <see cref="IsWaitingForOpponent"/> is true.
        /// </summary>
        public int GetOpponentRoll() => _opponentRoll;

        /// <summary>True when the environment is paused waiting for an external opponent action.</summary>
        public bool IsWaitingForOpponent => _waitingForOpponent;

        /// <summary>
        /// Executes the given action for the agent (Player 1).
        /// Returns (state, reward, done, info).
        ///
        /// For the "external" opponent mode, after the agent moves, if it's the
        /// opponent's turn the response includes "opponent_turn" = true in info.
        /// The caller must then supply the opponent's action via <see cref="OpponentStep"/>.
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
                if (OpponentType != "external")
                    SkipIfNoMoves();
                else
                    return ExternalSkipIfNoMoves(info);
                return (GetState(), 0f, _done, info);
            }

            // Opponent's turn(s)
            if (OpponentType == "external")
            {
                return StartExternalOpponentTurn(info);
            }

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
        /// Executes an opponent (Player 2) action in "external" opponent mode.
        /// The action is a board index (from <see cref="GetOpponentValidMoves"/>)
        /// or -1 for placing from hand.
        ///
        /// After executing, if the opponent gets a double turn, the response
        /// includes "opponent_turn" = true again; otherwise it rolls for the
        /// agent's next turn and returns the agent's state.
        /// </summary>
        public (float[] state, float reward, bool done, Dictionary<string, object> info) OpponentStep(int boardIdx)
        {
            var info = new Dictionary<string, object>();

            if (!_waitingForOpponent)
            {
                info["error"] = "Not waiting for opponent action.";
                return (GetState(), 0f, _done, info);
            }

            _waitingForOpponent = false;
            ExecuteMove(Player2, Player1, boardIdx, _opponentRoll);

            // Check if opponent won
            if (Player2.piecesInGoal >= 7)
            {
                _done = true;
                info["winner"] = 2;
                return (GetState(), -1f, true, info);
            }

            // If opponent got a double, keep going
            if (Player2.hasDouble)
            {
                Player2.hasDouble = false;
                return StartExternalOpponentTurn(info);
            }

            // Opponent done — roll for agent's next turn
            _currentRoll = RollDice();
            return ExternalSkipIfNoMoves(info);
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
        /// Plays the opponent (Player 2) until it's the agent's turn again.
        /// Handles double turns. Uses the opponent strategy set by <see cref="OpponentType"/>.
        /// </summary>
        private void PlayOpponentTurns()
        {
            while (true)
            {
                int opponentRoll = RollDice();
                var opponentMoves = _game.getPossibleMoves(Player2, opponentRoll);

                if (opponentMoves.Count > 0)
                {
                    int move = PickOpponentMove(opponentMoves, opponentRoll);
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
        /// Selects an opponent move based on the current <see cref="OpponentType"/>.
        /// </summary>
        private int PickOpponentMove(List<int> possibleMoves, int roll)
        {
            return OpponentType switch
            {
                "greedy" => PickGreedyMove(possibleMoves, roll),
                "defensive" => PickDefensiveMove(possibleMoves, roll),
                _ => possibleMoves[_rng.Next(possibleMoves.Count)], // random
            };
        }

        /// <summary>
        /// Greedy heuristic: prioritize scoring → capturing → landing on rosette → advancing furthest piece.
        /// </summary>
        private int PickGreedyMove(List<int> possibleMoves, int roll)
        {
            int bestMove = possibleMoves[0];
            int bestScore = int.MinValue;

            foreach (int boardIdx in possibleMoves)
            {
                int score = 0;
                int currentProgress;

                if (boardIdx == -1)
                {
                    // Place from hand: movementCounter = -1, new counter = -1 + roll = roll - 1
                    currentProgress = -1;
                }
                else
                {
                    var piece = Board.getPiece(boardIdx);
                    currentProgress = piece.movementCounter;
                }

                int newProgress = currentProgress + roll;
                int destinationBoardIdx = newProgress < 14 ? Player2.movementPattern[newProgress] : -1;

                // Scoring a piece is highest priority
                if (newProgress == 14)
                {
                    score = 10000;
                }
                // Capturing an opponent piece
                else if (destinationBoardIdx >= 0 && Board.gameBoard[destinationBoardIdx] != null
                         && Board.gameBoard[destinationBoardIdx].player.playerNum == Player1.playerNum)
                {
                    score = 5000;
                }
                // Landing on a rosette (double turn)
                else if (destinationBoardIdx >= 0 && IsRosette(destinationBoardIdx))
                {
                    score = 3000;
                }

                // Tiebreaker: prefer advancing the piece that's furthest along
                score += currentProgress + roll;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestMove = boardIdx;
                }
            }

            return bestMove;
        }

        /// <summary>
        /// Defensive heuristic: prioritize scoring → rosette safety → escaping shared lane → placing new pieces.
        /// </summary>
        private int PickDefensiveMove(List<int> possibleMoves, int roll)
        {
            int bestMove = possibleMoves[0];
            int bestScore = int.MinValue;

            // Shared lane board indices where captures can happen (6–13, excluding safe rosette 9)
            var dangerZone = new HashSet<int> { 6, 7, 8, 10, 11, 12, 13 };

            foreach (int boardIdx in possibleMoves)
            {
                int score = 0;
                int currentProgress;

                if (boardIdx == -1)
                {
                    currentProgress = -1;
                }
                else
                {
                    var piece = Board.getPiece(boardIdx);
                    currentProgress = piece.movementCounter;
                }

                int newProgress = currentProgress + roll;
                int destinationBoardIdx = newProgress < 14 ? Player2.movementPattern[newProgress] : -1;

                // Scoring a piece is highest priority
                if (newProgress == 14)
                {
                    score = 10000;
                }
                // Landing on a rosette (safe + double turn)
                else if (destinationBoardIdx >= 0 && IsRosette(destinationBoardIdx))
                {
                    score = 8000;
                }
                // Moving a piece OUT of the danger zone to safety
                else if (boardIdx >= 0 && dangerZone.Contains(boardIdx)
                         && destinationBoardIdx >= 0 && !dangerZone.Contains(destinationBoardIdx))
                {
                    score = 6000;
                }
                // Capturing an opponent (still good, but not the primary goal)
                else if (destinationBoardIdx >= 0 && Board.gameBoard[destinationBoardIdx] != null
                         && Board.gameBoard[destinationBoardIdx].player.playerNum == Player1.playerNum)
                {
                    score = 4000;
                }
                // Placing new piece from hand
                else if (boardIdx == -1)
                {
                    score = 2000;
                }
                // Avoid moving INTO the danger zone
                else if (destinationBoardIdx >= 0 && dangerZone.Contains(destinationBoardIdx))
                {
                    score = 500;
                }
                else
                {
                    score = 1000;
                }

                // Tiebreaker: prefer piece nearest to goal
                score += newProgress;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestMove = boardIdx;
                }
            }

            return bestMove;
        }

        private static bool IsRosette(int boardIdx)
        {
            return boardIdx == 3 || boardIdx == 4 || boardIdx == 9 || boardIdx == 17 || boardIdx == 18;
        }

        /// <summary>
        /// Start an opponent turn in external mode.  Rolls for the opponent; if
        /// the opponent has no moves, skips straight to the agent's next turn
        /// (which itself may also need to skip — see <see cref="ExternalSkipIfNoMoves"/>).
        /// </summary>
        private (float[] state, float reward, bool done, Dictionary<string, object> info) StartExternalOpponentTurn(
            Dictionary<string, object> info)
        {
            _opponentRoll = RollDice();
            var opponentMoves = _game.getPossibleMoves(Player2, _opponentRoll);
            if (opponentMoves.Count == 0)
            {
                // No moves for opponent — roll for agent's next turn
                _currentRoll = RollDice();
                return ExternalSkipIfNoMoves(info);
            }
            _waitingForOpponent = true;
            info["opponent_turn"] = true;
            info["opponent_roll"] = _opponentRoll;
            info["opponent_valid_moves"] = opponentMoves;
            return (GetState(), 0f, false, info);
        }

        /// <summary>
        /// In external mode, if the agent has no valid moves after a roll,
        /// loop: give opponent a turn (returning opponent_turn), then check again.
        /// If the opponent also has no moves, keep alternating until someone does
        /// or the game ends.
        /// </summary>
        private (float[] state, float reward, bool done, Dictionary<string, object> info) ExternalSkipIfNoMoves(
            Dictionary<string, object> info)
        {
            // Loop while agent has no moves
            while (!_done)
            {
                var agentMoves = _game.getPossibleMoves(Player1, _currentRoll);
                if (agentMoves.Count > 0)
                    return (GetState(), 0f, false, info);

                // Agent has no moves — give opponent a turn
                _opponentRoll = RollDice();
                var opponentMoves = _game.getPossibleMoves(Player2, _opponentRoll);
                if (opponentMoves.Count > 0)
                {
                    // Opponent has moves — delegate to caller
                    _waitingForOpponent = true;
                    info["opponent_turn"] = true;
                    info["opponent_roll"] = _opponentRoll;
                    info["opponent_valid_moves"] = opponentMoves;
                    return (GetState(), 0f, false, info);
                }

                // Neither side can move — roll again for agent
                _currentRoll = RollDice();
            }

            return (GetState(), 0f, _done, info);
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
