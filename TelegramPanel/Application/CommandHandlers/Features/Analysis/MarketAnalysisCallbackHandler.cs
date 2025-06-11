using Microsoft.Extensions.Logging;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramPanel.Application.CommandHandlers.Features.News;
using TelegramPanel.Application.CommandHandlers.MainMenu;
using TelegramPanel.Application.Interfaces;
using TelegramPanel.Formatters;
using TelegramPanel.Infrastructure;
using TelegramPanel.Infrastructure.Helpers;

namespace TelegramPanel.Application.CommandHandlers.Features.Analysis
{
    public class MarketAnalysisCallbackHandler : ITelegramCallbackQueryHandler, ITelegramCommandHandler
    {
        private readonly ILogger<MarketAnalysisCallbackHandler> _logger;
        private readonly ITelegramMessageSender _messageSender;
        private readonly IMarketDataService _marketDataService;

        private const string MarketAnalysisCallback = "market_analysis";
        private const string RefreshMarketDataCallback = "refresh_market_data";
        private const string SelectCurrencyCallback = "select_currency";
        private readonly IActualTelegramMessageActions _directMessageSender;
        // 13+ popular forex pairs + gold
        // Using country flag emojis for better visual representation of currency pairs.
        // Note: Emoji support depends on the user's device and Telegram client.
        private static readonly (string Symbol, string Label)[] SupportedSymbols = new[]
        {
        // 🇪🇺 - European Union (Euro), 🇺🇸 - United States Dollar
        ("EURUSD", "🇪🇺🇺🇸 EUR/USD"),
        // 🇬🇧 - United Kingdom (British Pound), 🇺🇸 - United States Dollar
        ("GBPUSD", "🇬🇧🇺🇸 GBP/USD"),
        // 🇺🇸 - United States Dollar, 🇯🇵 - Japan (Yen)
        ("USDJPY", "🇺🇸🇯🇵 USD/JPY"),
        // 🇦🇺 - Australia (Australian Dollar), 🇺🇸 - United States Dollar
        ("AUDUSD", "🇦🇺🇺🇸 AUD/USD"),
        // 🇺🇸 - United States Dollar, 🇨🇦 - Canada (Canadian Dollar)
        ("USDCAD", "🇺🇸🇨🇦 USD/CAD"),
        // 🇺🇸 - United States Dollar, 🇨🇭 - Switzerland (Swiss Franc)
        ("USDCHF", "🇺🇸🇨🇭 USD/CHF"),
        // 🇳🇿 - New Zealand (New Zealand Dollar), 🇺🇸 - United States Dollar
        ("NZDUSD", "🇳🇿🇺🇸 NZD/USD"),
        // 🇪🇺 - European Union, 🇬🇧 - United Kingdom
        ("EURGBP", "🇪🇺🇬🇧 EUR/GBP"),
        // 🇪🇺 - European Union, 🇯🇵 - Japan
        ("EURJPY", "🇪🇺🇯🇵 EUR/JPY"),
        // 🇬🇧 - United Kingdom, 🇯🇵 - Japan
        ("GBPJPY", "🇬🇧🇯🇵 GBP/JPY"),
        // 🇦🇺 - Australia, 🇯🇵 - Japan
        ("AUDJPY", "🇦🇺🇯🇵 AUD/JPY"),
        // 🇨🇭 - Switzerland, 🇯🇵 - Japan
        ("CHFJPY", "🇨🇭🇯🇵 CHF/JPY"),
        // 🇪🇺 - European Union, 🇦🇺 - Australia
        ("EURAUD", "🇪🇺🇦🇺 EUR/AUD"),
        // 🇪🇺 - European Union, 🇨🇦 - Canada
        ("EURCAD", "🇪🇺🇨🇦 EUR/CAD"),
        // 🇬🇧 - United Kingdom, 🇦🇺 - Australia
        ("GBPAUD", "🇬🇧🇦🇺 GBP/AUD"),
        // For Gold (XAU/USD), Gold emoji 🥇 is appropriate, and USD is one component.
        ("XAUUSD", "🥇🇺🇸 Gold (XAU/USD)")
    };

        public MarketAnalysisCallbackHandler(
            ILogger<MarketAnalysisCallbackHandler> logger,
            ITelegramMessageSender messageSender,
            IMarketDataService marketDataService,
            IActualTelegramMessageActions directMessageSender)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
            _marketDataService = marketDataService ?? throw new ArgumentNullException(nameof(marketDataService));
            _directMessageSender = directMessageSender ?? throw new ArgumentNullException(nameof(directMessageSender));
        }

        public bool CanHandle(Update update)
        {
            return update.CallbackQuery?.Data?.StartsWith(MarketAnalysisCallback) == true ||
                   update.CallbackQuery?.Data?.StartsWith(RefreshMarketDataCallback) == true ||
                   update.CallbackQuery?.Data?.StartsWith(SelectCurrencyCallback) == true;
        }


        // In MarketAnalysisCallbackHandler.cs

        /// <summary>
        /// Executes a long-running asynchronous operation while concurrently displaying an animated loading message.
        /// This method uses a direct, non-retrying Telegram API call for the animation to ensure real-time feedback.
        /// </summary>
        /// <typeparam name="TResult">The return type of the long-running operation.</typeparam>
        /// <param name="chatId">The identifier of the chat.</param>
        /// <param name="messageId">The identifier of the message to edit.</param>
        /// <param name="baseLoadingText">The static part of the loading message (e.g., "Fetching data...").</param>
        /// <param name="operationToExecute">A factory function for the long-running Task.</param>
        /// <param name="cancellationToken">The cancellation token for the entire operation.</param>
        /// <returns>The result of the long-running operation.</returns>
        private async Task<TResult> AnimateWhileExecutingAsync<TResult>(
            long chatId,
            int messageId,
            string baseLoadingText,
            Func<CancellationToken, Task<TResult>> operationToExecute,
            CancellationToken cancellationToken)
        {
            var animationFrames = new[] { " .", " . .", " . . .", " . . . ." };
            var frameIndex = 0;

            // This CTS allows us to cancel the animation loop from within this method
            // once the main data fetching task is complete.
            using var animationCts = new CancellationTokenSource();
            // This linked CTS ensures that if the original caller cancels, BOTH the data fetch AND the animation stop.
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, animationCts.Token);

            // Start the long-running data fetch operation but do not await it yet.
            var dataFetchTask = operationToExecute(linkedCts.Token);

            // This task runs the UI animation loop in a separate thread.
            var animationTask = Task.Run(async () =>
            {
                // This state variable prevents us from sending redundant API calls
                // if the animation frame text happens to be the same as the last one.
                string? lastSentText = null;

                try
                {
                    while (!linkedCts.Token.IsCancellationRequested)
                    {
                        var currentFrame = animationFrames[frameIndex++ % animationFrames.Length];
                        var newText = $"{baseLoadingText}{currentFrame}";

                        // **Proactive Check:** Only edit the message if the content has actually changed.
                        if (newText != lastSentText)
                        {
                            // **CRITICAL:** Call the DIRECT method that bypasses the Hangfire queue and Polly retries.
                            await _directMessageSender.EditMessageTextDirectAsync(
                                chatId,
                                messageId,
                                newText,
                                ParseMode.Markdown,
                                null, // No keyboard during animation
                                linkedCts.Token);

                            lastSentText = newText;
                        }

                        // Wait for the next frame at the end of the loop.
                        await Task.Delay(TimeSpan.FromMilliseconds(800), linkedCts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // This is the expected and clean way to exit the loop when cancelled.
                }
                catch (ApiRequestException apiEx) when (apiEx.Message.Contains("message is not modified"))
                {
                    // This is a defensive catch. The `lastSentText` check should prevent this,
                    // but if it ever happens, we safely ignore it and let the loop continue.
                    _logger.LogTrace("Ignoring a 'message not modified' exception during animation.");
                }
                catch (Exception ex)
                {
                    // For any other unexpected error, log it and terminate the animation gracefully.
                    _logger.LogWarning(ex, "Animation loop was terminated by an unexpected exception.");
                }
            }, linkedCts.Token);

            try
            {
                // Now, we wait for the main event: the data fetching operation.
                return await dataFetchTask;
            }
            finally
            {
                // **VERY IMPORTANT:** Regardless of success or failure, we MUST stop the animation loop.
                if (!animationCts.IsCancellationRequested)
                {
                    await animationCts.CancelAsync();
                }

                // Wait briefly for the animation task to fully stop. This prevents a race condition
                // where a final animation frame might overwrite the real success/error message
                // that the calling method is about to send.
                await Task.WhenAny(animationTask, Task.Delay(150));
            }
        }
        public async Task HandleAsync(Update update, CancellationToken cancellationToken)
        {
            if (update.CallbackQuery == null || update.CallbackQuery.Message == null)
            {
                _logger.LogWarning("CallbackQuery or its Message is null.");
                return;
            }

            var callbackQuery = update.CallbackQuery;
            var callbackData = callbackQuery.Data;
            var chatId = callbackQuery.Message.Chat.Id;
            var messageId = callbackQuery.Message.MessageId;

            if (string.IsNullOrEmpty(callbackData))
            {
                _logger.LogWarning("Callback query with empty data. UpdateID: {UpdateId}", update.Id);
                await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, "Empty callback.", cancellationToken: cancellationToken);
                return;
            }

            _logger.LogInformation("Handling CBQ. Data:{Data}, Chat:{ChatId}, Msg:{MsgId}, User:{UserId}",
                callbackData, chatId, messageId, callbackQuery.From.Id);

            try
            {
                string[] parts = callbackData.Split(new[] { ':' }, 2); // Split only on the first colon
                string action = parts[0];
                string? payload = parts.Length > 1 ? parts[1] : null;

                // It's good practice to answer the callback query promptly.
                // We can do a general one here, and specific actions can override if they need to show specific text in the toast.
                // However, if a subsequent EditMessageTextAsync fails with "message not modified", answering again can be an issue.
                // Let's try answering within each specific action block or if no action is matched.
                bool callbackAcknowledged = false;

                if (action == MarketAnalysisCallback) // This is for the INITIAL entry point to show the menu
                {
                    _logger.LogInformation("Action: Initial MarketAnalysisCallback. Showing currency menu. ChatID:{ChatId}", chatId);
                    await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
                    callbackAcknowledged = true;
                    await ShowCurrencySelectionMenu(chatId, messageId, cancellationToken);
                }
                else if (action == SelectCurrencyCallback)
                {
                    if (!string.IsNullOrEmpty(payload)) // A specific currency was selected FROM THE MENU
                    {
                        _logger.LogInformation("Action: SelectCurrencyCallback for {Symbol}. ChatID:{ChatId}", payload, chatId);
                        // ShowMarketAnalysis will handle its own loading message and final ack for this interaction path
                        // The AnswerCallbackQuery here is just to acknowledge the button press if ShowMarketAnalysis takes time to start editing
                        await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, $"Loading {payload}...", cancellationToken: cancellationToken);
                        callbackAcknowledged = true; // Consider it acknowledged for now.
                        await ShowMarketAnalysis(chatId, messageId, payload, isRefresh: false, callbackQuery.Id, cancellationToken);
                    }
                    else // This is the "Change Currency" button on an *existing analysis message*
                    {
                        _logger.LogInformation("Action: SelectCurrencyCallback (Change Currency button). Showing currency menu. ChatID:{ChatId}", chatId);
                        await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
                        callbackAcknowledged = true;
                        await ShowCurrencySelectionMenu(chatId, messageId, cancellationToken);
                    }
                }
                else if (action == RefreshMarketDataCallback)
                {
                    if (!string.IsNullOrEmpty(payload))
                    {
                        _logger.LogInformation("Action: RefreshMarketDataCallback for {Symbol}. ChatID:{ChatId}", payload, chatId);
                        // ShowMarketAnalysis will handle its own loading message and final ack
                        await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, "Refreshing...", cancellationToken: cancellationToken);
                        callbackAcknowledged = true; // Consider it acknowledged for now.
                        await ShowMarketAnalysis(chatId, messageId, payload, isRefresh: true, callbackQuery.Id, cancellationToken);
                    }
                    else
                    {
                        _logger.LogWarning("RefreshMarketDataCallback missing symbol payload. CBQID:{CBQID}", callbackQuery.Id);
                        await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, "Invalid refresh request.", showAlert: true, cancellationToken: cancellationToken);
                        callbackAcknowledged = true;
                    }
                }
                // else if (action == ViewTechnicalsCallback) { /* ... */ }
                else
                {
                    _logger.LogWarning("Unhandled callback action: {Action} with payload {Payload}. CBQID:{CBQID}", action, payload, callbackQuery.Id);
                    if (!callbackAcknowledged) // Only answer if no other branch did
                    {
                        await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, "Action not recognized.", cancellationToken: cancellationToken);
                    }
                }
            }
            catch (ApiRequestException apiEx) when (apiEx.Message.Contains("message is not modified"))
            {
                // This catch block is important here if ShowMarketAnalysis's "message not modified" exception bubbles up
                // and we haven't answered the callback query from a *refresh* action yet.
                _logger.LogInformation("HandleAsync: Message not modified. CBQID: {CBQID}. This might be from a refresh with no new data.", callbackQuery.Id);
                // If the original action was a refresh, this is where the "no new data" ack should ideally happen IF ShowMarketAnalysis didn't handle it.
                // However, ShowMarketAnalysis was modified to handle this specific ack, so this catch here might be redundant for that case.
                // For safety, ensure callbacks are always answered.
                try { await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, "Data is up to date.", showAlert: false, cancellationToken: CancellationToken.None); }
                catch { /* Already answered or error */ }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in HandleAsync for market analysis callback. Data: {CallbackData}, CBQID:{CBQID}", callbackData, callbackQuery.Id);
                try
                {
                    await _messageSender.AnswerCallbackQueryAsync(callbackQuery.Id, "An error occurred.", showAlert: true, cancellationToken: CancellationToken.None);
                }
                catch (Exception ackEx)
                {
                    _logger.LogError(ackEx, "Failed to ack callback query after error in HandleAsync. CBQID:{CBQID}", callbackQuery.Id);
                }
                // Optionally, edit the message to provide a "start over" option
                try
                {
                    var startOverKeyboard = new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("🔄 Start Over", MarketAnalysisCallback)); // Use the main menu callback
                    await _messageSender.EditMessageTextAsync(
                        chatId,
                        messageId,
                        "❌ An unexpected error occurred. Please try starting over.",
                        replyMarkup: startOverKeyboard,
                        cancellationToken: CancellationToken.None);
                }
                catch (Exception editEx)
                {
                    _logger.LogError(editEx, "Failed to edit message with generic error and start over. ChatID:{ChatId}, MsgID:{MsgId}", chatId, messageId);
                }

            }

        }


        // File: TelegramPanel/Application/CommandHandlers/MarketAnalysisCallbackHandler.cs
        // ...
        private async Task ShowCurrencySelectionMenu(long chatId, int messageId, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Attempting to show currency selection menu for ChatID {ChatId}, MessageID {MessageId}.", chatId, messageId);

            try
            {
                // Data source validation (basic check if SupportedSymbols could be null/empty unexpectedly)
                if (SupportedSymbols == null || !SupportedSymbols.Any())
                {
                    _logger.LogError("SupportedSymbols list is null or empty. Cannot build currency selection menu.");
                    // Optionally inform the user about this internal configuration error.
                    // try { await _messageSender.EditMessageTextAsync(chatId, messageId, "Configuration error: Currency list is unavailable.", cancellationToken: cancellationToken); } catch { }
                    return; // Exit if the data source is invalid
                }

                // Build button rows using LINQ (operations are generally safe)
                // 3 columns per row
                var buttonRowsArray = SupportedSymbols
                    .Select((pair, i) => new { pair, i })
                    .GroupBy(x => x.i / 3) // Grouping for rows
                    .Select(group => group.Select(item => // Each group is a row
                        InlineKeyboardButton.WithCallbackData(item.pair.Label, $"{SelectCurrencyCallback}:{item.pair.Symbol}"))
                        .ToArray()) // Convert each row group to an array of buttons
                    .ToArray(); // Convert all rows to an array of arrays (InlineKeyboardButton[][])


                // Add the "Back to Main Menu" button row
                var backButtonRow = new[] // This is a single row array of buttons
                {
                InlineKeyboardButton.WithCallbackData("⬅️ Back to Main Menu", MenuCallbackQueryHandler.BackToMainMenuGeneral)
            };

                // Concatenate currency button rows with the back button row
                var allButtonRowsArray = buttonRowsArray.Concat(new[] { backButtonRow }).ToArray();


                // Use MarkupBuilder to create the final keyboard. Assumed safe.
                var keyboard = MarkupBuilder.CreateInlineKeyboard(allButtonRowsArray);
                _logger.LogTrace("Currency selection keyboard built with {RowCount} rows.", allButtonRowsArray.Length);


                // Edit the message to show the currency selection menu. **Potential Telegram API call failure.**
                await _messageSender.EditMessageTextAsync(
                    chatId,
                    messageId,
                    "💱 *Select a Forex Pair for Analysis:*\n\nChoose from the most popular currency pairs:",
                    ParseMode.MarkdownV2, // Using MarkdownV2 consistently. Ensure text is escaped.
                    keyboard, // Pass the built keyboard
                    cancellationToken); // Pass CancellationToken
                _logger.LogInformation("Currency selection menu sent to ChatID {ChatId}, MessageID {MessageId}.", chatId, messageId);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                // Handle cancellation specifically.
                _logger.LogInformation(ex, "Showing currency selection menu was cancelled for ChatID {ChatId}, MessageID {MessageId}.", chatId, messageId);
                // Optionally inform user about cancellation if needed, but often not necessary for UI updates.
            }
            // Catch specific exceptions from MarkupBuilder or SupportedSymbols access if needed, though unlikely for standard operations.
            // catch (Exception exBuilder) { ... log builder error ... }
            catch (Exception ex)
            {
                // Catch any other unexpected technical exceptions (e.g., Telegram API errors).
                // Log the critical technical error.
                _logger.LogError(ex, "An unexpected error occurred while showing currency selection menu for ChatID {ChatId}, MessageID {MessageId}.", chatId, messageId);

                // Inform the user about the unexpected error by editing the message.
                // This EditMessageTextAsync might also fail.
                try
                {
                    await _messageSender.EditMessageTextAsync(
                        chatId,
                        messageId,
                        "An error occurred while loading the currency list. Please try again later. 😢",
                        cancellationToken: cancellationToken);
                }
                catch (Exception sendErrorEx)
                {
                    // Log if sending the error message also fails.
                    _logger.LogError(sendErrorEx, "Failed to send fallback error message to ChatID {ChatId} after currency menu failure.", chatId);
                }
                // Note: If this was triggered by a CallbackQuery, the AnswerCallbackQueryAsync should have been
                // handled in the calling handler method to dismiss the spinner.
            }
        }

        // --- CHANGE START: Replace the entire ShowMarketAnalysis method ---
        // --- REWRITE START ---
        private async Task ShowMarketAnalysis(long chatId, int messageId, string symbol, bool isRefresh, string callbackQueryId, CancellationToken cancellationToken)
        {
            string loadingMessageBase = isRefresh
                ? $"🔄 _Refreshing data for {symbol}_"
                : $"📊 _Fetching analysis for {symbol}_";

            (MarketData? Data, Exception? Error) result;
            try
            {
                // Execute the operation with the animation and await its combined result.
                var marketData = await AnimateWhileExecutingAsync(
                    chatId,
                    messageId,
                    loadingMessageBase,
                    ct => _marketDataService.GetMarketDataAsync(symbol, forceRefresh: isRefresh, cancellationToken: ct),
                    cancellationToken
                );
                result = (marketData, null);
            }
            catch (Exception serviceEx)
            {
                // This captures any failure from the data service or the animation logic.
                result = (null, serviceEx);
            }

            // Now, delegate to a specific handler based on the result.
            if (result.Error is not null)
            {
                await HandleServiceErrorAsync(chatId, messageId, symbol, callbackQueryId, result.Error, cancellationToken);
            }
            else if (result.Data is null || !result.Data.IsPriceLive && result.Data.DataSource == "Unavailable")
            {
                await HandleDataUnavailableAsync(chatId, messageId, symbol, callbackQueryId, result.Data, cancellationToken);
            }
            else
            {
                await HandleSuccessAsync(chatId, messageId, symbol, isRefresh, callbackQueryId, result.Data, cancellationToken);
            }
        }

        private async Task HandleSuccessAsync(long chatId, int messageId, string symbol, bool isRefresh, string callbackQueryId, MarketData marketData, CancellationToken cancellationToken)
        {
            var newMessageText = FormatMarketAnalysisMessage(marketData);
            var newKeyboard = GetMarketAnalysisKeyboard(symbol);

            // Create the task to edit the message. DO NOT await it yet.
            var editMessageTask = _messageSender.EditMessageTextAsync(
                chatId,
                messageId,
                newMessageText,
                ParseMode.Markdown,
                newKeyboard,
                cancellationToken);

            try
            {
                // For refresh actions, we also want to send a non-blocking "up-to-date" toast.
                if (isRefresh)
                {
                    var ackTask = _messageSender.AnswerCallbackQueryAsync(callbackQueryId, "Data refreshed!", showAlert: false);
                    // Execute both the message edit and the acknowledgement IN PARALLEL.
                    await Task.WhenAll(editMessageTask, ackTask);
                }
                else
                {
                    // If it's not a refresh, just perform the message edit.
                    await editMessageTask;
                }
            }
            catch (ApiRequestException apiEx) when (apiEx.Message.Contains("message is not modified"))
            {
                _logger.LogInformation("Message for {Symbol} not modified (data likely unchanged).", symbol);
                // If the content was the same and it was a refresh action, tell the user it's up to date.
                // This is a "fire-and-forget" call as the user's primary interaction is complete.
                if (isRefresh)
                {
                    await _messageSender.AnswerCallbackQueryAsync(callbackQueryId, "Data is already up to date.", showAlert: false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error rendering final successful analysis for {Symbol}", symbol);
                // If the final render fails, escalate to the main error handler logic.
                await HandleServiceErrorAsync(chatId, messageId, symbol, callbackQueryId, ex, cancellationToken);
            }
        }



        /// <summary>
        /// Handles and reports the "data unavailable" state to the user by editing the message
        /// and showing a pop-up alert. Logs the unavailability.
        /// This method is designed to be resilient against Telegram API errors
        /// while attempting to report the data status.
        /// </summary>
        /// <param name="chatId">The chat ID where the message should be edited.</param>
        /// <param name="messageId">The ID of the message to edit.</param>
        /// <param name="symbol">The symbol for which data is unavailable.</param>
        /// <param name="callbackQueryId">The ID of the callback query to acknowledge (for pop-up).</param>
        /// <param name="marketData">The market data object (might be null or incomplete, for logging context).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A Task representing the asynchronous operation (completion of reporting attempts).</returns>
        public async Task HandleDataUnavailableAsync(long chatId, int messageId, string symbol, string callbackQueryId, MarketData? marketData, CancellationToken cancellationToken)
        {
            // Log the data unavailability status
            _logger.LogWarning("Data unavailable for {Symbol}. IsLive:{IsLive}, Source:{Source} for ChatID {ChatId}, MessageID {MessageId}.",
                symbol, marketData?.IsPriceLive, marketData?.DataSource, chatId, messageId);

            // Prepare the status message and keyboard (these operations are safe and don't need try-catch)
            string errorText = $"⚠️ Live market data for *{TelegramMessageFormatter.EscapeMarkdownV2(symbol)}* is currently unavailable.\n\n" + // Escape symbol for MarkdownV2
                      $"However, you can fetch the latest fundamental news for this pair using the button below.";
            var errorKeyboard = GetMarketAnalysisKeyboard(symbol); // Assumes this method is safe

            // Use a list of Tasks to collect reporting attempts
            var reportingTasks = new List<Task>();

            // --- Attempt 1: Edit the message to show the "unavailable" state ---
            reportingTasks.Add(Task.Run(async () => // Use Task.Run to make this attempt independent
            {
                try
                {
                    await _messageSender.EditMessageTextAsync(chatId, messageId, errorText, ParseMode.MarkdownV2, errorKeyboard, cancellationToken); // Use MarkdownV2 consistently
                    _logger.LogDebug("Successfully sent 'data unavailable' message by editing message {MessageId} in chat {ChatId}.", messageId, chatId);
                }
                catch (OperationCanceledException) { throw; } // Re-throw if cancellation was requested
                catch (Exception editEx)
                {
                    // Log the failure to edit the message (this is a secondary error)
                    _logger.LogError(editEx, "Failed to edit message {MessageId} in chat {ChatId} to report data unavailability for {Symbol}.", messageId, chatId, symbol);
                    // Do NOT re-throw here
                }
            }, CancellationToken.None)); // Use CancellationToken.None for independence, or pass cancellationToken

            // --- Attempt 2: Send a pop-up alert to the user ---
            reportingTasks.Add(Task.Run(async () => // Use Task.Run to make this attempt independent
            {
                try
                {
                    // Use the original callbackQueryId passed to this method.
                    await _messageSender.AnswerCallbackQueryAsync(callbackQueryId, "Data for this pair is currently unavailable.", showAlert: true, cancellationToken: cancellationToken);
                    _logger.LogDebug("Successfully sent 'data unavailable' pop-up alert for callback {CallbackId}.", callbackQueryId);
                }
                catch (OperationCanceledException) { throw; } // Re-throw if cancellation was requested
                catch (Exception ackEx)
                {
                    // Log the failure to send the pop-up alert
                    _logger.LogError(ackEx, "Failed to send pop-up alert for callback {CallbackId} to report data unavailability for {Symbol}.", callbackQueryId, symbol);
                    // Do NOT re-throw here
                }
            }, CancellationToken.None)); // Use CancellationToken.None for independence, or pass cancellationToken


            // Execute reporting tasks in parallel.
            // We await Task.WhenAll to ensure all attempts finish (either successfully or with logged errors).
            // We wrap Task.WhenAll in a try-catch in case *it* throws (e.g., due to cancellation propagation).
            try
            {
                await Task.WhenAll(reportingTasks);
            }
            catch (OperationCanceledException)
            {
                // If cancellation occurred within the tasks, just log and exit.
                _logger.LogWarning("'Data unavailable' reporting tasks for {Symbol} were cancelled.", symbol);
                throw; // Re-throw cancellation if it was requested
            }
            catch (Exception exWhileReporting)
            {
                // This catch block is for errors within Task.WhenAll itself.
                _logger.LogError(exWhileReporting, "An error occurred within the 'data unavailable' reporting tasks for {Symbol}. Some reporting might have failed.", symbol);
                // Do NOT re-throw. This method's job is done after attempting to report.
            }

            // Return Task.CompletedTask as this method is complete after attempts to report.
            // (Or just 'return;' if the return type was Task).
        }

        /// <summary>
        /// Handles and reports a service error to the user by editing the message
        /// and showing a pop-up alert. Logs the original error.
        /// This method is designed to be resilient against Telegram API errors
        /// while attempting to report the primary service error.
        /// </summary>
        /// <param name="chatId">The chat ID where the message should be edited.</param>
        /// <param name="messageId">The ID of the message to edit.</param>
        /// <param name="symbol">The symbol related to the failed operation (for user message).</param>
        /// <param name="callbackQueryId">The ID of the callback query to acknowledge (for pop-up).</param>
        /// <param name="ex">The exception that occurred in the primary service operation.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A Task representing the asynchronous operation (completion of reporting attempts).</returns>
        public async Task HandleServiceErrorAsync(long chatId, int messageId, string symbol, string callbackQueryId, Exception ex, CancellationToken cancellationToken)
        {
            // Log the original service error immediately
            _logger.LogError(ex, "A service error occurred while fetching data for {Symbol} for ChatID {ChatId}, MessageID {MessageId}.", symbol, chatId, messageId);

            // Prepare the error message and keyboard (these operations are safe and don't need try-catch)
            string errorText = $"❌ An unexpected error occurred while fetching data for *{TelegramMessageFormatter.EscapeMarkdownV2(symbol)}*."; // Escape symbol for MarkdownV2
            var errorKeyboard = GetMarketAnalysisKeyboard(symbol); // Assumes this method is safe and purely building UI markup

            // Use a list of Tasks to collect reporting attempts
            var reportingTasks = new List<Task>();

            // --- Attempt 1: Edit the message to show the error state ---
            reportingTasks.Add(Task.Run(async () => // Use Task.Run to make this attempt independent and handle its own error
            {
                try
                {
                    await _messageSender.EditMessageTextAsync(chatId, messageId, errorText, ParseMode.MarkdownV2, errorKeyboard, cancellationToken); // Use MarkdownV2 consistently
                    _logger.LogDebug("Successfully sent error message by editing message {MessageId} in chat {ChatId}.", messageId, chatId);
                }
                catch (OperationCanceledException) { throw; } // Re-throw if cancellation was requested
                catch (Exception editEx)
                {
                    // Log the failure to edit the message (this is a secondary error)
                    _logger.LogError(editEx, "Failed to edit message {MessageId} in chat {ChatId} to report service error for {Symbol}.", messageId, chatId, symbol);
                    // Do NOT re-throw here, we want the other reporting attempts to proceed
                }
            }, CancellationToken.None)); // Use CancellationToken.None so this task runs even if the main method's token is cancelled immediately (unless explicit cancellation is handled internally)
                                         // Consider passing the original cancellationToken if editing should be cancelable.


            // --- Attempt 2: Send a prominent pop-up alert to the user ---
            reportingTasks.Add(Task.Run(async () => // Use Task.Run to make this attempt independent
            {
                try
                {
                    // Use the original callbackQueryId passed to this method.
                    // AnswerCallbackQueryAsync is often fire-and-forget or awaited briefly in the main handler,
                    // but ensuring its completion here means the alert *attempt* finishes.
                    await _messageSender.AnswerCallbackQueryAsync(callbackQueryId, "An error occurred. Please try again.", showAlert: true, cancellationToken: cancellationToken);
                    _logger.LogDebug("Successfully sent pop-up error alert for callback {CallbackId}.", callbackQueryId);
                }
                catch (OperationCanceledException) { throw; } // Re-throw if cancellation was requested
                catch (Exception ackEx)
                {
                    // Log the failure to send the pop-up alert
                    _logger.LogError(ackEx, "Failed to send pop-up alert for callback {CallbackId} to report service error for {Symbol}.", callbackQueryId, symbol);
                    // Do NOT re-throw here
                }
            }, CancellationToken.None)); // Use CancellationToken.None for independence, or pass cancellationToken


            // Execute reporting tasks in parallel.
            // We await Task.WhenAll to ensure all attempts finish (either successfully or with logged errors).
            // We wrap Task.WhenAll in a try-catch in case *it* throws (e.g., due to cancellation propagation
            // from the inner Task.Run calls if they re-throw cancellation).
            try
            {
                await Task.WhenAll(reportingTasks);
            }
            catch (OperationCanceledException)
            {
                // If cancellation occurred within the tasks, just log and exit.
                _logger.LogWarning("Service error reporting tasks for {Symbol} were cancelled.", symbol);
                throw; // Re-throw cancellation if it was requested
            }
            catch (Exception exWhileReporting)
            {
                // This catch block is for errors that happened within Task.WhenAll itself,
                // or possibly re-thrown exceptions from the inner tasks if not caught there.
                // Given the inner tasks catch and log, this outer catch is mostly for robustness
                // or if inner tasks re-throw non-cancellation exceptions.
                _logger.LogError(exWhileReporting, "An error occurred within the error reporting tasks for {Symbol}. Some reporting might have failed.", symbol);
                // Do NOT re-throw exWhileReporting if you want the primary service error to be the main focus.
                // This method's primary goal is to log the original error and attempt reporting.
            }

            // The original service error (ex) has already been logged at the start of the method.
            // We do NOT re-throw 'ex' here, as this method's job is to handle the *reporting* of 'ex', not propagate 'ex'.
            // The caller of HandleServiceErrorAsync should have already caught the original 'ex'.

            // Return Task.CompletedTask as this method is complete after attempts to report.
            // (Or just 'return;' if the return type was Task).
        }



        private string FormatMarketAnalysisMessage(MarketData data)
        {
            var priceChangeEmoji = data.Change24h >= 0 ? "📈" : "📉";
            var trendEmoji = data.Trend switch
            {
                "Strong Uptrend" => "🚀",
                "Strong Downtrend" => "📉",
                "Weak Uptrend" => "↗️",
                "Weak Downtrend" => "↘️",
                _ => "➡️"
            };
            var sentimentEmoji = data.MarketSentiment switch
            {
                "Extremely Bullish" => "🟢🟢",
                "Extremely Bearish" => "🔴🔴",
                "Bullish" => "🟢",
                "Bearish" => "🔴",
                _ => "⚪"
            };

            return $"*{data.CurrencyName} Market Analysis*\n" +
                   $"_{data.Description}_\n\n" +
                   $"*Current Market Status:*\n" +
                   $"💰 Price: `{data.Price:N5}` {priceChangeEmoji}\n" +
                   $"📊 24h Change: `{data.Change24h:N2}%`\n" +
                   $"💎 Volume: `{data.Volume:N0}`\n" +
                   $"📈 Trend: {data.Trend} {trendEmoji}\n" +
                   $"🎯 Market Sentiment: {data.MarketSentiment} {sentimentEmoji}\n\n" +
                   $"*Technical Analysis:*\n" +
                   $"📊 RSI: `{data.RSI:N2}` ({GetRSIInterpretation(data.RSI)})\n" +
                   $"📈 MACD: {data.MACD}\n" +
                   $"🎯 Support: `{data.Support:N5}`\n" +
                   $"🎯 Resistance: `{data.Resistance:N5}`\n" +
                   $"📊 Volatility: `{data.Volatility:N2}%`\n\n" +
                   $"*Market Insights:*\n" +
                   string.Join("\n", data.Insights.Select(i => $"• {i}")) + "\n\n" +
                   $"*Last Updated:* {data.LastUpdated:g} UTC";
        }

        private InlineKeyboardMarkup GetMarketAnalysisKeyboard(string symbol)
        {
            return MarkupBuilder.CreateInlineKeyboard(
        new[] // ردیف اول
        {
            InlineKeyboardButton.WithCallbackData(
                "🔄 Refresh Analysis",
                $"{RefreshMarketDataCallback}:{symbol}")
        },
        new[] // ردیف دوم
        {
            InlineKeyboardButton.WithCallbackData(
                "💱 Change Currency",
                MarketAnalysisCallback),
            InlineKeyboardButton.WithCallbackData(
                "📰 Fundamental News",
                $"{FundamentalAnalysisCallbackHandler.ViewFundamentalAnalysisPrefix}:{symbol}")
        },
        new[] // ردیف سوم
        {
            InlineKeyboardButton.WithCallbackData(
                "🏠 Back to Main Menu",
                MenuCallbackQueryHandler.BackToMainMenuGeneral)
        }
    );
        }

        private string GetRSIInterpretation(decimal rsi)
        {
            return rsi switch
            {
                > 70 => "Overbought",
                < 30 => "Oversold",
                _ => "Neutral"
            };
        }
    }
}