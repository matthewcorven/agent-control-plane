#!/usr/bin/env dotnet
#:include ../examples/chess-rules.cs
using ChessDotNet;

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var game = new ChessRules();
var start = game.Fen;
var opening = game.LegalMoves();
Require(opening.Count == 20 && opening.Contains("e2e4") && !opening.Contains("e2e5"), "opening legal moves");
Require(game.Fen == start, "enumeration changed position");
foreach (var move in new string?[] { null, "", "e2e5", "e7e5", "e2e4!", "E2E4", "e2e4q" })
{
    Require(!game.TryMove(move), $"accepted {move}");
    Require(game.Fen == start, $"changed board for {move}");
}
Require(game.TryMove("f2f3"), "f2f3");
Require(game.TryMove("e7e5"), "e7e5");
Require(game.TryMove("g2g4"), "g2g4");
Require(game.TryMove("d8h4"), "d8h4");
Require(game.Checkmate, "fool's mate");
var pinned = new ChessRules("k3r3/8/8/8/8/8/4R3/4K3 w - - 0 1");
var before = pinned.Fen;
Require(!pinned.TryMove("e2d2") && pinned.Fen == before, "pin must be enforced");
var promotion = new ChessRules("4k3/P7/8/8/8/8/8/4K3 w - - 0 1");
Require(promotion.LegalMoves().Where(m => m.StartsWith("a7a8", StringComparison.Ordinal)).Order().SequenceEqual(new[] { "a7a8b", "a7a8n", "a7a8q", "a7a8r" }), "promotion enumeration");
Require(!promotion.TryMove("a7a8"), "promotion piece required");
Require(promotion.TryMove("a7a8n"), "underpromotion");
var enPassant = new ChessRules("4k3/8/8/3pP3/8/8/8/4K3 w - d6 0 1");
Require(enPassant.TryMove("e5d6"), "en passant");
Console.WriteLine("Chess legality checks passed.");
