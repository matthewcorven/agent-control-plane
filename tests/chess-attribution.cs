#!/usr/bin/env dotnet
#:include ../examples/chess-attribution.cs
#:include ../examples/chess-rules.cs

const string reply = "{\"result\":{\"message\":{\"role\":\"ROLE_AGENT\",\"parts\":[{\"text\":\"e2e4\"}]}}}";
static void Reject(Action action)
{
    try { action(); } catch (InvalidOperationException) { return; }
    throw new Exception("Expected rejection");
}
var gate = new MoveAttribution();
var game = new ChessRules();
var initial = game.Fen;
gate.Begin("White");
Reject(() => gate.Complete("e2e4"));
gate.Begin("White");
var id = gate.Start("Black"); gate.Finish(id, true, reply);
Reject(() => gate.Complete("e2e4"));
gate.Begin("White");
id = gate.Start("White"); gate.Finish(id, true, reply);
Reject(() => gate.Complete("d2d4"));
gate.Begin("White");
id = gate.Start("White"); gate.Finish(id, false, reply);
Reject(() => gate.Complete("e2e4"));
gate.Begin("White");
id = gate.Start("White"); gate.Finish(id, true, "{}");
Reject(() => gate.Complete("e2e4"));
gate.Begin("White");
id = gate.Start("White"); gate.Finish(id, true, reply); gate.Start("White");
Reject(() => gate.Complete("e2e4"));
gate.Begin("White");
gate.Finish(id, true, reply);
Reject(() => gate.Complete("e2e4"));
if (game.Fen != initial) throw new Exception("Rejected attribution changed board");
gate.Begin("White");
id = gate.Start("White"); gate.Finish(id, true, reply);
if (!game.TryMove(gate.Complete("e2e4"))) throw new Exception("Valid attributed move rejected");
var after = game.Fen;
gate.Begin("Black");
id = gate.Start("Black"); gate.Finish(id, true, reply);
if (game.TryMove(gate.Complete("e2e4")) || game.Fen != after) throw new Exception("Illegal attributed move changed board");
Console.WriteLine("Attribution and legality rejection checks passed.");

const string request = "{\"params\":{\"message\":{\"parts\":[{\"text\":\"actual white request\"}]}}}";
if (ChessTranscript.Text(request, false) != "actual white request" || ChessTranscript.Text(reply, true) != "e2e4" || ChessTranscript.Text("{}", false) is not null)
    throw new Exception("A2A transcript extraction failed");
if (!ChessTranscript.Bound(new string('x', 301)).EndsWith("[truncated]", StringComparison.Ordinal)) throw new Exception("Transcript was not bounded");
