using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace BinaryRead;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private const long PageSize = 512 * 1024;
    private const int BytesPerLine = 16;

    private string? _filePath;
    private long _fileLength;
    private long _pageOffset;
    private string _pageDump = string.Empty;
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _refreshDebounce;

    public MainWindow()
    {
        InitializeComponent();

        _refreshDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _refreshDebounce.Tick += (_, _) =>
        {
            _refreshDebounce.Stop();
            RefreshFromDisk();
        };
    }

    private long PageCount => _fileLength <= 0 ? 1 : (_fileLength + PageSize - 1) / PageSize;

    private void SelectButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要读取的文件",
            Filter = "DAT 文件 (*.dat)|*.dat|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            OpenFile(dialog.FileName);
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_pageDump))
        {
            Clipboard.SetText(_pageDump);
            StatusText.Text = "本页内容已复制到剪贴板";
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshFromDisk();

    private void AutoRefreshCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = AutoRefreshCheck.IsChecked == true;
        }
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pageOffset >= PageSize)
        {
            _pageOffset -= PageSize;
            RenderPage();
        }
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pageOffset + PageSize < _fileLength)
        {
            _pageOffset += PageSize;
            RenderPage();
        }
    }

    private void JumpButton_Click(object sender, RoutedEventArgs e) => JumpToOffset();

    private void JumpBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            JumpToOffset();
        }
    }

    private void JumpToOffset()
    {
        if (_filePath is null)
        {
            return;
        }

        string text = JumpBox.Text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        if (!long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long target)
            || target < 0 || target >= _fileLength)
        {
            MessageBox.Show(this, "无效的偏移量（请输入十六进制，且在文件范围内）。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _pageOffset = target / PageSize * PageSize;
        RenderPage();
    }

    private void Column_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        e.Handled = true;
        var args = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender
        };
        DumpScroll.RaiseEvent(args);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            OpenFile(files[0]);
        }
    }

    private void OpenFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                MessageBox.Show(this, "文件不存在。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _filePath = path;
            _fileLength = info.Length;
            _pageOffset = 0;

            FileInfoText.Text = $"{info.Name}   ({_fileLength:N0} 字节)";
            NavPanel.IsEnabled = _fileLength > 0;
            CopyButton.IsEnabled = _fileLength > 0;
            RefreshButton.IsEnabled = true;
            AutoRefreshCheck.IsEnabled = true;

            SetupWatcher(path);
            RenderPage();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"读取文件失败：\n{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "读取失败";
        }
    }

    private void SetupWatcher(string path)
    {
        _watcher?.Dispose();

        string? dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        _watcher = new FileSystemWatcher(dir, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += OnFileChanged;
        _watcher.EnableRaisingEvents = AutoRefreshCheck.IsChecked == true;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _refreshDebounce.Stop();
            _refreshDebounce.Start();
        });
    }

    private void RefreshFromDisk()
    {
        if (_filePath is null)
        {
            return;
        }

        try
        {
            var info = new FileInfo(_filePath);
            if (!info.Exists)
            {
                StatusText.Text = "文件已不存在";
                return;
            }

            _fileLength = info.Length;
            if (_pageOffset >= _fileLength)
            {
                _pageOffset = _fileLength <= 0 ? 0 : (_fileLength - 1) / PageSize * PageSize;
            }

            FileInfoText.Text = $"{info.Name}   ({_fileLength:N0} 字节)";
            NavPanel.IsEnabled = _fileLength > 0;
            CopyButton.IsEnabled = _fileLength > 0;

            if (RenderPage(preserveScroll: true))
            {
                StatusText.Text = $"已刷新  {DateTime.Now:HH:mm:ss}   ({_fileLength:N0} 字节)";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"刷新失败：{ex.Message}";
        }
    }

    private bool RenderPage(bool preserveScroll = false)
    {
        if (_filePath is null)
        {
            return false;
        }

        try
        {
            StatusText.Text = "正在读取...";
            double vOffset = DumpScroll.VerticalOffset;
            double hOffset = DumpScroll.HorizontalOffset;

            int toRead = (int)Math.Min(PageSize, _fileLength - _pageOffset);
            byte[] buffer = new byte[toRead];

            using (var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Seek(_pageOffset, SeekOrigin.Begin);
                if (toRead > 0)
                {
                    fs.ReadExactly(buffer, 0, toRead);
                }
            }

            var (offsetText, hexText, asciiText, full) = BuildHexDump(buffer, _pageOffset);
            OffsetBox.Text = offsetText;
            HexBox.Text = hexText;
            AsciiBox.Text = asciiText;
            _pageDump = full;

            if (preserveScroll)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    DumpScroll.ScrollToVerticalOffset(vOffset);
                    DumpScroll.ScrollToHorizontalOffset(hOffset);
                }, DispatcherPriority.Loaded);
            }
            else
            {
                DumpScroll.ScrollToHome();
            }

            long pageIndex = _pageOffset / PageSize;
            PageInfoText.Text = $"第 {pageIndex + 1:N0} / {PageCount:N0} 页";
            PrevButton.IsEnabled = _pageOffset > 0;
            NextButton.IsEnabled = _pageOffset + PageSize < _fileLength;

            long pageEnd = _pageOffset + toRead;
            StatusText.Text = PageCount > 1
                ? $"已加载偏移 0x{_pageOffset:X} - 0x{pageEnd:X}（分片读取）"
                : "加载完成";
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"读取失败：{ex.Message}";
            return false;
        }
    }

    private static (string offset, string hex, string ascii, string full) BuildHexDump(byte[] bytes, long baseOffset)
    {
        var offsetSb = new StringBuilder();
        var hexSb = new StringBuilder();
        var asciiSb = new StringBuilder();
        var fullSb = new StringBuilder(bytes.Length * 4 + 128);

        var hexHeader = new StringBuilder();
        for (int i = 0; i < BytesPerLine; i++)
        {
            hexHeader.Append(i.ToString("X2"));
            if (i != BytesPerLine - 1)
            {
                hexHeader.Append(' ');
            }
        }

        fullSb.Append("Offset    ").Append(hexHeader).Append("  ASCII").Append('\n');

        for (int pos = 0; pos < bytes.Length; pos += BytesPerLine)
        {
            long absolute = baseOffset + pos;
            offsetSb.Append(absolute.ToString("X8")).Append('\n');
            fullSb.Append(absolute.ToString("X8")).Append("  ");

            int lineLength = Math.Min(BytesPerLine, bytes.Length - pos);

            for (int i = 0; i < BytesPerLine; i++)
            {
                if (i < lineLength)
                {
                    string h = bytes[pos + i].ToString("X2");
                    hexSb.Append(h);
                    if (i != lineLength - 1)
                    {
                        hexSb.Append(' ');
                    }

                    fullSb.Append(h).Append(' ');
                }
                else
                {
                    fullSb.Append("   ");
                }
            }

            hexSb.Append('\n');

            fullSb.Append(' ');
            for (int i = 0; i < lineLength; i++)
            {
                byte b = bytes[pos + i];
                char c = b >= 0x20 && b <= 0x7E ? (char)b : '.';
                asciiSb.Append(c);
                fullSb.Append(c);
            }

            asciiSb.Append('\n');
            fullSb.Append('\n');
        }

        return (offsetSb.ToString(), hexSb.ToString(), asciiSb.ToString(), fullSb.ToString());
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshDebounce.Stop();
        _watcher?.Dispose();
        base.OnClosed(e);
    }
}
