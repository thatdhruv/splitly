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
