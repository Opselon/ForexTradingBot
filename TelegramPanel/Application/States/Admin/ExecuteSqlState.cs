// --- START OF FILE: ExecuteSqlState.cs ---
using Application.Interfaces;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Infrastructure;

namespace TelegramPanel.Application.States.Admin
{
    public class ExecuteSqlState : ITelegramState
    {
        public string Name => "WaitingForSqlQuery";
        private readonly ILogger<ExecuteSqlState> _logger;
        private readonly IAdminService _adminService;
        private readonly ITelegramMessageSender _messageSender;

        public ExecuteSqlState(ILogger<ExecuteSqlState> logger, IAdminService adminService, ITelegramMessageSender messageSender)
        {
            _logger = logger;
            _adminService = adminService;
            _messageSender = messageSender;
        }

        public Task<string?> GetEntryMessageAsync(long userId, Update? triggerUpdate, CancellationToken ct)
        {
            return Task.FromResult<string?>("👨‍💻 *Execute Raw SQL Query*\n\n⚠️ *WARNING:* This is a dangerous, direct interface to the database. Use with extreme caution. Only `SELECT` statements are recommended.\n\nSend the full SQL query to execute, or /cancel to abort.");
        }

        public async Task<string?> ProcessUpdateAsync(Update update, CancellationToken cancellationToken)
        {
            if (update.Message?.From == null || string.IsNullOrWhiteSpace(update.Message.Text))
            {
                return Name;
            }

            var message = update.Message;
            var adminId = message.From.Id;

            if (message.Text.Trim() == "/cancel")
            {
                await _messageSender.SendTextMessageAsync(adminId, "SQL execution cancelled.", cancellationToken: cancellationToken);
                return null;
            }

            await _messageSender.SendTextMessageAsync(adminId, "⏳ Executing query...", cancellationToken: cancellationToken);

            string result = await _adminService.ExecuteRawSqlQueryAsync(message.Text, cancellationToken);

            // Telegram messages have a 4096 character limit. Truncate if necessary.
            if (result.Length > 4000)
            {
                result = result.Substring(0, 4000) + "\n\n... (result truncated)";
            }

            await _messageSender.SendTextMessageAsync(adminId, result, ParseMode.Markdown, cancellationToken: cancellationToken);

            // Stay in the state to allow for another query.
            await _messageSender.SendTextMessageAsync(adminId, "Enter another query, or type /cancel.", cancellationToken: cancellationToken);
            return Name;
        }
    }
}