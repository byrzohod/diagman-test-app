namespace DiagManTestApp.Services;

/// <summary>
/// Configuration service with an intentional startup crash due to missing env vars.
/// BUG: The service requires certain environment variables but the validation
/// error message is misleading, and the code path that crashes is not obvious.
///
/// The actual bug is that PAYMENT_API_KEY is validated but PAYMENT_API_SECRET
/// is accessed without validation, causing NullReferenceException.
///
/// This will cause CrashLoopBackOff in Kubernetes.
/// DiagMan should identify:
/// 1. CrashLoopBackOff status from container
/// 2. NullReferenceException in logs
/// 3. The bug in this file: PAYMENT_API_SECRET not validated before use
/// </summary>
public class ConfigurationService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConfigurationService> _logger;

    // Required environment variables
    private const string PaymentApiKeyVar = "PAYMENT_API_KEY";
    private const string PaymentApiSecretVar = "PAYMENT_API_SECRET";
    private const string PaymentApiUrlVar = "PAYMENT_API_URL";
    private const string EncryptionKeyVar = "ENCRYPTION_KEY";

    public string PaymentApiKey { get; private set; } = string.Empty;
    public string PaymentApiSecret { get; private set; } = string.Empty;
    public string PaymentApiUrl { get; private set; } = string.Empty;
    public string EncryptionKey { get; private set; } = string.Empty;

    public ConfigurationService(IConfiguration configuration, ILogger<ConfigurationService> logger)
    {
        _configuration = configuration;
        _logger = logger;

        ValidateAndLoadConfiguration();
    }

    private void ValidateAndLoadConfiguration()
    {
        _logger.LogInformation("Validating application configuration...");

        var errors = new List<string>();

        // Validate PAYMENT_API_KEY
        var apiKey = _configuration[PaymentApiKeyVar];
        if (string.IsNullOrEmpty(apiKey))
        {
            errors.Add($"Missing required environment variable: {PaymentApiKeyVar}");
        }
        else
        {
            PaymentApiKey = apiKey;
            _logger.LogInformation("Loaded {Var} (length: {Length})", PaymentApiKeyVar, apiKey.Length);
        }

        // BUG: We validate PAYMENT_API_KEY above, but we don't validate PAYMENT_API_SECRET
        // We just load it directly without null check
        // This will cause issues when we try to use it later

        // Validate PAYMENT_API_URL
        var apiUrl = _configuration[PaymentApiUrlVar];
        if (string.IsNullOrEmpty(apiUrl))
        {
            // Default to a URL, but log a warning
            PaymentApiUrl = "https://api.payment-provider.com/v1";
            _logger.LogWarning("Using default value for {Var}", PaymentApiUrlVar);
        }
        else
        {
            PaymentApiUrl = apiUrl;
        }

        // Validate ENCRYPTION_KEY with proper check
        var encryptionKey = _configuration[EncryptionKeyVar];
        if (string.IsNullOrEmpty(encryptionKey))
        {
            errors.Add($"Missing required environment variable: {EncryptionKeyVar}");
        }
        else if (encryptionKey.Length < 32)
        {
            errors.Add($"{EncryptionKeyVar} must be at least 32 characters for AES-256");
        }
        else
        {
            EncryptionKey = encryptionKey;
        }

        // Report validation errors
        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                _logger.LogError("Configuration error: {Error}", error);
            }
            throw new InvalidOperationException(
                $"Configuration validation failed with {errors.Count} error(s). " +
                $"Check the logs for details. First error: {errors[0]}");
        }

        // BUG: This is where it crashes - we try to use PAYMENT_API_SECRET
        // without having validated it above. If it's not set, this will throw.
        InitializePaymentClient();

        _logger.LogInformation("Configuration validation completed successfully");
    }

    private void InitializePaymentClient()
    {
        _logger.LogInformation("Initializing payment client...");

        // Load the secret (BUG: not validated, might be null)
        var apiSecret = _configuration[PaymentApiSecretVar];

        // BUG: NullReferenceException here if PAYMENT_API_SECRET is not set!
        // The error message in the exception won't mention the missing env var,
        // making it hard to debug without looking at the source code.
        var secretHash = ComputeSecretHash(apiSecret!);

        PaymentApiSecret = apiSecret!;

        _logger.LogInformation(
            "Payment client initialized with key prefix: {KeyPrefix}, secret hash: {Hash}",
            PaymentApiKey[..Math.Min(8, PaymentApiKey.Length)] + "...",
            secretHash[..16] + "...");
    }

    private static string ComputeSecretHash(string secret)
    {
        // BUG: This will throw NullReferenceException if secret is null
        // because we call .Length on a null string
        if (secret.Length < 16)
        {
            throw new ArgumentException("API secret must be at least 16 characters");
        }

        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hash);
    }

    public bool IsConfigured => !string.IsNullOrEmpty(PaymentApiKey)
                                && !string.IsNullOrEmpty(PaymentApiSecret)
                                && !string.IsNullOrEmpty(EncryptionKey);
}
