namespace NickHR.Core.DTOs.Department;

public class OrgChartNodeDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? HeadName { get; set; }
    public string? HeadPhotoUrl { get; set; }
    public int EmployeeCount { get; set; }
    public List<OrgChartNodeDto> Children { get; set; } = new();
}
