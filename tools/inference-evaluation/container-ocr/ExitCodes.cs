// Copyright (c) Nick TC-Scan Ltd. All rights reserved.

namespace NickERP.Tools.OcrEvaluation;

/// <summary>
/// Process exit codes. Same numbering as the sibling Python harvester
/// (<c>tools/inference-training/container-ocr/harvest_plates.py</c>) so
/// CI pipelines that already grok the harvester's exit map don't need to
/// re-learn another scheme.
/// </summary>
internal static class ExitCodes
{
    public const int Ok = 0;
    public const int BadArgs = 2;
    public const int ForbiddenOut = 3;
    public const int CorpusUnavailable = 4;
    public const int NoRows = 5;
    public const int EngineUnsupported = 6;
    public const int TessdataNotFound = 7;
}
