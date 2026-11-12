using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace WhatsAppRelay.Controllers;

public abstract record MetaEvent(
    string Key,
    string Id,
    string Sender,
    long Timestamp)
{
    public abstract string Kind { get; }
}

public sealed record IncomingMessage(
    string Key,
    string Id,
    string Sender,
    long Timestamp,
    string Text,
    MediaAttachment? Media = null,
    bool Unsupported = false)
    : MetaEvent(Key, Id, Sender, Timestamp)
{
    public override string Kind => "message";
}

public sealed record DeliveryStatus(
    string Key,
    string Id,
    string Sender,
    long Timestamp,
    string MessageId,
    string Status)
    : MetaEvent(Key, Id, Sender, Timestamp)
{
    public override string Kind => "status";
}

public sealed record MediaAttachment(
    string Kind,
    string Id,
    string Mime,
    string? Name = null);

public sealed class RelayException : Exception
{
    public string Code { get; }

    public RelayException(string code, string message) : base(message)
    {
        Code = code;
    }
}

public static class MetaWebhookParser
{
    private static readonly JsonSerializerOptions KeyJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static MetaEvent[] Parse(
        JsonElement root,
        string accountId,
        string phoneId)
    {
        if (string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(phoneId))
        {
            throw new ArgumentException(
                "The expected account ID and phone ID must be configured.");
        }

        if (Text(root, "object", 64) != "whatsapp_business_account")
        {
            throw new RelayException(
                "invalid_object", "Unexpected webhook object.");
        }

        var output = new List<MetaEvent>();
        var latestTimestamp =
            DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds();

        foreach (var entry in Items(root, "entry"))
        {
            if (Text(entry, "id", 256) != accountId)
            {
                throw new RelayException(
                    "wrong_account", "Unexpected business account.");
            }

            foreach (var change in Items(entry, "changes"))
            {
                if (Text(change, "field", 256) != "messages")
                    continue;

                var payload = Object(Field(change, "value"));
                var metadata = Field(payload, "metadata");

                if (Text(metadata, "phone_number_id", 256) != phoneId)
                {
                    throw new RelayException(
                        "wrong_phone", "Unexpected business number.");
                }

                foreach (var message in Items(payload, "messages", optional: true))
                {
                    var sender = Text(message, "from", 256);
                    var timestamp = Timestamp(
                        message, latestTimestamp, "invalid_sender_time");

                    if (sender.Length == 0)
                    {
                        throw new RelayException(
                            "invalid_sender_time", "Invalid message metadata.");
                    }

                    var id = Text(message, "id", 512);

                    if (id.Length == 0)
                    {
                        throw new RelayException(
                            "missing_id", "The message has no ID.");
                    }

                    var type = Text(message, "type", 64);
                    var text = "";
                    MediaAttachment? media = null;
                    var unsupported = false;

                    switch (type)
                    {
                        case "text":
                            text = Text(Field(message, "text"), "body", 16_384);
                            break;

                        case "image":
                        case "document":
                        case "audio":
                        case "video":
                        {
                            var attachment = Object(Field(message, type));
                            var mediaId = Text(attachment, "id", 256);

                            if (mediaId.Length == 0)
                            {
                                throw new RelayException(
                                    "missing_media_id",
                                    "The attachment has no media ID.");
                            }

                            var name = attachment.TryGetProperty("filename", out _)
                                ? Text(attachment, "filename", 512)
                                : null;

                            media = new MediaAttachment(
                                type,
                                mediaId,
                                Text(attachment, "mime_type", 256),
                                name);

                            if (attachment.TryGetProperty("caption", out _))
                                text = Text(attachment, "caption", 4096);

                            break;
                        }

                        default:
                            unsupported = true;
                            break;
                    }
//we add a unique key to each message, this key basically only is unqiue for a sender, its not unique for a message.
// its more for if the services supports multiple Meta WhatsApp for Business telephone numbers
// but we dont actually do anything with it yet. Its a todo item.

//what uniquely identifies a messages, is the Id field in the message, which is added below.
                    output.Add(new IncomingMessage(
                        CreateKey(accountId, phoneId, sender),
                        id,
                        sender,
                        timestamp,
                        text,
                        media,
                        unsupported));
                }

                foreach (var status in Items(payload, "statuses", optional: true))
                {
                    var sender = Text(status, "recipient_id", 256);
                    var messageId = Text(status, "id", 512);
                    var state = Text(status, "status", 64);
                    var timestamp = Timestamp(
                        status, latestTimestamp, "invalid_status");

                    if (sender.Length == 0 ||
                        messageId.Length == 0 ||
                        state.Length == 0)
                    {
                        throw new RelayException(
                            "invalid_status", "Invalid delivery status.");
                    }

                    var id = messageId + ":" + state + ":" +
                        timestamp.ToString(CultureInfo.InvariantCulture);
//we add a unique key to each message, this key basically only is unqiue for a sender, its not unique for a message.
// its more for if the services supports multiple Meta WhatsApp for Business telephone numbers
// but we dont actually do anything with it yet. Its a todo item.

//what uniquely identifies a status, is the Id field in the status, which is added below.
                    output.Add(new DeliveryStatus(
                        CreateKey(accountId, phoneId, sender),
                        id,
                        sender,
                        timestamp,
                        messageId,
                        state));
                }

                if (payload.TryGetProperty("errors", out _) &&
                    !payload.TryGetProperty("messages", out _))
                {
                    throw new RelayException(
                        "meta_webhook_error",
                        "Meta reported a messaging error.");
                }
            }
        }

        return output.ToArray();
    }

    private static string CreateKey(
        string accountId,
        string phoneId,
        string sender)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new[] { accountId, phoneId, sender },
            KeyJsonOptions);

        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static long Timestamp(
        JsonElement value,
        long latestTimestamp,
        string errorCode)
    {
        var text = Text(value, "timestamp", 16);

        if (!long.TryParse(
                text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var seconds) ||
            seconds <= 0 ||
            seconds > latestTimestamp / 1000)
        {
            throw new RelayException(errorCode, "Invalid event timestamp.");
        }

        return seconds * 1000;
    }

    private static JsonElement Object(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new RelayException(
                "invalid_payload", "Expected a JSON object.");
        }

        return value;
    }

    private static JsonElement Field(JsonElement value, string name)
    {
        if (!Object(value).TryGetProperty(name, out var field))
        {
            throw new RelayException(
                "invalid_payload", $"Missing field '{name}'.");
        }

        return field;
    }

    private static string Text(JsonElement value, string name, int maximumLength)
    {
        var field = Field(value, name);

        if (field.ValueKind != JsonValueKind.String)
        {
            throw new RelayException(
                "invalid_payload", $"Field '{name}' must be a string.");
        }

        var text = field.GetString();

        if (text == null || text.Length > maximumLength)
        {
            throw new RelayException(
                "invalid_payload", $"Invalid length for field '{name}'.");
        }

        return text;
    }

    private static IEnumerable<JsonElement> Items(
        JsonElement value,
        string name,
        bool optional = false)
    {
        if (!Object(value).TryGetProperty(name, out var array))
        {
            if (optional)
                return Array.Empty<JsonElement>();

            throw new RelayException(
                "invalid_payload", $"Missing array '{name}'.");
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new RelayException(
                "invalid_payload", $"Field '{name}' must be an array.");
        }

        return array.EnumerateArray();
    }
}