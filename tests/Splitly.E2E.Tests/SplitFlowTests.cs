using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Splitly.E2E.Tests;

[Collection(StackCollection.Name)]
public class SplitFlowTests(StackFixture stack)
{
    private const string Password = "Sup3r-secret-pw!";

    [Fact]
    public async Task Sign_up_create_group_and_add_expense()
    {
        await using var ctx = await stack.Browser.NewContextAsync();
        var page = await ctx.NewPageAsync();

        await SignUpAsync(page, "Alice", "/login");
        await Fill(page, "#group-name", "Roomies");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create group" }).ClickAsync();
        await Expect(page.Locator("h1")).ToHaveTextAsync("Roomies");

        await Fill(page, "#exp-desc", "Groceries");
        await Fill(page, "#exp-amount", "42.50");
        await page.Locator("#exp-submit").ClickAsync();

        await Expect(page.Locator("#expenses")).ToContainTextAsync("Groceries");
        await Expect(page.Locator("#expenses")).ToContainTextAsync("42.50 USD");
    }

    [Fact]
    public async Task Invited_friend_sees_balances_update_live_and_settle_up_is_computed()
    {
        await using var ctxA = await stack.Browser.NewContextAsync();
        await using var ctxB = await stack.Browser.NewContextAsync();
        var alice = await ctxA.NewPageAsync();
        var bob = await ctxB.NewPageAsync();

        // Alice creates a group and an invite link.
        await SignUpAsync(alice, "Alice", "/login");
        await Fill(alice, "#group-name", "Trip");
        await alice.GetByRole(AriaRole.Button, new() { Name = "Create group" }).ClickAsync();
        await Expect(alice.Locator("h1")).ToHaveTextAsync("Trip");
        await alice.Locator("#invite").ClickAsync();
        var inviteUrl = await alice.Locator("#invite-url").InputValueAsync();

        // Bob follows it: sign-in bounces him through /login and back to the invite.
        await Goto(bob, inviteUrl.Replace(new Uri(inviteUrl).GetLeftPart(UriPartial.Authority), stack.WebUrl));
        await Expect(bob).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/login"));
        await bob.Locator("#to-signup").ClickAsync();
        await FillAuthAsync(bob, "Bob");
        await Expect(bob.Locator("#accept-invite")).ToBeVisibleAsync();
        await bob.Locator("#accept-invite").ClickAsync();
        await Expect(bob.Locator("h1")).ToHaveTextAsync("Trip");
        await Expect(bob.Locator(".pill")).ToContainTextAsync("live");

        // Alice (still on her page, no reload) now sees Bob arrive, then adds a $90 expense split two ways.
        await Expect(alice.Locator("#exp-payer option")).ToHaveCountAsync(2);
        await Fill(alice, "#exp-desc", "Hotel");
        await Fill(alice, "#exp-amount", "90");
        await alice.Locator("#exp-submit").ClickAsync();

        // Bob's open page updates over SignalR with no interaction.
        await Expect(bob.Locator("#balances")).ToContainTextAsync("owes 45.00");
        await Expect(bob.Locator("#settle-up")).ToContainTextAsync("Bob pays Alice 45.00 USD");
        await Expect(alice.Locator("#balances")).ToContainTextAsync("is owed 45.00");

        // Bob marks the settle-up payment as paid; both screens go to settled.
        await bob.GetByRole(AriaRole.Button, new() { Name = "Mark paid" }).ClickAsync();
        await Expect(bob.Locator("#balances")).Not.ToContainTextAsync("owes");
        await Expect(alice.Locator("#balances")).Not.ToContainTextAsync("is owed");
    }

    [Fact]
    public async Task Receipt_is_parsed_flagged_reviewed_claimed_and_turned_into_an_expense()
    {
        await using var ctx = await stack.Browser.NewContextAsync();
        var page = await ctx.NewPageAsync();
        

        await SignUpAsync(page, "Alice", "/login");
        await Fill(page, "#group-name", "Dinner club");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create group" }).ClickAsync();
        await Expect(page.Locator("h1")).ToHaveTextAsync("Dinner club");

        // Upload a photo; the background worker picks it up and parses it.
        await page.Locator("#receipt-file").SetInputFilesAsync(new FilePayload
        {
            Name = "receipt.png", MimeType = "image/png", Buffer = TinyPng,
        });
        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/receipts/"));
        await Expect(page.Locator("#receipt-status")).ToHaveAttributeAsync("data-status", "NeedsReview",
            new() { Timeout = 30_000 });

        // The low-confidence line is highlighted; fix it up and confirm.
        var flagged = page.Locator("[data-item='Smudged line']");
        await Expect(flagged).ToBeVisibleAsync();
        await flagged.Locator("input[aria-label=Description]").FillAsync("Cheesecake");
        await flagged.Locator("input[aria-label=Description]").PressAsync("Tab");
        await flagged.GetByRole(AriaRole.Button, new() { Name = "Looks right" }).ClickAsync();
        await Expect(page.Locator("#receipt-status")).ToHaveAttributeAsync("data-status", "Ready");

        // Claim every item (Alice is the only member), then check the preview and add the expense.
        foreach (var item in new[] { "Burger", "Garden salad", "Cheesecake" })
        {
            var box = page.Locator($"[data-item='{item}'] input[type=checkbox]");
            await box.CheckAsync();
            await Expect(box).ToBeCheckedAsync();
        }
        await Expect(page.Locator("#receipt-preview")).ToContainTextAsync("33.05 USD");
        await page.Locator("#finalize").ClickAsync();

        await Expect(page.Locator("h1")).ToHaveTextAsync("Dinner club");
        await Expect(page.Locator("#expenses")).ToContainTextAsync("Corner Bistro");
        await Expect(page.Locator("#expenses")).ToContainTextAsync("33.05 USD");
    }

    [Fact]
    public async Task Recurring_expense_is_scheduled_and_the_worker_creates_the_first_occurrence()
    {
        await using var ctx = await stack.Browser.NewContextAsync();
        var page = await ctx.NewPageAsync();

        await SignUpAsync(page, "Alice", "/login");
        await Fill(page, "#group-name", "Flat");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create group" }).ClickAsync();
        await Expect(page.Locator("h1")).ToHaveTextAsync("Flat");

        await Fill(page, "#exp-desc", "Internet");
        await Fill(page, "#exp-amount", "45");
        await page.Locator("#exp-repeat").SelectOptionAsync("Monthly");
        await page.Locator("#exp-submit").ClickAsync();

        await Expect(page.Locator("#recurring")).ToContainTextAsync("Internet");
        // Start date is today, so the (fast-ticking) worker creates the first occurrence; the page refreshes via reload.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!(await page.Locator("#expenses").InnerTextAsync()).Contains("Internet"))
        {
            Assert.True(DateTime.UtcNow < deadline, "The worker never created the first occurrence.");
            await Task.Delay(1000);
            await page.ReloadAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Expect(page.Locator("h1")).ToHaveTextAsync("Flat");
        }
        await Expect(page.Locator("#expenses")).ToContainTextAsync("45.00 USD");

        await page.GetByRole(AriaRole.Button, new() { Name = "Stop" }).ClickAsync();
        await Expect(page.Locator("#recurring")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Passkey_sign_up_sign_out_sign_in_and_manage()
    {
        await using var ctx = await stack.Browser.NewContextAsync();
        var page = await ctx.NewPageAsync();
        var device = await AddVirtualAuthenticatorAsync(ctx, page);

        // Sign up with no password at all.
        await Goto(page, stack.WebUrl + "/login");
        await page.Locator("#to-signup").ClickAsync();
        await Fill(page, "#displayName", "Passkey Pat");
        await Fill(page, "#email", $"pat-{Guid.NewGuid():N}@example.com");
        await page.Locator("#passkey-signup").ClickAsync();
        await ExpectWithContext(page, page.Locator("#group-name"), "after passkey sign-up");
        await ExpectWithContext(page, page.Locator("#account-link"), "header after passkey sign-up", "Passkey Pat");

        // Sign out, then back in using only the passkey (no email typed).
        await page.Locator("#logout").ClickAsync();
        await Goto(page, stack.WebUrl + "/login");
        await page.Locator("#passkey-signin").ClickAsync();
        await ExpectWithContext(page, page.Locator("#group-name"), "after passkey sign-in");
        await ExpectWithContext(page, page.Locator("#account-link"), "header after passkey sign-in", "Passkey Pat");

        // Manage passkeys: add a second one, and the last remaining one can't be removed.
        await page.Locator("#account-link").ClickAsync();
        await Expect(page.Locator("#passkeys li")).ToHaveCountAsync(1);
        // A second passkey comes from a different device: an authenticator refuses to enrol the same account twice.
        await device.ReplaceWithNewDeviceAsync();
        await Fill(page, "#passkey-name", "Work laptop");
        await page.Locator("#add-passkey").ClickAsync();
        await Expect(page.Locator("#passkeys li")).ToHaveCountAsync(2);
        await Expect(page.Locator("#passkeys")).ToContainTextAsync("Work laptop");

        await page.Locator("#passkeys li").First.GetByRole(AriaRole.Button, new() { Name = "Remove" }).ClickAsync();
        await Expect(page.Locator("#passkeys li")).ToHaveCountAsync(1);
        await page.Locator("#passkeys li").First.GetByRole(AriaRole.Button, new() { Name = "Remove" }).ClickAsync();
        await Expect(page.Locator("[role=alert]")).ToContainTextAsync("only way to sign in");
        await Expect(page.Locator("#passkeys li")).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task Passkey_sign_in_is_refused_for_an_unknown_credential()
    {
        // A fresh authenticator has no credential for this site, so there's nothing to sign in with.
        await using var ctx = await stack.Browser.NewContextAsync();
        var page = await ctx.NewPageAsync();
        await AddVirtualAuthenticatorAsync(ctx, page);
        await Goto(page, stack.WebUrl + "/login");
        await page.Locator("#passkey-signin").ClickAsync();
        await Expect(page.Locator("[role=alert]")).ToBeVisibleAsync();
        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/login"));
    }

    // ---- helpers ----

    /// <summary>Asserts, and on failure reports what the page actually showed (a bare timeout says nothing about why).</summary>
    private static async Task ExpectWithContext(IPage page, ILocator locator, string step, string? text = null)
    {
        try
        {
            if (text is null) await Expect(locator).ToBeVisibleAsync();
            else await Expect(locator).ToHaveTextAsync(text);
        }
        catch (PlaywrightException ex)
        {
            var body = (await page.Locator("body").InnerTextAsync()).ReplaceLineEndings(" | ");
            var reason = ex.Message.Split(Environment.NewLine)[0];
            throw new Xunit.Sdk.XunitException($"{step}: {reason} -- url={page.Url} -- page: {body[..Math.Min(body.Length, 400)]}");
        }
    }

    /// <summary>A software authenticator so WebAuthn works headless: stores discoverable credentials and auto-approves user verification.</summary>
    private static async Task<VirtualDevice> AddVirtualAuthenticatorAsync(IBrowserContext ctx, IPage page)
    {
        var device = new VirtualDevice(await ctx.NewCDPSessionAsync(page));
        await device.Cdp.SendAsync("WebAuthn.enable");
        await device.AddAsync();
        return device;
    }

    private sealed class VirtualDevice(ICDPSession cdp)
    {
        public ICDPSession Cdp { get; } = cdp;
        private string? _id;

        public async Task AddAsync()
        {
            var res = await Cdp.SendAsync("WebAuthn.addVirtualAuthenticator", new Dictionary<string, object>
            {
                ["options"] = new Dictionary<string, object>
                {
                    ["protocol"] = "ctap2", ["transport"] = "internal", ["hasResidentKey"] = true, ["hasUserVerification"] = true,
                    ["isUserVerified"] = true, ["automaticPresenceSimulation"] = true,
                },
            });
            _id = res!.Value.GetProperty("authenticatorId").GetString();
        }

        public async Task ReplaceWithNewDeviceAsync()
        {
            await Cdp.SendAsync("WebAuthn.removeVirtualAuthenticator", new Dictionary<string, object> { ["authenticatorId"] = _id! });
            await AddAsync();
        }
    }

    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private async Task SignUpAsync(IPage page, string name, string path)
    {
        await Goto(page, stack.WebUrl + path);
        await page.Locator("#to-signup").ClickAsync();
        await FillAuthAsync(page, name);
        await Expect(page.Locator("#group-name")).ToBeVisibleAsync();
    }

    private static async Task FillAuthAsync(IPage page, string name)
    {
        await Fill(page, "#displayName", name);
        await Fill(page, "#email", $"{name.ToLower()}-{Guid.NewGuid():N}@example.com");
        await Fill(page, "#password", Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign up", Exact = true }).ClickAsync();
    }

    /// <summary>Navigates and waits for the Blazor circuit, so clicks aren't lost to a not-yet-interactive page.</summary>
    private static async Task Goto(IPage page, string url)
    {
        await page.GotoAsync(url);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    /// <summary>Blazor binds text inputs on change, so blur after filling.</summary>
    private static async Task Fill(IPage page, string selector, string value)
    {
        await page.Locator(selector).FillAsync(value);
        await page.Locator(selector).PressAsync("Tab");
    }
}
