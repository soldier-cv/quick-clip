namespace QuickClip.Services;

/// <summary>离线 OCR 模型档：官方 Small / Medium，或用户自备目录。</summary>
public enum OcrLocalPack
{
    Small,
    Medium,
    Custom
}

/// <summary>单个需下载的模型文件。</summary>
public sealed class OcrRemoteFile
{
    public required string FileName { get; init; }
    public required string Url { get; init; }
    public string? Sha256 { get; init; }
}

/// <summary>官方模型包的展示与下载清单（不含自定义）。</summary>
public sealed class OcrPackDefinition
{
    public required OcrLocalPack Pack { get; init; }
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string ModelName { get; init; }
    public required string SizeLabel { get; init; }
    public required string AccuracyLabel { get; init; }
    public required string Hint { get; init; }
    public required bool Recommended { get; init; }
    public required IReadOnlyList<OcrRemoteFile> Files { get; init; }
}

/// <summary>
/// PP-OCRv6 官方包目录。地址与 SHA256 来自 RapidOCR v3.9.2 模型清单，
/// 下载失败时用户仍可用「自定义」自行放入文件。
///
/// @author xudong.hua,grok
/// @since 2026-09-02
/// </summary>
public static class OcrModelCatalog
{
    private const string ModelScopeBase =
        "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2";

    private static readonly OcrRemoteFile ClsFile = new()
    {
        FileName = "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx",
        Url = ModelScopeBase + "/onnx/PP-OCRv5/cls/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx",
        Sha256 = "54379ae5174d026780215fc748a7f31910dee36818e63d49e17dc598ecc82df7"
    };

    /// <summary>Small / Medium 共用同一份 PP-OCRv6 字典。</summary>
    private static readonly OcrRemoteFile DictFile = new()
    {
        FileName = "ppocrv6_dict.txt",
        Url = ModelScopeBase + "/paddle/PP-OCRv6/rec/PP-OCRv6_rec_medium/ppocrv6_dict.txt",
        Sha256 = "b5f2bfe2bdd9448429e3e82b51c789775d9b42f2403d082b00662eb77e401c5d"
    };

    public static OcrPackDefinition Small { get; } = new()
    {
        Pack = OcrLocalPack.Small,
        Id = "ppocrv6-small",
        Title = "均衡",
        ModelName = "PP-OCRv6 Small",
        SizeLabel = "约 30 MB",
        AccuracyLabel = "识别 81.3% · 检测 84.1%",
        Hint = "日常截图够用",
        Recommended = false,
        Files =
        [
            new OcrRemoteFile
            {
                FileName = "PP-OCRv6_det_small.onnx",
                Url = ModelScopeBase + "/onnx/PP-OCRv6/det/PP-OCRv6_det_small.onnx",
                Sha256 = "090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f"
            },
            new OcrRemoteFile
            {
                FileName = "PP-OCRv6_rec_small.onnx",
                Url = ModelScopeBase + "/onnx/PP-OCRv6/rec/PP-OCRv6_rec_small.onnx",
                Sha256 = "6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884"
            },
            DictFile,
            ClsFile
        ]
    };

    public static OcrPackDefinition Medium { get; } = new()
    {
        Pack = OcrLocalPack.Medium,
        Id = "ppocrv6-medium",
        Title = "高精度",
        ModelName = "PP-OCRv6 Medium",
        SizeLabel = "约 132 MB",
        AccuracyLabel = "识别 83.2% · 检测 86.2%",
        Hint = "屏幕、混排更稳",
        Recommended = true,
        Files =
        [
            new OcrRemoteFile
            {
                FileName = "PP-OCRv6_det_medium.onnx",
                Url = ModelScopeBase + "/onnx/PP-OCRv6/det/PP-OCRv6_det_medium.onnx",
                Sha256 = "92078b7355007ccfffcd4c8cd441a3afd4538904d06881b29a155e1e679907c2"
            },
            new OcrRemoteFile
            {
                FileName = "PP-OCRv6_rec_medium.onnx",
                Url = ModelScopeBase + "/onnx/PP-OCRv6/rec/PP-OCRv6_rec_medium.onnx",
                Sha256 = "eef444829dbbe18d7fea59a3f6eb75647518d2b3a9568d27c92e42940204894b"
            },
            DictFile,
            ClsFile
        ]
    };

    public static IReadOnlyList<OcrPackDefinition> OfficialPacks { get; } = [Small, Medium];

    public static OcrPackDefinition? Find(OcrLocalPack pack) => pack switch
    {
        OcrLocalPack.Small => Small,
        OcrLocalPack.Medium => Medium,
        _ => null
    };
}
