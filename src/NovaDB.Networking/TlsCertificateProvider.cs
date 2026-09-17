using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovaDB.Configuration;

namespace NovaDB.Networking;

/// <summary>
/// Loads and caches the TLS server certificate configured for RESP connections.
/// </summary>
public sealed class TlsCertificateProvider
{
    private readonly NovaDbOptions _options;
    private readonly ILogger<TlsCertificateProvider> _logger;
    private readonly object _gate = new();
    private X509Certificate2? _certificate;

    /// <summary>
    /// Initializes a new instance of the <see cref="TlsCertificateProvider"/> class.
    /// </summary>
    public TlsCertificateProvider(IOptions<NovaDbOptions> options, ILogger<TlsCertificateProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets a value indicating whether TLS is enabled and a certificate is configured.
    /// </summary>
    public bool IsEnabled => _options.TlsEnabled;

    /// <summary>
    /// Returns the server certificate, loading it on first use.
    /// </summary>
    public X509Certificate2 GetCertificate()
    {
        if (!_options.TlsEnabled)
        {
            throw new InvalidOperationException("TLS is not enabled.");
        }

        if (string.IsNullOrWhiteSpace(_options.TlsCertificatePath))
        {
            throw new InvalidOperationException("NovaDB:TlsCertificatePath must be set when TlsEnabled is true.");
        }

        lock (_gate)
        {
            if (_certificate is not null)
            {
                return _certificate;
            }

            _certificate = string.IsNullOrEmpty(_options.TlsCertificatePassword)
                ? X509CertificateLoader.LoadPkcs12FromFile(_options.TlsCertificatePath, null)
                : X509CertificateLoader.LoadPkcs12FromFile(_options.TlsCertificatePath, _options.TlsCertificatePassword);

            _logger.LogInformation(
                "Loaded TLS certificate {Subject} from {Path}",
                _certificate.Subject,
                _options.TlsCertificatePath);

            return _certificate;
        }
    }
}
