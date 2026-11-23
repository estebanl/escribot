using System.Text;

namespace Escribot.Api.Ai;

/// <summary>
/// Watches streamed JSON chunks from an <c>AIAgent</c> and fires semantic progress milestones
/// once each expected top-level key appears, without exposing raw JSON fragments to the UI.
/// </summary>
internal sealed class JsonMilestoneScanner(
    Action<string>? onProgress,
    IReadOnlyList<(string Marker, string Message)> milestones)
{
    private readonly StringBuilder _buffer = new();
    private readonly bool[] _fired = new bool[milestones.Count];

    public void Append(string chunk)
    {
        if (onProgress is null || string.IsNullOrEmpty(chunk))
        {
            return;
        }

        _buffer.Append(chunk);
        var snapshot = _buffer.ToString();
        for (var i = 0; i < milestones.Count; i++)
        {
            if (!_fired[i] && snapshot.Contains(milestones[i].Marker, StringComparison.Ordinal))
            {
                _fired[i] = true;
                onProgress(milestones[i].Message);
            }
        }
    }
}
