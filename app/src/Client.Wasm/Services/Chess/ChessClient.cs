namespace AGUIWebChat.Client.Services.Chess;

public sealed record ChessMessage(string Actor, string Direction, string Text);
public sealed record ChessSnapshot(bool Running, string Fen, string BoardStatus, string Coordinator, string White, string Black, IReadOnlyList<ChessMessage> Messages);
public sealed class ChessClient(HttpClient http)
{
    private async Task PostAsync(string path) { using var request = new HttpRequestMessage(HttpMethod.Post, path); request.Headers.Add("X-Chess-Request", "1"); using var response = await http.SendAsync(request); response.EnsureSuccessStatusCode(); }
    public async Task<ChessSnapshot?> GetAsync() => await http.GetFromJsonAsync<ChessSnapshot>("/api/chess/");
    public Task StartAsync(int plies) => PostAsync($"/api/chess/start/{plies}");
    public Task StopAsync() => PostAsync("/api/chess/stop");
}
