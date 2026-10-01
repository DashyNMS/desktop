using System.Globalization;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DesktopNMS.Core.Security;

/// <summary>
/// A server certificate the normal checks rejected, with what someone needs
/// to decide whether to trust it: who it names, who issued it, when it is
/// valid and its SHA-256 fingerprint to compare against the server's own.
/// </summary>
public sealed record CertificateDetails(
    string Host,
    string Fingerprint,
    string Subject,
    string Issuer,
    DateTime NotBefore,
    DateTime NotAfter,
    SslPolicyErrors Errors,
    bool ReplacesTrustedCertificate)
{
    /// <summary>The certificate is issued for a different name than the server was asked for.</summary>
    public bool NameMismatch => Errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch);

    /// <summary>The certificate doesn't chain to an authority this machine trusts (self-signed, internal CA, expired).</summary>
    public bool ChainProblem => Errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors);
}

/// <summary>
/// What "Allow untrusted certificate" means (#189). It used to accept every
/// certificate, including one for a different host, so anyone on the network
/// path could intercept the connection and read the token. Now:
/// <list type="bullet">
/// <item>A certificate that passes the normal checks is always accepted.</item>
/// <item>With the option off, nothing else is.</item>
/// <item>With it on, only a certificate whose SHA-256 fingerprint the user has
/// explicitly accepted is - so the exact certificate they looked at, and no
/// other, even one for the same name. Every rejection is reported with its
/// details so the app can ask whether to trust it, and says whether it
/// replaces one already trusted (the certificate changed).</item>
/// </list>
/// Pinning the exact certificate is stronger than checking the host name: an
/// attacker would need that certificate's private key, not just any
/// certificate naming the host.
/// </summary>
public static class CertificateTrust
{
    /// <summary>"AB:CD:..." - the certificate's SHA-256 fingerprint as browsers show it.</summary>
    public static string Fingerprint(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var hash = SHA256.HashData(certificate.GetRawCertData());
        return string.Join(':', hash.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
    }

    /// <summary>Compares fingerprints ignoring case and separators, so "ab cd" matches "AB:CD".</summary>
    public static bool SameFingerprint(string? a, string? b)
        => a is not null && b is not null && string.Equals(Normalise(a), Normalise(b), StringComparison.Ordinal);

    /// <summary>The decision itself, separate from any certificate object so it can be tested directly.</summary>
    public static bool Accepts(SslPolicyErrors errors, string? fingerprint, bool allowUntrusted, IReadOnlyCollection<string>? trustedFingerprints)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        if (!allowUntrusted || fingerprint is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            return false;
        }

        return trustedFingerprints?.Any(trusted => SameFingerprint(trusted, fingerprint)) == true;
    }

    /// <summary>
    /// A validation callback for an HTTP handler. <paramref name="onRejected"/>
    /// is told about every certificate turned down while
    /// <paramref name="allowUntrusted"/> is on, so the caller can ask the user.
    /// </summary>
    public static RemoteCertificateValidationCallback CreateCallback(
        string host,
        bool allowUntrusted,
        IReadOnlyCollection<string>? trustedFingerprints,
        Action<CertificateDetails>? onRejected)
    {
        var trusted = trustedFingerprints?.ToArray() ?? Array.Empty<string>();

        return (_, certificate, _, errors) =>
        {
            var fingerprint = certificate is null ? null : Fingerprint(certificate);
            if (Accepts(errors, fingerprint, allowUntrusted, trusted))
            {
                return true;
            }

            if (allowUntrusted && certificate is not null && onRejected is not null)
            {
                onRejected(Describe(host, certificate, errors, replacesTrusted: trusted.Length > 0));
            }

            return false;
        };
    }

    public static CertificateDetails Describe(string host, X509Certificate certificate, SslPolicyErrors errors, bool replacesTrusted)
    {
        // The handler hands over an X509Certificate2 in practice; only copy
        // (and so own and dispose) one when it doesn't.
        using var copy = certificate as X509Certificate2 is null ? new X509Certificate2(certificate) : null;
        var cert = copy ?? (X509Certificate2)certificate;
        return new CertificateDetails(
            host,
            Fingerprint(cert),
            cert.Subject,
            cert.Issuer,
            cert.NotBefore,
            cert.NotAfter,
            errors,
            replacesTrusted);
    }

    /// <summary>One line for an error or status bar: why the connection was refused, for <paramref name="service"/> ("LibreNMS", "Graylog", "Unimus").</summary>
    public static string DescribeRejection(CertificateDetails details, string service) => details.ReplacesTrustedCertificate
        ? $"{service}'s certificate has changed since you trusted it. Check the new one before trusting it - a changed certificate can mean the connection is being intercepted."
        : $"{service}'s certificate isn't trusted yet. Check its fingerprint before trusting it.";

    /// <summary>
    /// The title and body for asking whether to trust <paramref name="details"/>:
    /// what's wrong with it, then everything needed to check it against the
    /// server's own certificate. Shared so desktop and mobile ask the same way.
    /// </summary>
    public static (string Title, string Message) DescribeForPrompt(CertificateDetails details, string service)
    {
        ArgumentNullException.ThrowIfNull(details);

        var lines = new List<string>();
        if (details.ReplacesTrustedCertificate)
        {
            lines.Add($"The certificate {details.Host} presents isn't the one you trusted before. That's expected after it's renewed or replaced, but it can also mean someone is intercepting the connection. Only trust it if you know it changed.");
        }
        else
        {
            lines.Add($"{details.Host} presented a certificate this device can't verify. Check its fingerprint against the server's own before trusting it. {service} will then be trusted with this certificate only.");
        }

        lines.Add(string.Empty);
        if (details.NameMismatch)
        {
            lines.Add($"• It isn't issued for {details.Host}.");
        }

        if (details.ChainProblem)
        {
            lines.Add("• It isn't from an authority this device trusts (self-signed, or an internal CA).");
        }

        if (details.NotAfter < DateTime.Now)
        {
            lines.Add("• It has expired.");
        }

        lines.Add(string.Empty);
        lines.Add("Issued to:  " + details.Subject);
        lines.Add("Issued by:  " + details.Issuer);
        lines.Add(string.Create(CultureInfo.CurrentCulture, $"Valid:  {details.NotBefore:d} to {details.NotAfter:d}"));
        lines.Add("SHA-256:  " + details.Fingerprint);

        var title = details.ReplacesTrustedCertificate
            ? $"{service}'s certificate has changed"
            : $"Trust {service}'s certificate?";

        return (title, string.Join(Environment.NewLine, lines));
    }

    private static string Normalise(string fingerprint)
        => new(fingerprint.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
}
