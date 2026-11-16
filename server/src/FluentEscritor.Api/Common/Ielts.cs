namespace FluentEscritor.Api.Common;

/// <summary>IELTS arithmetic shared by the API. Ported from <c>src/lib/ielts-utils.ts</c>.</summary>
public static class Ielts
{
    public const string Task1 = "task1";
    public const string Task2 = "task2";

    /// <summary>
    /// Official rounding to the nearest half band: .25 rounds up to .5 and .75 up to the
    /// next whole band. The value is first limited to 0–9.
    /// </summary>
    public static double RoundBand(double value)
    {
        var clamped = Math.Clamp(value, 0, 9);
        // Halves always round up, as in the original; Math.Round would round them to even.
        return Math.Floor((clamped * 2) + 0.5) / 2;
    }

    /// <summary>The combined Writing score: Task 2 carries twice the weight of Task 1.</summary>
    public static double WeightedExamBand(double task1Band, double task2Band) =>
        RoundBand((task1Band + (task2Band * 2)) / 3);

    /// <summary>Words separated by any white space.</summary>
    public static int CountWords(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 0;
        var inWord = false;
        foreach (var c in text)
        {
            // U+FEFF counts as white space in the browser's count, so it does here too.
            var space = char.IsWhiteSpace(c) || c == '﻿';
            if (!space && !inWord)
            {
                count++;
            }

            inWord = !space;
        }

        return count;
    }

    public static int MinimumWords(string taskType) => taskType == Task1 ? 150 : 250;

    public static int RecommendedMinutes(string taskType) => taskType == Task1 ? 20 : 40;

    /// <summary>The default drill difficulty for a student's current band.</summary>
    public static string LevelFromBand(double? band) => band switch
    {
        null or double.NaN => "intermediate",
        <= 5.5 => "beginner",
        <= 6.5 => "intermediate",
        _ => "advanced",
    };

    public static bool IsDrillLevel(string? value) => value is "beginner" or "intermediate" or "advanced";
}
