using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using YourTube.Playback;

namespace YourTube.App;

internal sealed partial class MainWindow
{
    private IReadOnlyList<VideoChapter> seekChapters = [];
    private double seekPreviewFraction;
    private double seekPreviewSeconds;
    private SeekStoryboardState? seekStoryboard;

    private sealed class SeekStoryboardState(PlaybackMedia catalog)
    {
        internal PlaybackMedia Catalog { get; } = catalog;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal Storyboard? Board;
        internal bool Initialized, Fetching;
        internal Dictionary<int, BitmapImage?> Sheets { get; } = new();
        internal Queue<int> SheetOrder { get; } = new();
    }

    private void ResetSeekStoryboard(PlaybackMedia? catalog)
    {
        var previous = seekStoryboard;
        seekStoryboard = catalog is null || closed ? null : new(catalog);
        previous?.Cancellation.Cancel();
        previous?.Cancellation.Dispose();
        HideSeekPreview();
    }

    private bool OwnsSeekStoryboard(SeekStoryboardState state) => !closed &&
        ReferenceEquals(state, seekStoryboard) && ReferenceEquals(state.Catalog, playback.Active?.Catalog) &&
        state.Catalog.ExpiresAt > DateTimeOffset.UtcNow;

    private void SetSeekChapters(string? description, TimeSpan duration)
    {
        seekChapters = VideoChapters.Parse(description, duration);
        if (closed) return;
        UpdateChapterMarks();
        if (C<Border>("PlayerSeekPreview").Visibility == Visibility.Visible)
            UpdateSeekPreviewLabel();
    }

    private void UpdateChapterMarks()
    {
        var marks = C<Canvas>("PlayerChapterMarks");
        marks.Children.Clear();
        var slider = C<Slider>("PlayerSeek");
        double width = C<Border>("PlayerSeekHitArea").ActualWidth;
        if (InteractivePlayer is null || !slider.IsEnabled || width <= 2) return;
        for (int i = 1; i < seekChapters.Count; i++)
        {
            double fraction = seekChapters[i].StartSeconds / slider.Maximum;
            if (fraction >= 1) break;
            var mark = new Border { Width = 2, Height = 8, Background = slider.Foreground,
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Black), BorderThickness = new Thickness(.5) };
            Canvas.SetLeft(mark, Math.Clamp(fraction * width - 1, 0, width - 2));
            Canvas.SetTop(mark, 1);
            marks.Children.Add(mark);
        }
    }

    private void PreviewSeekHover(PointerRoutedEventArgs args)
    {
        if (seekPointer is not null || args.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse ||
            InteractivePlayer is null || !C<Grid>("PlayerOverlay").IsHitTestVisible) return;
        var seek = C<Slider>("PlayerSeek");
        var hitArea = C<Border>("PlayerSeekHitArea");
        var position = args.GetCurrentPoint(hitArea).Position;
        double width = hitArea.ActualWidth;
        if (!seek.IsEnabled || width <= 0 || position.X < 0 || position.X > width ||
            position.Y < 0 || position.Y > hitArea.ActualHeight) return;
        double fraction = position.X / width;
        ShowSeekPreview(seek.Maximum * fraction, fraction);
    }

    private void ShowSeekPreview(double seconds, double fraction)
    {
        seekPreviewFraction = fraction;
        seekPreviewSeconds = seconds;
        UpdateSeekPreviewLabel();
        C<Border>("PlayerSeekPreview").Visibility = Visibility.Visible;
        PositionSeekPreview();
        UpdateSeekThumbnail();
    }

    private void UpdateSeekPreviewLabel()
    {
        var chapter = VideoChapters.At(seekChapters, seekPreviewSeconds);
        string timestamp = FormatTime(seekPreviewSeconds);
        var label = C<TextBlock>("PlayerSeekPreviewLabel");
        string text = chapter is null ? timestamp : $"{timestamp} · {chapter.Title}";
        if (label.Text != text) label.Text = text;
    }

    private void PositionSeekPreview()
    {
        var preview = C<Border>("PlayerSeekPreview");
        if (closed || preview.Visibility != Visibility.Visible) return;
        var overlay = C<Grid>("PlayerOverlay");
        var hitArea = C<Border>("PlayerSeekHitArea");
        if (overlay.ActualWidth <= 0) return;
        Point anchor = hitArea.TransformToVisual(overlay).TransformPoint(new Point(hitArea.ActualWidth * seekPreviewFraction, 0));
        double width = Math.Min(200, overlay.ActualWidth);
        preview.Width = width;
        Canvas.SetLeft(preview, Math.Clamp(anchor.X - width / 2, 0, overlay.ActualWidth - width));
        Canvas.SetTop(preview, Math.Max(0, anchor.Y - preview.ActualHeight - 4));
    }

    private void HideSeekPreview()
    {
        if (closed) return;
        C<Border>("PlayerSeekPreview").Visibility = Visibility.Collapsed;
        ClearSeekThumbnail();
    }

    private void ClearSeekThumbnail()
    {
        var canvas = C<Canvas>("PlayerSeekThumbnail");
        if (canvas.Visibility == Visibility.Collapsed) return;
        canvas.Visibility = Visibility.Collapsed;
        C<Image>("PlayerSeekSheet").Source = null;
    }

    private void UpdateSeekThumbnail()
    {
        if (seekStoryboard is not { } state || !OwnsSeekStoryboard(state) || InteractivePlayer is null ||
            C<Border>("PlayerSeekPreview").Visibility != Visibility.Visible)
        {
            ClearSeekThumbnail();
            return;
        }

        if (state.Board?.FrameAt(seekPreviewSeconds) is not { } frame)
        {
            ClearSeekThumbnail();
            if (!state.Initialized)
            {
                state.Initialized = true;
                _ = InitializeSeekStoryboardAsync(state, state.Cancellation.Token);
            }
            return;
        }
        if (state.Sheets.TryGetValue(frame.Sheet, out var bitmap))
        {
            if (bitmap is null || !RenderSeekThumbnail(bitmap, frame)) ClearSeekThumbnail();
            return;
        }
        ClearSeekThumbnail();
        if (!state.Fetching)
        {
            state.Fetching = true;
            _ = LoadSeekSheetAsync(state, state.Board, frame.Sheet, state.Cancellation.Token);
        }
    }

    private async Task InitializeSeekStoryboardAsync(SeekStoryboardState state, CancellationToken token)
    {
        try
        {
            var board = await playbackResolver.GetStoryboardAsync(state.Catalog, token);
            if (board is null || !OwnsSeekStoryboard(state)) return;
            state.Board = board;
            UpdateSeekThumbnail();
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadSeekSheetAsync(SeekStoryboardState state, Storyboard board, int sheet, CancellationToken token)
    {
        BitmapImage? bitmap = null;
        try
        {
            var bytes = await playbackResolver.GetStoryboardSheetAsync(board, sheet, token);
            if (!OwnsSeekStoryboard(state)) return;
            if (bytes is not null) bitmap = await DecodeImageAsync(bytes, width: 0);
        }
        catch (OperationCanceledException) { }
        catch (System.Runtime.InteropServices.COMException) { } // The time and chapter remain usable on decode failure.
        finally { state.Fetching = false; }
        if (!OwnsSeekStoryboard(state) || token.IsCancellationRequested) return;

        state.Sheets[sheet] = bitmap;
        state.SheetOrder.Enqueue(sheet);
        if (state.SheetOrder.Count > 3) state.Sheets.Remove(state.SheetOrder.Dequeue());
        // Re-evaluate the current pointer position, not the one that started this request.
        UpdateSeekThumbnail();
    }

    private bool RenderSeekThumbnail(BitmapImage bitmap, StoryboardFrame frame)
    {
        int sourceX = frame.Column * frame.Width, sourceY = frame.Row * frame.Height;
        if (sourceX + frame.Width > bitmap.PixelWidth || sourceY + frame.Height > bitmap.PixelHeight) return false;
        var overlay = C<Grid>("PlayerOverlay");
        double room = C<Border>("PlayerSeekHitArea").TransformToVisual(overlay).TransformPoint(new Point(0, 0)).Y;
        double scale = Math.Min((C<Border>("PlayerSeekPreview").Width - 12) / frame.Width, (room - 39) / frame.Height);
        if (scale <= 0 || frame.Height * scale < 24) return false;

        var canvas = C<Canvas>("PlayerSeekThumbnail");
        double width = frame.Width * scale, height = frame.Height * scale;
        if (canvas.Clip is null || canvas.Width != width || canvas.Height != height)
        {
            canvas.Width = width;
            canvas.Height = height;
            canvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, height) };
        }
        var image = C<Image>("PlayerSeekSheet");
        image.Width = bitmap.PixelWidth * scale;
        image.Height = bitmap.PixelHeight * scale;
        Canvas.SetLeft(image, -sourceX * scale);
        Canvas.SetTop(image, -sourceY * scale);
        if (!ReferenceEquals(image.Source, bitmap)) image.Source = bitmap;
        canvas.Visibility = Visibility.Visible;
        return true;
    }
}
