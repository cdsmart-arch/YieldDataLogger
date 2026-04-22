namespace YieldDataLogger.Core;

/// <summary>
/// Translates vendor-native symbols (CNBC short codes, Investing.com pids) into the in-house
/// canonical ticker used for storage. Ported from the old ProcessPoller.TranslateSymbolNames.
/// </summary>
public static class SymbolTranslator
{
    private static readonly Dictionary<string, string> CnbcEquivalents = new(StringComparer.Ordinal)
    {
        ["US3M"] = "US03M",
        ["US2Y"] = "US02Y",
        ["US5Y"] = "US05Y",
        ["US7Y"] = "US07Y",
        ["DE2Y"] = "DE02Y",
        ["DE5Y"] = "DE05Y",
        ["AU2Y"] = "AU02Y",
        ["AU5Y"] = "AU05Y",
        ["JP2Y"] = "JP02Y",
        ["JP5Y"] = "JP05Y",
        ["IT2Y"] = "IT02Y",
        ["IT5Y"] = "IT05Y",
        ["UK2Y"] = "GB02Y",
        ["UK5Y"] = "GB05Y",
        ["UK10Y"] = "GB10Y",
        ["UK30Y"] = "GB30Y",
        ["CA2Y"] = "CA02Y",
        ["CA5Y"] = "CA05Y",
        ["FR2Y"] = "FR02Y",
        ["FR5Y"] = "FR05Y",
        ["XAU="] = "GOLD",
        ["XAG="] = "SILVER",
        ["EUR="] = "EURUSD",
        ["GBP="] = "GBPUSD",
        ["NZD="] = "NZDUSD",
        ["AUD="] = "AUDUSD",
        ["JPY="] = "USDJPY",
        ["CAD="] = "USDCAD",
        ["CHF="] = "USDCHF",
        ["CNY="] = "USDCNY",
    };

    /// <summary>Map a CNBC quick-quote symbol to the canonical in-house ticker.</summary>
    public static string FromCnbc(string cnbcSymbol)
    {
        if (string.IsNullOrEmpty(cnbcSymbol)) return cnbcSymbol;

        // Explicit dictionary handles short-codes (EUR= → EURUSD) and renames (UK10Y → GB10Y).
        if (CnbcEquivalents.TryGetValue(cnbcSymbol, out var mapped))
            return mapped.Replace(".", string.Empty);

        // Cross-pair FX symbols (e.g. AUDJPY=, GBPJPY=) are stored by CNBC with a trailing =
        // but our canonical symbol has none — strip it here so dispatching works correctly.
        var s = cnbcSymbol.TrimEnd('=');
        return s.Replace(".", string.Empty);
    }
}
