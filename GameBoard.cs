using System;
using System.Collections.Generic;
using System.Linq;

namespace Ur
{
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


        // Rosette squares that grant a double turn. Index 9 is also the safe square (no captures).
        private static readonly HashSet<int> RosetteSquares = new HashSet<int> { 3, 4, 9, 17, 18 };

        // Boardspaces are either null/empty or Gamepiece
        public GamePiece[] gameBoard = new GamePiece[20];

        public GameBoard()
        {
            for (int i = 0; i < gameBoard.Length; i++)
            {
                gameBoard[i] = null;
            }
        }

        public List<int> getPlayerPieceIndexes(Player player)
        {
            List<int> playerPieceIndexes = new List<int>();
            for (int i = 0; i < gameBoard.Length; i++)
            {
                if (gameBoard[i] != null && gameBoard[i].player.playerNum == player.playerNum)
                {
                    playerPieceIndexes.Add(i);
                }
            }
            return playerPieceIndexes;
        }
        public GameBoard(GameBoard gameBoardTarget)
        {
            this.gameBoard = gameBoardTarget.gameBoard.Select(a => a != null ? (GamePiece)a.Clone() : null).ToArray();
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
        public int movePiece(Player player, Player opponent, GamePiece piece, int roll)
        {
            if (player.hasDouble)
            {
                player.hasDouble = false;
            }
            // Fix: roll == 0 means no movement is possible; return failure (1) rather than success (0).
            if (roll == 0)
            {
                return 1;
            }

            // Fix: calculate prospective new counter without mutating the piece yet.
            int newCounter = piece.movementCounter + roll;

            // Goal condition
            if (newCounter == 14)
            {
                int previousBoardIdx = player.movementPattern[piece.movementCounter];
                piece.movementCounter = newCounter;
                player.pieceRanHome();
                if (!piece.inHand)
                {
                    gameBoard[previousBoardIdx] = null;
                }
                else
                {
                    player.piecesInHand--;
                    piece.inHand = false;
                }
                return 0;
            }
            else if (newCounter > 14)
            {
                // Overshoot: move is illegal, piece is not mutated.
                return 1;
            }

            // translate destination index
            int destinationIdx = player.movementPattern[newCounter];

            // typical movement and/or capture
            // check if space is occupied. detectCollision will return 0 for empty or 1 or 2 for playerNum occupying
            switch (detectCollision(destinationIdx))
            {
                case 0:
                    // Destination empty — commit movement
                    piece.movementCounter = newCounter;
                    gameBoard[destinationIdx] = piece;
                    break;
                case 1:
                    if (player.playerNum == 1 || destinationIdx == 9)
                    {
                        return 1;
                    }
                    else
                    {
                        piece.movementCounter = newCounter;
                        capturePiece(piece, destinationIdx);
                        opponent.piecesInHand++;
                    }
                    break;
                case 2:
                    if (player.playerNum == 2 || destinationIdx == 9)
                    {
                        return 1;
                    }
                    else
                    {
                        piece.movementCounter = newCounter;
                        capturePiece(piece, destinationIdx);
                        opponent.piecesInHand++;
                    }
                    break;
            }
            // if destination happens to be a double roll square
            if (RosetteSquares.Contains(destinationIdx)) {
                player.hasDouble = true;
            }
            // remove piece from previous position, provided it was not in hand
            if (piece.inHand) {
                piece.inHand = false;
                player.piecesInHand--;
                return 0;
            }
            // need to translate movement counter back to index in movement pattern array to address the correct gameboard index
            gameBoard[player.movementPattern[piece.movementCounter - roll]] = null;
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
