namespace Drps.Ingestion;

public class SecEdgarOptions
{
    // SEC's fair-use policy requires every request to carry a descriptive User-Agent
    // identifying the requester and a real contact method (email or domain). Real value as
    // of 2026-07-27 (was a placeholder, "REPLACE-WITH-REAL-CONTACT-EMAIL", flagged 2026-07-16
    // and confirmed still unresolved 2026-07-27 in CLAUDE.md's Known Open Questions) - no
    // "SecEdgar" config section exists in appsettings.json/appsettings.Development.json/the
    // shared secrets file/user-secrets, so this class-level default is the only place this
    // value is actually set; Configure<SecEdgarOptions> would override it if one existed.
    public string UserAgent { get; set; } = "DRPS/1.0 (contact: kent@octolux.ai)";

    public string CompanyTickersUrl { get; set; } = "https://www.sec.gov/files/company_tickers.json";

    public string SubmissionsBaseUrl { get; set; } = "https://data.sec.gov/submissions";

    // Individual filing documents (e.g. a Form 4's XML body), per the Insider Form 4
    // Adjuster Multiplier Decision (CLAUDE.md 2026-07-19) - EdgarForm4Feeder's own read path.
    public string ArchivesBaseUrl { get; set; } = "https://data.sec.gov/Archives/edgar/data";
}
