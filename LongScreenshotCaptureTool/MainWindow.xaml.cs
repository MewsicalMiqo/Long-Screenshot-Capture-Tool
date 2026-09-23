using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using WpfBitmapFrame = System.Windows.Media.Imaging.BitmapFrame;
using WpfPoint = System.Windows.Point;
using MessageBox = System.Windows.MessageBox;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace LongScreenshotCaptureTool
{
public partial class MainWindow : Window
{
    private readonly List<BitmapSource> frames = [];
    private readonly List<WindowItem> availableWindows = [];
    private BitmapSource? compositeImage;
    private Int32Rect? captureRegion;
    private IntPtr targetWindow;
    private WpfPoint selectionStart;
    private bool selecting;
    private bool previewFitToWindow; // when true, the preview is scaled to fit fully (no scrollbars)
    private WpfPoint? dragPanStart;  // screen position where a click-drag pan of the preview began
    private bool stitching;
    private int droppedFrameCount;
    private BitmapSource? firstFullCapture; // the original full first capture, for re-selection after undo

    // Original window size from the XAML — used to reset the window after a capture session.
    private const int DefaultWindowWidth = 900;
    private const int DefaultWindowHeight = 900;

    // Tunables for the frame-alignment heuristic in FindOffset.
    private const int ColorDiffThreshold = 8;      // per-channel difference (0-255) that marks a row as "dynamic"
    private const int MinOverlap = 10;             // minimum rows that must still overlap when matching
    private const int MaxRowSamples = 6;           // row samples used when scoring an offset
    private const int MaxColumnSamples = 80;       // column samples used when scanning a row
    private const int MinUsefulOffset = 2;         // offsets at or below this are treated as "no scroll"
    private const int MaxCompositePixelHeight = 150_000; // hard cap to keep memory in check

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public MainWindow()
    {
        InitializeComponent();
        // MouseLeave is a direct (non-routed) event, so it can't be wired in XAML.
        PreviewContentGrid.MouseLeave += (_, _) => dragPanStart = null;
        RefreshWindows();
    }

    private void RefreshWindowsButton_Click(object sender, RoutedEventArgs e)
        => RefreshWindows();

    private void RefreshWindows()
    {
        var selectedHandle = targetWindow;
        availableWindows.Clear();
        var ownHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        EnumWindows((hWnd, _) =>
        {
            if (hWnd == ownHandle || !IsWindowVisible(hWnd) || !IsWindow(hWnd)) return true;
            var length = GetWindowTextLength(hWnd);
            if (length == 0) return true;
            var title = new StringBuilder(length + 1);
            GetWindowText(hWnd, title, title.Capacity);
            if (title.Length > 0) availableWindows.Add(new WindowItem(hWnd, title.ToString()));
            return true;
        }, IntPtr.Zero);
        WindowSelector.ItemsSource = null;
        WindowSelector.ItemsSource = availableWindows;
        WindowSelector.SelectedValue = selectedHandle;
        // If the selected window is no longer in the list, the ComboBox will clear its selection
        // (SelectionChanged fires with null, which we ignore). Reflect that in the status.
        var stillAvailable = availableWindows.Any(w => w.Handle == selectedHandle);
        if (selectedHandle != IntPtr.Zero && !stillAvailable)
            StatusText.Text = "The previously selected window is no longer available. Pick a new one.";
        else if (availableWindows.Count == 0)
            StatusText.Text = "No active windows found.";
        else if (!stillAvailable)
            StatusText.Text = "Select a window, then press Start.";
        else
            StatusText.Text = "Target selected. Press Start, then scroll slowly.";
    }

    private void WindowSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WindowSelector.SelectedValue is IntPtr handle)
        {
            targetWindow = handle;
            StatusText.Text = "Target selected. Press Start, then scroll slowly.";
        }
        else
        {
            // Selection cleared (e.g. the previously selected window is gone).
            targetWindow = IntPtr.Zero;
        }
    }

    private void CaptureFrameButton_Click(object sender, RoutedEventArgs e)
    {
        if (targetWindow == IntPtr.Zero || !IsWindow(targetWindow))
        {
            MessageBox.Show(this, "Select an active window first.", "Target required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (frames.Count > 0 && captureRegion is not Int32Rect)
        {
            StatusText.Text = "Select an area on the first frame before capturing again.";
            return;
        }

        DoCapture();
    }

    private void DoCapture()
    {
        if (stitching) return;
        var frame = CaptureFrame();
        if (frame is null) return;
        if (frames.Count == 0)
        {
            frames.Add(frame);
            firstFullCapture = frame;
            compositeImage = frame;
            previewFitToWindow = true; // show the whole first frame, no scrollbars
            UpdatePreview(frame);
            SelectionCanvas.IsHitTestVisible = true;
            CaptureFrameButton.Content = "Capture next frame";
            UndoButton.IsEnabled = true;
            StatusText.Text = "Drag a selection over the first frame.";
            return;
        }

        var region = captureRegion.Value;
        frames.Add(CropFrame(frame, region));
        UndoButton.IsEnabled = true;
        FinishCapture();
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (frames.Count == 0 || stitching) return;

        frames.RemoveAt(frames.Count - 1);
        if (frames.Count == 0)
        {
            ResetToIdleState();
            StatusText.Text = "Select a window, then capture the first frame.";
            return;
        }

        if (frames.Count == 1)
        {
            // Back to just the first frame: restore the full original capture so the user can
            // re-select a different region (avoiding a crop-of-a-crop).
            captureRegion = null;
            if (firstFullCapture is not null)
            {
                frames[0] = firstFullCapture;
            }
            compositeImage = frames[0];
            previewFitToWindow = true; // show the whole first frame for re-selection, no scrollbars
            SelectionCanvas.IsHitTestVisible = true;
            SelectionRectangle.Visibility = Visibility.Collapsed;
            UpdatePreview(frames[0]);
            SavePreviewButton.IsEnabled = true; // the cropped first frame is a valid preview
            CaptureFrameButton.Content = "Capture next frame";
            StatusText.Text = "Back to the first frame. Drag a new selection, or capture again.";
            return;
        }

        FinishCapture();
        StatusText.Text = $"Undid the last frame. {frames.Count} frame{(frames.Count == 1 ? "" : "s")} remain.";
    }

    private void StartOverButton_Click(object sender, RoutedEventArgs e)
    {
        if (frames.Count > 0)
        {
            var result = MessageBox.Show(this,
                "Discard the entire capture session?",
                "Start over",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (result != MessageBoxResult.Yes) return;
        }

        frames.Clear();
        ResetToIdleState();
        StatusText.Text = "Select a window, then capture the first frame.";
    }

    private void ResetToIdleState()
    {
        compositeImage = null;
        firstFullCapture = null;
        captureRegion = null;
        selecting = false;
        previewFitToWindow = false;
        SelectionCanvas.ReleaseMouseCapture();
        SelectionCanvas.IsHitTestVisible = false;
        SelectionRectangle.Visibility = Visibility.Collapsed;
        CompositeImage.Source = null;
        // Clearing the source collapses the preview, but the ScrollViewer keeps its previous
        // scroll offset — force a layout pass and reset it so no "ghost" scrollbar remains.
        PreviewScrollViewer.UpdateLayout();
        PreviewScrollViewer.ScrollToHorizontalOffset(0);
        PreviewScrollViewer.ScrollToVerticalOffset(0);
        SavePreviewButton.IsEnabled = false;
        UndoButton.IsEnabled = false;
        CaptureFrameButton.Content = "Capture frame";
        // Return to the original window size (matching the XAML default) rather than
        // resizing to the full screen — WorkArea would balloon the window horizontally.
        Width = DefaultWindowWidth;
        Height = DefaultWindowHeight;
    }

    private void SelectionCanvas_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (frames.Count != 1 || CompositeImage.Source is not BitmapSource image) return;
        var point = e.GetPosition(SelectionCanvas);
        if (!TryGetDisplayedImageBounds(image, out var bounds) || !bounds.Contains(point)) return;
        selecting = true;
        selectionStart = point;
        SelectionRectangle.Visibility = Visibility.Visible;
        SelectionCanvas.CaptureMouse();
        UpdateSelectionRectangle(point);
    }

    private void SelectionCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (selecting) UpdateSelectionRectangle(e.GetPosition(SelectionCanvas));
    }

    private void SelectionCanvas_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!selecting || CompositeImage.Source is not BitmapSource image) return;
        selecting = false;
        SelectionCanvas.ReleaseMouseCapture();
        var end = e.GetPosition(SelectionCanvas);
        if (TryGetDisplayedImageBounds(image, out var bounds))
        {
            var left = Math.Max(bounds.Left, Math.Min(selectionStart.X, end.X));
            var top = Math.Max(bounds.Top, Math.Min(selectionStart.Y, end.Y));
            var right = Math.Min(bounds.Right, Math.Max(selectionStart.X, end.X));
            var bottom = Math.Min(bounds.Bottom, Math.Max(selectionStart.Y, end.Y));
            var scale = bounds.Width / image.PixelWidth;
            if (scale <= 0) return;
            var region = new Int32Rect(
                (int)((left - bounds.Left) / scale),
                (int)((top - bounds.Top) / scale),
                Math.Max(1, (int)((right - left) / scale)),
                Math.Max(1, (int)((bottom - top) / scale)));
            var capture = new Int32Rect(region.X, region.Y,
                Math.Min(region.Width, image.PixelWidth - region.X),
                Math.Min(region.Height, image.PixelHeight - region.Y));
            if (capture.Width < 1 || capture.Height < 1)
            {
                StatusText.Text = "Selection too small. Try a larger area.";
                return;
            }
            captureRegion = capture;
            frames[0] = CropFrame(image, capture);
            previewFitToWindow = false; // composites grow with a vertical scrollbar from here on
            SelectionCanvas.IsHitTestVisible = false;
            SelectionRectangle.Visibility = Visibility.Collapsed;
            UpdatePreview(frames[0]);
            FinishCapture();
            StatusText.Text = "Area selected. Scroll the target, then capture the next frame.";
        }
    }

    private void UpdateSelectionRectangle(WpfPoint point)
    {
        if (CompositeImage.Source is not BitmapSource image || !TryGetDisplayedImageBounds(image, out var bounds)) return;
        var endX = Math.Max(bounds.Left, Math.Min(bounds.Right, point.X));
        var endY = Math.Max(bounds.Top, Math.Min(bounds.Bottom, point.Y));
        var left = Math.Min(selectionStart.X, endX);
        var top = Math.Min(selectionStart.Y, endY);
        SelectionRectangle.Width = Math.Abs(endX - selectionStart.X);
        SelectionRectangle.Height = Math.Abs(endY - selectionStart.Y);
        Canvas.SetLeft(SelectionRectangle, left);
        Canvas.SetTop(SelectionRectangle, top);
    }

    private bool TryGetDisplayedImageBounds(BitmapSource image, out Rect bounds)
    {
        var scale = Math.Min(CompositeImage.ActualWidth / image.PixelWidth, CompositeImage.ActualHeight / image.PixelHeight);
        var width = image.PixelWidth * scale;
        var height = image.PixelHeight * scale;
        bounds = new Rect((CompositeImage.ActualWidth - width) / 2, (CompositeImage.ActualHeight - height) / 2, width, height);
        return scale > 0;
    }

    private static BitmapSource CropFrame(BitmapSource frame, Int32Rect region)
    {
        // Copy the region into a fresh bitmap so the full-size source can be released.
        var pixels = new byte[region.Width * region.Height * 4];
        frame.CopyPixels(region, pixels, region.Width * 4, 0);
        var cropped = BitmapSource.Create(
            region.Width, region.Height, frame.DpiX, frame.DpiY, frame.Format, null, pixels, region.Width * 4);
        cropped.Freeze();
        return cropped;
    }

    private void UpdatePreview(BitmapSource image)
    {
        CompositeImage.Source = image;
        ApplyPreviewFitMode();
        // Scroll after layout/render so ActualHeight reflects the new content.
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => PreviewScrollViewer.ScrollToBottom()));
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (previewFitToWindow && CompositeImage.Source is not null)
            ApplyPreviewFitMode();
    }

    // Click-drag panning of the preview, handled on the scrollable content grid (not the ScrollViewer)
    // so the scrollbar parts keep their own mouse input. No CaptureMouse: capturing would trigger the
    // ScrollViewer's built-in edge auto-scroll and cause double scrolling.
    private const double PreviewPanSpeed = 1; // fraction of pointer movement applied to scroll

    private void PreviewContent_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (selecting || CompositeImage.Source is null) return;
        if (SelectionCanvas.IsHitTestVisible) return; // region selection active — don't steal the mouse
        dragPanStart = e.GetPosition(this); // window-relative DIPs → DPI-independent pan speed
    }

    private void PreviewContent_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (dragPanStart is not WpfPoint last) return;
        var current = e.GetPosition(this);
        // Dragging down should reveal content above → scroll up by the delta.
        PreviewScrollViewer.ScrollToVerticalOffset(PreviewScrollViewer.VerticalOffset - (current.Y - last.Y) * PreviewPanSpeed);
        dragPanStart = current;
    }

    private void PreviewContent_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        dragPanStart = null;
    }

    // In fit mode the preview shrinks to fit the whole window (no scrollbars); otherwise it
    // scales with the window width and scrolls vertically.
    private void ApplyPreviewFitMode()
    {
        if (CompositeImage.Source is not BitmapSource image) return;
        var viewport = new System.Windows.Size(PreviewScrollViewer.ViewportWidth, PreviewScrollViewer.ViewportHeight);
        if (viewport.Width <= 0 || viewport.Height <= 0) return;

        if (previewFitToWindow)
        {
            // Scale the whole image to fit inside the viewport.
            var scale = Math.Min(viewport.Width / image.PixelWidth, viewport.Height / image.PixelHeight);
            CompositeImage.MaxWidth = double.PositiveInfinity;
            CompositeImage.MaxHeight = double.PositiveInfinity;
            CompositeImage.Width = image.PixelWidth * scale;
            CompositeImage.Height = image.PixelHeight * scale;
        }
        else
        {
            // Scale to the window width (no upscaling); height scrolls. The XAML MaxWidth binding
            // drives this, so clear any code-set value — setting it here would freeze the binding
            // and stop the preview from following window resizes.
            CompositeImage.ClearValue(MaxWidthProperty);
            CompositeImage.MaxHeight = double.PositiveInfinity;
            CompositeImage.Width = double.NaN;
            CompositeImage.Height = double.NaN;
        }
    }

    private BitmapSource? CaptureFrame()
    {
        if (!GetWindowRect(targetWindow, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
        {
            StatusText.Text = "Unable to capture the selected window.";
            return null;
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        // Grab the screen region and copy it straight into a WPF bitmap — no PNG round-trip.
        using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size);
        }

        var pixelBuffer = new byte[width * height * 4];
        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            // 32bpp rows are already 4-byte aligned, so stride == width*4 and a single copy is safe.
            Marshal.Copy(data.Scan0, pixelBuffer, 0, pixelBuffer.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        var writable = new WriteableBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        writable.WritePixels(new Int32Rect(0, 0, width, height), pixelBuffer, width * 4, 0);
        writable.Freeze();
        return writable;
    }

    private void FinishCapture()
    {
        if (stitching) return;
        stitching = true;
        SavePreviewButton.IsEnabled = false;
        UndoButton.IsEnabled = false;
        StatusText.Text = "Stitching frames…";

        var framesToStitch = frames;
        Task.Run(() =>
        {
            BitmapSource? composite = null;
            string? error = null;
            try
            {
                composite = StitchFrames(framesToStitch, out droppedFrameCount);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            finally
            {
                // Hop back to the UI thread.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    stitching = false;
                    if (error is not null || composite is null)
                    {
                        SavePreviewButton.IsEnabled = false;
                        UndoButton.IsEnabled = true;
                        StatusText.Text = "Stitching failed.";
                        MessageBox.Show(this, error ?? "Stitching failed.", "Capture error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    compositeImage = composite;
                    UpdatePreview(composite);
                    SavePreviewButton.IsEnabled = true;
                    UndoButton.IsEnabled = true;
                    StatusText.Text = droppedFrameCount > 0
                        ? $"Done. {droppedFrameCount} frame{(droppedFrameCount == 1 ? "" : "s")} dropped (scroll less than one screen at a time). Composite previewed."
                        : "Done. Composite screenshot previewed.";
                }));
            }
        });
    }

    private static BitmapSource StitchFrames(IReadOnlyList<BitmapSource> source, out int droppedCount)
    {
        droppedCount = 0;
        var first = source[0];
        var width = first.PixelWidth;
        var height = first.PixelHeight;

        // Pass 1: compute the offset of each subsequent frame relative to its predecessor.
        var offsets = new int[source.Count];
        offsets[0] = 0;
        var previous = new byte[width * height * 4];
        first.CopyPixels(previous, width * 4, 0);
        for (var i = 1; i < source.Count; i++)
        {
            var frame = source[i];
            if (frame.PixelWidth != width || frame.PixelHeight != height)
            {
                droppedCount++;
                offsets[i] = 0;
                continue;
            }
            var current = new byte[width * height * 4];
            frame.CopyPixels(current, width * 4, 0);
            var down = FindOffset(previous, current, height, width, false);
            var up = FindOffset(previous, current, height, width, true);
            previous = current;
            if (down.score == long.MaxValue && up.score == long.MaxValue)
            {
                droppedCount++;
                offsets[i] = 0;
                continue;
            }
            var offset = down.score <= up.score ? down.offset : up.offset;
            if (offset <= MinUsefulOffset)
            {
                droppedCount++;
                offsets[i] = 0;
                continue;
            }
            offsets[i] = (down.score <= up.score) ? offset : -offset; // negative = frame is above previous
        }

        // Pass 2: lay the frames out on a virtual canvas, then allocate the final buffer once.
        // offsets[i] > 0  → user scrolled down, frame i's content is below frame i-1 (top increases)
        // offsets[i] < 0  → user scrolled up,   frame i's content is above frame i-1 (top decreases)
        var tops = new long[source.Count];
        tops[0] = 0;
        for (var i = 1; i < source.Count; i++)
        {
            if (offsets[i] == 0) { tops[i] = tops[i - 1]; continue; } // dropped frame: pin to previous top
            tops[i] = tops[i - 1] + offsets[i];
        }

        var minTop = tops.Min();
        var maxBottom = tops.Max() + (long)height;
        var totalHeight = (int)(maxBottom - minTop);
        if (totalHeight > MaxCompositePixelHeight)
            throw new InvalidOperationException(
                $"Composite would be {totalHeight}px tall (limit {MaxCompositePixelHeight}px). " +
                "Capture fewer frames or a narrower region.");

        var pixels = new byte[width * totalHeight * 4];
        for (var i = 0; i < source.Count; i++)
        {
            var frame = source[i];
            if (offsets[i] == 0 && i > 0) continue; // dropped
            var frameBytes = new byte[width * height * 4];
            frame.CopyPixels(frameBytes, width * 4, 0);
            var y = (int)(tops[i] - minTop);
            System.Buffer.BlockCopy(frameBytes, 0, pixels, y * width * 4, frameBytes.Length);
        }

        // Freeze so the bitmap is thread-affine-free and can be touched from the UI thread
        // (e.g. drawing a selection over it, saving, re-stitching) without "different thread owns it".
        var composite = BitmapSource.Create(width, totalHeight, first.DpiX, first.DpiY, first.Format, first.Palette, pixels, width * 4);
        composite.Freeze();
        return composite;
    }

    private static (int offset, long score) FindOffset(byte[] previous, byte[] current, int height, int width, bool upward)
    {
        var bestOffset = height;
        var bestScore = long.MaxValue;
        var dynamicRows = new bool[height];
        var xStep = Math.Max(1, width / MaxColumnSamples);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x += xStep)
            {
                var index = (y * width + x) * 4;
                if (Math.Abs(previous[index] - current[index]) > ColorDiffThreshold ||
                    Math.Abs(previous[index + 1] - current[index + 1]) > ColorDiffThreshold ||
                    Math.Abs(previous[index + 2] - current[index + 2]) > ColorDiffThreshold)
                {
                    dynamicRows[y] = true;
                    break;
                }
            }
        }

        for (var offset = 1; offset < height - MinOverlap; offset++)
        {
            long score = 0;
            var samples = 0;
            var overlapHeight = height - offset;
            var yStep = Math.Max(1, (overlapHeight - MinOverlap) / MaxRowSamples);
            for (var y = 0; y <= overlapHeight - MinOverlap; y += yStep)
            {
                var currentY = upward ? offset + y : y;
                if (!dynamicRows[currentY]) continue;
                for (var x = 0; x < width; x += Math.Max(1, width / MaxColumnSamples))
                {
                    samples++;
                    for (var channel = 0; channel < 3; channel++)
                        score += upward
                            ? Math.Abs(previous[y * width * 4 + x * 4 + channel] - current[(offset + y) * width * 4 + x * 4 + channel])
                            : Math.Abs(previous[(offset + y) * width * 4 + x * 4 + channel] - current[y * width * 4 + x * 4 + channel]);
                }
            }
            if (samples == 0) continue;
            // Normalize by sample count so offsets with more overlap aren't unfairly penalized.
            var avg = score / samples;
            if (avg < bestScore) { bestScore = avg; bestOffset = offset; }
        }
        return (bestOffset, bestScore);
    }

    private void SavePreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (compositeImage is null) return;

        var dialog = new SaveFileDialog
        {
            Filter = "PNG image (*.png)|*.png",
            FileName = $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            AddExtension = true,
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                SavePng(compositeImage, dialog.FileName);
                StatusText.Text = $"Preview saved as {Path.GetFileName(dialog.FileName)}.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Failed to save the preview.";
                MessageBox.Show(this, ex.Message, "Save error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private static void SavePng(BitmapSource image, string path)
    {
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(WpfBitmapFrame.Create(image));
        encoder.Save(stream);
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    private sealed record WindowItem(IntPtr Handle, string Title);
}
}
