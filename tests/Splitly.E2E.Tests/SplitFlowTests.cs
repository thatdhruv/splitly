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

    // ---- helpers ----

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
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign up" }).ClickAsync();
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
