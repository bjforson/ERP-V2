using System.Text;
using Ganss.Xss;
using Microsoft.EntityFrameworkCore;
using NickHR.Core.Entities.System;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Letter;

public class LetterService
{
    private readonly NickHRDbContext _db;
    private readonly IHtmlSanitizer _sanitizer;

    public LetterService(NickHRDbContext db, IHtmlSanitizer sanitizer)
    {
        _db = db;
        _sanitizer = sanitizer;
    }

    public async Task<List<LetterTemplate>> GetTemplatesAsync()
    {
        return await _db.Set<LetterTemplate>()
            .Where(t => t.IsActive)
            .OrderBy(t => t.Name)
            .ToListAsync();
    }

    public async Task<LetterTemplate?> GetTemplateByIdAsync(int id)
    {
        return await _db.Set<LetterTemplate>().FindAsync(id);
    }

    public async Task<LetterTemplate> CreateTemplateAsync(LetterTemplate template)
    {
        _db.Set<LetterTemplate>().Add(template);
        await _db.SaveChangesAsync();
        return template;
    }

    public async Task<string> GeneratePreviewAsync(int templateId, int employeeId)
    {
        var template = await _db.Set<LetterTemplate>().FindAsync(templateId)
            ?? throw new KeyNotFoundException("Template not found.");

        var emp = await _db.Employees
            .Include(e => e.Department)
            .Include(e => e.Designation)
            .FirstOrDefaultAsync(e => e.Id == employeeId)
            ?? throw new KeyNotFoundException("Employee not found.");

        return MergeFields(template.HtmlBody, emp);
    }

    public async Task<byte[]> GeneratePdfAsync(int templateId, int employeeId, int? generatedById)
    {
        var html = await GeneratePreviewAsync(templateId, employeeId);

        // Simple HTML-to-text PDF generation (using basic text for now)
        // In production, use QuestPDF or similar library
        var pdfContent = ConvertHtmlToSimplePdf(html);

        // Record the generation
        var record = new GeneratedLetter
        {
            LetterTemplateId = templateId,
            EmployeeId = employeeId,
            GeneratedById = generatedById,
            GeneratedAt = DateTime.UtcNow
        };
        _db.Set<GeneratedLetter>().Add(record);
        await _db.SaveChangesAsync();

        return pdfContent;
    }

    public async Task<List<GeneratedLetter>> GetGeneratedLettersAsync(int? employeeId = null)
    {
        var query = _db.Set<GeneratedLetter>()
            .Include(g => g.LetterTemplate)
            .Include(g => g.Employee)
            .AsQueryable();

        if (employeeId.HasValue)
            query = query.Where(g => g.EmployeeId == employeeId.Value);

        return await query.OrderByDescending(g => g.GeneratedAt).ToListAsync();
    }

    public async Task SeedDefaultTemplatesAsync()
    {
        if (await _db.Set<LetterTemplate>().AnyAsync()) return;

        var templates = new[]
        {
            new LetterTemplate
            {
                Name = "Employment Confirmation Letter",
                Code = "EMPLOYMENT_CONFIRMATION",
                Category = "Employment",
                HtmlBody = @"<h2>Employment Confirmation Letter</h2>
<p>Date: {{Date}}</p>
<p>To Whom It May Concern,</p>
<p>This is to confirm that <strong>{{FirstName}} {{LastName}}</strong> (Employee Code: {{EmployeeCode}})
is currently employed at our organization in the <strong>{{Department}}</strong> department
as a <strong>{{Designation}}</strong>.</p>
<p>Date of Employment: {{HireDate}}</p>
<p>This letter is issued upon request for whatever purpose it may serve.</p>
<p>Sincerely,<br/>Human Resources Department</p>"
            },
            new LetterTemplate
            {
                Name = "Reference Letter",
                Code = "REFERENCE_LETTER",
                Category = "Employment",
                HtmlBody = @"<h2>Letter of Reference</h2>
<p>Date: {{Date}}</p>
<p>To Whom It May Concern,</p>
<p>I am writing to recommend <strong>{{FirstName}} {{LastName}}</strong> who has been employed
with our organization since {{HireDate}} in the capacity of <strong>{{Designation}}</strong>
in the <strong>{{Department}}</strong> department.</p>
<p>During their tenure, they have demonstrated professionalism and dedication to their role.</p>
<p>I recommend them without reservation.</p>
<p>Sincerely,<br/>Human Resources Department</p>"
            },
            new LetterTemplate
            {
                Name = "Warning Letter",
                Code = "WARNING_LETTER",
                Category = "Disciplinary",
                HtmlBody = @"<h2>Warning Letter</h2>
<p>Date: {{Date}}</p>
<p>Dear {{FirstName}} {{LastName}},</p>
<p>Employee Code: {{EmployeeCode}}<br/>
Department: {{Department}}<br/>
Designation: {{Designation}}</p>
<p>This letter serves as a formal warning regarding your conduct/performance.
Please take the necessary steps to address this matter promptly.</p>
<p>Failure to improve may result in further disciplinary action.</p>
<p>Sincerely,<br/>Human Resources Department</p>"
            }
        };

        _db.Set<LetterTemplate>().AddRange(templates);
        await _db.SaveChangesAsync();
    }

    private string MergeFields(string htmlBody, NickHR.Core.Entities.Core.Employee emp)
    {
        // TRUST BOUNDARY (Wave 2J): templates are privileged-author trusted (HR
        // admins authoring rich HTML via EmailTemplateDialog), but merge values
        // come from user-editable employee fields and could contain `<script>`,
        // `<img onerror=...>`, or `javascript:` URLs. A full HtmlSanitizer pass
        // over the assembled body strips XSS payloads from EITHER source while
        // preserving the template's intentional HTML structure (headings,
        // <strong>, paragraphs, etc.).

        var merged = htmlBody
            .Replace("{{FirstName}}", emp.FirstName ?? string.Empty)
            .Replace("{{LastName}}", emp.LastName ?? string.Empty)
            .Replace("{{EmployeeCode}}", emp.EmployeeCode ?? string.Empty)
            .Replace("{{Department}}", emp.Department?.Name ?? "N/A")
            .Replace("{{Designation}}", emp.Designation?.Title ?? "N/A")
            .Replace("{{HireDate}}", emp.HireDate?.ToString("dd MMM yyyy") ?? "N/A")
            .Replace("{{BasicSalary}}", emp.BasicSalary.ToString("N2"))
            .Replace("{{Date}}", DateTime.UtcNow.ToString("dd MMM yyyy"));

        return _sanitizer.Sanitize(merged);
    }

    private byte[] ConvertHtmlToSimplePdf(string html)
    {
        // Simple text-based PDF placeholder
        // In production, this would use QuestPDF to render proper PDF
        var text = html
            .Replace("<br/>", "\n")
            .Replace("<br>", "\n")
            .Replace("</p>", "\n\n")
            .Replace("</h2>", "\n\n")
            .Replace("</h1>", "\n\n");

        // Strip remaining HTML tags
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "");
        text = System.Net.WebUtility.HtmlDecode(text);

        return Encoding.UTF8.GetBytes(text);
    }
}
