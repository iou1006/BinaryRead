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
    private const int BytesPerLine = 32;

    public static readonly RoutedUICommand JumpCommand = new RoutedUICommand("跳转", "Jump", typeof(MainWindow));
    public static readonly RoutedUICommand RefreshCommand = new RoutedUICommand("刷新", "Refresh", typeof(MainWindow));
    public static readonly RoutedUICommand DeleteByteCommand = new RoutedUICommand("删除字节", "DeleteByte", typeof(MainWindow));
    public static readonly RoutedUICommand InsertByteCommand = new RoutedUICommand("插入字节", "InsertByte", typeof(MainWindow));
    public static readonly RoutedUICommand SaveAsCommand = new RoutedUICommand("另存为", "SaveAs", typeof(MainWindow));
    public static readonly RoutedUICommand FocusSearchCommand = new RoutedUICommand("搜索", "FocusSearch", typeof(MainWindow));
    public static readonly RoutedUICommand SearchNextCommand = new RoutedUICommand("下一个匹配", "SearchNext", typeof(MainWindow));
    public static readonly RoutedUICommand SearchPrevCommand = new RoutedUICommand("上一个匹配", "SearchPrev", typeof(MainWindow));

    private string? _filePath;
    private long _fileLength;
    private long _pageOffset;
    private string _pageDump = string.Empty;
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _refreshDebounce;

    private byte[] _originalData = Array.Empty<byte>();
    private bool _isDirty;
    private bool _parseError;
    private bool _isSyncing;
    private string _baseTitle;

    private byte[]? _delimiter;
    private int[] _byteToTextPos = Array.Empty<int>();

    private readonly List<long> _searchResults = new List<long>();
    private int _searchIndex = -1;
    private byte[] _searchPattern = Array.Empty<byte>();
    private bool _searchInProgress;
    private FileStream? _searchStream;
    private long _searchFileLengthAtScan;
    private long _searchScannedBytes;
    private byte[] _searchChunk = Array.Empty<byte>();
    private byte[] _searchCarry = Array.Empty<byte>();
    private int _searchCarryCount;

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

        HexHeaderBox.Text = BuildHexHeader();

        CommandBindings.Add(new CommandBinding(JumpCommand, (s, e) => JumpToOffset()));
        CommandBindings.Add(new CommandBinding(RefreshCommand, (s, e) => RefreshFromDisk()));
        CommandBindings.Add(new CommandBinding(DeleteByteCommand, (s, e) => DeleteSelectedBytes()));
        CommandBindings.Add(new CommandBinding(InsertByteCommand, (s, e) => InsertByte()));
        CommandBindings.Add(new CommandBinding(SaveAsCommand, (s, e) => SaveFileAs()));
        CommandBindings.Add(new CommandBinding(FocusSearchCommand, (s, e) =>
        {
            if (SearchPanel.IsEnabled)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
            }
        }));
        CommandBindings.Add(new CommandBinding(SearchNextCommand, (s, e) => SearchNext()));
        CommandBindings.Add(new CommandBinding(SearchPrevCommand, (s, e) => SearchPrev()));
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

    private static string BuildHexHeader()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < BytesPerLine; i++)
        {
            sb.Append(i.ToString("X2"));
            if (i != BytesPerLine - 1) sb.Append(' ');
        }
        return sb.ToString();
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
    // 保存一律使用 _originalData（字节级数据模型），
    // HexBox 中的显示换行（含分隔符格式化换行）只是空白字符，永远不会写入文件。

    private void SaveFile()
    {
        if (_filePath is null || !_isDirty) return;

        if (_parseError)
        {
            MessageBox.Show(this, "Hex 区域存在无法解析的内容，请修正后再保存。", "保存失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            byte[] pageData = _originalData;

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
            StatusText.Text = string.Format("已保存  {0:HH:mm:ss}", DateTime.Now);

            var info = new FileInfo(_filePath);
            _fileLength = info.Length;
            FileInfoText.Text = string.Format("{0}   ({1:N0} 字节)", info.Name, _fileLength);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, string.Format("保存失败：{0}", ex.Message), "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveFileAs()
    {
        if (_filePath is null) return;

        if (_parseError)
        {
            MessageBox.Show(this, "Hex 区域存在无法解析的内容，请修正后再保存。", "保存失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

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
                byte[] pageData = _originalData;

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

                StatusText.Text = string.Format("已另存为 {0}", dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, string.Format("另存为失败：{0}", ex.Message), "错误",
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

            RenderFromData(byteIndex + 1);
            MarkDirty();
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

            RenderFromData(byteIndex);
            MarkDirty();
            HexBox.Focus();
        }
    }

    #endregion

    #region Hex Editing & Sync

    private void HexBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_isSyncing) return;

        byte[]? bytes = ParseHexText(HexBox.Text);
        if (bytes is null)
        {
            _parseError = true;
            StatusText.Text = "Hex 区域存在无法解析的内容，保存已被禁止";
            return;
        }
        _parseError = false;

        int caretByte = CursorToByteIndex(HexBox.Text, HexBox.CaretIndex);
        _originalData = bytes;

        _isSyncing = true;
        try
        {
            if (_delimiter != null)
            {
                // 分隔符模式：整体重渲染保持三列行结构一致，并恢复光标
                RenderFromData(caretByte);
            }
            else
            {
                AsciiBox.Text = BytesToAsciiText(bytes);
                _byteToTextPos = BuildByteToTextPos(bytes.Length);
            }
        }
        finally
        {
            _isSyncing = false;
        }

        MarkDirty();
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

    #region Search (page-by-page incremental scan)

    private void SearchButton_Click(object sender, RoutedEventArgs e) => DoSearch();

    private void SearchNextButton_Click(object sender, RoutedEventArgs e) => SearchNext();

    private void SearchPrevButton_Click(object sender, RoutedEventArgs e) => SearchPrev();

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            DoSearch();
        }
    }

    private void CancelSearch()
    {
        _searchInProgress = false;
        if (_searchStream != null)
        {
            _searchStream.Dispose();
            _searchStream = null;
        }
    }

    private void DoSearch()
    {
        if (_filePath is null) return;

        byte[]? pattern = ParseSearchPattern(SearchBox.Text);
        if (pattern is null || pattern.Length == 0)
        {
            StatusText.Text = "请输入有效的搜索内容（Hex 如 \"0D 0A\" 或文本如 \"hello\"）";
            return;
        }

        CancelSearch();

        _searchPattern = pattern;
        _searchResults.Clear();
        _searchIndex = -1;
        _searchScannedBytes = 0;
        _searchCarryCount = 0;
        _searchCarry = new byte[Math.Max(0, pattern.Length - 1)];

        if (_isDirty)
        {
            StatusText.Text = "注意：搜索基于已保存的文件内容（当前有未保存修改）";
        }

        try
        {
            _searchStream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _searchFileLengthAtScan = _searchStream.Length;
        }
        catch (Exception ex)
        {
            StatusText.Text = string.Format("搜索失败：{0}", ex.Message);
            return;
        }

        _searchChunk = new byte[PageSize];
        _searchInProgress = true;
        SearchInfoText.Text = "扫描中 0%";
        Dispatcher.BeginInvoke(new Action(ProcessSearchChunk), DispatcherPriority.Background);
    }

    // 逐页（每次 512KB）扫描，页与页之间让出 UI 线程，避免卡顿
    private void ProcessSearchChunk()
    {
        if (!_searchInProgress || _searchStream is null) return;

        byte[] pattern = _searchPattern;
        int n;
        try
        {
            n = _searchStream.Read(_searchChunk, 0, _searchChunk.Length);
        }
        catch (Exception ex)
        {
            StatusText.Text = string.Format("搜索失败：{0}", ex.Message);
            FinishSearch();
            return;
        }

        if (n <= 0)
        {
            FinishSearch();
            return;
        }

        if (pattern.Length > 0)
        {
            var scan = new byte[_searchCarryCount + n];
            Array.Copy(_searchCarry, 0, scan, 0, _searchCarryCount);
            Array.Copy(_searchChunk, 0, scan, _searchCarryCount, n);

            long baseOffset = _searchScannedBytes - _searchCarryCount;
            for (int i = 0; i + pattern.Length <= scan.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (scan[i + j] != pattern[j]) { match = false; break; }
                }
                if (match) _searchResults.Add(baseOffset + i);
            }

            int newCarry = Math.Min(pattern.Length - 1, scan.Length);
            for (int i = 0; i < newCarry; i++)
                _searchCarry[i] = scan[scan.Length - newCarry + i];
            _searchCarryCount = newCarry;
        }

        _searchScannedBytes += n;

        if (_searchResults.Count == 1 && _searchIndex == -1)
        {
            _searchIndex = 0;
            GoToSearchResult(0);
        }

        if (_searchScannedBytes < _searchFileLengthAtScan)
        {
            int pct = (int)(_searchScannedBytes * 100 / Math.Max(1, _searchFileLengthAtScan));
            SearchInfoText.Text = string.Format("扫描中 {0}%   已找到 {1}", pct, _searchResults.Count);
            Dispatcher.BeginInvoke(new Action(ProcessSearchChunk), DispatcherPriority.Background);
        }
        else
        {
            FinishSearch();
        }
    }

    private void FinishSearch()
    {
        CancelSearch();

        if (_searchResults.Count == 0)
        {
            SearchInfoText.Text = "无匹配";
            StatusText.Text = "未找到匹配项";
            return;
        }

        if (_searchIndex == -1)
        {
            _searchIndex = 0;
            GoToSearchResult(0);
        }

        SearchInfoText.Text = string.Format("共 {0} 处匹配   当前 {1}",
            _searchResults.Count, _searchIndex + 1);
        StatusText.Text = string.Format("搜索完成，共 {0} 处匹配", _searchResults.Count);
    }

    private void SearchNext()
    {
        if (_searchResults.Count == 0)
        {
            if (_searchPattern.Length > 0 && !_searchInProgress)
            {
                DoSearch();
            }
            else if (_searchInProgress)
            {
                StatusText.Text = "仍在扫描，请稍候…";
            }
            return;
        }

        if (_searchIndex == _searchResults.Count - 1 && _searchInProgress)
        {
            StatusText.Text = "仍在扫描更多匹配，请稍候…";
        }

        _searchIndex = (_searchIndex + 1) % _searchResults.Count;
        GoToSearchResult(_searchIndex);
    }

    private void SearchPrev()
    {
        if (_searchResults.Count == 0)
        {
            if (_searchPattern.Length > 0 && !_searchInProgress)
            {
                DoSearch();
            }
            return;
        }

        _searchIndex = (_searchIndex - 1 + _searchResults.Count) % _searchResults.Count;
        GoToSearchResult(_searchIndex);
    }

    private void GoToSearchResult(int index)
    {
        long offset = _searchResults[index];
        long targetPage = offset / PageSize * PageSize;

        if (targetPage != _pageOffset && !ConfirmDiscard())
        {
            return;
        }

        SelectMatch(offset, _searchPattern.Length);
        SearchInfoText.Text = _searchInProgress
            ? string.Format("扫描中…   当前 {0} / 已找到 {1}", index + 1, _searchResults.Count)
            : string.Format("{0} / {1}", index + 1, _searchResults.Count);
        StatusText.Text = string.Format("匹配位置 0x{0:X}", offset);
    }

    private void SelectMatch(long absoluteOffset, int length)
    {
        long pageOffset = absoluteOffset / PageSize * PageSize;
        if (pageOffset != _pageOffset)
        {
            _pageOffset = pageOffset;
            RenderPage();
        }

        int byteIndex = (int)(absoluteOffset - _pageOffset);
        if (byteIndex >= 0 && byteIndex < _byteToTextPos.Length)
        {
            int available = _byteToTextPos.Length - byteIndex;
            int selBytes = Math.Min(length, available);
            if (selBytes > 0)
            {
                int start = _byteToTextPos[byteIndex];
                int end = _byteToTextPos[byteIndex + selBytes - 1] + 2;
                HexBox.Select(start, end - start);
                HexBox.Focus();
            }
        }
    }

    private static byte[]? ParseSearchPattern(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        string trimmed = input.Trim();

        bool looksLikeHex = trimmed.Length > 0;
        foreach (char c in trimmed)
        {
            if (!IsHexDigit(c) && !char.IsWhiteSpace(c))
            {
                looksLikeHex = false;
                break;
            }
        }

        if (looksLikeHex)
        {
            var hexOnly = new string(trimmed.Where(c => IsHexDigit(c)).ToArray());
            if (hexOnly.Length % 2 != 0) return null;

            var bytes = new byte[hexOnly.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                if (!byte.TryParse(hexOnly.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                    return null;
            }
            return bytes;
        }

        return Encoding.ASCII.GetBytes(trimmed);
    }

    #endregion

    #region Delimiter Formatting (display-only)

    private void ApplyDelimiterButton_Click(object sender, RoutedEventArgs e) => ApplyDelimiter();

    private void ClearDelimiterButton_Click(object sender, RoutedEventArgs e) => ClearDelimiter();

    private void DelimiterBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyDelimiter();
        }
    }

    private void ApplyDelimiter()
    {
        string text = DelimiterBox.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            ClearDelimiter();
            return;
        }

        byte[]? delim = ParseSearchPattern(text);
        if (delim is null || delim.Length == 0)
        {
            MessageBox.Show(this, "无效的分隔符。请输入 Hex 字节（如 \"0D 0A\"）或文本。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _delimiter = delim;
        DelimiterInfoText.Text = string.Format("分隔符: {0} ({1} 字节)  —  仅显示，不写入文件", text, delim.Length);

        // 分页渲染：先让 UI 响应，再后台重绘当前页
        StatusText.Text = "正在应用格式化…";
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ReRenderCurrentBuffer(preserveScroll: true);
            StatusText.Text = "已应用换行分隔符（仅显示）";
        }), DispatcherPriority.Background);
    }

    private void ClearDelimiter()
    {
        if (_delimiter is null) return;

        _delimiter = null;
        DelimiterBox.Text = "";
        DelimiterInfoText.Text = "";
        ReRenderCurrentBuffer(preserveScroll: true);
        StatusText.Text = "已清除换行分隔符";
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

    private static bool IsHexDigit(char c)
    {
        return (c >= '0' && c <= '9') ||
               (c >= 'A' && c <= 'F') ||
               (c >= 'a' && c <= 'f');
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

    private static int[] BuildByteToTextPos(int byteCount)
    {
        var map = new int[byteCount];
        for (int i = 0; i < byteCount; i++)
            map[i] = i * 3;
        return map;
    }

    #endregion

    #region Render

    // 以 _originalData 为唯一数据源统一渲染三列（含分隔符换行），并重建字节↔文本映射。
    // 显示换行只存在于 TextBox 文本中；保存时一律使用 _originalData，不受影响。
    private void RenderFromData(int? caretByte = null)
    {
        _isSyncing = true;
        try
        {
            var (offsetText, hexText, asciiText, full, byteToTextPos) =
                BuildHexDump(_originalData, _pageOffset, _delimiter);
            _byteToTextPos = byteToTextPos;
            OffsetBox.Text = offsetText;
            HexBox.Text = hexText;
            AsciiBox.Text = asciiText;
            _pageDump = full;

            if (caretByte.HasValue && _byteToTextPos.Length > 0)
            {
                int idx = Math.Min(Math.Max(caretByte.Value, 0), _byteToTextPos.Length - 1);
                HexBox.CaretIndex = Math.Min(_byteToTextPos[idx] + 2, HexBox.Text.Length);
            }
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private void ReRenderCurrentBuffer(bool preserveScroll = false)
    {
        if (_filePath is null) return;

        double vOffset = DumpScroll.VerticalOffset;
        double hOffset = DumpScroll.HorizontalOffset;
        int caretByte = CursorToByteIndex(HexBox.Text, HexBox.CaretIndex);

        RenderFromData(caretByte);

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
            _parseError = false;
            UpdateTitle();

            var (offsetText, hexText, asciiText, full, byteToTextPos) = BuildHexDump(buffer, _pageOffset, _delimiter);
            _byteToTextPos = byteToTextPos;
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

    // 换行规则：
    //  1) 每行最多 BytesPerLine 字节；
    //  2) 行内（不含行首）一旦匹配到分隔符，则在该分隔符“之前”换行——分隔符成为下一行行首；
    //  3) 分隔符已在行首时不额外换行（扫描从 pos+1 开始）。
    // 同时构建 byteToTextPos 映射（字节索引 → HexBox 文本位置），供搜索定位选区使用。
    private static (string offset, string hex, string ascii, string full, int[] byteToTextPos) BuildHexDump(
        byte[] bytes, long baseOffset, byte[]? delimiter)
    {
        var offsetSb = new StringBuilder();
        var hexSb = new StringBuilder();
        var asciiSb = new StringBuilder();
        var fullSb = new StringBuilder(bytes.Length * 4 + 128);
        var byteToTextPos = new int[bytes.Length];

        var hexHeader = BuildHexHeader();

        fullSb.Append("Offset    ").Append(hexHeader).Append("  ASCII").Append('\n');

        int hexTextPos = 0;
        int pos = 0;
        while (pos < bytes.Length)
        {
            int lineEnd = Math.Min(pos + BytesPerLine, bytes.Length);

            if (delimiter != null && delimiter.Length > 0)
            {
                int limit = Math.Min(lineEnd, bytes.Length - delimiter.Length + 1);
                for (int i = pos + 1; i < limit; i++)
                {
                    bool match = true;
                    for (int j = 0; j < delimiter.Length; j++)
                    {
                        if (bytes[i + j] != delimiter[j]) { match = false; break; }
                    }
                    if (match)
                    {
                        lineEnd = i;
                        break;
                    }
                }
            }

            long absolute = baseOffset + pos;
            offsetSb.Append(absolute.ToString("X8")).Append('\n');
            fullSb.Append(absolute.ToString("X8")).Append("  ");

            for (int i = pos; i < lineEnd; i++)
            {
                byteToTextPos[i] = hexTextPos;
                string h = bytes[i].ToString("X2");
                hexSb.Append(h);
                fullSb.Append(h);
                hexTextPos += 2;
                if (i != lineEnd - 1)
                {
                    hexSb.Append(' ');
                    fullSb.Append(' ');
                    hexTextPos += 1;
                }
            }
            hexSb.Append('\n');
            fullSb.Append("  ");
            hexTextPos += 1;

            for (int i = pos; i < lineEnd; i++)
            {
                byte b = bytes[i];
                char c = b >= 0x20 && b <= 0x7E ? (char)b : '.';
                asciiSb.Append(c);
                fullSb.Append(c);
            }

            asciiSb.Append('\n');
            fullSb.Append('\n');

            pos = lineEnd;
        }

        return (offsetSb.ToString(), hexSb.ToString(), asciiSb.ToString(), fullSb.ToString(), byteToTextPos);
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
        CancelSearch();

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

            CancelSearch();
            _searchResults.Clear();
            _searchIndex = -1;
            SearchInfoText.Text = "";

            _filePath = path;
            _fileLength = info.Length;
            _pageOffset = 0;
            _delimiter = null;
            DelimiterBox.Text = "";
            DelimiterInfoText.Text = "";

            FileInfoText.Text = string.Format("{0}   ({1:N0} 字节)", info.Name, _fileLength);
            NavPanel.IsEnabled = _fileLength > 0;
            CopyButton.IsEnabled = _fileLength > 0;
            RefreshButton.IsEnabled = true;
            AutoRefreshCheck.IsEnabled = true;
            EditPanel.IsEnabled = _fileLength > 0;
            SearchPanel.IsEnabled = _fileLength > 0;
            FormatPanel.IsEnabled = _fileLength > 0;

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
        CancelSearch();
        if (_watcher != null)
        {
            _watcher.Dispose();
            _watcher = null;
        }
        base.OnClosed(e);
    }
}

}
