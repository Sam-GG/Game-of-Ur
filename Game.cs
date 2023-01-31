using System;

namespace Ur
{
    
    class Game
    {
        Player player1;
        Player player2;
        GameBoard gameBoard;

        public void undoMove()
        {
            //TODO: implement undo move
            // I think an undo last move feature would allow easy generation of possible moves
            // by utilizing the already in place moving system, checking for return of 0 on move success,
            // and then undoing the move. could also prove useful later for AI implementation
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

        static void Main(string[] args)
        {
            Game game = new Game();
            game.player1 = new Player(1);
            game.player2 = new Player(2);
            game.gameBoard = new GameBoard();
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

        // Move piece. returns 0 on successful movement or capture of piece, and 1 on inability to move piece.
        public int movePiece(Player player, GamePiece piece, int roll)
        {
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
            gameBoard[piece.movementCounter - roll] = null;
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
