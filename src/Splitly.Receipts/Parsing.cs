using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace Splitly.Receipts;

public sealed record ParsedItem(string Description, decimal Price, double Confidence);

public sealed record ParsedReceipt(
    string Currency, string? Merchant, IReadOnlyList<ParsedItem> Items, decimal Tax, decimal Tip, decimal? Total);

public interface IReceiptParser
{
    Task<ParsedReceipt> ParseAsync(Stream image, string contentType, string? currencyHint, CancellationToken ct);
}

/// <summary>The model declined or returned something unusable; retrying the same image won't help.</summary>
public sealed class ReceiptParseException(string message) : Exception(message);

/// <summary>Deterministic parser for development without an API key, and for tests. Selected explicitly with Receipts:Parser=fake.</summary>
public sealed class FakeReceiptParser(ParsedReceipt? result = null) : IReceiptParser
{
    public static readonly ParsedReceipt Sample = new("USD", "Corner Bistro",
        [new("Burger", 12.50m, 0.97), new("Garden salad", 9.00m, 0.95), new("Smudged line", 4.25m, 0.40)],
        Tax: 2.30m, Tip: 5.00m, Total: 33.05m);

    public Task<ParsedReceipt> ParseAsync(Stream image, string contentType, string? currencyHint, CancellationToken ct) =>
        Task.FromResult(result ?? Sample);
}

/// <summary>
/// Extracts line items from a receipt photo with Claude vision. The model reports its own per-item confidence;
/// the processor flags low-confidence items (and totals that don't add up) for human review.
/// </summary>
public sealed class ClaudeReceiptParser(AnthropicClient client, string model = "claude-sonnet-5-5") : IReceiptParser
{
    private const string Prompt = """
        Extract the purchased line items from this receipt photo.

        Reply with ONLY a JSON object, no prose and no code fences, in exactly this shape:
        {"currency":"USD","merchant":"Name or null","items":[{"description":"Item name","price":12.50,"confidence":0.95}],"tax":0.00,"tip":0.00,"total":0.00}

        Rules:
        - One entry per printed line. "price" is the line's total price (already multiplied by quantity) as a plain number in major currency units.
        - Discounts or coupons are separate lines with a negative price.
        - Do not include subtotal, tax, tip, service charge, change or payment lines in "items". Put tax in "tax" and any tip/gratuity/service charge in "tip" (0 if absent).
        - "total" is the grand total printed on the receipt, or null if you cannot read it.
        - "confidence" (0 to 1) is how sure you are that the description AND price were read correctly. Use below 0.7 for blurry, cut-off or ambiguous lines. Do not guess silently.
        - "currency" is the ISO 4217 code (infer from symbols and locale; null if unclear).
        """;

    public async Task<ParsedReceipt> ParseAsync(Stream image, string contentType, string? currencyHint, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await image.CopyToAsync(buffer, ct);

        var prompt = currencyHint is null ? Prompt : $"{Prompt}\nIf the currency is unclear, assume {currencyHint}.";
        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = model,
            MaxTokens = 16000,
            // Claude Sonnet 5.5 always thinks (no "disabled", no temperature); low effort is plenty for transcription.
            OutputConfig = new OutputConfig { Effort = Effort.Low },
            Messages =
            [
                new UserMessageParam
                {
                    Content = new List<ContentBlockParam>
                    {
                        new ImageBlockParam
                        {
                            Source = new Base64ImageSource
                            {
                                Data = Convert.ToBase64String(buffer.ToArray()),
                                MediaType = ToMediaType(contentType),
                            },
                        },
                        new TextBlockParam { Text = prompt },
                    },
                },
            ],
        }, ct);

        if (response.StopReason == "refusal")
            throw new ReceiptParseException("The model declined to read this image.");
        if (response.StopReason == "max_tokens")
            throw new ReceiptParseException("The receipt was too long to read in one pass.");

        var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        return ParseJson(text);
    }

    private static MediaType ToMediaType(string contentType) => contentType switch
    {
        "image/jpeg" => MediaType.ImageJpeg,
        "image/png" => MediaType.ImagePng,
        "image/webp" => MediaType.ImageWebP,
        _ => throw new ReceiptParseException($"Unsupported image type {contentType}."),
    };

    /// <summary>Parses the model's JSON reply, tolerating stray code fences or surrounding prose. Public for unit tests.</summary>
    public static ParsedReceipt ParseJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new ReceiptParseException("The model did not return receipt data.");

        JsonElement root;
        try { root = JsonDocument.Parse(text[start..(end + 1)]).RootElement; }
        catch (JsonException) { throw new ReceiptParseException("The model returned malformed receipt data."); }

        if (!root.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
            throw new ReceiptParseException("The model returned no line items.");

        var items = new List<ParsedItem>();
        foreach (var el in itemsEl.EnumerateArray())
        {
            var description = el.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : "";
            if (!Decimal(el, "price", out var price)) continue; // a line without a readable price can't be split
            var confidence = Decimal(el, "confidence", out var c) ? (double)Math.Clamp(c, 0m, 1m) : 0.5;
            items.Add(new ParsedItem(description.Trim(), price, confidence));
        }

        var currency = root.TryGetProperty("currency", out var cur) && cur.ValueKind == JsonValueKind.String ? cur.GetString()! : "";
        var merchant = root.TryGetProperty("merchant", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        Decimal(root, "tax", out var tax);
        Decimal(root, "tip", out var tip);
        decimal? total = Decimal(root, "total", out var t) ? t : null;
        return new ParsedReceipt(currency, merchant, items, Math.Max(0, tax), Math.Max(0, tip), total);
    }

    private static bool Decimal(JsonElement el, string name, out decimal value)
    {
        value = 0;
        return el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out value);
    }
}
