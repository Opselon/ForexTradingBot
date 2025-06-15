using Domain.Entities;
using TelegramPanel.Application.Interfaces;

namespace TelegramPanel.Infrastructure.Services
{
    public class UserContext : IUserContext
    {
        public User? CurrentUser { get; private set; }

        public void SetCurrentUser(User user)
        {
            CurrentUser = user;
        }
    }
}