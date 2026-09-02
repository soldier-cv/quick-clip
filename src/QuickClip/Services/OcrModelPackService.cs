using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using RapidOcrNet;

namespace QuickClip.Services;

/// <summary>离线模型包下载进度（按已完成字节，不写文件内容到日志）。</summary>
public sealed class OcrDownloadProgress
{
    public OcrLocalPack Pack { get; init; }
    public string Message { get; init; } = string.Empty;
    public long BytesReceived { get; init; }
    public long BytesTotal { get; init; }
    /// <summary>整包 0～100；无总长度时仍按文件序号估算。</summary>
    public int Percent { get; init; }
    public bool IsIndeterminate { get; init; }
}

/// <summary>已解析出的本地模型路径；Error 非空表示当前不可用。</summary>
public sealed class OcrResolvedModels
{
    public string? Error { get; init; }
    public string? DetPath { get; init; }
    public string? RecPath { get; init; }
    public string? KeysPath { get; init; }
    public string? ClsPath { get; init; }
    public bool IsV6 { get; init; }
    public string Title { get; init; } = "离线增强";
}

/// <summary>
/// 离线 OCR 模型包：下载、校验、解析自定义目录、懒加载 RapidOcr。
/// 模型落在 %LOCALAPPDATA%\QuickClip\ocr\，不打进安装包。
///
/// @author xudong.hua,grok
/// @since 2026-09-02
/// </summary>
public sealed class OcrModelPackService : IDisposable
{
    private const long MaxFileBytes = 200L * 1024 * 1024;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan ProgressUiInterval = TimeSpan.FromMilliseconds(200);

    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HttpClient _http = CreateHttpClient();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly object _engineLock = new();

    private RapidOcr? _engine;
    private string? _engineKey;
    private CancellationTokenSource? _downloadCts;
    private OcrLocalPack? _downloadingPack;
    private OcrDownloadProgress? _currentProgress;
    private DateTime _lastProgressUtc = DateTime.MinValue;
    private readonly Dictionary<OcrLocalPack, string> _lastOutcome = new();

    public event Action<OcrDownloadProgress>? DownloadProgress;
    public event Action? PacksChanged;

    public bool IsDownloading => _downloadingPack != null;

    public OcrLocalPack? DownloadingPack => _downloadingPack;

    public OcrDownloadProgress? CurrentProgress => _currentProgress;

    public OcrModelPackService(AppPaths paths, SettingsService settings)
    {
        _paths = paths;
        _settings = settings;
        Directory.CreateDirectory(_paths.OcrDir);
    }

    public string PackDirectory(OcrPackDefinition pack) =>
        Path.Combine(_paths.OcrDir, pack.Id);

    public bool IsOfficialInstalled(OcrLocalPack pack)
    {
        var def = OcrModelCatalog.Find(pack);
        return def != null && ResolveOfficial(def).Error == null;
    }

    public long InstalledBytes(OcrLocalPack pack)
    {
        var def = OcrModelCatalog.Find(pack);
        if (def == null)
        {
            return 0;
        }

        string dir = PackDirectory(def);
        if (!Directory.Exists(dir))
        {
            return 0;
        }

        long total = 0;
        foreach (var file in def.Files)
        {
            string path = Path.Combine(dir, file.FileName);
            if (File.Exists(path))
            {
                total += new FileInfo(path).Length;
            }
        }

        return total;
    }

    public OcrResolvedModels InspectCustom(string? directory) => ResolveCustom(directory);

    public OcrResolvedModels ResolveCurrent()
    {
        if (_settings.OcrLocalPack == OcrLocalPack.Custom)
        {
            return ResolveCustom(_settings.OcrCustomDir);
        }

        var def = OcrModelCatalog.Find(_settings.OcrLocalPack) ?? OcrModelCatalog.Medium;
        return ResolveOfficial(def);
    }

    public string DescribeCurrentPack()
    {
        var resolved = ResolveCurrent();
        return resolved.Title;
    }

    /// <summary>
    /// 设置页状态文案：下载中显示进度；失败/取消保留原因，避免刷新后变成「未下载」。
    /// </summary>
    public string OfficialStatusText(OcrLocalPack pack)
    {
        if (_downloadingPack == pack)
        {
            return _currentProgress?.Message ?? "正在下载…";
        }

        if (IsOfficialInstalled(pack))
        {
            return "已安装 · " + FormatMb(InstalledBytes(pack));
        }

        if (_lastOutcome.TryGetValue(pack, out string? outcome) && !string.IsNullOrWhiteSpace(outcome))
        {
            return outcome;
        }

        return "未下载";
    }

    public async Task DownloadAsync(OcrLocalPack pack)
    {
        var def = OcrModelCatalog.Find(pack);
        if (def == null)
        {
            throw new InvalidOperationException("自定义模型请放到本地目录，不能在线下载。");
        }

        if (_downloadingPack != null)
        {
            throw new InvalidOperationException("已有模型正在下载。");
        }

        var cts = new CancellationTokenSource();
        _downloadCts = cts;
        _downloadingPack = pack;
        _lastOutcome.Remove(pack);
        string destDir = PackDirectory(def);
        string tmpDir = Path.Combine(_paths.OcrDir, "tmp-" + def.Id);
        DebugLog.Log($"开始下载离线 OCR 模型 pack={def.Id} files={def.Files.Count} dest={destDir}");
        try
        {
            Directory.CreateDirectory(tmpDir);
            int fileCount = def.Files.Count;
            Report(pack, "正在准备下载 " + def.ModelName + "…", 0, 0, 0, indeterminate: false, force: true);

            for (int i = 0; i < fileCount; i++)
            {
                var file = def.Files[i];
                string destPath = Path.Combine(destDir, file.FileName);
                if (File.Exists(destPath) && FileLooksComplete(destPath, file))
                {
                    int skipped = (int)Math.Clamp((i + 1) * 100.0 / fileCount, 0, 100);
                    long size = new FileInfo(destPath).Length;
                    DebugLog.Log($"OCR 文件已就绪，跳过下载: {file.FileName} size={size}");
                    Report(pack, $"已跳过 {file.FileName}（已就绪） · {skipped}%", 0, 0, skipped, force: true);
                    continue;
                }

                string tmpPath = Path.Combine(tmpDir, file.FileName);
                await DownloadFileAsync(pack, file, tmpPath, i, fileCount, cts.Token);
            }

            Directory.CreateDirectory(destDir);
            await _runGate.WaitAsync(cts.Token);
            try
            {
                InvalidateEngine();
                foreach (var file in def.Files)
                {
                    string tmpPath = Path.Combine(tmpDir, file.FileName);
                    string destPath = Path.Combine(destDir, file.FileName);
                    if (!File.Exists(tmpPath))
                    {
                        continue;
                    }

                    if (File.Exists(destPath))
                    {
                        File.Delete(destPath);
                    }

                    File.Move(tmpPath, destPath);
                    DebugLog.Log($"OCR 文件已落地: {file.FileName} size={new FileInfo(destPath).Length}");
                }
            }
            finally
            {
                _runGate.Release();
            }

            var resolved = ResolveOfficial(def);
            if (resolved.Error != null)
            {
                string detail = DescribePackFiles(def);
                DebugLog.Log($"OCR 模型下载后仍不完整: {def.Id} error={resolved.Error} {detail}");
                throw new InvalidOperationException(resolved.Error + "（" + detail + "）");
            }

            _lastOutcome.Remove(pack);
            Report(pack, def.ModelName + " 已就绪", 0, 0, 100, force: true);
            DebugLog.Log("离线 OCR 模型已下载: " + def.Id + " " + DescribePackFiles(def));
        }
        catch (OperationCanceledException)
        {
            const string canceled = "已取消下载";
            _lastOutcome[pack] = canceled;
            Report(pack, canceled, 0, 0, 0, force: true);
            DebugLog.Log("已取消下载离线 OCR 模型: " + def.Id);
            throw;
        }
        catch (Exception ex)
        {
            string failed = "下载失败：" + TrimOneLine(ex.Message);
            _lastOutcome[pack] = failed;
            DebugLog.LogException("下载离线 OCR 模型失败 pack=" + def.Id, ex);
            Report(pack, failed, 0, 0, 0, force: true);
            throw;
        }
        finally
        {
            _downloadingPack = null;
            _downloadCts = null;
            _currentProgress = null;
            cts.Dispose();
            TryDeleteDir(tmpDir);
            PacksChanged?.Invoke();
        }
    }

    public void CancelDownload()
    {
        try
        {
            if (_downloadingPack != null)
            {
                DebugLog.Log("请求取消 OCR 模型下载: " + _downloadingPack);
            }

            _downloadCts?.Cancel();
        }
        catch (Exception ex)
        {
            DebugLog.LogException("取消 OCR 模型下载失败", ex);
        }
    }

    public void DeleteOfficial(OcrLocalPack pack)
    {
        var def = OcrModelCatalog.Find(pack);
        if (def == null)
        {
            return;
        }

        _runGate.Wait();
        try
        {
            InvalidateEngine();
            TryDeleteDir(PackDirectory(def));
        }
        finally
        {
            _runGate.Release();
        }

        _lastOutcome.Remove(pack);
        PacksChanged?.Invoke();
        DebugLog.Log("已删除离线 OCR 模型: " + def.Id);
    }

    public async Task<string?> RecognizeAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        var resolved = ResolveCurrent();
        if (resolved.Error != null ||
            resolved.DetPath == null ||
            resolved.RecPath == null ||
            resolved.KeysPath == null ||
            resolved.ClsPath == null)
        {
            DebugLog.Log("离线 OCR 无法识别: " + (resolved.Error ?? "离线模型不完整") +
                         " pack=" + _settings.OcrLocalPack);
            throw new InvalidOperationException(resolved.Error ?? "离线模型不完整");
        }

        await _runGate.WaitAsync(cancellationToken);
        try
        {
            RapidOcr engine = await Task.Run(() => EnsureEngine(resolved), cancellationToken);
            var options = resolved.IsV6 ? RapidOcrOptions.PPOCRv6 : RapidOcrOptions.Default;
            var result = await engine.DetectAsync(imagePath, options, null, cancellationToken);
            string? text = result.StrRes;
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        finally
        {
            _runGate.Release();
        }
    }

    public void InvalidateEngine()
    {
        lock (_engineLock)
        {
            _engine?.Dispose();
            _engine = null;
            _engineKey = null;
        }
    }

    public void Dispose()
    {
        CancelDownload();
        InvalidateEngine();
        _http.Dispose();
        _runGate.Dispose();
    }

    private RapidOcr EnsureEngine(OcrResolvedModels resolved)
    {
        string key = string.Join("|",
            resolved.DetPath, resolved.RecPath, resolved.KeysPath, resolved.ClsPath, resolved.IsV6);
        lock (_engineLock)
        {
            if (_engine != null && _engineKey == key)
            {
                return _engine;
            }

            _engine?.Dispose();
            var modelSet = (resolved.IsV6 ? RapidOcrModelSet.PPOCRv6Small : RapidOcrModelSet.PPOCRv5Latin) with
            {
                DetModelPath = resolved.DetPath!,
                RecModelPath = resolved.RecPath!,
                KeysPath = resolved.KeysPath!,
                ClsModelPath = resolved.ClsPath!
            };

            var engine = new RapidOcr();
            engine.InitModels(modelSet);
            _engine = engine;
            _engineKey = key;
            DebugLog.Log("已加载 RapidOCR 引擎: " + resolved.Title + " v6=" + resolved.IsV6);
            return engine;
        }
    }

    private OcrResolvedModels ResolveOfficial(OcrPackDefinition def)
    {
        string dir = PackDirectory(def);
        var missing = new List<string>();
        string? det = null, rec = null, keys = null, cls = null;
        foreach (var file in def.Files)
        {
            string path = Path.Combine(dir, file.FileName);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                missing.Add(file.FileName);
                continue;
            }

            string name = file.FileName.ToLowerInvariant();
            if (name.Contains("det")) det = path;
            else if (name.Contains("rec")) rec = path;
            else if (name.Contains("dict") || name.Contains("keys")) keys = path;
            else if (name.Contains("cls")) cls = path;
        }

        if (missing.Count > 0 || det == null || rec == null || keys == null || cls == null)
        {
            return new OcrResolvedModels
            {
                Error = "未下载 " + def.ModelName,
                Title = def.ModelName
            };
        }

        return new OcrResolvedModels
        {
            DetPath = det,
            RecPath = rec,
            KeysPath = keys,
            ClsPath = cls,
            IsV6 = true,
            Title = def.ModelName
        };
    }

    private OcrResolvedModels ResolveCustom(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return new OcrResolvedModels
            {
                Error = "请选择包含 det / rec / 字典 的模型目录",
                Title = "自定义"
            };
        }

        string dir;
        try
        {
            dir = Path.GetFullPath(directory.Trim());
        }
        catch (Exception)
        {
            return new OcrResolvedModels
            {
                Error = "模型目录路径无效",
                Title = "自定义"
            };
        }

        if (!Directory.Exists(dir))
        {
            return new OcrResolvedModels
            {
                Error = "请选择包含 det / rec / 字典 的模型目录",
                Title = "自定义"
            };
        }
        string? det = FindModelFile(dir, "det", ".onnx");
        string? rec = FindModelFile(dir, "rec", ".onnx");
        string? keys = FindModelFile(dir, "dict", ".txt") ?? FindModelFile(dir, "keys", ".txt");
        string? cls = FindModelFile(dir, "cls", ".onnx") ?? FindOfficialCls();

        var missing = new List<string>();
        if (det == null) missing.Add("检测模型（*det*.onnx）");
        if (rec == null) missing.Add("识别模型（*rec*.onnx）");
        if (keys == null) missing.Add("字典（*dict*.txt / keys.txt）");
        if (cls == null) missing.Add("方向分类（*cls*.onnx，或先下载一个官方包）");

        if (missing.Count > 0)
        {
            return new OcrResolvedModels
            {
                Error = "自定义目录缺少：" + string.Join("、", missing),
                Title = "自定义"
            };
        }

        bool isV6 = ContainsV6(det) || ContainsV6(rec);
        return new OcrResolvedModels
        {
            DetPath = det,
            RecPath = rec,
            KeysPath = keys,
            ClsPath = cls,
            IsV6 = isV6,
            Title = "自定义"
        };
    }

    private string? FindOfficialCls()
    {
        foreach (var pack in OcrModelCatalog.OfficialPacks)
        {
            string path = Path.Combine(PackDirectory(pack), OcrModelCatalog.Small.Files[^1].FileName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string? FindModelFile(string dir, string token, string extension)
    {
        string exact = Path.Combine(dir, token + extension);
        if (File.Exists(exact))
        {
            return exact;
        }

        try
        {
            var matches = Directory.GetFiles(dir, "*" + extension)
                .Where(path => Path.GetFileName(path)
                    .Contains(token, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetFileName(path).Length)
                .ToArray();
            return matches.Length == 0 ? null : matches[0];
        }
        catch (Exception ex)
        {
            DebugLog.LogException("扫描自定义 OCR 目录失败", ex);
            return null;
        }
    }

    private static bool ContainsV6(string? path) =>
        path != null &&
        (path.Contains("v6", StringComparison.OrdinalIgnoreCase) ||
         path.Contains("ocrv6", StringComparison.OrdinalIgnoreCase));

    private async Task DownloadFileAsync(
        OcrLocalPack pack,
        OcrRemoteFile file,
        string destPath,
        int fileIndex,
        int fileCount,
        CancellationToken cancellationToken)
    {
        DebugLog.Log($"OCR 开始拉取 {file.FileName} url={DebugLog.DescribeUrl(file.Url)}");
        using var response = await _http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        string? contentType = response.Content.Headers.ContentType?.MediaType;
        long? length = response.Content.Headers.ContentLength;
        string finalUrl = DebugLog.DescribeUrl(response.RequestMessage?.RequestUri?.ToString() ?? file.Url);
        DebugLog.Log(
            $"OCR 响应 {file.FileName} http={(int)response.StatusCode} type={contentType ?? "-"} " +
            $"len={length?.ToString() ?? "unknown"} final={finalUrl}");

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} 下载 {file.FileName} 失败");
        }

        if (contentType != null &&
            contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                file.FileName + " 下载到的是网页而不是模型文件（可能被拦截或需登录）");
        }

        if (length is > MaxFileBytes)
        {
            throw new InvalidOperationException(file.FileName + " 超过单文件上限");
        }

        if (length is 0)
        {
            throw new InvalidOperationException(file.FileName + " 服务器返回空文件");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        string actual;
        long received;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[81920];
            received = 0;
            int startPercent = (int)Math.Clamp(fileIndex * 100.0 / fileCount, 0, 99);
            Report(
                pack,
                $"{fileIndex + 1}/{fileCount}  {file.FileName}  连接中…  ·  {startPercent}%",
                0,
                length ?? 0,
                startPercent,
                force: true);

            while (true)
            {
                int read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                received += read;
                if (received > MaxFileBytes)
                {
                    throw new InvalidOperationException(file.FileName + " 超过单文件上限");
                }

                hasher.AppendData(buffer.AsSpan(0, read));
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

                long total = length ?? 0;
                double fileFrac = total > 0 ? Math.Clamp((double)received / total, 0, 1) : 0;
                int percent = (int)Math.Clamp((fileIndex + fileFrac) / fileCount * 100, 0, 99);
                string prefix = $"{fileIndex + 1}/{fileCount}  {file.FileName}";
                string size = total > 0
                    ? $"{FormatMb(received)} / {FormatMb(total)}"
                    : FormatMb(received);
                Report(pack, $"{prefix}  {size}  ·  {percent}%", received, total, percent);
            }

            await output.FlushAsync(cancellationToken);
            actual = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }

        // 必须先关掉写入流再读盘：字典只有约 75KB，整份还在 80KB 缓冲里时 Length=0，会被误判无效。
        if (!string.IsNullOrEmpty(file.Sha256) &&
            !actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            DebugLog.Log(
                $"OCR 校验失败 {file.FileName} size={received} expected={file.Sha256} actual={actual}");
            TryDeleteFile(destPath);
            throw new InvalidOperationException(file.FileName + " 校验失败，请重试");
        }

        if (file.Sha256 == null && !LooksLikeDict(destPath))
        {
            long onDisk = File.Exists(destPath) ? new FileInfo(destPath).Length : -1;
            DebugLog.Log($"OCR 字典无效 {file.FileName} received={received} onDisk={onDisk}");
            TryDeleteFile(destPath);
            throw new InvalidOperationException(file.FileName + " 不是有效字典文件");
        }

        DebugLog.Log($"OCR 文件下载完成 {file.FileName} size={received} sha256ok={!string.IsNullOrEmpty(file.Sha256)}");
    }

    private void Report(
        OcrLocalPack pack,
        string message,
        long received,
        long total,
        int percent,
        bool indeterminate = false,
        bool force = false)
    {
        DateTime now = DateTime.UtcNow;
        if (!force && now - _lastProgressUtc < ProgressUiInterval)
        {
            return;
        }

        _lastProgressUtc = now;
        var progress = new OcrDownloadProgress
        {
            Pack = pack,
            Message = message,
            BytesReceived = received,
            BytesTotal = total,
            Percent = Math.Clamp(percent, 0, 100),
            IsIndeterminate = indeterminate
        };
        _currentProgress = progress;
        DownloadProgress?.Invoke(progress);
    }

    private string DescribePackFiles(OcrPackDefinition def)
    {
        string dir = PackDirectory(def);
        var parts = new List<string>();
        foreach (var file in def.Files)
        {
            string path = Path.Combine(dir, file.FileName);
            if (!File.Exists(path))
            {
                parts.Add(file.FileName + "=missing");
                continue;
            }

            parts.Add(file.FileName + "=" + new FileInfo(path).Length);
        }

        return string.Join("; ", parts);
    }

    /// <summary>跳过已落盘文件时只看存在与非空；哈希只在下载过程中算，避免设置页反复扫 132MB。</summary>
    private static bool FileLooksComplete(string path, OcrRemoteFile file)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                return false;
            }

            if (file.Sha256 == null ||
                Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
            {
                return LooksLikeDict(path);
            }

            return Path.GetExtension(path).Equals(".onnx", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            DebugLog.LogException("检查 OCR 模型文件失败", ex);
            return false;
        }
    }

    /// <summary>
    /// PP-OCR 字典是一行一字，前几行就是 ! " # … 以及后来的 &lt; {。
    /// 只看全文首字符会把 HTML/JSON 和合法字典搞混；按行判断更稳。
    /// </summary>
    private static bool LooksLikeDict(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length is < 32 or > 2 * 1024 * 1024)
            {
                return false;
            }

            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string? first = null;
            int lines = 0;
            while (lines < 8 && reader.ReadLine() is { } raw)
            {
                string line = raw.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                first ??= line;
                lines++;
            }

            if (first == null)
            {
                return false;
            }

            // 整页 HTML / JSON 对象，不是一行一字的字典
            if (first.StartsWith("<", StringComparison.Ordinal) ||
                first.StartsWith("{", StringComparison.Ordinal) ||
                first.StartsWith("[", StringComparison.Ordinal))
            {
                return first.Length <= 4;
            }

            return true;
        }
        catch (Exception ex)
        {
            DebugLog.LogException("检查 OCR 字典失败", ex);
            return false;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = DownloadTimeout };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            $"QuickClip/{UpdateService.CurrentVersion}");
        return client;
    }

    private static string FormatMb(long bytes) =>
        (bytes / (1024d * 1024d)).ToString("0.0") + " MB";

    private static string TrimOneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string one = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return one.Length <= 160 ? one : one[..160] + "…";
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch (Exception ex)
        {
            DebugLog.LogException("清理 OCR 临时目录失败", ex);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            DebugLog.LogException("删除损坏 OCR 文件失败", ex);
        }
    }
}
