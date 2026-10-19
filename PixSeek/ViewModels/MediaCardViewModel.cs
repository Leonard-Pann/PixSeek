using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PixSeek.Core;

namespace PixSeek.ViewModels;
public partial class MediaCardViewModel(SearchResult result) : ObservableObject
{
    private SearchResult _result = result;
    public MediaFile Media => _result.Media;
    public string ThumbnailPath => _result.ThumbnailPath;
    public string FileName => Media.FileName;
    public bool IsVideo => Media.MediaType == MediaType.Video;
    public string Badge => IsVideo ? "▶ " + TimeSpan.FromMilliseconds(_result.TimestampMilliseconds ?? Media.DurationMilliseconds).ToString(@"mm\:ss") : "";
    public long Timestamp => _result.TimestampMilliseconds ?? 0;
    public string Details => $"{Media.Width} × {Media.Height} · {MediaFormats.Size(Media.SizeBytes)} · {Media.Codec} · {Media.Status}\n{Media.FullPath}\n{Media.ErrorMessage}";
    public string Score => _result.Similarity > 0 ? $"Similarité : {_result.Similarity:F3}" : "";

    [ObservableProperty]
    private bool _isSelected;
    public void Update(SearchResult result)
    {
        if (_result == result)
            return;
        var old = _result;
        _result = result;
        OnPropertyChanged(nameof(Media));
        OnPropertyChanged(nameof(Details));
        if (old.ThumbnailPath != result.ThumbnailPath)
            OnPropertyChanged(nameof(ThumbnailPath));
        if (old.Media.FileName != result.Media.FileName)
            OnPropertyChanged(nameof(FileName));
        if (old.Similarity != result.Similarity)
            OnPropertyChanged(nameof(Score));
        if (old.TimestampMilliseconds != result.TimestampMilliseconds || old.Media.DurationMilliseconds != result.Media.DurationMilliseconds)
        {
            OnPropertyChanged(nameof(Badge));
            OnPropertyChanged(nameof(Timestamp));
        }
    }
}

public sealed class MediaRow(IEnumerable<MediaCardViewModel> cards)
{
    public ObservableCollection<MediaCardViewModel> Cards { get; } = new(cards);
}
