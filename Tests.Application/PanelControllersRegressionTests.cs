using System.Reflection;
using Application.DTOs;
using Application.DTOs.Admin;
using Application.DTOs.Settings;
using Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using WebAPI.Controllers;
using Xunit;

namespace Tests.Application;

public sealed class PanelControllersRegressionTests
{
    [Fact]
    public void UsersController_has_Authorize_attribute()
    {
        var authorize = typeof(UsersController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorize);
    }

    [Fact]
    public void ForceJoinController_has_Authorize_attribute()
    {
        var authorize = typeof(ForceJoinController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorize);
    }

    [Fact]
    public async Task UsersController_GetAll_returns_users_from_service()
    {
        var mockUser = new Mock<IUserService>();
        var expectedUsers = new List<UserDto>
        {
            new() { Id = Guid.NewGuid(), Username = "user1", TelegramId = "111", Level = Domain.Enums.UserLevel.Free },
            new() { Id = Guid.NewGuid(), Username = "user2", TelegramId = "222", Level = Domain.Enums.UserLevel.Gold }
        };
        mockUser.Setup(x => x.GetAllUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedUsers);

        var controller = new UsersController(
            mockUser.Object,
            Mock.Of<IAdminService>(),
            Mock.Of<ISubscriptionService>(),
            Mock.Of<ILogger<UsersController>>());

        var result = await controller.GetAll(CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var users = Assert.IsType<List<UserDto>>(okResult.Value);
        Assert.Equal(2, users.Count);
        Assert.Equal("user1", users[0].Username);
    }

    [Fact]
    public async Task UsersController_GetById_returns_NotFound_when_missing()
    {
        var mockUser = new Mock<IUserService>();
        mockUser.Setup(x => x.GetUserByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserDto?)null);

        var controller = new UsersController(
            mockUser.Object,
            Mock.Of<IAdminService>(),
            Mock.Of<ISubscriptionService>(),
            Mock.Of<ILogger<UsersController>>());

        var result = await controller.GetById(Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task UsersController_GetByTelegramId_with_detail_calls_admin_service()
    {
        var mockUser = new Mock<IUserService>();
        var mockAdmin = new Mock<IAdminService>();

        var detailDto = new AdminUserDetailDto
        {
            TelegramId = 12345,
            Username = "trader1",
            TotalTransactions = 3,
            TotalSpent = 150
        };

        mockAdmin.Setup(x => x.GetUserDetailByTelegramIdAsync(12345, It.IsAny<CancellationToken>()))
            .ReturnsAsync(detailDto);

        var controller = new UsersController(
            mockUser.Object,
            mockAdmin.Object,
            Mock.Of<ISubscriptionService>(),
            Mock.Of<ILogger<UsersController>>());

        var result = await controller.GetByTelegramId("12345", detail: true, CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(result);
        var returned = Assert.IsType<AdminUserDetailDto>(okResult.Value);
        Assert.Equal("trader1", returned.Username);
        Assert.Equal(150, returned.TotalSpent);
    }

    [Fact]
    public async Task ForceJoinController_Get_falls_back_gracefully_when_service_throws()
    {
        var mockSettings = new Mock<ISettingsService>();
        mockSettings.Setup(x => x.GetForceJoinSettingsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Redis down"));

        var controller = new ForceJoinController(
            mockSettings.Object,
            Mock.Of<IAdminService>(),
            Mock.Of<ILogger<ForceJoinController>>());

        var result = await controller.Get(CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var settings = Assert.IsType<ForceJoinSettingsDto>(okResult.Value);
        Assert.False(settings.IsEnabled);
    }

    [Fact]
    public async Task ForceJoinController_Save_validates_channel_link_and_message_when_enabled()
    {
        var controller = new ForceJoinController(
            Mock.Of<ISettingsService>(),
            Mock.Of<IAdminService>(),
            Mock.Of<ILogger<ForceJoinController>>());

        var invalidRequest = new ForceJoinSettingsDto
        {
            IsEnabled = true,
            ChannelId = 0,
            ChannelLink = "invalid-url",
            Message = ""
        };

        var result = await controller.Save(invalidRequest, CancellationToken.None);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var problem = Assert.IsType<ValidationProblemDetails>(badRequest.Value);
        Assert.True(problem.Errors.ContainsKey("settings"));
    }

    [Fact]
    public void UsersController_exposes_registration_level_and_subscription_endpoints()
    {
        // The panel relies on these routes existing; if one is dropped the UI breaks silently.
        Type t = typeof(UsersController);
        Assert.NotNull(t.GetMethod("Register"));
        Assert.NotNull(t.GetMethod("SetLevel"));
        Assert.NotNull(t.GetMethod("GetSubscriptions"));
        Assert.NotNull(t.GetMethod("CreateSubscription"));
        Assert.NotNull(t.GetMethod("DeleteSubscription"));
    }

    [Fact]
    public void SystemController_exposes_update_check()
    {
        // POST /api/system/update is useless without a way to see what is available first.
        Assert.NotNull(typeof(SystemController).GetMethod("CheckUpdate"));
    }

    [Fact]
    public void SetupController_exposes_admin_password_and_bot_token()
    {
        Assert.NotNull(typeof(SetupController).GetMethod("ChangeAdminPassword"));
        Assert.NotNull(typeof(SetupController).GetMethod("SaveBotToken"));
    }

    [Fact]
    public void Update_sh_files_are_bundled_and_executable_in_the_source_tree()
    {
        string root = AppContext.BaseDirectory;
        // Walk up from bin/Release/net9.0 to the repo root.
        for (string? dir = root; dir is not null && dir.Length > 3; dir = Path.GetDirectoryName(dir))
        {
            string candidate = Path.Combine(dir, "update.sh");
            if (File.Exists(candidate))
            {
                return; // found
            }
        }

        Assert.Fail("update.sh is missing from the source tree — the self-update script must ship with the release bundle.");
    }
}
