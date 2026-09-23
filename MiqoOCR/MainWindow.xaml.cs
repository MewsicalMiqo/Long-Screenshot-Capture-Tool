using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WpfBitmapFrame = System.Windows.Media.Imaging.BitmapFrame;
using WpfPngBitmapDecoder = System.Windows.Media.Imaging.PngBitmapDecoder;
using WinBitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;
using WpfPoint = System.Windows.Point;

namespace MiqoOCR
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
        StatusText.Text = availableWindows.Count == 0 ? "No active windows found." : "Select a window, then press Start.";
    }

    private void WindowSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (WindowSelector.SelectedValue is IntPtr handle)
        {
            targetWindow = handle;
            StatusText.Text = "Target selected. Press Start, then scroll slowly.";
        }
    }

    private void CaptureFrameButton_Click(object sender, RoutedEventArgs e)
    {
        if (targetWindow == IntPtr.Zero || !IsWindow(targetWindow))
        {
            System.Windows.MessageBox.Show(this, "Select an active window first.", "Target required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var frame = CaptureFrame();
        if (frame is null) return;
        if (frames.Count == 0)
        {
            frames.Add(frame);
            compositeImage = frame;
            UpdatePreview(frame);
            SelectionCanvas.IsHitTestVisible = true;
            CaptureFrameButton.Content = "Capture next frame";
            UndoButton.IsEnabled = true;
            StatusText.Text = "Drag a selection over the first frame.";
            return;
        }

        if (captureRegion is not Int32Rect region)
        {
            StatusText.Text = "Select an area on the first frame before capturing again.";
            return;
        }

        frames.Add(CropFrame(frame, region));
        UndoButton.IsEnabled = true;
        StatusText.Text = $"Captured {frames.Count} frames. Composite updated.";
        FinishCapture();
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (frames.Count == 0) return;

        frames.RemoveAt(frames.Count - 1);
        if (frames.Count == 0)
        {
            compositeImage = null;
            captureRegion = null;
            SelectionCanvas.IsHitTestVisible = false;
            SelectionRectangle.Visibility = Visibility.Collapsed;
            CompositeImage.Source = null;
            SavePreviewButton.IsEnabled = false;
            UndoButton.IsEnabled = false;
            CaptureFrameButton.Content = "Capture frame";
            Width = 900;
            Height = 900;
            StatusText.Text = "Select a window, then capture the first frame.";
            return;
        }

        FinishCapture();
        StatusText.Text = $"Undid the last frame. {frames.Count} frame{(frames.Count == 1 ? "" : "s")} remain.";
    }

    private void StartOverButton_Click(object sender, RoutedEventArgs e)
    {
        frames.Clear();
        compositeImage = null;
        captureRegion = null;
        selecting = false;
        SelectionCanvas.ReleaseMouseCapture();
        SelectionCanvas.IsHitTestVisible = false;
        SelectionRectangle.Visibility = Visibility.Collapsed;
        CompositeImage.Source = null;
        SavePreviewButton.IsEnabled = false;
        UndoButton.IsEnabled = false;
        CaptureFrameButton.Content = "Capture frame";
        Width = 800;
        Height = 900;
        StatusText.Text = "Select a window, then capture the first frame.";
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
            var region = new Int32Rect(
                (int)((left - bounds.Left) / scale),
                (int)((top - bounds.Top) / scale),
                Math.Max(1, (int)((right - left) / scale)),
                Math.Max(1, (int)((bottom - top) / scale)));
            captureRegion = new Int32Rect(region.X, region.Y,
                Math.Min(region.Width, image.PixelWidth - region.X),
                Math.Min(region.Height, image.PixelHeight - region.Y));
            frames[0] = CropFrame(image, captureRegion.Value);
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
        System.Windows.Controls.Canvas.SetLeft(SelectionRectangle, left);
        System.Windows.Controls.Canvas.SetTop(SelectionRectangle, top);
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
        var cropped = new CroppedBitmap(frame, region);
        cropped.Freeze();
        return cropped;
    }

    private void UpdatePreview(BitmapSource image)
    {
        CompositeImage.Source = image;
        Width = Math.Max(MinWidth, image.PixelWidth + 48);
        Height = Math.Max(MinHeight, Math.Min(SystemParameters.WorkArea.Height, image.PixelHeight + 220));
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => PreviewScrollViewer.ScrollToBottom()));
    }

    private BitmapSource? CaptureFrame()
    {
        if (!GetWindowRect(targetWindow, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
        {
            StatusText.Text = "Unable to capture the selected window.";
            return null;
        }
        using var bitmap = new System.Drawing.Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size);
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        stream.Position = 0;
        var decoder = new WpfPngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return decoder.Frames[0];
    }

    private void FinishCapture()
    {
        try
        {
            var composite = StitchFrames(frames);
            compositeImage = composite;
            UpdatePreview(composite);
            SavePreviewButton.IsEnabled = true;
            StatusText.Text = "Done. Composite screenshot previewed.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Capture failed.";
            System.Windows.MessageBox.Show(this, ex.Message, "Capture error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static BitmapSource StitchFrames(IReadOnlyList<BitmapSource> source)
    {
        var first = source[0];
        var width = first.PixelWidth;
        var height = first.PixelHeight;
        var pixels = new byte[width * height * 4];
        first.CopyPixels(pixels, width * 4, 0);
        var previousFrame = pixels;
        var totalHeight = height;
        foreach (var frame in source.Skip(1))
        {
            if (frame.PixelWidth != width || frame.PixelHeight != height) continue;
            var current = new byte[width * height * 4];
            frame.CopyPixels(current, width * 4, 0);
            var down = FindOffset(previousFrame, current, height, width, false);
            var up = FindOffset(previousFrame, current, height, width, true);
            previousFrame = current;
            if (Math.Min(down.score, up.score) == long.MaxValue) continue;
            var offset = down.score <= up.score ? down.offset : up.offset;
            if (offset <= 2) continue;
            var expanded = new byte[width * (totalHeight + offset) * 4];
            if (down.score <= up.score)
            {
                System.Buffer.BlockCopy(pixels, 0, expanded, 0, pixels.Length);
                System.Buffer.BlockCopy(current, (height - offset) * width * 4, expanded, totalHeight * width * 4, offset * width * 4);
            }
            else
            {
                System.Buffer.BlockCopy(current, 0, expanded, 0, offset * width * 4);
                System.Buffer.BlockCopy(pixels, 0, expanded, offset * width * 4, pixels.Length);
            }
            pixels = expanded;
            totalHeight += offset;
        }
        return BitmapSource.Create(width, totalHeight, first.DpiX, first.DpiY, first.Format, first.Palette, pixels, width * 4);
    }

    private static (int offset, long score) FindOffset(byte[] previous, byte[] current, int height, int width, bool upward)
    {
        var bestOffset = height;
        var bestScore = long.MaxValue;
        var dynamicRows = new bool[height];
        var xStep = Math.Max(1, width / 80);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x += xStep)
            {
                var index = (y * width + x) * 4;
                if (Math.Abs(previous[index] - current[index]) > 8 ||
                    Math.Abs(previous[index + 1] - current[index + 1]) > 8 ||
                    Math.Abs(previous[index + 2] - current[index + 2]) > 8)
                {
                    dynamicRows[y] = true;
                    break;
                }
            }
        }

        for (var offset = 1; offset < height - 10; offset++)
        {
            long score = 0;
            var samples = 0;
            var overlapHeight = height - offset;
            var yStep = Math.Max(1, (overlapHeight - 10) / 6);
            for (var y = 0; y <= overlapHeight - 10; y += yStep)
            {
                var currentY = upward ? offset + y : y;
                if (!dynamicRows[currentY]) continue;
                for (var x = 0; x < width; x += Math.Max(1, width / 80))
                {
                    samples++;
                    for (var channel = 0; channel < 3; channel++)
                        score += upward
                            ? Math.Abs(previous[y * width * 4 + x * 4 + channel] - current[(offset + y) * width * 4 + x * 4 + channel])
                            : Math.Abs(previous[(offset + y) * width * 4 + x * 4 + channel] - current[y * width * 4 + x * 4 + channel]);
                }
            }
            if (samples == 0) continue;
            if (score < bestScore) { bestScore = score; bestOffset = offset; }
        }
        return (bestOffset, bestScore);
    }

    private void SavePreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (compositeImage is null) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG image (*.png)|*.png",
            FileName = $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            AddExtension = true,
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            SavePng(compositeImage, dialog.FileName);
            StatusText.Text = $"Preview saved as {Path.GetFileName(dialog.FileName)}.";
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
