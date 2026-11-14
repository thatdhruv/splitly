using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Splitly.Api.Auth;
using Splitly.Contracts;
using Splitly.Data;
using Splitly.Domain;

namespace Splitly.Api.Endpoints;

/// <summary>
/// WebAuthn needs to know which website it is protecting. The browser talks to the Blazor app, not to this API, so the
/// relying-party id and origin are those of the web app. Configure <c>Passkeys:RelyingPartyId</c> (e.g. "localhost" or your domain)
/// and <c>Passkeys:AllowedOrigins</c> (e.g. "https://localhost:7000").
/// </summary>
public static class PasskeyConfig
{
    public static void Apply(IdentityPasskeyOptions options, IConfiguration config)
    {
        if (config["Passkeys:RelyingPartyId"] is { Length: > 0 } rp) options.ServerDomain = rp;

        var allowed = config.GetSection("Passkeys:AllowedOrigins").Get<string[]>() ?? [];
        if (allowed.Length > 0)
            options.ValidateOrigin = ctx => ValueTask.FromResult(
                !ctx.CrossOrigin && ctx.Origin is not null && allowed.Contains(ctx.Origin, StringComparer.OrdinalIgnoreCase));
    }
}

public static class PasskeyEndpoints
{
    public static void MapPasskeys(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/auth/passkey");

        // --- sign up with a passkey instead of a password ---
        // Creates the account (no password) and signs it in, so the caller can immediately register a passkey through the
        // authenticated flow below. Until a credential is added the account can't be used again, and re-running sign-up for a
        // credential-less account just resumes it.
        g.MapPost("/signup", async (PasskeySignUpRequest req, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.DisplayName))
                throw new DomainValidationException("Name and email are required.");

            var user = await users.FindByEmailAsync(req.Email);
            if (user is null)
            {
                user = new AppUser { UserName = req.Email, Email = req.Email, DisplayName = req.DisplayName.Trim() };
                var created = await users.CreateAsync(user);
                if (!created.Succeeded) throw new DomainValidationException(string.Join(" ", created.Errors.Select(e => e.Description)));
            }
            else if (await users.HasPasswordAsync(user) || (await users.GetPasskeysAsync(user)).Count > 0)
            {
                throw new DomainValidationException("That email already has an account. Sign in instead.");
            }

            signIn.AuthenticationScheme = IdentityConstants.BearerScheme; // answer with bearer tokens, like /auth/login
            await signIn.SignInAsync(user, isPersistent: false);
            return Results.Empty;
        }).AllowAnonymous();

        // --- add a passkey to the signed-in account ---
        g.MapPost("/creation-options", async (ClaimsPrincipal principal, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var user = await users.GetUserAsync(principal) ?? throw new UnauthorizedAccessException();
            var options = await signIn.MakePasskeyCreationOptionsAsync(new PasskeyUserEntity
            {
                Id = user.Id.ToString(), Name = user.Email ?? user.UserName ?? user.Id.ToString(), DisplayName = user.DisplayName,
            });
            return Results.Text(options, "application/json");
        }).RequireAuthorization();

        g.MapPost("/register", async (PasskeyCredentialRequest req, ClaimsPrincipal principal, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var user = await users.GetUserAsync(principal) ?? throw new UnauthorizedAccessException();
            var attestation = await signIn.PerformPasskeyAttestationAsync(req.CredentialJson);
            if (!attestation.Succeeded) throw new DomainValidationException($"The passkey couldn't be verified: {attestation.Failure.Message}");

            attestation.Passkey.Name = string.IsNullOrWhiteSpace(req.Name) ? "Passkey" : req.Name.Trim();
            var saved = await users.AddOrUpdatePasskeyAsync(user, attestation.Passkey);
            if (!saved.Succeeded) throw new DomainValidationException(string.Join(" ", saved.Errors.Select(e => e.Description)));
            return Results.NoContent();
        }).RequireAuthorization();

        g.MapGet("/", async (ClaimsPrincipal principal, UserManager<AppUser> users) =>
        {
            var user = await users.GetUserAsync(principal) ?? throw new UnauthorizedAccessException();
            return (await users.GetPasskeysAsync(user))
                .Select(p => new PasskeyDto(Convert.ToBase64String(p.CredentialId), p.Name ?? "Passkey", p.CreatedAt))
                .OrderBy(p => p.CreatedAt).ToList();
        }).RequireAuthorization();

        g.MapDelete("/{id}", async (string id, ClaimsPrincipal principal, UserManager<AppUser> users) =>
        {
            var user = await users.GetUserAsync(principal) ?? throw new UnauthorizedAccessException();
            var passkeys = await users.GetPasskeysAsync(user);

            // Never lock the user out: keep at least one way to sign in.
            if (passkeys.Count <= 1 && !await users.HasPasswordAsync(user))
                throw new DomainValidationException("That's your only way to sign in. Add another passkey or set a password first.");

            byte[] credentialId;
            try { credentialId = Convert.FromBase64String(id.Replace('-', '+').Replace('_', '/')); }
            catch (FormatException) { throw new DomainValidationException("Unknown passkey."); }
            var result = await users.RemovePasskeyAsync(user, credentialId);
            return result.Succeeded ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization();

        // --- sign in with a passkey (no email needed: discoverable credentials) ---
        g.MapPost("/request-options", async (SignInManager<AppUser> signIn) =>
        {
            var options = await signIn.MakePasskeyRequestOptionsAsync(user: null);
            return Results.Text(options, "application/json");
        }).AllowAnonymous();

        g.MapPost("/login", async (PasskeyCredentialRequest req, SignInManager<AppUser> signIn) =>
        {
            signIn.AuthenticationScheme = IdentityConstants.BearerScheme;
            var result = await signIn.PasskeySignInAsync(req.CredentialJson);
            return result.Succeeded ? Results.Empty : Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Passkey sign-in failed");
        }).AllowAnonymous();
    }
}
