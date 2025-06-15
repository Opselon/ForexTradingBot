// Location: Application/Interfaces/IUserContext.cs
using Domain.Entities;

namespace TelegramPanel.Application.Interfaces
{
    /// <summary>
    /// A scoped service to hold the authenticated user's information for the duration of a single update processing.
    /// </summary>
    public interface IUserContext
    {
        /// <summary>
        /// The authenticated user entity. Will be null if the user is not authenticated.
        /// </summary>
        User? CurrentUser { get; }

        /// <summary>
        /// Sets the authenticated user for the current scope.
        /// </summary>
        void SetCurrentUser(User user);
    }
}