#:package ChessDotNet@1.0.0
using ChessDotNet;

// A strict UCI move boundary; the game state changes only for legal moves.
sealed class ChessRules
{
    private readonly ChessGame game;
    public ChessRules(string? fen = null) => game = fen is null ? new ChessGame() : new ChessGame(fen);
    public string Fen => game.GetFen();
    public Player Turn => game.WhoseTurn;
    public bool Checkmate => game.IsCheckmated(game.WhoseTurn);
    public bool Stalemate => game.IsStalemated(game.WhoseTurn);
    public IReadOnlyList<string> LegalMoves()
    {
        var result = new List<string>();
        for (char fromFile = 'a'; fromFile <= 'h'; fromFile++)
        for (char fromRank = '1'; fromRank <= '8'; fromRank++)
        for (char toFile = 'a'; toFile <= 'h'; toFile++)
        for (char toRank = '1'; toRank <= '8'; toRank++)
        {
            var from = $"{fromFile}{fromRank}";
            var to = $"{toFile}{toRank}";
            var piece = game.GetPieceAt(new Position(from));
            bool promotion = piece is ChessDotNet.Pieces.Pawn &&
                ((game.WhoseTurn == Player.White && toRank == '8') || (game.WhoseTurn == Player.Black && toRank == '1'));
            foreach (char suffix in promotion ? "qrbn" : "\0")
                if (game.IsValidMove(new Move(from, to, game.WhoseTurn, suffix == '\0' ? null : suffix)))
                    result.Add(from + to + (suffix == '\0' ? "" : suffix.ToString()));
        }
        return result;
    }

    public bool TryMove(string? text)
    {
        if (text is null || text.Length is not (4 or 5) ||
            text[0] is < 'a' or > 'h' || text[1] is < '1' or > '8' ||
            text[2] is < 'a' or > 'h' || text[3] is < '1' or > '8' ||
            (text.Length == 5 && text[4] is not ('q' or 'r' or 'b' or 'n')))
            return false;
        var move = new Move(text[..2], text.Substring(2, 2), game.WhoseTurn,
            text.Length == 5 ? text[4] : null);
        var piece = game.GetPieceAt(move.OriginalPosition);
        bool promotes = piece is ChessDotNet.Pieces.Pawn &&
            ((game.WhoseTurn == Player.White && text[3] == '8') ||
             (game.WhoseTurn == Player.Black && text[3] == '1'));
        if ((text.Length == 5) != promotes) return false;
        return game.MakeMove(move, false) != MoveType.Invalid;
    }
}
