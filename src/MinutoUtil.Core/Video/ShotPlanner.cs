using MinutoUtil.Core.Voice;

namespace MinutoUtil.Core.Video;

public enum ShotEffect { Clip, KenBurns, Parallax }

/// <summary>
/// One shot of the video: from cut frame <see cref="Start"/> to <see cref="End"/> (the cross-fade is centered on each
/// cut), while section <see cref="Section"/> is spoken (0 is the hook).
/// </summary>
public sealed record Shot(int Index, int Section, int Start, int End, ShotEffect Effect, int Direction, bool ZoomIn)
{
    public int Frames => End - Start;

    /// <summary>The media item shown, set once pictures are assigned.</summary>
    public string? MediaId { get; init; }
}

/// <summary>
/// Turns the voice's timing into shots. Each section gets one shot, or two (three at most) when it runs longer than
/// <see cref="VideoOptions.MaxShotSeconds"/>, split at the word nearest the middle, preferring a word that ends a
/// clause. Cuts fall in the pause before a section's first word, so the picture changes with the story. Still shots
/// alternate parallax and Ken Burns, and their direction alternates too, so no two neighbours move alike.
/// </summary>
public static class ShotPlanner
{
    /// <param name="clipSeconds">Length of the real footage clip for the first shot; 0 when there is none.</param>
    public static IReadOnlyList<Shot> Plan(SpeechTimings timings, double duration, VideoOptions options, double clipSeconds)
    {
        var fps = options.Fps;
        var totalFrames = (int)Math.Round(duration * fps);
        var sections = timings.Sections;

        // Section boundaries: halfway through the pause between one section's end and the next one's start.
        var bounds = new List<double> { 0 };
        for (var i = 1; i < sections.Count; i++) bounds.Add((sections[i - 1].End + sections[i].Start) / 2);
        bounds.Add(duration);

        var cuts = new List<(double Time, int Section)>();
        for (var s = 0; s < sections.Count; s++)
        {
            var (from, to) = (bounds[s], bounds[s + 1]);
            cuts.Add((from, s));
            var span = to - from;
            var limit = s == 0 && clipSeconds > 0 ? Math.Min(clipSeconds, options.MaxShotSeconds) : options.MaxShotSeconds;
            var pieces = Math.Min(3, (int)Math.Ceiling(span / limit - 0.05));
            if (s == 0 && clipSeconds > 0 && span > clipSeconds)
            {
                // The clip runs as long as it is, then a still takes over for the rest of the hook.
                var cut = SplitNear(timings.Words, s, from + clipSeconds - options.FadeSeconds / 2, from + 1.2, Math.Min(to - 1.2, from + clipSeconds));
                if (cut > from + 1 && cut < to - 1) cuts.Add((cut, s));
                continue;
            }
            for (var p = 1; p < pieces; p++)
            {
                var target = from + span * p / pieces;
                var cut = SplitNear(timings.Words, s, target, from + 1.5, to - 1.5);
                if (cut > cuts[^1].Time + 1.5 && cut < to - 1.5) cuts.Add((cut, s));
            }
        }

        var shots = new List<Shot>();
        var stills = 0;
        for (var i = 0; i < cuts.Count; i++)
        {
            var start = (int)Math.Round(cuts[i].Time * fps);
            var end = i + 1 < cuts.Count ? (int)Math.Round(cuts[i + 1].Time * fps) : totalFrames;
            ShotEffect effect;
            if (i == 0 && clipSeconds > 0) effect = ShotEffect.Clip;
            else effect = stills++ % 2 == 0 ? ShotEffect.Parallax : ShotEffect.KenBurns;
            // Directions alternate per still pair, so two parallax shots in a row of shots never slide the same way.
            var direction = stills / 2 % 2 == 0 ? 1 : -1;
            shots.Add(new Shot(i, cuts[i].Section, start, end, effect, direction, ZoomIn: stills % 3 != 0));
        }
        return shots;
    }

    /// <summary>
    /// A cut time between two words of the section, as close to <paramref name="target"/> as possible; a word that
    /// ends a clause (comma, period) wins when it is about as close.
    /// </summary>
    internal static double SplitNear(IReadOnlyList<SpokenWord> words, int section, double target, double earliest, double latest)
    {
        var own = words.Where(w => w.Section == section).ToList();
        var best = target;
        var bestCost = double.MaxValue;
        for (var i = 0; i + 1 < own.Count; i++)
        {
            var gap = (own[i].End + own[i + 1].Start) / 2;
            if (gap < earliest || gap > latest) continue;
            var cost = Math.Abs(gap - target);
            if (".,;:!?".Contains(own[i].Text[^1])) cost -= 0.8;
            if (cost < bestCost)
            {
                bestCost = cost;
                best = gap;
            }
        }
        return best;
    }

    /// <summary>
    /// Frames each shot's file must have so that cross-fades of <paramref name="fadeFrames"/> are centered on the cuts
    /// and the joined video is exactly as long as the last shot's end.
    /// </summary>
    public static int SegmentFrames(IReadOnlyList<Shot> shots, int index, int fadeFrames)
    {
        var half = fadeFrames / 2;
        var shot = shots[index];
        if (shots.Count == 1) return shot.Frames;
        var frames = shot.Frames + (index == 0 || index == shots.Count - 1 ? half : 2 * half);
        return frames;
    }
}
