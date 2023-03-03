using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.ServiceModel.Channels;
using System.Transactions;
using NetMQ;
using NetMQ.Sockets;

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
                    // move the piece, and if its successful add its index to the list and then undo the move
                    updateStacks();
                    int result = gameBoard.movePiece(player, getOppositePlayer(player), gameBoard.getPiece(idx), roll);
                    if (result == 0)
                    {
                        possibleMoves.Add(idx);
                        undoMove();
                    }
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
                if (result == 0) {
                    possibleMoves.Add(-1);
                    undoMove();
                }
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

        public int playGame()
        {
            // ZeroMQ setup for IPC between .Net and Python
            var pushSocket = new NetMQ.Sockets.PushSocket();
            pushSocket.Bind("tcp://localhost:4976");    // not actually tcp, but pretends to be and is very fast

            var pullSocket = new NetMQ.Sockets.PullSocket();
            pullSocket.Connect("tcp://localhost:4977");

            // Gameplay loop
            currentPlayer = 1;
            int roll = 0;
            while (player1.piecesInGoal < 7 && player2.piecesInGoal < 7)
            {
                // Add the current game state to the stack before making a move
                updateStacks();

                // clear terminal
                //Console.Clear();

                // output game info
                //Console.WriteLine("Player 1 has " + player1.piecesInGoal + " pieces in goal and " + player1.piecesInHand + " pieces in hand.");
                //Console.WriteLine("Player 2 has " + player2.piecesInGoal + " pieces in goal and " + player2.piecesInHand + " pieces in hand.");

                // roll
                roll = this.roll();
                //Console.WriteLine("Player " + currentPlayer + " rolled a " + roll);

                // gather possible moves
                List<int> possibleMoves = getPossibleMoves(getCurrentPlayer(), roll);
                if (possibleMoves.Count == 0)
                {
                    //Console.WriteLine("Player " + currentPlayer + " has no legal moves. Skipping turn.");
                    //System.Threading.Thread.Sleep(1500);
                    changeTurns();
                    continue;
                }

                int move;
                if (currentPlayer == 1)
                {
                    // compile and send game state to python client for neural network 
                    sendState(pushSocket, roll, possibleMoves);
                    // receive move from python client
                    string networkMove = pullSocket.ReceiveFrameString();
                    //Console.WriteLine("Received move: " + networkMove);
                    move = Int32.Parse(networkMove);
                }
                else
                {
                    // P2 random move
                    Random random = new Random();
                    move = possibleMoves[random.Next(0, possibleMoves.Count)];
                }
                // Show board
                //gameBoard.printBoard();

                // A.I. vs A.I.
                //System.Threading.Thread.Sleep(1500);

                //Calculate reward and done
                int reward = 0;
                bool done = false;
                int currentPlayerScore = getCurrentPlayer().piecesInGoal;

                // perform move
                if (move == -1)
                {
                    gameBoard.movePiece(getCurrentPlayer(), getOppositePlayer(getCurrentPlayer()), new GamePiece(getCurrentPlayer()), roll);
                }
                else
                {
                    gameBoard.movePiece(getCurrentPlayer(), getOppositePlayer(getCurrentPlayer()), gameBoard.getPiece(move), roll);
                }
                
                // simple short-term memory reward system, revamp later
                if (currentPlayerScore < getCurrentPlayer().piecesInGoal)
                {
                    reward = 1;
                }

                // check if game is over
                if (getCurrentPlayer().piecesInGoal == 7)
                {
                    done = true;
                }

                if (currentPlayer == 1)
                {
                    // send reward and done to python client
                    pushSocket.SendFrame(reward.ToString());
                    pushSocket.SendFrame(done.ToString());

                    // send new game state to python client
                    sendState(pushSocket, roll, possibleMoves);
                }

                if (getCurrentPlayer().hasDouble)
                {
                    //Console.WriteLine("Double Roll! Go again.");
                    //System.Threading.Thread.Sleep(1500);
                }
                else
                {
                    changeTurns();
                }


                // A.I. vs Human
                /*if (currentPlayer == 2)
                {
                    System.Threading.Thread.Sleep(1500);
                    Random rand = new Random();
                    int move = possibleMoves[rand.Next(0, possibleMoves.Count)];
                    if (move == -1)
                    {
                        gameBoard.movePiece(player2, player1, new GamePiece(player2), roll);
                    }
                    else
                    {
                        gameBoard.movePiece(player2, player1, gameBoard.getPiece(move), roll);
                    }
                }
                else
                {
                    // ask and execute human move
                    humanMove(roll);
                }*/
            }
            int winner = (player1.piecesInGoal > player2.piecesInGoal) ? player1.playerNum : player2.playerNum;
            pushSocket.Unbind("tcp://localhost:4976");
            pushSocket.Close();
            pullSocket.Close();
            return winner;
        }

        public void sendState(PushSocket pushSocket, int roll, List<int> possibleMoves)
        {
            // compile and send game state to python client for neural network 
            pushSocket.SendFrame(string.Join("", gameBoard.getBoardasInt()) + roll +
                getCurrentPlayer().piecesInGoal + getCurrentPlayer().piecesInHand +
                getOppositePlayer(getCurrentPlayer()).piecesInGoal + getOppositePlayer(getCurrentPlayer()).piecesInHand);
            pushSocket.SendFrame(string.Join(",", possibleMoves));
        }

// DOESN"T ACCOUNT FOR OTHER PLAYERS MOVE
/*        public double evaluateMove(int move, int roll)
        {
            // both positive and negative score is calculated here. e.g. if a point is scored, +8. If a piece is sent back to hand, -1
            // or enemy point scored, -8. If enemy piece is sent back to hand, +1
            // DONT UNDO MOVE. PERFORM, EVALUATE, AND LEAVE IT
            int before = getCurrentPlayer().piecesInGoal;
            if (move == -1)
            {
                gameBoard.movePiece(getCurrentPlayer(), getOppositePlayer(getCurrentPlayer()), new GamePiece(getCurrentPlayer()), roll);
            }
            else
            {
                gameBoard.movePiece(getCurrentPlayer(), getOppositePlayer(getCurrentPlayer()), gameBoard.getPiece(move), roll);
            }
            return 10*(getCurrentPlayer().piecesInGoal - before);
        }

        public double bestMoveSearch(List<int> possibleMoves, int roll, double score=0.0, int depth = 5)
        {
            //TODO: store gamestate evalutations in a hashmap for constant time calculation
            if (depth == 0)
            {
                return score;
            }
            if (possibleMoves.Count == 0)
            {
                return score;
            }
            depth--;
            double[] moveScores = new double[possibleMoves.Count];
            for (int i = 0; i < possibleMoves.Count; i++)
            {
                moveScores[i] = evaluateMove(possibleMoves[i], roll);
                score += moveScores[i];
                // add up calls for possible rolls multiplied by their respective probability and add to score
                score += (0.0625*bestMoveSearch(getPossibleMoves(getCurrentPlayer(), 0), 0, score, depth)) +
                    (0.25 * bestMoveSearch(getPossibleMoves(getCurrentPlayer(), 1), 1, score, depth)) +
                    (0.375 * bestMoveSearch(getPossibleMoves(getCurrentPlayer(), 2), 2, score, depth)) +
                    (0.25 * bestMoveSearch(getPossibleMoves(getCurrentPlayer(), 3), 3, score, depth)) +
                    (0.0625 * bestMoveSearch(getPossibleMoves(getCurrentPlayer(), 4), 4, score, depth));
                undoMove();
            }
            return possibleMoves[Array.IndexOf(moveScores, moveScores.Max())];
        }*/


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
            int p1=0, p2=0;
            for (int i = 0; i < 2000; i++) { 
                Game game = new Game();
                game.player1 = new Player(1);
                game.player2 = new Player(2);
                game.gameBoard = new GameBoard();
                int winner = game.playGame();
                if (winner == 1) { p1 += 1; } else if (winner == 2){ p2 += 1; }
                Console.WriteLine("Player 1: " + p1 + " Player 2: " + p2);
            }
        }
    }

    class Player
    {
        public int playerNum;
        public int[] movementPattern;
        public int piecesInGoal = 0;
        public int piecesInHand = 7;
        internal bool hasDouble;

        public Player(int playerNum)
        {
            this.playerNum = playerNum;
            this.hasDouble = false;
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
            this.hasDouble = player.hasDouble;
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
            // player.piecesInHand += 1;
        }

        internal GamePiece Clone()
        {
            GamePiece piece = new GamePiece(player);
            piece.movementCounter = this.movementCounter;
            piece.inHand = this.inHand;
            return piece;
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

        public int[] getBoardasInt()
        {
            int[] boardAsInt = new int[20];
            for (int i = 0; i < gameBoard.Length; i++)
            {
                if (gameBoard[i] == null)
                {
                    boardAsInt[i] = 0;
                }
                else
                {
                    boardAsInt[i] = gameBoard[i].player.playerNum;
                }
            }
            return boardAsInt;
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
            if (roll == 0)
            {
                return 0;
            }

            piece.movementCounter += roll;
            // Goal condition
            if (piece.movementCounter == 14)
            {
                player.pieceRanHome();
                gameBoard[player.movementPattern[piece.movementCounter - roll]] = null;
                return 0;
            }
            else if (piece.movementCounter > 14) {
                piece.movementCounter -= roll;
                return 1;
            }
            // translate destination index
            int destinationIdx = player.movementPattern[piece.movementCounter];

            // typical movement and/or capture
            // check if space is occupied. detectCollision will return 0 for empty or 1 or 2 for playerNum occupying
            switch (detectCollision(destinationIdx))
            {
                case 0:
                    gameBoard[destinationIdx] = piece;
                    break;
                case 1:
                    if (player.playerNum == 1 || destinationIdx == 9) {
                        piece.movementCounter -= roll;
                        return 1;
                    }
                    else{
                        capturePiece(piece, destinationIdx);
                        opponent.piecesInHand++;
                    }
                    break;
                case 2:
                    if (player.playerNum == 2 || destinationIdx == 9) {
                        piece.movementCounter -= roll;
                        return 1;
                    }
                    else {
                        capturePiece(piece, destinationIdx);
                        opponent.piecesInHand++;
                    }
                    break;
            }
            // if destination happens to be a double roll sqaure
            if (new HashSet<int> { 3, 4, 9, 17, 18 }.Contains(destinationIdx)) {
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
    
