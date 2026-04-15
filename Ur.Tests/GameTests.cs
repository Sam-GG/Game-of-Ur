using System.Collections.Generic;
using Xunit;
using Ur;

namespace Ur.Tests
{
    /// <summary>
    /// Helper to build a GameBoard and Players in a known state.
    /// </summary>
    internal static class TestHelpers
    {
        /// <summary>Creates a fresh board with two players, all pieces in hand.</summary>
        internal static (GameBoard board, Player p1, Player p2) NewGame()
        {
            var p1 = new Player(1);
            var p2 = new Player(2);
            var board = new GameBoard();
            return (board, p1, p2);
        }

        /// <summary>Places a piece at the given progress counter for a player (not from hand).</summary>
        internal static GamePiece PlacePieceAtProgress(GameBoard board, Player player, int progress)
        {
            var piece = new GamePiece(player);
            piece.movementCounter = progress;
            piece.inHand = false;
            board.gameBoard[player.movementPattern[progress]] = piece;
            player.piecesInHand--;
            return piece;
        }
    }

    public class MovePieceTests
    {
        // ── Basic placement ───────────────────────────────────────────────────────

        [Fact]
        public void PlacePieceFromHand_Succeeds()
        {
            var (board, p1, p2) = TestHelpers.NewGame();
            var piece = new GamePiece(p1);  // movementCounter = -1, inHand = true
            int result = board.movePiece(p1, p2, piece, 1);

            Assert.Equal(0, result);
            // piece should now sit at movementPattern[0] = index 0
            Assert.Equal(0, piece.movementCounter);
            Assert.False(piece.inHand);
            Assert.Equal(6, p1.piecesInHand); // 7 - 1
            Assert.Equal(0, p1.movementPattern[0]);
            Assert.Same(piece, board.gameBoard[0]);
        }

        // ── Basic movement ────────────────────────────────────────────────────────

        [Fact]
        public void MovePiece_AdvancesCorrectly()
        {
            var (board, p1, p2) = TestHelpers.NewGame();
            var piece = TestHelpers.PlacePieceAtProgress(board, p1, 2); // board index 2
            int result = board.movePiece(p1, p2, piece, 3);

            Assert.Equal(0, result);
            Assert.Equal(5, piece.movementCounter);
            // movementPattern[5] = 7
            Assert.Same(piece, board.gameBoard[p1.movementPattern[5]]);
            Assert.Null(board.gameBoard[p1.movementPattern[2]]); // old spot cleared
        }

        // ── Goal scoring ──────────────────────────────────────────────────────────

        [Fact]
        public void PieceReachesExactly14_Scores()
        {
            var (board, p1, p2) = TestHelpers.NewGame();
            var piece = TestHelpers.PlacePieceAtProgress(board, p1, 11); // 3 away from goal
            int result = board.movePiece(p1, p2, piece, 3);

            Assert.Equal(0, result);
            Assert.Equal(1, p1.piecesInGoal);
            // board index that held the piece should now be empty
            Assert.Null(board.gameBoard[p1.movementPattern[11]]);
        }

        // ── Overshoot ────────────────────────────────────────────────────────────

        [Fact]
        public void PieceOvershoots14_Rejected()
        {
            var (board, p1, p2) = TestHelpers.NewGame();
            var piece = TestHelpers.PlacePieceAtProgress(board, p1, 12); // 2 from goal
            int result = board.movePiece(p1, p2, piece, 3); // would land at 15

            Assert.Equal(1, result);
            Assert.Equal(12, piece.movementCounter); // unchanged
            Assert.Equal(0, p1.piecesInGoal);
        }

        // ── Capture ──────────────────────────────────────────────────────────────

        [Fact]
        public void LandingOnOpponent_CapturesPiece()
        {
            var (board, p1, p2) = TestHelpers.NewGame();
            // Put p1 piece 3 steps from shared index 6 (movementPattern[4]=6)
            var attacker = TestHelpers.PlacePieceAtProgress(board, p1, 3); // board idx 3
            // Put p2 piece at shared index 6 (p2 movementPattern[4]=6)
            var defender = TestHelpers.PlacePieceAtProgress(board, p2, 4); // board idx 6

            int p2HandBefore = p2.piecesInHand;
            int result = board.movePiece(p1, p2, attacker, 1);

            Assert.Equal(0, result);
            // attacker now occupies index 6
            Assert.Same(attacker, board.gameBoard[6]);
            Assert.Equal(4, attacker.movementCounter);
            // defender was captured — back in hand
            Assert.True(defender.inHand);
            Assert.Equal(-1, defender.movementCounter);
            Assert.Equal(p2HandBefore + 1, p2.piecesInHand);
        }

        // ── Safe rosette (index 9) — no capture ──────────────────────────────────

        [Fact]
        public void LandingOnSafeRosette_CannotCapture()
        {
            var (board, p1, p2) = TestHelpers.NewGame();
            // p2 piece at progress 7 → movementPattern[7]=9 (the safe rosette)
            var defender = TestHelpers.PlacePieceAtProgress(board, p2, 7); // board idx 9
            // p1 piece at progress 6 → movementPattern[6]=8; move by 1 → lands at 9
            var attacker = TestHelpers.PlacePieceAtProgress(board, p1, 6); // board idx 8

            int result = board.movePiece(p1, p2, attacker, 1);

            Assert.Equal(1, result); // illegal — safe rosette
            Assert.Equal(6, attacker.movementCounter); // unchanged
            Assert.Equal(7, defender.movementCounter); // defender untouched
        }

        // ── Friendly block ────────────────────────────────────────────────────────

        [Fact]
        public void LandingOnOwnPiece_Rejected()
        {
            var (board, p1, p2) = TestHelpers.NewGame();
            var piece1 = TestHelpers.PlacePieceAtProgress(board, p1, 2); // board idx 2
            var piece2 = TestHelpers.PlacePieceAtProgress(board, p1, 0); // board idx 0

            // piece2 (at progress 0) tries to move 2 → lands on progress 2 where piece1 sits
            int result = board.movePiece(p1, p2, piece2, 2);

            Assert.Equal(1, result);
            Assert.Equal(0, piece2.movementCounter); // unchanged
        }

        // ── Rosette grants double ─────────────────────────────────────────────────

        [Fact]
        public void LandingOnRosette_SetsHasDouble()
        {
            var (board, p1, p2) = TestHelpers.NewGame();
            // p1 movementPattern[2] = 2, movementPattern[3] = 3 (rosette)
            var piece = TestHelpers.PlacePieceAtProgress(board, p1, 2); // board idx 2

            board.movePiece(p1, p2, piece, 1); // moves to progress 3 → board idx 3 (rosette)

            Assert.True(p1.hasDouble);
        }

        [Fact]
        public void LandingOnSharedCenterRosette9_SetsHasDouble()
        {
            var (board, p1, p2) = TestHelpers.NewGame();
            // p1 movementPattern[7] = 9. Place at progress 6 (board idx 8).
            var piece = TestHelpers.PlacePieceAtProgress(board, p1, 6);

            board.movePiece(p1, p2, piece, 1); // lands on progress 7 → board idx 9

            Assert.True(p1.hasDouble);
        }

        // ── roll == 0 ─────────────────────────────────────────────────────────────

        [Fact]
        public void RollZero_ReturnsFailure()
        {
            var (board, p1, p2) = TestHelpers.NewGame();
            var piece = TestHelpers.PlacePieceAtProgress(board, p1, 2);
            int counterBefore = piece.movementCounter;

            int result = board.movePiece(p1, p2, piece, 0);

            Assert.Equal(1, result);
            Assert.Equal(counterBefore, piece.movementCounter); // piece unchanged
        }

        // ── Win condition ─────────────────────────────────────────────────────────

        [Fact]
        public void SevenPiecesInGoal_WinCondition()
        {
            var (board, p1, p2) = TestHelpers.NewGame();
            // Score 7 pieces for p1
            for (int i = 0; i < 7; i++)
            {
                var piece = TestHelpers.PlacePieceAtProgress(board, p1, 13); // 1 step from goal
                board.movePiece(p1, p2, piece, 1);
            }
            Assert.Equal(7, p1.piecesInGoal);
        }
    }

    public class GetPossibleMovesTests
    {
        // ── Helpers ──────────────────────────────────────────────────────────────

        /// <summary>Wraps Game's getPossibleMoves via a minimal Game instance.</summary>
        private static (List<int> moves, TestGame game) GetMovesWithGame(GameBoard board, Player p1, Player p2, Player activePlayer, int roll)
        {
            var game = new TestGame(p1, p2, board);
            var moves = game.CallGetPossibleMoves(activePlayer, roll);
            return (moves, game);
        }

        private static List<int> GetMoves(GameBoard board, Player p1, Player p2, Player activePlayer, int roll)
        {
            return GetMovesWithGame(board, p1, p2, activePlayer, roll).moves;
        }

        // ── Roll zero ─────────────────────────────────────────────────────────────

        [Fact]
        public void RollZero_ReturnsEmptyList()
        {
            var (board, p1, p2) = TestHelpers.NewGame();
            TestHelpers.PlacePieceAtProgress(board, p1, 2);
            var moves = GetMoves(board, p1, p2, p1, 0);
            Assert.Empty(moves);
        }

        // ── No legal moves ────────────────────────────────────────────────────────

        [Fact]
        public void NoLegalMoves_ReturnsEmptyList()
        {
            var (board, p1, p2) = TestHelpers.NewGame();
            // Block the only destination: p1 piece at progress 12, friendly at 13
            var piece = TestHelpers.PlacePieceAtProgress(board, p1, 12);
            var blocker = TestHelpers.PlacePieceAtProgress(board, p1, 13);
            // p1 has no pieces in hand (we placed 2)
            p1.piecesInHand = 0; // override

            var moves = GetMoves(board, p1, p2, p1, 1);
            Assert.DoesNotContain(p1.movementPattern[12], moves);
        }

        // ── State not corrupted after call ────────────────────────────────────────

        [Fact]
        public void GetPossibleMoves_DoesNotCorruptBoardState()
        {
            var (board, p1, p2) = TestHelpers.NewGame();
            TestHelpers.PlacePieceAtProgress(board, p1, 2); // board idx = movementPattern[2] = 2

            int handBefore = p1.piecesInHand;
            int goalBefore = p1.piecesInGoal;
            int boardIdx = p1.movementPattern[2]; // = 2

            var (moves, game) = GetMovesWithGame(board, p1, p2, p1, 2);

            // After probing, undoMove() restores the board. Access state through the game
            // so we follow the restored reference rather than the stale local 'board'.
            var restoredPiece = game.CurrentBoard.gameBoard[boardIdx];
            Assert.NotNull(restoredPiece);
            Assert.Equal(2, restoredPiece.movementCounter);
            Assert.Equal(handBefore, game.CurrentPlayer1.piecesInHand);
            Assert.Equal(goalBefore, game.CurrentPlayer1.piecesInGoal);
        }

        // ── Correct move list ─────────────────────────────────────────────────────

        [Fact]
        public void GetPossibleMoves_IncludesHandPlacement()
        {
            var (board, p1, p2) = TestHelpers.NewGame();
            // p1 has 7 pieces in hand, none on board
            var moves = GetMoves(board, p1, p2, p1, 2);
            Assert.Contains(-1, moves); // -1 means place from hand
        }

        [Fact]
        public void GetPossibleMoves_ExcludesBlockedMoves()
        {
            var (board, p1, p2) = TestHelpers.NewGame();
            // p1 piece at progress 2 (board 2), p1 blocker at progress 4 (board 6)
            var piece = TestHelpers.PlacePieceAtProgress(board, p1, 2);
            var blocker = TestHelpers.PlacePieceAtProgress(board, p1, 4); // progress 4 = board 6
            p1.piecesInHand = 0;

            var moves = GetMoves(board, p1, p2, p1, 2); // would land at progress 4 — blocked
            Assert.DoesNotContain(p1.movementPattern[2], moves);
        }
    }

    /// <summary>
    /// Thin wrapper around Game that exposes getPossibleMoves for testing.
    /// </summary>
    internal class TestGame : Game
    {
        public TestGame(Player p1, Player p2, GameBoard board)
        {
            this.player1 = p1;
            this.player2 = p2;
            this.gameBoard = board;
        }

        public List<int> CallGetPossibleMoves(Player player, int roll)
        {
            return getPossibleMoves(player, roll);
        }

        /// <summary>Returns the current board (may change after undoMove).</summary>
        public GameBoard CurrentBoard => gameBoard;
        /// <summary>Returns the current player1 state (may change after undoMove).</summary>
        public Player CurrentPlayer1 => player1;
        /// <summary>Returns the current player2 state (may change after undoMove).</summary>
        public Player CurrentPlayer2 => player2;
    }
}
