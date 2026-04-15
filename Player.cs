namespace Ur
{
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
}
