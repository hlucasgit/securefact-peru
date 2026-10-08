namespace SecureFact.Gre.Contracts;

/// <summary>
/// Where the REST channel to SUNAT's GRE platform lives (S28, S29). Both addresses are explicit: production is never chosen implicitly.
/// </summary>
/// <param name="SecurityBase">Base of the token service; the path <c>/v1/clientessol/{client_id}/oauth2/token/</c> is added to it.</param>
/// <param name="ApiBase">Base of the GRE service; the path <c>/v1/contribuyente/gem/comprobantes/…</c> is added to it.</param>
public sealed record GreChannelOptions(Uri SecurityBase, Uri ApiBase, TimeSpan Timeout)
{
    /// <summary>The scope that the token request asks for (S28).</summary>
    public const string TokenScope = "https://api-cpe.sunat.gob.pe";

    public static GreChannelOptions Production { get; } = new(new Uri("https://api-seguridad.sunat.gob.pe"), new Uri("https://api-cpe.sunat.gob.pe"), TimeSpan.FromSeconds(60));
}
