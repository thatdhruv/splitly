using Splitly.Domain.Splitting;

namespace Splitly.Contracts;

public record SignUpRequest(string Email, string Password, string DisplayName);
public record ProfileDto(Guid Id, string Email, string DisplayName);

public record CreateGroupRequest(string Name, string BaseCurrency);
public record MemberDto(Guid UserId, string DisplayName, string Role);
public record GroupSummaryDto(Guid Id, string Name, string BaseCurrency, int MemberCount);
public record GroupDto(Guid Id, string Name, string BaseCurrency, IReadOnlyList<MemberDto> Members);

public record CreateInviteRequest(int? ExpiresInHours = null, int? MaxUses = null);
/// <summary>The raw token is returned exactly once, at creation.</summary>
public record InviteDto(Guid Id, string Token, DateTimeOffset ExpiresAt, int MaxUses);
public record AcceptInviteRequest(string Token);
public record InvitePreviewDto(string GroupName, DateTimeOffset ExpiresAt);

public record ParticipantInput(Guid UserId, decimal? Value = null);

/// <param name="ExchangeRate">Optional override; otherwise the rate for <paramref name="Date"/> is looked up and locked.</param>
/// <param name="RowVersion">Required on update: the version the client last saw.</param>
public record ExpenseRequest(
    string Description, decimal Amount, string Currency, DateOnly Date, Guid PaidBy,
    SplitType SplitType, IReadOnlyList<ParticipantInput> Participants,
    decimal? ExchangeRate = null, string? RowVersion = null);

public record ShareDto(Guid UserId, decimal Amount, decimal BaseAmount);

public record ExpenseDto(
    Guid Id, Guid GroupId, string Description, decimal Amount, string Currency, decimal ExchangeRate,
    decimal BaseAmount, DateOnly Date, Guid PaidBy, SplitType SplitType, IReadOnlyList<ShareDto> Shares,
    string RowVersion);

public record BalanceDto(Guid UserId, string DisplayName, decimal Amount);
public record BalancesDto(Guid GroupId, string Currency, IReadOnlyList<BalanceDto> Balances);

public record TransferDto(Guid From, Guid To, decimal Amount);
public record SettleUpDto(string Currency, IReadOnlyList<TransferDto> Transfers);

public record PaymentRequest(Guid From, Guid To, decimal Amount);
public record PaymentDto(Guid Id, Guid From, Guid To, decimal Amount, DateTimeOffset CreatedAt);

// ---- receipts ----
public record ReceiptItemDto(Guid Id, string Description, decimal Price, double Confidence, bool NeedsReview, IReadOnlyList<Guid> ClaimedBy);

/// <param name="Status">Uploaded, Processing, NeedsReview, Ready, Failed or Finalized.</param>
public record ReceiptDto(
    Guid Id, Guid GroupId, string Status, string Currency, string? Merchant, decimal Tax, decimal Tip, decimal? StatedTotal,
    string? Note, string? FailureReason, Guid? ExpenseId, DateTimeOffset CreatedAt, IReadOnlyList<ReceiptItemDto> Items);

public record ReceiptSummaryDto(Guid Id, string Status, string? Merchant, string Currency, int ItemCount, DateTimeOffset CreatedAt);

public record ItemRequest(string Description, decimal Price);
public record ReceiptTotalsRequest(decimal Tax, decimal Tip, string? Currency = null);
public record ClaimRequest(bool Claimed);
public record FinalizeReceiptRequest(Guid PaidBy, DateOnly Date);

// ---- recurring expenses ----
public record RecurringRequest(
    string Description, decimal Amount, string Currency, Guid PaidBy, SplitType SplitType,
    IReadOnlyList<ParticipantInput> Participants, Recurrence Recurrence, DateOnly StartDate, DateOnly? EndDate = null);

public record RecurringDto(
    Guid Id, Guid GroupId, string Description, decimal Amount, string Currency, Guid PaidBy, SplitType SplitType,
    IReadOnlyList<ParticipantInput> Participants, Recurrence Recurrence, DateOnly StartDate, DateOnly NextRunDate, DateOnly? EndDate, bool IsActive);

// ---- passkeys ----
public record PasskeySignUpRequest(string Email, string DisplayName);
/// <summary>The JSON the browser's WebAuthn API produced (navigator.credentials.create/get), passed through untouched.</summary>
public record PasskeyCredentialRequest(string CredentialJson, string? Name = null);
public record PasskeyDto(string Id, string Name, DateTimeOffset CreatedAt);
