using Discord;
using Discord.WebSocket;

namespace ServerComms.Bot;

// A feature that owns slash commands and/or buttons. Custom IDs are "sc:<feature>:..." so each
// module only sees its own components.
public interface IInteractionModule
{
    IEnumerable<SlashCommandProperties> Commands { get; }
    /// <summary>Returns true when the module handled the command.</summary>
    Task<bool> HandleSlashAsync(SocketSlashCommand command);
    /// <summary>Returns true when the module handled the button or select menu.</summary>
    Task<bool> HandleComponentAsync(SocketMessageComponent component);
    /// <summary>Returns true when the module handled the submitted form.</summary>
    Task<bool> HandleModalAsync(SocketModal modal) => Task.FromResult(false);
    /// <summary>Returns true when the module answered the autocomplete request.</summary>
    Task<bool> HandleAutocompleteAsync(SocketAutocompleteInteraction autocomplete) => Task.FromResult(false);
}

// Registers every module's slash commands on the guild once Discord is ready (replacing whatever the
// application had registered before, including the old bot's commands) and routes interactions to
// the modules. Handlers run off the gateway thread; an unhandled error answers the user instead of
// leaving Discord's "interaction failed".
public sealed class Interactions(DiscordConnection discord, IEnumerable<IInteractionModule> modules,
    ILogger<Interactions> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        discord.Client.SlashCommandExecuted += command => Dispatch(command, () => RouteSlash(command));
        discord.Client.ButtonExecuted += component => Dispatch(component, () => RouteComponent(component));
        discord.Client.SelectMenuExecuted += component => Dispatch(component, () => RouteComponent(component));
        discord.Client.ModalSubmitted += modal => Dispatch(modal, () => RouteModal(modal));
        discord.Client.AutocompleteExecuted += autocomplete => Dispatch(autocomplete, () => RouteAutocomplete(autocomplete));

        await discord.Ready.WaitAsync(ct);
        var commands = modules.SelectMany(m => m.Commands).Cast<ApplicationCommandProperties>().ToArray();
        try
        {
            await discord.Guild!.BulkOverwriteApplicationCommandAsync(commands);
            await discord.Client.BulkOverwriteGlobalApplicationCommandsAsync([]);   // old bots' global commands
            logger.LogInformation("Registered {Count} slash commands", commands.Length);
        }
        catch (Exception e)
        {
            logger.LogError("Slash command registration failed: {Error}", e.Message);
        }
    }

    private async Task RouteSlash(SocketSlashCommand command)
    {
        foreach (var module in modules)
            if (await module.HandleSlashAsync(command))
                return;
        await command.RespondAsync("That command isn't handled by this bot.", ephemeral: true);
    }

    private async Task RouteComponent(SocketMessageComponent component)
    {
        foreach (var module in modules)
            if (await module.HandleComponentAsync(component))
                return;
        // Buttons left on messages from the old bots land here.
        await component.RespondAsync("This button is no longer in use.", ephemeral: true);
    }

    private async Task RouteModal(SocketModal modal)
    {
        foreach (var module in modules)
            if (await module.HandleModalAsync(modal))
                return;
        await modal.RespondAsync("This form is no longer in use.", ephemeral: true);
    }

    private async Task RouteAutocomplete(SocketAutocompleteInteraction autocomplete)
    {
        foreach (var module in modules)
            if (await module.HandleAutocompleteAsync(autocomplete))
                return;
        await autocomplete.RespondAsync([]);
    }

    private Task Dispatch(SocketInteraction interaction, Func<Task> handler)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await handler();
            }
            catch (Exception e)
            {
                logger.LogError(e, "Interaction from {User} failed", interaction.User.Username);
                if (interaction is SocketAutocompleteInteraction)
                    return;
                try
                {
                    const string text = "Something went wrong. Please try again in a moment.";
                    if (interaction.HasResponded)
                        await interaction.FollowupAsync(text, ephemeral: true);
                    else
                        await interaction.RespondAsync(text, ephemeral: true);
                }
                catch { /* interaction expired */ }
            }
        });
        return Task.CompletedTask;
    }
}
