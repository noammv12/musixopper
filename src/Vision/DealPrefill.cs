namespace Palon.Vision;

/// <summary>
/// What the New deal sheet is pre-filled with from a receipt. Region and
/// source are deliberately absent — those are the user's call. The sheet
/// shows the banner + warnings and never saves on its own.
/// </summary>
sealed record DealPrefill(
    string? ClientName,
    decimal? AmountUsd,
    int Year,
    int Month,
    DateTime? Date,
    string? Note,
    bool AmountVerified,
    IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// Receipt → prefill. The month follows the receipt date when it is a
    /// real, recent past date (this month or up to two back); otherwise the
    /// current month and a warning. A non-USD amount is still filled (the
    /// user converts it) — the warning from extraction says so.
    /// </summary>
    public static DealPrefill FromReceipt(ReceiptData r, DateTime nowLocal)
    {
        var warnings = new List<string>(r.Warnings);
        var year = nowLocal.Year;
        var month = nowLocal.Month;
        DateTime? date = null;
        if (r.Date is { } d)
        {
            var monthsBack = (nowLocal.Year - d.Year) * 12 + nowLocal.Month - d.Month;
            if (d.Date <= nowLocal.Date && monthsBack is >= 0 and <= 2)
            {
                year = d.Year;
                month = d.Month;
                date = d.Date;
            }
            else
            {
                warnings.Add($"תאריך הקבלה ({d:dd/MM/yyyy}) לא בחודשים האחרונים — העסקה נרשמת בחודש הנוכחי");
            }
        }
        var noteParts = new List<string>();
        if (r.Method is { } m) noteParts.Add(m);
        if (r.Reference is { } rf) noteParts.Add($"אסמכתא {rf}");
        if (r.Currency is { } c && c != "USD" && r.AmountText is { } at) noteParts.Add($"בקבלה: {at} {c}");
        var note = noteParts.Count > 0 ? "מקבלה: " + string.Join(" · ", noteParts) : null;
        return new DealPrefill(r.PayerName, r.Amount, year, month, date, note, r.AmountVerified, warnings);
    }
}
