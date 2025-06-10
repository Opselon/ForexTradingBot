// File: TelegramPanel/Application/CommandHandlers/Features/EconomicCalendar/EconomicCalendarCallbackHandler.cs
using Application.Interfaces;
using Microsoft.Extensions.Logging;
using System.Text;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.CommandHandlers.MainMenu;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Formatters;
using TelegramPanel.Infrastructure;
using TelegramPanel.Infrastructure.Helpers;

namespace TelegramPanel.Application.CommandHandlers.Features.EconomicCalendar
{
    public class EconomicCalendarCallbackHandler : ITelegramCallbackQueryHandler
    {
        private readonly ILogger<EconomicCalendarCallbackHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly IEconomicCalendarService _calendarService;
        private const int PageSize = 7;

        public EconomicCalendarCallbackHandler(
            ILogger<EconomicCalendarCallbackHandler> logger,
            ITelegramMessageSender messageSender,
            IEconomicCalendarService calendarService)
        {
            _logger = logger;
            _messageSender = messageSender;
            _calendarService = calendarService;
        }

        public bool CanHandle(Update update) =>
            update.CallbackQuery?.Data?.StartsWith("menu_econ_calendar") == true;

        public async Task HandleAsync(Update update, CancellationToken cancellationToken = default)
        {
            var callbackQuery = update.CallbackQuery!;
            await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);

            var chatId = callbackQuery.Message!.Chat.Id;
            var messageId = callbackQuery.Message.MessageId;
            var parts = callbackQuery.Data!.Split(':');
            int page = parts.Length > 1 && int.TryParse(parts[1], out int p) ? p : 1;

            await _messageSender.EditMessageTextAsync(chatId, messageId, "⏳ Fetching economic calendar...", cancellationToken: cancellationToken);

            var result = await _calendarService.GetReleasesAsync(page, PageSize, cancellationToken);

            if (!result.Succeeded || !result.Data.Any())
            {
                await _messageSender.EditMessageTextAsync(chatId, messageId, "❌ Could not retrieve economic releases at this time.", replyMarkup: GetPaginationKeyboard(page, false), cancellationToken: cancellationToken);
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("🗓️ *Recent & Upcoming Economic Releases*");
            sb.AppendLine("`-----------------------------------`");

            foreach (var release in result.Data)
            {
                sb.AppendLine($"\n*{TelegramMessageFormatter.EscapeMarkdownV2(release.Name)}*");
                if (!string.IsNullOrWhiteSpace(release.Link))
                {
                    sb.AppendLine($"[Official Source]({release.Link})");
                }
            }

            var keyboard = GetPaginationKeyboard(page, result.Data.Count == PageSize);

            await _messageSender.EditMessageTextAsync(
                chatId,
                messageId,
                sb.ToString(),
                ParseMode.MarkdownV2,
                keyboard,
                cancellationToken);
        }

        private InlineKeyboardMarkup GetPaginationKeyboard(int currentPage, bool hasMore)
        {
            var row = new List<InlineKeyboardButton>();
            if (currentPage > 1)
            {
                row.Add(InlineKeyboardButton.WithCallbackData("⬅️ Previous", $"menu_econ_calendar:{currentPage - 1}"));
            }
            if (hasMore)
            {
                row.Add(InlineKeyboardButton.WithCallbackData("Next ➡️", $"menu_econ_calendar:{currentPage + 1}"));
            }

            var keyboard = new List<List<InlineKeyboardButton>>();
            if (row.Any()) keyboard.Add(row);

            keyboard.Add(new List<InlineKeyboardButton> {
                InlineKeyboardButton.WithCallbackData("🏠 Main Menu", MenuCallbackQueryHandler.BackToMainMenuGeneral)
            });

            return new InlineKeyboardMarkup(keyboard);
        }
    }
}