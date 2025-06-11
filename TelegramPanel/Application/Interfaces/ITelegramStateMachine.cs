// FILE TO EDIT: TelegramPanel/Application/Interfaces/ITelegramStateMachine.cs

using Telegram.Bot.Types;

namespace TelegramPanel.Application.Interfaces
{
    /// <summary>
    /// Defines a contract for managing user conversation states using a name-based system.
    /// This design is clean, SOLID, and supports dependency injection of individual state logic.
    /// </summary>
    public interface ITelegramStateMachine
    {
        /// <summary>
        /// Transitions a user to a new state identified by its unique string name.
        /// This is the primary method for changing states.
        /// </summary>
        Task SetStateAsync(long userId, string? stateName, Update? triggerUpdate = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the current state's implementation for a user, allowing direct interaction with the state logic.
        /// </summary>
        Task<ITelegramState?> GetCurrentStateAsync(long userId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Processes an incoming update using the user's currently active state.
        /// </summary>
        Task ProcessUpdateInCurrentStateAsync(long userId, Update update, CancellationToken cancellationToken = default);

        /// <summary>
        /// Clears any active conversation state for a user.
        /// </summary>
        Task ClearStateAsync(long userId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a state implementation by its unique name.
        /// </summary>
        /// <param name="stateName">The name of the state to retrieve.</param>
        /// <returns>The ITelegramState implementation or null if not found.</returns>
        ITelegramState? GetState(string stateName); // <<< NEW METHOD
    }
}