using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace OnScreenKeyboard;

/// <summary>
/// Word completion and next-word prediction. Combines bundled frequency lists (English and Arabic)
/// with the words, and word pairs, the user types. Learned data is encrypted with DPAPI.
/// </summary>
public class Predictor
{
    private const int MaxWords = 20000, MaxFollowers = 16, KnownAfter = 3;

    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OnScreenKeyboard", "predictions.dat");

    private sealed class WordList(string[] words)
    {
        public readonly string[] Words = words;
        /// <summary>Lower-case word → frequency rank (0 = most common).</summary>
        public readonly Dictionary<string, int> Rank = words
            .Select((w, i) => (Key: w.ToLowerInvariant(), i)).DistinctBy(p => p.Key).ToDictionary(p => p.Key, p => p.i);
    }

    private static readonly Lazy<WordList> English = new(() => LoadList("en"));
    private static readonly Lazy<WordList> Arabic = new(() => LoadList("ar"));

    private class Data
    {
        public Dictionary<string, int> Words { get; set; } = [];
        public Dictionary<string, Dictionary<string, int>> Next { get; set; } = [];
    }

    private readonly string? _path;
    private Data _data = new();

    public bool Dirty { get; private set; }

    /// <summary>An in-memory predictor that never touches disk (used by tests).</summary>
    public Predictor() { }

    private Predictor(string path) => _path = path;

    public static Predictor Load()
    {
        var predictor = new Predictor(DefaultPath);
        try
        {
            if (File.Exists(DefaultPath))
            {
                var json = ProtectedData.Unprotect(File.ReadAllBytes(DefaultPath), null, DataProtectionScope.CurrentUser);
                predictor._data = JsonSerializer.Deserialize<Data>(json) ?? new();
            }
        }
        catch { /* unreadable: start fresh */ }
        return predictor;
    }

    public bool Save()
    {
        if (_path == null || !Dirty) return true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.SerializeToUtf8Bytes(_data);
            File.WriteAllBytes(_path, ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser));
            Dirty = false;
            return true;
        }
        catch { return false; }
    }

    public void Clear()
    {
        _data = new();
        Dirty = true;
        if (_path != null)
        {
            try { File.Delete(_path); Dirty = false; } catch { /* saved on next change */ }
        }
    }

    private static string Key(string word) => word.ToLowerInvariant();

    /// <summary>Records that <paramref name="word"/> was typed, optionally right after <paramref name="previous"/>.</summary>
    public void Learn(string? previous, string word)
    {
        var key = Key(word);
        _data.Words[key] = _data.Words.GetValueOrDefault(key) + 1;
        if (previous != null)
        {
            var prev = Key(previous);
            if (!_data.Next.TryGetValue(prev, out var followers)) _data.Next[prev] = followers = [];
            followers[key] = followers.GetValueOrDefault(key) + 1;
            if (followers.Count > MaxFollowers) followers.Remove(followers.MinBy(p => p.Value).Key);
        }
        if (_data.Words.Count > MaxWords) Prune();
        Dirty = true;
    }

    /// <summary>Undoes one <see cref="Learn"/> of a word (e.g. a typo the user then corrected).</summary>
    public void Forget(string word)
    {
        var key = Key(word);
        if (!_data.Words.TryGetValue(key, out int count)) return;
        if (count <= 1) _data.Words.Remove(key);
        else _data.Words[key] = count - 1;
        Dirty = true;
    }

    /// <summary>Marks a word as known so spell check stops flagging it (e.g. after undoing an auto-correct).</summary>
    public void MarkKnown(string word)
    {
        var key = Key(word);
        _data.Words[key] = Math.Max(_data.Words.GetValueOrDefault(key), KnownAfter);
        Dirty = true;
    }

    /// <summary>Forgets a learned word completely, including the word pairs it is part of.</summary>
    public void Remove(string word)
    {
        var key = Key(word);
        _data.Words.Remove(key);
        _data.Next.Remove(key);
        foreach (var followers in _data.Next.Values) followers.Remove(key);
        Dirty = true;
    }

    /// <summary>Learned words, most used first.</summary>
    public IEnumerable<(string Word, int Count)> Learned() =>
        _data.Words.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.CurrentCulture).Select(p => (Display(p.Key), p.Value));

    /// <summary>The bundled word list, most common first.</summary>
    public static IReadOnlyList<string> BuiltInWords(bool arabic) => (arabic ? Arabic : English).Value.Words;

    private void Prune()
    {
        foreach (var key in _data.Words.OrderBy(p => p.Value).Take(_data.Words.Count / 5).Select(p => p.Key).ToList())
        {
            _data.Words.Remove(key);
            _data.Next.Remove(key);
        }
    }

    public int Count(string word) => _data.Words.GetValueOrDefault(Key(word));

    /// <summary>The user types this word often enough that it should not be flagged as misspelled.</summary>
    public bool IsKnown(string word) => Count(word) >= KnownAfter;

    public static bool IsArabic(string text) => text.Any(c => c is >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ'
        or >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻿');

    /// <summary>Most likely words starting with <paramref name="prefix"/>, cased like the prefix.</summary>
    public List<string> Complete(string prefix, string? previous, int max = 4)
    {
        if (prefix.Length == 0) return [];
        var key = Key(prefix);
        var list = (IsArabic(prefix) ? Arabic : English).Value;
        var followers = previous != null ? _data.Next.GetValueOrDefault(Key(previous)) : null;
        var scores = new Dictionary<string, double>();

        // Bundled list: score in (0, 1] by rank, so one real use outranks any unused word.
        for (int i = 0; i < list.Words.Length; i++)
        {
            var word = list.Words[i];
            if (word.Length > key.Length && word.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                scores.TryAdd(Key(word), 1.0 - (double)i / list.Words.Length);
        }
        foreach (var (word, count) in _data.Words)
        {
            if (word.Length <= key.Length || !word.StartsWith(key, StringComparison.Ordinal)) continue;
            bool listed = scores.ContainsKey(word);
            if (!listed && count < 2) continue; // a one-off unknown word may be a typo
            scores[word] = scores.GetValueOrDefault(word) + count;
        }
        if (followers != null)
        {
            foreach (var (word, count) in followers)
                if (scores.ContainsKey(word)) scores[word] += 10.0 * count;
        }
        return scores.OrderByDescending(p => p.Value).Take(max).Select(p => MatchCase(Display(p.Key), prefix)).ToList();
    }

    /// <summary>Words the user has typed after <paramref name="previous"/>, most frequent first.</summary>
    public List<string> PredictNext(string previous, int max = 4)
    {
        if (!_data.Next.TryGetValue(Key(previous), out var followers)) return [];
        return followers.OrderByDescending(p => p.Value).Take(max).Select(p => Display(p.Key)).ToList();
    }

    /// <summary>Restores dictionary casing for words like "I'm".</summary>
    private static string Display(string key)
    {
        var list = (IsArabic(key) ? Arabic : English).Value;
        return list.Rank.TryGetValue(key, out int rank) ? list.Words[rank] : key;
    }

    private static string MatchCase(string word, string typed)
    {
        if (typed.Length > 1 && typed.All(c => !char.IsLetter(c) || char.IsUpper(c))) return word.ToUpperInvariant();
        if (char.IsUpper(typed[0])) return char.ToUpperInvariant(word[0]) + word[1..];
        return word;
    }

    private static WordList LoadList(string lang)
    {
        try
        {
            using var stream = typeof(Predictor).Assembly.GetManifestResourceStream($"words.{lang}.txt");
            if (stream == null) return new([]);
            using var reader = new StreamReader(stream);
            return new(reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        catch { return new([]); }
    }
}
