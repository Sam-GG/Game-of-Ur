using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Runtime.InteropServices;
using System.Transactions;

namespace Ur
{
    
    class Game
    {
        Player player1;
        Player player2;
        int currentPlayer;
        GameBoard gameBoard;

        // for undoing moves, potentially expensive implementation - but easy
        Stack<GameBoard> gameStates = new Stack<GameBoard>();
        Stack<Player> player1States = new Stack<Player>();
        Stack<Player> player2States = new Stack<Player>();

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

                // Switch the current player to the one who made the previous move
                currentPlayer = (currentPlayer == 1) ? 2 : 1;
            }
            else
            {
                Console.WriteLine("There are no moves to undo.");
            }
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

        public void playGame()
        {
            // Gameplay loop
            currentPlayer = 1;
            int roll = 0;
            while (player1.piecesInGoal < 7 && player2.piecesInGoal < 7)
            {
                // Add the current game state to the stack before making a move
                gameStates.Push(new GameBoard(gameBoard));
                player1States.Push(new Player(player1));
                player2States.Push(new Player(player2));

                // clear terminal
                Console.Clear();

                // roll
                roll = this.roll();
                Console.WriteLine("Player " + currentPlayer + " rolled a " + roll);

                // If A.I.
                    // gather possible moves
                    // determine move

                // Show board
                gameBoard.printBoard();

                // ask and execute human move
                humanMove(roll);

                // switch player
                currentPlayer = (currentPlayer == 1) ? 2 : 1;
            }
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
                result = gameBoard.movePiece(getCurrentPlayer(), new GamePiece(getCurrentPlayer()), roll);
            }
            else
            {
                if (gameBoard.getPiece(int.Parse(pieceIndex)) == null)
                {
                    Console.WriteLine("Please choose a legal move.");
                    humanMove(roll);
                    return;
                }
                result = gameBoard.movePiece(getCurrentPlayer(), gameBoard.getPiece(int.Parse(pieceIndex)), roll);
            }
            if (result == 1)
            {
                Console.WriteLine("Please choose a legal move.");
                humanMove(roll);
            }
        }

        static void Main(string[] args)
        {
            Game game = new Game();
            game.player1 = new Player(1);
            game.player2 = new Player(2);
            game.gameBoard = new GameBoard();
            game.playGame();
        }
    }

    class Player
    {
        public int playerNum;
        public int[] movementPattern;
        public int piecesInGoal = 0;
        public int piecesInHand = 7;
        public Player(int playerNum)
        {
            this.playerNum = playerNum;
            if (playerNum == 1)
            {
                this.movementPattern = new int[] { 0, 1, 2, 3, 6, 7, 8, 9, 10, 11, 12, 13, 5, 4 };
            } else if (playerNum == 2)
            {
                this.movementPattern = new int[] { 14, 15, 16, 17, 6, 7, 8, 9, 10, 11, 12, 13, 19, 18 };
            }
        }

        public Player(Player player)
        {
            this.playerNum = player.playerNum;
            this.movementPattern = player.movementPattern;
            this.piecesInGoal = player.piecesInGoal;
            this.piecesInHand = player.piecesInHand;
        }

        internal void pieceRanHome()
        {
            piecesInGoal++;
        }

    }
    class GamePiece
    {
        // Gamepieces keep track of movement, so movement counter directly corresponds to index of playermovement array to 
        // get index for gameboard array. For example, If a piece is movement counter 3, and I roll a 2, add 2 to the counter to get 5,
        // and then check index 5 of that players movement array to get the index the piece should move to in the gameboard array.
        public Player player;
        public int movementCounter;
        public bool inHand = true;

        public GamePiece(Player player)
        {
            this.player = player;
            this.movementCounter = -1;
        }

        public void captured(){
            // reset piece after being captured
            inHand = true;
            movementCounter = -1;
            player.piecesInHand++;
        }
    }
    class GameBoard
    {
        /*
             Ascii-art representation of board indexing                            
             ████████████████        ████████ 
             █0 ██1 ██2 ██3 █        █4 ██5 █ 
             ████████████████        ████████ 
             ████████████████████████████████ 
             █6 ██7 ██8 ██9 ██10██11██12██13█ 
             ████████████████████████████████ 
             ████████████████        ████████ 
             █14██15██16██17█        █18██19█   
             ████████████████        ████████                
        */

        // going modified array route again, going to try and decouple as much as possible 
        // go with the above ascii art representation
        // have a separate reference array for each player that defines their movement pattern, but does not store any other info
        // used said reference array to advance player in actual gameboard array.


        // Boardspaces are either null/empty or Gamepiece
        GamePiece[] gameBoard = new GamePiece[20];

        public GameBoard()
        {
            for (int i = 0; i < gameBoard.Length; i++)
            {
                gameBoard[i] = null;
            }
        }

        public GameBoard(GameBoard gameBoardTarget)
        {
            Array.Copy(gameBoardTarget.gameBoard, this.gameBoard, 20);
        }

        public GamePiece getPiece(int idx)
        {
            if (gameBoard[idx] == null) {
                Console.WriteLine("Error, no piece at index " + idx + "");
            }
            return gameBoard[idx];
        }

        public String GameSpaceToSymbol(int idx)
        {
            // returns symbol for gameboard space
            if (gameBoard[idx] == null)
            {
                return " ";
            }
            else
            {
                return gameBoard[idx].player.playerNum.ToString();
            }
        }

        public void printBoard()
        {
            // lazy way to print board for now
            Console.WriteLine("████████████████        ████████");
            Console.WriteLine("█"+ GameSpaceToSymbol(0) +" ██" + GameSpaceToSymbol(1) + " ██"+ GameSpaceToSymbol(2)+" ██"+GameSpaceToSymbol(3)+" █        █" + GameSpaceToSymbol(4) + " ██" + GameSpaceToSymbol(5) + " █");
            Console.WriteLine("████████████████        ████████");
            Console.WriteLine("████████████████████████████████");
            Console.WriteLine("█" + GameSpaceToSymbol(6) + " ██" + GameSpaceToSymbol(7) + " ██" + GameSpaceToSymbol(8) + " ██" + GameSpaceToSymbol(9) + " ██" + GameSpaceToSymbol(10) + " ██" + GameSpaceToSymbol(11) + " ██" + GameSpaceToSymbol(12) + " ██" + GameSpaceToSymbol(13) + " █");
            Console.WriteLine("████████████████████████████████");
            Console.WriteLine("████████████████        ████████");
            Console.WriteLine("█" + GameSpaceToSymbol(14) + " ██" + GameSpaceToSymbol(15) + " ██" + GameSpaceToSymbol(16) + " ██" + GameSpaceToSymbol(17) + " █        █" + GameSpaceToSymbol(18) + " ██" + GameSpaceToSymbol(19) + " █");
            Console.WriteLine("████████████████        ████████"); 

        }

            // Move piece. returns 0 on successful movement or capture of piece, and 1 on inability to move piece.
        public int movePiece(Player player, GamePiece piece, int roll)
        {
            if (roll == 0)
            {
                return 0;
            }
            piece.movementCounter += roll;
            int destinationIdx = player.movementPattern[piece.movementCounter];
            // Goal condition
            if (piece.movementCounter > 14)
            {
                player.pieceRanHome();
            }
            // typical movement and/or capture
            else
            {
                // check if space is occupied. detectCollision will return 0 for empty or 1 or 2 for playerNum occupying
                switch (detectCollision(destinationIdx))
                {
                    case 0:
                        gameBoard[destinationIdx] = piece;
                        break;
                    case 1:
                        if (player.playerNum == 1) {
                            piece.movementCounter -= roll;
                            return 1; 
                        }
                        else{ capturePiece(piece, destinationIdx); }
                        break;
                    case 2:
                        if (player.playerNum == 2){
                            piece.movementCounter -= roll;
                            return 1;
                        }
                        else { capturePiece(piece, destinationIdx); }
                        break;
                }
            }
            // remove piece from previous position, provided it was not in hand
            if (piece.inHand) {
                piece.inHand = false;
                player.piecesInHand--;
                return 0;
            }
            // need to translate movement counter back to index in movement pattern array to address the correct gameboard index
            gameBoard[piece.player.movementPattern[piece.movementCounter - roll]] = null;
            return 0;
        }

        public int detectCollision(int space)
        // Detects if a space is occupied, and if so, returns the player number of the piece occupying it
        {
            if (gameBoard[space] == null)
                return 0;
            return gameBoard[space].player.playerNum;
        }

        public void capturePiece(GamePiece attackerPiece, int defenderIndex)
        {
            gameBoard[defenderIndex].captured();
            gameBoard[defenderIndex] = attackerPiece;
        }
    }
}
    
