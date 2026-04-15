using System;
using System.Collections.Generic;

namespace Ur
{
    class Game
    {
        protected Player player1;
        protected Player player2;
        int currentPlayer;
        protected GameBoard gameBoard;

        // for undoing moves, potentially expensive implementation - but easy
        Stack<GameBoard> gameStates = new Stack<GameBoard>();
        Stack<Player> player1States = new Stack<Player>();
        Stack<Player> player2States = new Stack<Player>();

        public void updateStacks()
        {
            // Add the current game state to the stack
            gameStates.Push(new GameBoard(gameBoard));
            player1States.Push(new Player(player1));
            player2States.Push(new Player(player2));
        }
        public void undoMove()
        {
            // I think an undo last move feature would allow easy generation of possible moves
            // by utilizing the already in place moving system, checking for return of 0 on move success,
            // and then undoing the move. could also prove useful later for AI implementation

            // The easiest way would be to have a stack of gameboard states and player states, and push the current state to the stack each move
            // Then, when undoing a move, pop the last state off the stack and set the current state to that
            // However, this could chew up memory, and add some overhead to the game loop. I'll likely start with this now and possibly redo later.
            if (gameStates.Count > 0)
            {
                // Get the previous game states from the stack and restore them
                gameBoard = gameStates.Pop();
                player1 = player1States.Pop();
                player2 = player2States.Pop();
            }
            else
            {
                Console.WriteLine("There are no moves to undo.");
            }
        }

        public List<int> getPossibleMoves(Player player, int roll)
        {
            List<int> possibleMoves = new List<int>();
            // if roll is 0, return empty list
            if (roll == 0)
            {
                return possibleMoves;
            }
            // check if any of the players onboard pieces can be moved
            foreach (int idx in gameBoard.getPlayerPieceIndexes(player))
            {
                try
                {
                    // move the piece, and if its successful add its index to the list, then always undo the move
                    updateStacks();
                    int result = gameBoard.movePiece(player, getOppositePlayer(player), gameBoard.getPiece(idx), roll);
                    if (result == 0)
                    {
                        possibleMoves.Add(idx);
                    }
                    undoMove(); // Always restore state after probing
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());
                }
            }
            // check if the player can place a piece
            if (player.piecesInHand > 0)
            {
                updateStacks();
                int result = gameBoard.movePiece(player, getOppositePlayer(player), new GamePiece(player), roll);
                if (result == 0)
                {
                    possibleMoves.Add(-1);
                }
                undoMove(); // Always restore state after probing
            }
            return possibleMoves;
        }
        public int roll()
        {
            // returns sum of 4 random numbers of either 0 or 1
            Random rand = new Random();
            int roll = 0;
            for (int i = 0; i < 4; i++)
            {
                roll += rand.Next(0, 2);
            }
            return roll;
        }

        public Player getCurrentPlayer()
        {
            return (currentPlayer == 1) ? player1 : player2;
        }

        public Player getOppositePlayer(Player player)
        {
            return (player.playerNum == player1.playerNum) ? player2 : player1;
        }

        public void changeTurns()
        {
            currentPlayer = (currentPlayer == 1) ? 2 : 1;
        }

        public void playGame()
        {
            // Gameplay loop
            currentPlayer = 1;
            int roll = 0;
            while (player1.piecesInGoal < 7 && player2.piecesInGoal < 7)
            {
                // Add the current game state to the stack before making a move
                updateStacks();

                // clear terminal
                Console.Clear();

                // output game info
                Console.WriteLine("Player 1 has " + player1.piecesInGoal + " pieces in goal and " + player1.piecesInHand + " pieces in hand.");
                Console.WriteLine("Player 2 has " + player2.piecesInGoal + " pieces in goal and " + player2.piecesInHand + " pieces in hand.");

                // roll
                roll = this.roll();
                Console.WriteLine("Player " + currentPlayer + " rolled a " + roll);

                // gather possible moves
                List<int> possibleMoves = getPossibleMoves(getCurrentPlayer(), roll);
                if (possibleMoves.Count == 0)
                {
                    Console.WriteLine("Player " + currentPlayer + " has no legal moves. Skipping turn.");
                    System.Threading.Thread.Sleep(1500);
                    changeTurns();
                    continue;
                }

                // Show board
                gameBoard.printBoard();

                // If A.I. determine move
                if (currentPlayer == 2)
                {
                    System.Threading.Thread.Sleep(1500);
                    Random rand = new Random();
                    int move = possibleMoves[rand.Next(0, possibleMoves.Count)];
                    if (move == -1)
                    {
                        gameBoard.movePiece(player2, player1, new GamePiece(player2), roll);
                    }else
                    {
                        gameBoard.movePiece(player2, player1, gameBoard.getPiece(move), roll);
                    }
                }
                else
                {
                    // ask and execute human move
                    humanMove(roll);
                }

                if (getCurrentPlayer().hasDouble)
                {
                    Console.WriteLine("Double Roll! Go again.");
                    System.Threading.Thread.Sleep(1500);
                }
                else
                {
                    changeTurns();
                }
            }
            Console.WriteLine("Game Complete.");
        }

        public void humanMove(int roll)
        {
            int result;
            // get move
            Console.WriteLine("Which piece do you want to move? Type index of piece (for placing new type 'new')");
            String pieceIndex = Console.ReadLine();

            // move piece
            if (pieceIndex == "new")
            {
                result = gameBoard.movePiece(getCurrentPlayer(), getOppositePlayer(getCurrentPlayer()), new GamePiece(getCurrentPlayer()), roll);
            }
            else if (int.TryParse(pieceIndex, out int value))
            {
                if (gameBoard.getPiece(int.Parse(pieceIndex)) == null)
                {
                    Console.WriteLine("Please choose a legal move.");
                    humanMove(roll);
                    return;
                }
                result = gameBoard.movePiece(getCurrentPlayer(), getOppositePlayer(getCurrentPlayer()), gameBoard.getPiece(int.Parse(pieceIndex)), roll);
            }
            else
            {
                Console.WriteLine("Entered Illegal input.");
                humanMove(roll);
                return;
            }
            if (result == 1)
            {
                Console.WriteLine("Please choose a legal move.");
                humanMove(roll);
            }
        }

        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--bridge")
            {
                EnvironmentBridge.RunBridge(args);
                return;
            }

            Game game = new Game();
            game.player1 = new Player(1);
            game.player2 = new Player(2);
            game.gameBoard = new GameBoard();
            game.playGame();
        }
    }
}

