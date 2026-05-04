# NickERP v2 vulnerability scan

**Date (UTC):** 2026-05-04
**Tool:** `dotnet list package --vulnerable --include-transitive`
**Source:** C:/Shared/ERP V2/.worktrees/sprint-30
**Project count:** 51

Reference: docs/security/audit-checklist-2026.md SEC-DEP-1.

---

## modules\inspection\plugins\NickERP.Inspection.Inference.OCR.ContainerNumber\NickERP.Inspection.Inference.OCR.ContainerNumber.csproj

```
  Determining projects to restore...
C:\Shared\ERP V2\.worktrees\sprint-30\modules\inspection\plugins\NickERP.Inspection.Inference.OCR.ContainerNumber\NickERP.Inspection.Inference.OCR.ContainerNumber.csproj : warning NU1902: Package 'SixLabors.ImageSharp' 3.1.10 has a known moderate severity vulnerability, https://github.com/advisories/GHSA-rxmq-m78w-7wmc
  All projects are up-to-date for restore.

The following sources were used:
   https://api.nuget.org/v3/index.json

Project `NickERP.Inspection.Inference.OCR.ContainerNumber` has the following vulnerable packages
   [net10.0]: 
   Top-level Package           Requested   Resolved   Severity   Advisory URL                                     
   > SixLabors.ImageSharp      3.1.10      3.1.10     Moderate   https://github.com/advisories/GHSA-rxmq-m78w-7wmc
```

## modules\inspection\tools\OcrSmokeTest\NickERP.Inspection.Tools.OcrSmokeTest.csproj

```
  Determining projects to restore...
C:\Shared\ERP V2\.worktrees\sprint-30\modules\inspection\plugins\NickERP.Inspection.Inference.OCR.ContainerNumber\NickERP.Inspection.Inference.OCR.ContainerNumber.csproj : warning NU1902: Package 'SixLabors.ImageSharp' 3.1.10 has a known moderate severity vulnerability, https://github.com/advisories/GHSA-rxmq-m78w-7wmc [C:\Shared\ERP V2\.worktrees\sprint-30\modules\inspection\tools\OcrSmokeTest\NickERP.Inspection.Tools.OcrSmokeTest.csproj]
C:\Shared\ERP V2\.worktrees\sprint-30\modules\inspection\tools\OcrSmokeTest\NickERP.Inspection.Tools.OcrSmokeTest.csproj : warning NU1902: Package 'SixLabors.ImageSharp' 3.1.10 has a known moderate severity vulnerability, https://github.com/advisories/GHSA-rxmq-m78w-7wmc
  All projects are up-to-date for restore.

The following sources were used:
   https://api.nuget.org/v3/index.json

Project `NickERP.Inspection.Tools.OcrSmokeTest` has the following vulnerable packages
   [net10.0]: 
   Top-level Package           Requested   Resolved   Severity   Advisory URL                                     
   > SixLabors.ImageSharp      3.1.10      3.1.10     Moderate   https://github.com/advisories/GHSA-rxmq-m78w-7wmc
```

## tools\inference-evaluation\container-ocr\NickERP.Tools.OcrEvaluation.csproj

```
  Determining projects to restore...
C:\Shared\ERP V2\.worktrees\sprint-30\modules\inspection\plugins\NickERP.Inspection.Inference.OCR.ContainerNumber\NickERP.Inspection.Inference.OCR.ContainerNumber.csproj : warning NU1902: Package 'SixLabors.ImageSharp' 3.1.10 has a known moderate severity vulnerability, https://github.com/advisories/GHSA-rxmq-m78w-7wmc [C:\Shared\ERP V2\.worktrees\sprint-30\tools\inference-evaluation\container-ocr\NickERP.Tools.OcrEvaluation.csproj]
  All projects are up-to-date for restore.

The following sources were used:
   https://api.nuget.org/v3/index.json

Project `NickERP.Tools.OcrEvaluation` has the following vulnerable packages
   [net10.0]: 
   Transitive Package          Resolved   Severity   Advisory URL                                     
   > SixLabors.ImageSharp      3.1.10     Moderate   https://github.com/advisories/GHSA-rxmq-m78w-7wmc
```

---

## Summary

- Projects scanned: 51
- Projects with vulnerable packages: 3
- Total vulnerable references: 3

**Result:** 3 finding(s) require investigation. See per-project sections above.

Severity guidance per SEC-DEP-1:
- **High** / **Critical** at pilot time: P0 (block pilot).
- **Moderate**: P1 (fix-before-launch).
- **Low**: P2 (fix-by-launch+1mo).
