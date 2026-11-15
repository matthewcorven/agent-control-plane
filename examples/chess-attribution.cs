using System.Text.Json;

sealed class MoveAttribution
{
    private readonly object gate = new();
    private int generation, started, finished;
    private string? expected, observed;
    private bool invalid;

    public void Begin(string color)
    {
        lock (gate)
        {
            if (expected is not null) throw new InvalidOperationException("Previous turn not closed");
            generation++; expected = color; started = finished = 0; observed = null; invalid = false;
        }
    }

    public int Start(string color)
    {
        lock (gate)
        {
            if (expected is null) throw new InvalidOperationException("A2A request outside an active turn");
            started++;
            if (color != expected || started != 1) invalid = true;
            return generation;
        }
    }

    public void Finish(int turn, bool success, string json)
    {
        lock (gate)
        {
            if (turn != generation || expected is null) return;
            finished++;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var message = doc.RootElement.GetProperty("result").GetProperty("message");
                var parts = message.GetProperty("parts");
                if (!success || message.GetProperty("role").GetString() != "ROLE_AGENT" || parts.GetArrayLength() != 1)
                    throw new InvalidOperationException();
                observed = parts[0].GetProperty("text").GetString()?.Trim();
                if (string.IsNullOrEmpty(observed)) invalid = true;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                invalid = true;
            }
        }
    }

    public string Complete(string coordinatorResponse)
    {
        lock (gate)
        {
            try
            {
                if (expected is null || invalid || started != 1 || finished != 1 || observed is null ||
                    !string.Equals(observed, coordinatorResponse.Trim(), StringComparison.Ordinal))
                    throw new InvalidOperationException("Coordinator response is not attributable to exactly one completed active-player A2A response");
                return observed;
            }
            finally { expected = null; }
        }
    }
}
