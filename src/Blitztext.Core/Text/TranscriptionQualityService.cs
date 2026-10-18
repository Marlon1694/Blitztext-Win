namespace Blitztext.Core.Text;

public static class TranscriptionQualityService
{
    public const double MinimumRecordingDuration = 0.3;

    public static bool ShouldRejectRecording(double duration) => duration < MinimumRecordingDuration;

    public static string CleanedTranscript(string text) => text.Trim();

    public static bool IsLikelyArtifact(string text, double recordingDuration)
    {
        var cleaned = CleanedTranscript(text);
        if (cleaned.Length == 0) return true;

        var words = cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var letters = cleaned.Count(char.IsLetter);

        if (letters == 0) return true;
        if (recordingDuration < 0.55 && (words.Length >= 5 || cleaned.Length >= 32)) return true;
        if (recordingDuration < 0.8 && cleaned.Length >= 56) return true;
        return false;
    }
}
