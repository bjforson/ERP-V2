namespace NickHR.Services.Common;

public class LocalizationService
{
    private readonly Dictionary<string, Dictionary<string, string>> _translations;
    private string _currentLanguage = "en";

    public LocalizationService()
    {
        _translations = new Dictionary<string, Dictionary<string, string>>
        {
            ["en"] = new Dictionary<string, string>
            {
                ["Dashboard"] = "Dashboard",
                ["Employees"] = "Employees",
                ["Leave"] = "Leave",
                ["Payroll"] = "Payroll",
                ["Attendance"] = "Attendance",
                ["Approve"] = "Approve",
                ["Reject"] = "Reject",
                ["Submit"] = "Submit",
                ["Save"] = "Save",
                ["Cancel"] = "Cancel",
                ["Delete"] = "Delete",
                ["Edit"] = "Edit",
                ["Add"] = "Add",
                ["Search"] = "Search",
                ["Filter"] = "Filter",
                ["Export"] = "Export",
                ["Import"] = "Import",
                ["Reports"] = "Reports",
                ["Settings"] = "Settings",
                ["Profile"] = "Profile",
                ["Logout"] = "Logout",
                ["Login"] = "Login",
                ["Welcome"] = "Welcome",
                ["Performance"] = "Performance",
                ["Training"] = "Training",
                ["Recruitment"] = "Recruitment",
                ["Department"] = "Department",
                ["Designation"] = "Designation",
                ["Salary"] = "Salary",
                ["Overtime"] = "Overtime",
                ["Expenses"] = "Expenses",
                ["Assets"] = "Assets",
                ["Policies"] = "Policies",
                ["Announcements"] = "Announcements",
                ["Calendar"] = "Calendar",
                ["Notifications"] = "Notifications",
                ["Pending"] = "Pending",
                ["Approved"] = "Approved",
                ["Rejected"] = "Rejected",
                ["Active"] = "Active",
                ["Inactive"] = "Inactive",
                ["Name"] = "Name",
                ["Email"] = "Email",
                ["Phone"] = "Phone",
                ["Date"] = "Date",
                ["Status"] = "Status",
                ["Actions"] = "Actions",
                ["Total"] = "Total",
                ["Details"] = "Details",
                ["Close"] = "Close",
                ["Confirm"] = "Confirm",
                ["Yes"] = "Yes",
                ["No"] = "No"
            },
            ["tw"] = new Dictionary<string, string>
            {
                ["Dashboard"] = "Adwumayeden",
                ["Employees"] = "Adwumayefo",
                ["Leave"] = "Akwamma",
                ["Payroll"] = "Akatua",
                ["Attendance"] = "Aba mu",
                ["Approve"] = "Pene so",
                ["Reject"] = "Po ani",
                ["Submit"] = "De bra",
                ["Save"] = "Sie",
                ["Cancel"] = "Twa mu",
                ["Delete"] = "Yi fi ho",
                ["Edit"] = "Sesa",
                ["Add"] = "Ka ho",
                ["Search"] = "Hwehwe",
                ["Filter"] = "Hwehwe mu",
                ["Export"] = "Yi fi mu",
                ["Import"] = "De ba mu",
                ["Reports"] = "Amaneboo",
                ["Settings"] = "Nhyehyee",
                ["Profile"] = "Ho nsem",
                ["Logout"] = "Fi mu",
                ["Login"] = "Bra mu",
                ["Welcome"] = "Akwaaba",
                ["Performance"] = "Adwumayede",
                ["Training"] = "Ntetee",
                ["Recruitment"] = "Adwuma pere",
                ["Department"] = "Dwumadibea",
                ["Designation"] = "Dibea",
                ["Salary"] = "Akatua",
                ["Overtime"] = "Bere soronko",
                ["Expenses"] = "Sika a wode",
                ["Assets"] = "Agyapade",
                ["Policies"] = "Nhyehyee",
                ["Announcements"] = "Nkrado",
                ["Calendar"] = "Dabere",
                ["Notifications"] = "Nkra",
                ["Pending"] = "Retwon",
                ["Approved"] = "Woapene so",
                ["Rejected"] = "Woapo",
                ["Active"] = "Edi dwuma",
                ["Inactive"] = "Enni dwuma",
                ["Name"] = "Din",
                ["Email"] = "Email",
                ["Phone"] = "Ahomatorofo",
                ["Date"] = "Da",
                ["Status"] = "Tebea",
                ["Actions"] = "Nneye",
                ["Total"] = "Nyinaa",
                ["Details"] = "Nsem",
                ["Close"] = "To mu",
                ["Confirm"] = "Si so dua",
                ["Yes"] = "Aane",
                ["No"] = "Dabi"
            },
            ["ee"] = new Dictionary<string, string>
            {
                ["Dashboard"] = "Dukodo",
                ["Employees"] = "Duwlawo",
                ["Leave"] = "Gbegble",
                ["Payroll"] = "Fetu",
                ["Attendance"] = "Godo le enu",
                ["Approve"] = "De dzi",
                ["Reject"] = "Gbla",
                ["Submit"] = "Do go",
                ["Save"] = "Dzra",
                ["Cancel"] = "Tso le eme",
                ["Delete"] = "Tutui",
                ["Edit"] = "Troa",
                ["Add"] = "Tsoe dzi",
                ["Search"] = "Di",
                ["Filter"] = "Fli le eme",
                ["Export"] = "Do go",
                ["Import"] = "Tso va eme",
                ["Reports"] = "Gbalevi",
                ["Settings"] = "Dodo",
                ["Profile"] = "Numewo",
                ["Logout"] = "Do go",
                ["Login"] = "Ge eme",
                ["Welcome"] = "Woezor",
                ["Performance"] = "Duwodo",
                ["Training"] = "Hehe",
                ["Recruitment"] = "Duwlawo didi",
                ["Department"] = "Akpa",
                ["Designation"] = "Dzinuwo",
                ["Salary"] = "Fetu",
                ["Overtime"] = "Gameyiyi",
                ["Expenses"] = "Gaxexe",
                ["Assets"] = "Dowoawo",
                ["Policies"] = "Sewo",
                ["Announcements"] = "Gbala",
                ["Calendar"] = "Nutila",
                ["Notifications"] = "Nyatefe",
                ["Pending"] = "Le ncncm",
                ["Approved"] = "Wode dzi",
                ["Rejected"] = "Wobla",
                ["Active"] = "Le duwome",
                ["Inactive"] = "Mele duwome o",
                ["Name"] = "Nko",
                ["Email"] = "Email",
                ["Phone"] = "Telefon",
                ["Date"] = "Nkeke",
                ["Status"] = "Norme",
                ["Actions"] = "Dowowo",
                ["Total"] = "Kataa",
                ["Details"] = "Numewo",
                ["Close"] = "Tu",
                ["Confirm"] = "Ka kpoo",
                ["Yes"] = "E",
                ["No"] = "Ao"
            }
        };
    }

    public string CurrentLanguage => _currentLanguage;

    public string[] AvailableLanguages => new[] { "en", "tw", "ee" };

    public Dictionary<string, string> LanguageNames => new()
    {
        ["en"] = "English",
        ["tw"] = "Twi",
        ["ee"] = "Ewe"
    };

    public void SetLanguage(string languageCode)
    {
        if (_translations.ContainsKey(languageCode))
            _currentLanguage = languageCode;
    }

    public string Translate(string key)
    {
        if (_translations.TryGetValue(_currentLanguage, out var dict) && dict.TryGetValue(key, out var value))
            return value;

        // Fallback to English
        if (_translations.TryGetValue("en", out var enDict) && enDict.TryGetValue(key, out var enValue))
            return enValue;

        return key;
    }

    public string T(string key) => Translate(key);
}
