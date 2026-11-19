using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using WhatsAppRelay.Messaging;

namespace WhatsAppRelay.Controllers;

[ApiController]
[Route("[controller]")]
public class WhatsAppRelayController : ControllerBase
{
    private readonly IMessageQueue _messageQueue;
    private readonly ILogger<WhatsAppRelayController> _logger;

    private readonly string _metaAppSecret;
    private readonly string _metaPhoneNumberId;
    private readonly string _metaWabaId;
    private readonly string _metaVerifyToken;

    public WhatsAppRelayController(
        IConfiguration configuration,
        IMessageQueue messageQueue,
        ILogger<WhatsAppRelayController> logger)
    {
        _messageQueue = messageQueue;
        _logger = logger;

        _metaAppSecret =
            configuration.GetValue<string>("Meta:AppSecret")
            ?? string.Empty;

        _metaPhoneNumberId =
            configuration.GetValue<string>("Meta:PhoneNumberId")
            ?? string.Empty;

        _metaWabaId =
            configuration.GetValue<string>("Meta:WabaId")
            ?? string.Empty;

        _metaVerifyToken =
            configuration.GetValue<string>("Meta:VerifyToken")
            ?? string.Empty;

        if (string.IsNullOrEmpty(_metaAppSecret) ||
            string.IsNullOrEmpty(_metaPhoneNumberId) ||
            string.IsNullOrEmpty(_metaWabaId) ||
            string.IsNullOrEmpty(_metaVerifyToken))
        {
            throw new InvalidOperationException(
                "Required Meta webhook settings are missing: " +
                "AppSecret, PhoneNumberId, WabaId, or VerifyToken.");
        }
    }

    /// <summary>
    /// Called by Meta to verify the webhook during setup. When you click verify in the Meta developer portal, Meta will send a GET request to this endpoint with the mode, challenge, and verify token.
    /// </summary>
    /// <param name="mode">Always is subscribe</param>
    /// <param name="challenge">The challenge string that should be echoed back if verification is successful</param>
    /// <param name="verifyToken">The token sent by Meta to verify the webhook. You add this into the developer portal, and this method checks if it matches the one you configured</param>
    /// <returns></returns>
    [HttpGet("messagerelay")]
    public ActionResult VerifyWebhook(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.challenge")] string? challenge,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken)
    {
        if (mode != "subscribe" ||
            !string.Equals(
                verifyToken,
                _metaVerifyToken,
                StringComparison.Ordinal) ||
            string.IsNullOrEmpty(challenge))
        {
            _logger.LogInformation("Invalid Meta webhook verification request.");
            return new ContentResult
            {
                Content = "Invalid request",
                ContentType = "text/plain",
                StatusCode = StatusCodes.Status403Forbidden
            };
        }
        _logger.LogInformation("Valid Meta webhook verification request.");
        return new ContentResult
        {
            Content = challenge,
            ContentType = "text/plain",
            StatusCode = StatusCodes.Status200OK
        };
    }


    /// <summary>
    /// Handles incoming Meta webhook events for WhatsApp messages and delivery statuses. This is what is called by meta and they post JSON to this endpoint
    /// </summary>
    /// <param name="signature"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    [HttpPost("messagerelay")]
    [Consumes("application/json")]
    public async Task<ActionResult> MessageRelay(
        [FromHeader(Name = "X-Hub-Signature-256")] string? signature,
        CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();

        await Request.Body.CopyToAsync(
            body,
            cancellationToken);

        byte[] payload = body.ToArray();

        if (!IsMetaRequestValid(
                payload,
                signature,
                out var errorMessage))
        {
            _logger.LogWarning(
                "Rejected Meta webhook: {Reason}",
                errorMessage);

            return Unauthorized(errorMessage);
        }

        MetaEvent[] events;

        try
        {
            using var document = JsonDocument.Parse(payload);

            events = MetaWebhookParser.Parse(
                document.RootElement,
                _metaWabaId,
                _metaPhoneNumberId);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "Meta webhook contained invalid JSON.");

            return BadRequest("Invalid JSON.");
        }
        catch (RelayException exception)
        {
            _logger.LogWarning(
                exception,
                "Meta webhook validation failed: {ErrorCode}",
                exception.Code);

            return BadRequest(new
            {
                error = exception.Code,
                message = exception.Message
            });
        }

        IncomingMessage[] messages =
            events.OfType<IncomingMessage>().ToArray();

        DeliveryStatus[] statuses =
            events.OfType<DeliveryStatus>().ToArray();



        //for each incoming message from meta, add the message to a queue

        int acceptedCount = 0;
        int duplicateCount = 0;

        foreach (var message in messages)
        {
            bool accepted = await _messageQueue.EnqueueAsync(
                message,
                cancellationToken);

            if (accepted)
            {
                acceptedCount++;
            }
            else
            {
                duplicateCount++;

                _logger.LogInformation(
                    "Skipped duplicate incoming message {MessageId}.",
                    message.Id);
            }
        }

        //if message count is greate than 1 then log a special message
        if (acceptedCount > 0)
        {
            _logger.LogInformation(
           "Pushed {acceptedCount} messages into the queue.",
           acceptedCount);
        }

        //if duplicate count is greater than 0 then log a special message
        if (duplicateCount > 0)
        {
            _logger.LogInformation(
                "Skipped {DuplicateCount} duplicate messages.",
                duplicateCount);
        }


        // we not doing anything with status messages from meta for now, we just logging them
        foreach (var status in statuses)
        {
            _logger.LogInformation(
       "Meta delivery | Sender: {Sender} | Kind: {Kind} | Status: {Status} | MessageId: {MessageId}",
       status.Sender,
       status.Kind,
       status.Status,
       status.MessageId);
        }



        return new ContentResult
        {
            Content = string.Empty,
            ContentType = "text/plain",
            StatusCode = StatusCodes.Status200OK
        };
    }

    //check if message from meta conforms to their rules / guidance, i.e. is valid
    private bool IsMetaRequestValid(
        byte[] payload,
        string? signature,
        out string errorMessage)
    {
        errorMessage = string.Empty;

        if (signature is null ||
            signature.Length != 71 ||
            !signature.StartsWith(
                "sha256=",
                StringComparison.Ordinal))
        {
            errorMessage = "Missing or invalid webhook signature.";
            return false;
        }

        byte[] suppliedHash;

        try
        {
            suppliedHash = Convert.FromHexString(signature[7..]);
        }
        catch (FormatException)
        {
            errorMessage = "Invalid webhook signature.";
            return false;
        }

        using var hmac = new HMACSHA256(
            Encoding.UTF8.GetBytes(_metaAppSecret));

        byte[] expectedHash = hmac.ComputeHash(payload);

        if (!CryptographicOperations.FixedTimeEquals(
                expectedHash,
                suppliedHash))
        {
            errorMessage = "Invalid webhook signature.";
            return false;
        }

        return true;
    }
}