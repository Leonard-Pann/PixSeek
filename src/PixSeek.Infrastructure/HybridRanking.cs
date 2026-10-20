using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PixSeek.Core;

namespace PixSeek.Infrastructure;
public static class HybridRanking
{
    public static string Normalize(string text) => Regex.Replace(new string (text.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
    public static double NameConfidence(string query, string filename)
    {
        var q = Normalize(Path.GetFileNameWithoutExtension(query));
        var name = Normalize(Path.GetFileNameWithoutExtension(filename));
        if (q.Length == 0 || name.Length == 0)
            return 0;
        if (q == name)
            return 1;
        if (q.Length >= 3 && (" " + name + " ").Contains(" " + q + " ", StringComparison.Ordinal))
            return .95;
        if (q.Length < 4)
            return 0;
        // Fuzzy matching is deliberately gated: incidental common characters have no weight.
        var longer = Math.Max(q.Length, name.Length);
        if (Math.Abs(q.Length - name.Length) > longer * .22 || longer > 256)
            return 0;
        var previous = Enumerable.Range(0, name.Length + 1).ToArray();
        for (var i = 1; i <= q.Length; i++)
        {
            var row = new int[name.Length + 1];
            row[0] = i;
            for (var j = 1; j <= name.Length; j++)
                row[j] = Math.Min(Math.Min(row[j - 1] + 1, previous[j] + 1), previous[j - 1] + (q[i - 1] == name[j - 1] ? 0 : 1));
            previous = row;
        }

        var similarity = 1 - previous[^1] / (double)longer;
        return similarity >= .84 ? similarity : 0;
    }

    public static IReadOnlyList<SearchResult> Merge(string query, IReadOnlyList<SearchResult> semantic, IReadOnlyList<MediaFile> names, int limit)
    {
        var scores = semantic.ToDictionary(r => r.Media.Id, r => r);
        foreach (var media in names)
            if (!scores.ContainsKey(media.Id))
                scores[media.Id] = new(media, 0, null, media.ThumbnailPath);
        return scores.Values.Select(r =>
        {
            var confidence = NameConfidence(query, r.Media.FileName);
            // Strong name matches lead; below the gate the semantic order is exactly preserved.
            return (Result: r, Rank: confidence > 0 ? 1 + confidence * .8 + Math.Max(0, r.Similarity) * .1 : r.Similarity);
        }).OrderByDescending(x => x.Rank).Take(limit).Select(x => x.Result).ToArray();
    }
}
