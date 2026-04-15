namespace Ur
{
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

        public void captured()
        {
            // reset piece after being captured
            inHand = true;
            movementCounter = -1;
        }

        internal GamePiece Clone()
        {
            GamePiece piece = new GamePiece(player);
            piece.movementCounter = this.movementCounter;
            piece.inHand = this.inHand;
            return piece;
        }
    }
}
