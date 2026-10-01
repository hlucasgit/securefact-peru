using System.Globalization;

namespace SecureFact.CpeEngine.Printing;

/// <summary>"SON: CIENTO DIECIOCHO CON 00/100 SOLES": the amount in words that Peruvian invoices customarily print (legend 1000 of catalogue 52).</summary>
internal static class AmountInWords
{
    private static readonly string[] Units =
    [
        "CERO", "UNO", "DOS", "TRES", "CUATRO", "CINCO", "SEIS", "SIETE", "OCHO", "NUEVE", "DIEZ", "ONCE", "DOCE", "TRECE", "CATORCE", "QUINCE",
        "DIECISÉIS", "DIECISIETE", "DIECIOCHO", "DIECINUEVE", "VEINTE", "VEINTIUNO", "VEINTIDÓS", "VEINTITRÉS", "VEINTICUATRO", "VEINTICINCO",
        "VEINTISÉIS", "VEINTISIETE", "VEINTIOCHO", "VEINTINUEVE",
    ];

    private static readonly string[] Tens = ["", "", "", "TREINTA", "CUARENTA", "CINCUENTA", "SESENTA", "SETENTA", "OCHENTA", "NOVENTA"];

    private static readonly string[] Hundreds =
        ["", "CIENTO", "DOSCIENTOS", "TRESCIENTOS", "CUATROCIENTOS", "QUINIENTOS", "SEISCIENTOS", "SETECIENTOS", "OCHOCIENTOS", "NOVECIENTOS"];

    public const decimal Max = 999_999_999_999.99m;

    public static string Describe(decimal amount, string currency)
    {
        if (amount < 0 || amount > Max)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        var rounded = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        var whole = (long)Math.Truncate(rounded);
        var cents = (int)((rounded - whole) * 100m);
        return $"SON: {Words(whole)} CON {cents.ToString("00", CultureInfo.InvariantCulture)}/100 {CurrencyName(currency)}";
    }

    internal static string CurrencyName(string currency) => currency switch
    {
        "PEN" => "SOLES",
        "USD" => "DÓLARES AMERICANOS",
        "EUR" => "EUROS",
        _ => currency,
    };

    internal static string Words(long number)
    {
        if (number == 0)
        {
            return Units[0];
        }

        var parts = new List<string>();
        var millions = number / 1_000_000;
        var rest = number % 1_000_000;
        if (millions > 0)
        {
            // 1 000 000 = "UN MILLÓN"; 2 000 000 = "DOS MILLONES"; the thousands of millions chain through "MIL".
            parts.Add(millions == 1 ? "UN MILLÓN" : $"{Below1000000(millions, apocope: true)} MILLONES");
        }

        if (rest > 0)
        {
            parts.Add(Below1000000(rest, apocope: false));
        }

        return string.Join(' ', parts);
    }

    /// <summary>0 &lt; n &lt; 1 000 000. With <paramref name="apocope"/> a trailing "uno" becomes "un" (it precedes a noun: "veintiún mil", "un millón").</summary>
    private static string Below1000000(long n, bool apocope)
    {
        var thousands = n / 1000;
        var rest = n % 1000;
        var parts = new List<string>();
        if (thousands > 0)
        {
            parts.Add(thousands == 1 ? "MIL" : $"{Below1000(thousands, apocope: true)} MIL");
        }

        if (rest > 0)
        {
            parts.Add(Below1000(rest, apocope));
        }

        return string.Join(' ', parts);
    }

    private static string Below1000(long n, bool apocope)
    {
        if (n == 100)
        {
            return "CIEN";
        }

        var parts = new List<string>();
        var hundreds = (int)(n / 100);
        var rest = (int)(n % 100);
        if (hundreds > 0)
        {
            parts.Add(Hundreds[hundreds]);
        }

        if (rest > 0)
        {
            string text;
            if (rest < 30)
            {
                text = Units[rest];
            }
            else
            {
                var tens = Tens[rest / 10];
                text = rest % 10 == 0 ? tens : $"{tens} Y {Units[rest % 10]}";
            }

            parts.Add(apocope ? Apocope(text) : text);
        }

        return string.Join(' ', parts);
    }

    private static string Apocope(string text) =>
        text.EndsWith("VEINTIUNO", StringComparison.Ordinal) ? text[..^"VEINTIUNO".Length] + "VEINTIÚN"
        : text.EndsWith("UNO", StringComparison.Ordinal) ? text[..^1]
        : text;
}
