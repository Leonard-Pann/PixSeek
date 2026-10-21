namespace PixSeek.Core;

public sealed record DetectedFace(string SourcePath, string ThumbnailPath, float[] Vector, double Confidence, double X, double Y, double Width, double Height);
public sealed record PersonProfile(long Id, IReadOnlyList<string> Keywords);
public sealed record PersonSearchMatch(MediaFile Media, double Similarity, int MatchedPeople);
