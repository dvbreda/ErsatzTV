using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Scheduling;

namespace ErsatzTV.Core.Scheduling;

/// <summary>
/// Weighted shuffle enumerator: elke show krijgt gelijke frequentie ongeacht het aantal afleveringen.
/// Per "ronde" wordt één aflevering per show gepakt (in willekeurige show-volgorde).
/// Afleveringen binnen een show worden willekeurig geschud.
/// Met randomStartPoint begint elke show op een willekeurige aflevering.
/// </summary>
public class ShuffleWeightedCollectionEnumerator : IMediaCollectionEnumerator
{
    private readonly CancellationToken _cancellationToken;
    private readonly IList<CollectionWithItems> _collections;
    private readonly Lazy<Option<TimeSpan>> _lazyMinimumDuration;
    private readonly int _mediaItemCount;
    private readonly bool _randomStartPoint;
    private Random _random;
    private MediaItem[] _shuffled;

    public ShuffleWeightedCollectionEnumerator(
        IList<CollectionWithItems> collections,
        CollectionEnumeratorState state,
        bool randomStartPoint,
        CancellationToken cancellationToken)
    {
        CurrentIncludeInProgramGuide = Option<bool>.None;

        _collections = collections.Filter(c => c.MediaItems.Count > 0).ToList();
        _randomStartPoint = randomStartPoint;
        _cancellationToken = cancellationToken;

        int numShows = _collections.Count;
        int maxEps = numShows > 0 ? _collections.Max(c => c.MediaItems.Count) : 0;
        _mediaItemCount = numShows * maxEps;

        if (_mediaItemCount > 0 && state.Index >= _mediaItemCount)
        {
            state.Index = 0;
            state.Seed = new Random(state.Seed).Next();
        }

        _random = new Random(state.Seed);
        _shuffled = BuildWeightedSequence(_collections, _random);
        _lazyMinimumDuration =
            new Lazy<Option<TimeSpan>>(() =>
                _shuffled.Bind(i => i.GetNonZeroDuration()).OrderBy(identity).HeadOrNone());

        State = new CollectionEnumeratorState { Seed = state.Seed };
        while (State.Index < state.Index)
        {
            MoveNext(Option<DateTimeOffset>.None);
        }
    }

    public void ResetState(CollectionEnumeratorState state)
    {
        if (State.Seed != state.Seed)
        {
            _random = new Random(state.Seed);
            _shuffled = BuildWeightedSequence(_collections, _random);
        }

        State.Seed = state.Seed;
        State.Index = state.Index;
    }

    public string SchedulingContextName => "Shuffle Weighted";

    public CollectionEnumeratorState State { get; }

    public Option<MediaItem> Current => _shuffled.Length != 0 ? _shuffled[State.Index % _mediaItemCount] : None;
    public Option<bool> CurrentIncludeInProgramGuide { get; }

    public void MoveNext(Option<DateTimeOffset> scheduledAt)
    {
        if (_shuffled.Length == 0)
        {
            return;
        }

        if ((State.Index + 1) % _shuffled.Length == 0)
        {
            Option<MediaItem> tail = Current;

            State.Index = 0;
            do
            {
                State.Seed = _random.Next();
                _random = new Random(State.Seed);
                _shuffled = BuildWeightedSequence(_collections, _random);
            } while (!_cancellationToken.IsCancellationRequested && _collections.Count > 1 &&
                     _shuffled.Length > 0 && Current.Map(x => x.Id) == tail.Map(x => x.Id));
        }
        else
        {
            State.Index++;
        }

        if (_shuffled.Length > 0)
        {
            State.Index %= _shuffled.Length;
        }
    }

    public Option<TimeSpan> MinimumDuration => _lazyMinimumDuration.Value;

    public int Count => _shuffled.Length;

    private MediaItem[] BuildWeightedSequence(IList<CollectionWithItems> collections, Random random)
    {
        if (collections.Count == 0)
        {
            return [];
        }

        int maxEps = collections.Max(c => c.MediaItems.Count);
        if (maxEps == 0)
        {
            return [];
        }

        // Schud afleveringen willekeurig per show
        var shuffledShows = collections
            .Select(c => ShuffleArray(c.MediaItems.ToArray(), random))
            .ToArray();

        // Optioneel: begin elke show op een willekeurige positie
        int[] startOffsets = shuffledShows
            .Select(show => _randomStartPoint ? random.Next(0, show.Length) : 0)
            .ToArray();

        int numShows = shuffledShows.Length;
        var result = new List<MediaItem>(numShows * maxEps);

        // Per ronde: één aflevering van elke show, in willekeurige show-volgorde
        int[] showOrder = Enumerable.Range(0, numShows).ToArray();
        for (int round = 0; round < maxEps; round++)
        {
            ShuffleIntArray(showOrder, random);
            foreach (int showIdx in showOrder)
            {
                var show = shuffledShows[showIdx];
                result.Add(show[(round + startOffsets[showIdx]) % show.Length]);
            }
        }

        return result.ToArray();
    }

    private static MediaItem[] ShuffleArray(MediaItem[] items, Random random)
    {
        var copy = (MediaItem[])items.Clone();
        int n = copy.Length;
        while (n > 1)
        {
            n--;
            int k = random.Next(n + 1);
            (copy[k], copy[n]) = (copy[n], copy[k]);
        }

        return copy;
    }

    private static void ShuffleIntArray(int[] items, Random random)
    {
        int n = items.Length;
        while (n > 1)
        {
            n--;
            int k = random.Next(n + 1);
            (items[k], items[n]) = (items[n], items[k]);
        }
    }
}
