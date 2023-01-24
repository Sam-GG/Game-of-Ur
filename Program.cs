using System;

namespace Ur
{
    
    class Program
    {
        static void Main(string[] args)
        {
            
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
    }
    class GamePiece
    {
        // Gamepieces keep track of movement, so movement counter directly corresponds to index of playermovement array to 
        // get index for gameboard array. For example, If a piece is movement counter 3, and I roll a 2, add 2 to the counter to get 5,
        // and then check index 5 of that players movement array to get the index the piece should move to in the gameboard array.
        public int playerNum;
        public int movementCounter;
        public bool inHand = true;
        public bool inGoal = false;

        public GamePiece(int playerNum)
        {
            this.playerNum = playerNum;
            this.movementCounter = 0;
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

        // Move piece
        public void movePiece(Player player, GamePiece piece, int roll)
        {
            piece.movementCounter += roll;
            // Goal condition
            if (piece.movementCounter > 14)
            {
                piece.inGoal = true;
            }
            // typical movement
            else
            {
                gameBoard[player.movementPattern[piece.movementCounter]] = piece;
            }
            // remove piece from previous position
            gameBoard[piece.movementCounter - roll] = null;
        }
    }
}
