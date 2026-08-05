using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace BinaryRead
{

public partial class MainWindow : Window
{
    private const long PageSize = 512 * 1024;
    private const int BytesPerLine = 16;

    public static readonly RoutedUICommand JumpCommand = new RoutedUICommand("跳转", "Jump", typeof(MainWindow));
    public static readonly RoutedUICommand RefreshCommand = new RoutedUICommand("刷新", "Refresh", typeof(MainWindow));
    public static readonly RoutedUICommand DeleteByteCommand = new RoutedUICommand("删除字节", "DeleteByte", typeof(MainWindow));
    public static readonly RoutedUICommand InsertByteCommand = new RoutedUICommand("插入字节", "InsertByte", typeof(MainWindow));
    public static readonly RoutedUICommand SaveAsCommand = new RoutedUICommand("另存为", "SaveAs", typeof(MainWindow));

    private string? _filePath;
    private long _fileLength;
    private long _pageOffset;
    private string _pageDump = string.Empty;
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _refreshDebounce;

    private byte[] _originalData = Array.Empty<byte>();
    private bool _isDirty;
    private bool _isSyncing;
    private string _baseTitle;

    public MainWindow()
    {
        InitializeComponent();

        _baseTitle = Title;
        _refreshDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _refreshDebounce.Tick += (s, e) =>
        {
            _refreshDebounce.Stop();
            RefreshFromDisk();
        };

        CommandBindings.Add(new CommandBinding(JumpCommand, (s, e) => JumpToOffset()));
        CommandBindings.Add(new CommandBinding(RefreshCommand, (s, e) => RefreshFromDisk()));
        CommandBindings.Add(new CommandBinding(DeleteByteCommand, (s, e) => DeleteSelectedBytes()));
        CommandBindings.Add(new CommandBinding(InsertByteCommand, (s, e) => InsertByte()));
        CommandBindings.Add(new CommandBinding(SaveAsCommand, (s, e) => SaveFileAs()));
    }

    private long PageCount => _fileLength <= 0 ? 1 : (_fileLength + PageSize - 1) / PageSize;

    private void UpdateTitle()
    {
        Title = _isDirty ? "* " + _baseTitle : _baseTitle;
    }

    private bool ConfirmDiscard()
    {
        if (!_isDirty) return true;

        var result = MessageBox.Show(this, "当前页面有未保存的修改，是否放弃？", "确认",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return result == MessageBoxResult.Yes;
    }

    #region File Operations

    private void SelectButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要读取的文件",
            Filter = "DAT 文件 (*.dat)|*.dat|BAT 文件 (*.bat)|*.bat|所有文件 (*.*)|*.*",
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
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = AutoRefreshCheck.IsChecked == true;
        }
    }

    #endregion

    #region Edit Operations

    private void SaveButton_Click(object sender, RoutedEventArgs e) => SaveFile();

    private void SaveAsButton_Click(object sender, RoutedEventArgs e) => SaveFileAs();

    private void InsertButton_Click(object sender, RoutedEventArgs e) => InsertByte();

    private void DeleteButton_Click(object sender, RoutedEventArgs e) => DeleteSelectedBytes();

    private void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (HexBox.CanUndo) HexBox.Undo();
    }

    private void RedoButton_Click(object sender, RoutedEventArgs e)
    {
        if (HexBox.CanRedo) HexBox.Redo();
    }

    #endregion

    #region Command Handlers

    private void SaveCommand_Executed(object sender, ExecutedRoutedEventArgs e) => SaveFile();

    private void UndoCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (HexBox.CanUndo) HexBox.Undo();
    }

    private void RedoCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (HexBox.CanRedo) HexBox.Redo();
    }

    #endregion

    #region Save Logic

    private void SaveFile()
    {
        if (_filePath is null || !_isDirty) return;

        try
        {
            byte[]? pageData = ParseHexText(HexBox.Text);
            if (pageData is null)
            {
                MessageBox.Show(this, "Hex 区域存在无效字符，请修正后再保存。", "保存失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (pageData.Length == _originalData.Length)
            {
                using (var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    fs.Seek(_pageOffset, SeekOrigin.Begin);
                    fs.Write(pageData, 0, pageData.Length);
                }
            }
            else
            {
                byte[] entireFile = File.ReadAllBytes(_filePath);
                int fileLen = entireFile.Length;
                int origStart = (int)_pageOffset;
                int origEnd = origStart + _originalData.Length;
                int newSize = fileLen - _originalData.Length + pageData.Length;
                var newFile = new byte[newSize];

                Array.Copy(entireFile, 0, newFile, 0, origStart);
                Array.Copy(pageData, 0, newFile, origStart, pageData.Length);
                int tailLen = fileLen - origEnd;
                if (tailLen > 0)
                    Array.Copy(entireFile, origEnd, newFile, origStart + pageData.Length, tailLen);

                File.WriteAllBytes(_filePath, newFile);
                _fileLength = newSize;
            }

            _originalData = (byte[])pageData.Clone();
            _isDirty = false;
            UpdateTitle();
            StatusText.Text = $"已保存  {DateTime.Now:HH:mm:ss}";

            var info = new FileInfo(_filePath);
            _fileLength = info.Length;
            FileInfoText.Text = $"{info.Name}   ({_fileLength:N0} 字节)";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveFileAs()
    {
        if (_filePath is null) return;

        var dialog = new SaveFileDialog
        {
            Title = "另存为",
            FileName = Path.GetFileName(_filePath),
            Filter = "所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                byte[]? pageData = ParseHexText(HexBox.Text);
                if (pageData is null)
                {
                    MessageBox.Show(this, "Hex 区域存在无效字符，请修正后再保存。", "保存失败",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (pageData.Length == _originalData.Length)
                {
                    byte[] entireFile = File.ReadAllBytes(_filePath);
                    int origStart = (int)_pageOffset;
                    Array.Copy(pageData, 0, entireFile, origStart, pageData.Length);
                    File.WriteAllBytes(dialog.FileName, entireFile);
                }
                else
                {
                    byte[] entireFile = File.ReadAllBytes(_filePath);
                    int fileLen = entireFile.Length;
                    int origStart = (int)_pageOffset;
                    int origEnd = origStart + _originalData.Length;
                    int newSize = fileLen - _originalData.Length + pageData.Length;
                    var newFile = new byte[newSize];

                    Array.Copy(entireFile, 0, newFile, 0, origStart);
                    Array.Copy(pageData, 0, newFile, origStart, pageData.Length);
                    int tailLen = fileLen - origEnd;
                    if (tailLen > 0)
                        Array.Copy(entireFile, origEnd, newFile, origStart + pageData.Length, tailLen);

                    File.WriteAllBytes(dialog.FileName, newFile);
                }

                StatusText.Text = $"已另存为 {dialog.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"另存为失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    #endregion

    #region Insert / Delete Bytes

    private void InsertByte()
    {
        if (HexBox.IsReadOnly) return;

        int cursorPos = HexBox.CaretIndex;
        string text = HexBox.Text;

        int byteIndex = CursorToByteIndex(text, cursorPos);
        if (byteIndex >= 0 && byteIndex <= _originalData.Length)
        {
            var newData = new byte[_originalData.Length + 1];
            Array.Copy(_originalData, 0, newData, 0, byteIndex);
            newData[byteIndex] = 0x00;
            Array.Copy(_originalData, byteIndex, newData, byteIndex + 1, _originalData.Length - byteIndex);
            _originalData = newData;

            RenderBytes(newData);
            MarkDirty();

            int newCursor = ByteIndexToCursor(HexBox.Text, byteIndex) + 2;
            HexBox.CaretIndex = Math.Min(newCursor, HexBox.Text.Length);
            HexBox.Focus();
        }
    }

    private void DeleteSelectedBytes()
    {
        if (HexBox.IsReadOnly) return;

        if (HexBox.SelectionLength > 0)
        {
            HexBox.SelectedText = string.Empty;
            return;
        }

        int cursorPos = HexBox.CaretIndex;
        string text = HexBox.Text;

        int byteIndex = CursorToByteIndex(text, cursorPos);
        if (byteIndex >= 0 && byteIndex < _originalData.Length)
        {
            var newData = new byte[_originalData.Length - 1];
            Array.Copy(_originalData, 0, newData, 0, byteIndex);
            Array.Copy(_originalData, byteIndex + 1, newData, byteIndex, _originalData.Length - byteIndex - 1);
            _originalData = newData;

            RenderBytes(newData);
            MarkDirty();

            int newCursor = ByteIndexToCursor(HexBox.Text, byteIndex);
            HexBox.CaretIndex = Math.Min(newCursor, HexBox.Text.Length);
            HexBox.Focus();
        }
    }

    #endregion

    #region Hex Editing & Sync

    private void HexBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_isSyncing) return;

        _isSyncing = true;
        try
        {
            byte[]? bytes = ParseHexText(HexBox.Text);
            if (bytes != null)
            {
                _originalData = bytes;
                string ascii = BytesToAsciiText(bytes);
                AsciiBox.Text = ascii;
                MarkDirty();
            }
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private void HexBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        foreach (char c in e.Text)
        {
            if (!IsValidHexChar(c))
            {
                e.Handled = true;
                StatusText.Text = "Hex 区域只允许输入 0-9 A-F 和空格";
                return;
            }
        }
    }

    private void HexBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            DeleteSelectedBytes();
            e.Handled = true;
        }
        else if (e.Key == Key.Insert)
        {
            InsertByte();
            e.Handled = true;
        }
    }

    private void HexBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateByteInfo();
    }

    private void UpdateByteInfo()
    {
        string text = HexBox.Text;
        int cursorPos = HexBox.CaretIndex;
        int byteIndex = CursorToByteIndex(text, cursorPos);

        if (byteIndex >= 0 && byteIndex < _originalData.Length)
        {
            byte b = _originalData[byteIndex];
            long absOffset = _pageOffset + byteIndex;
            ByteInfoText.Text = string.Format("偏移: 0x{0:X8} | Hex: {1:X2} | Dec: {2} | ASCII: '{3}'",
                absOffset, b, b, b >= 0x20 && b <= 0x7E ? (char)b : '.');
        }
        else
        {
            ByteInfoText.Text = "";
        }
    }

    #endregion

    #region Hex Parsing

    private static bool IsValidHexChar(char c)
    {
        return (c >= '0' && c <= '9') ||
               (c >= 'A' && c <= 'F') ||
               (c >= 'a' && c <= 'f') ||
               c == ' ' || c == '\t' || c == '\n' || c == '\r';
    }

    private static byte[]? ParseHexText(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<byte>();

        var bytes = new List<byte>();
        var token = new StringBuilder(2);

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == ' ' || c == '\n' || c == '\r' || c == '\t')
            {
                if (token.Length > 0)
                {
                    if (token.Length == 1) token.Append('0');
                    byte val;
                    if (byte.TryParse(token.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out val))
                        bytes.Add(val);
                    else
                        return null;
                    token.Clear();
                }
            }
            else if (IsHexDigit(c))
            {
                token.Append(char.ToUpperInvariant(c));
                if (token.Length == 2)
                {
                    byte val;
                    if (byte.TryParse(token.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out val))
                        bytes.Add(val);
                    else
                        return null;
                    token.Clear();
                }
            }
            else
            {
                return null;
            }
        }

        if (token.Length > 0)
        {
            if (token.Length == 1) token.Append('0');
            if (byte.TryParse(token.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte val))
                bytes.Add(val);
            else
                return null;
        }

        return bytes.ToArray();
    }

    private static bool IsHexDigit(char c)
    {
        return (c >= '0' && c <= '9') ||
               (c >= 'A' && c <= 'F') ||
               (c >= 'a' && c <= 'f');
    }

    private static string BytesToAsciiText(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        for (int pos = 0; pos < bytes.Length; pos += BytesPerLine)
        {
            int lineLen = Math.Min(BytesPerLine, bytes.Length - pos);
            for (int i = 0; i < lineLen; i++)
            {
                byte b = bytes[pos + i];
                sb.Append(b >= 0x20 && b <= 0x7E ? (char)b : '.');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string BytesToHexText(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 3);
        for (int i = 0; i < bytes.Length; i++)
        {
            sb.Append(bytes[i].ToString("X2"));
            if (i < bytes.Length - 1)
                sb.Append(' ');
        }
        return sb.ToString();
    }

    private static int CursorToByteIndex(string hexText, int cursorPos)
    {
        if (string.IsNullOrEmpty(hexText) || cursorPos <= 0) return 0;

        int byteCount = 0;
        int hexChars = 0;
        for (int i = 0; i < cursorPos && i < hexText.Length; i++)
        {
            char c = hexText[i];
            if (IsHexDigit(c))
            {
                hexChars++;
                if (hexChars % 2 == 0) byteCount++;
            }
        }
        return byteCount;
    }

    private static int ByteIndexToCursor(string hexText, int targetByteIndex)
    {
        if (string.IsNullOrEmpty(hexText) || targetByteIndex <= 0) return 0;

        int byteCount = 0;
        int hexChars = 0;
        for (int i = 0; i < hexText.Length; i++)
        {
            if (byteCount >= targetByteIndex) return i;

            char c = hexText[i];
            if (IsHexDigit(c))
            {
                hexChars++;
                if (hexChars % 2 == 0) byteCount++;
            }
        }
        return hexText.Length;
    }

    #endregion

    #region Render

    private void RenderBytes(byte[] bytes)
    {
        _isSyncing = true;
        try
        {
            HexBox.Text = BytesToHexText(bytes);
            AsciiBox.Text = BytesToAsciiText(bytes);
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private void MarkDirty()
    {
        if (!_isDirty && _filePath != null)
        {
            _isDirty = true;
            UpdateTitle();
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
                    int offset = 0;
                    while (offset < toRead)
                    {
                        int n = fs.Read(buffer, offset, toRead - offset);
                        if (n == 0) break;
                        offset += n;
                    }
                }
            }

            _originalData = buffer;
            _isDirty = false;
            UpdateTitle();

            var (offsetText, hexText, asciiText, full) = BuildHexDump(buffer, _pageOffset);
            OffsetBox.Text = offsetText;
            _isSyncing = true;
            HexBox.Text = hexText;
            AsciiBox.Text = asciiText;
            _isSyncing = false;
            _pageDump = full;

            if (preserveScroll)
            {
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    DumpScroll.ScrollToVerticalOffset(vOffset);
                    DumpScroll.ScrollToHorizontalOffset(hOffset);
                }), DispatcherPriority.Loaded);
            }
            else
            {
                DumpScroll.ScrollToHome();
            }

            long pageIndex = _pageOffset / PageSize;
            PageInfoText.Text = string.Format("第 {0:N0} / {1:N0} 页", pageIndex + 1, PageCount);
            PrevButton.IsEnabled = _pageOffset > 0;
            NextButton.IsEnabled = _pageOffset + PageSize < _fileLength;

            long pageEnd = _pageOffset + toRead;
            StatusText.Text = PageCount > 1
                ? string.Format("已加载偏移 0x{0:X} - 0x{1:X}（分片读取）", _pageOffset, pageEnd)
                : "加载完成";
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = string.Format("读取失败：{0}", ex.Message);
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

    #endregion

    #region Navigation

    private void PrevButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;

        if (_pageOffset >= PageSize)
        {
            _pageOffset -= PageSize;
            RenderPage();
        }
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;

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
        if (_filePath is null) return;
        if (!ConfirmDiscard()) return;

        string text = JumpBox.Text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring(2);
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

    #endregion

    #region File Open / Refresh

    private void Column_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;

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
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (!ConfirmDiscard()) return;

        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            OpenFile(files[0]);
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isDirty)
        {
            var result = MessageBox.Show(this, "有未保存的修改，是否在关闭前保存？", "确认",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                SaveFile();
            }
            else if (result == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
            }
        }
    }

    public void OpenFile(string path)
    {
        if (!ConfirmDiscard()) return;

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

            FileInfoText.Text = string.Format("{0}   ({1:N0} 字节)", info.Name, _fileLength);
            NavPanel.IsEnabled = _fileLength > 0;
            CopyButton.IsEnabled = _fileLength > 0;
            RefreshButton.IsEnabled = true;
            AutoRefreshCheck.IsEnabled = true;
            EditPanel.IsEnabled = _fileLength > 0;

            SetupWatcher(path);
            RenderPage();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, string.Format("读取文件失败：\n{0}", ex.Message), "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "读取失败";
        }
    }

    private void SetupWatcher(string path)
    {
        if (_watcher != null)
        {
            _watcher.Dispose();
            _watcher = null;
        }

        string dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return;

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
        Dispatcher.BeginInvoke((Action)(() =>
        {
            _refreshDebounce.Stop();
            _refreshDebounce.Start();
        }));
    }

    private void RefreshFromDisk()
    {
        if (_filePath is null) return;

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

            FileInfoText.Text = string.Format("{0}   ({1:N0} 字节)", info.Name, _fileLength);
            NavPanel.IsEnabled = _fileLength > 0;
            CopyButton.IsEnabled = _fileLength > 0;

            if (RenderPage(preserveScroll: true))
            {
                StatusText.Text = string.Format("已刷新  {0:HH:mm:ss}   ({1:N0} 字节)", DateTime.Now, _fileLength);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = string.Format("刷新失败：{0}", ex.Message);
        }
    }

    #endregion

    protected override void OnClosed(EventArgs e)
    {
        _refreshDebounce.Stop();
        if (_watcher != null)
        {
            _watcher.Dispose();
            _watcher = null;
        }
        base.OnClosed(e);
    }
}

}
