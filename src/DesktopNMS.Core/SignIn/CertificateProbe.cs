using DesktopNMS.Core.Security;

namespace DesktopNMS.Core.SignIn;

/// <summary>What a look at the server's website found before signing in on it.</summary>
public sealed record ProbeResult(bool Reached, CertificateDetails? UntrustedCertificate = null, string? ErrorMessage = null);

/// <summary>
/// Opens the server's website once before "Sign in with LibreNMS" shows it.
/// A web view can't ask about a certificate it doesn't trust, so a
/// self-signed one is found here first and offered through the same
/// "Trust this certificate?" prompt as the API's (#189); the web view then
/// accepts exactly the certificates trusted that way. It also catches an
/// address that doesn't answer, with a clearer message than a blank web view.
/// </summary>
public interface ICertificateProbe
{
    Task<ProbeResult> ProbeAsync(Uri webRoot, bool allowUntrustedCertificate, IReadOnlyCollection<string> trustedCertificates, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ICertificateProbe"/>
public sealed class CertificateProbe : ICertificateProbe
{
    public async Task<ProbeResult> ProbeAsync(Uri webRoot, bool allowUntrustedCertificate, IReadOnlyCollection<string> trustedCertificates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(webRoot);

        CertificateDetails? rejected = null;
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        handler.SslOptions.RemoteCertificateValidationCallback = CertificateTrust.CreateCallback(
            webRoot.Host,
            allowUntrustedCertificate,
            trustedCertificates,
            details => rejected = details);

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            // Any answer will do - a login redirect, a page, even an error
            // page: the server is there and its certificate has been seen.
            using var response = await http.GetAsync(webRoot, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return new ProbeResult(true);
        }
        catch (HttpRequestException) when (rejected is not null)
        {
            return new ProbeResult(false, rejected);
        }
        catch (HttpRequestException ex) when (!allowUntrustedCertificate && ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            return new ProbeResult(false, ErrorMessage: WebSignInMessages.CertificateNotTrusted);
        }
        catch (HttpRequestException)
        {
            return new ProbeResult(false, ErrorMessage: WebSignInMessages.Unreachable(webRoot.Host));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeResult(false, ErrorMessage: webRoot.Host + " didn't answer in time.");
        }
    }
}
