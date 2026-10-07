using Application.Common.Interfaces;
using Application.DTOs;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using AutoMapper;
using Microsoft.Extensions.Logging;
using Moq;

namespace Tests.Application;

public sealed class SubscriptionServiceRegressionTests
{
    [Fact]
    public async Task Create_rejects_null_dto_without_touching_dependencies()
    {
        var (service, repository, _, context) = CreateService();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.CreateSubscriptionAsync(null!));

        repository.Verify(
            x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
        context.Verify(
            x => x.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Create_rejects_missing_user_without_writing_or_saving()
    {
        var (service, repository, userRepository, context) = CreateService();
        var userId = Guid.NewGuid();

        userRepository
            .Setup(x => x.GetByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        var dto = new CreateSubscriptionDto
        {
            UserId = userId,
            StartDate = DateTime.UtcNow,
            EndDate = DateTime.UtcNow.AddDays(30)
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateSubscriptionAsync(dto));

        Assert.Contains(userId.ToString(), ex.Message);
        repository.Verify(
            x => x.AddAsync(It.IsAny<Subscription>(), It.IsAny<CancellationToken>()),
            Times.Never);
        context.Verify(
            x => x.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Create_assigns_new_identity_and_timestamp_and_saves_once()
    {
        var (service, repository, userRepository, context, mapper) = CreateServiceWithMapper();
        var userId = Guid.NewGuid();
        var start = DateTime.UtcNow.AddMinutes(1);
        var end = start.AddDays(30);
        var captured = new List<Subscription>();

        userRepository
            .Setup(x => x.GetByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Id = userId });

        repository
            .Setup(x => x.AddAsync(It.IsAny<Subscription>(), It.IsAny<CancellationToken>()))
            .Callback<Subscription, CancellationToken>((subscription, _) => captured.Add(subscription))
            .Returns(Task.CompletedTask);

        context
            .Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var returned = new SubscriptionDto
        {
            UserId = userId,
            StartDate = start,
            EndDate = end
        };

        mapper.Setup(x => x.Map<Subscription>(It.IsAny<CreateSubscriptionDto>()))
            .Returns((CreateSubscriptionDto input) => new Subscription
            {
                UserId = input.UserId,
                StartDate = input.StartDate,
                EndDate = input.EndDate
            });

        mapper.Setup(x => x.Map<SubscriptionDto>(It.IsAny<Subscription>()))
            .Returns(returned);

        var before = DateTime.UtcNow;
        var result = await service.CreateSubscriptionAsync(new CreateSubscriptionDto
        {
            UserId = userId,
            StartDate = start,
            EndDate = end
        });
        var after = DateTime.UtcNow;

        var created = Assert.Single(captured);
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(userId, created.UserId);
        Assert.Equal(start, created.StartDate);
        Assert.Equal(end, created.EndDate);
        Assert.InRange(created.CreatedAt, before, after);
        Assert.Same(returned, result);

        repository.Verify(
            x => x.AddAsync(It.IsAny<Subscription>(), It.IsAny<CancellationToken>()),
            Times.Once);
        context.Verify(
            x => x.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Create_wraps_unexpected_repository_failure_and_does_not_report_success()
    {
        var (service, repository, userRepository, context) = CreateService();
        var userId = Guid.NewGuid();
        userRepository
            .Setup(x => x.GetByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidDataException("database exploded"));

        var dto = new CreateSubscriptionDto
        {
            UserId = userId,
            StartDate = DateTime.UtcNow,
            EndDate = DateTime.UtcNow.AddDays(7)
        };

        var ex = await Assert.ThrowsAsync<ApplicationException>(
            () => service.CreateSubscriptionAsync(dto));

        Assert.Contains("unexpected error", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<InvalidDataException>(ex.InnerException);
        context.Verify(
            x => x.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Create_preserves_cancellation_from_the_repository()
    {
        var (service, repository, userRepository, _) = CreateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        userRepository
            .Setup(x => x.GetByIdAsync(It.IsAny<Guid>(), cts.Token))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var dto = new CreateSubscriptionDto
        {
            UserId = Guid.NewGuid(),
            StartDate = DateTime.UtcNow,
            EndDate = DateTime.UtcNow.AddDays(7)
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CreateSubscriptionAsync(dto, cts.Token));
    }

    [Fact]
    public async Task Get_active_returns_null_without_mapping_when_repository_has_no_match()
    {
        var (service, repository, _, _, mapper) = CreateServiceWithMapper();
        var userId = Guid.NewGuid();

        repository
            .Setup(x => x.GetActiveSubscriptionByUserIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Subscription?)null);

        var result = await service.GetActiveSubscriptionByUserIdAsync(userId);

        Assert.Null(result);
        mapper.Verify(x => x.Map<SubscriptionDto>(It.IsAny<Subscription>()), Times.Never);
    }

    [Fact]
    public async Task Get_active_maps_the_repository_entity()
    {
        var (service, repository, _, _, mapper) = CreateServiceWithMapper();
        var userId = Guid.NewGuid();
        var entity = new Subscription { UserId = userId };
        var dto = new SubscriptionDto { UserId = userId };

        repository
            .Setup(x => x.GetActiveSubscriptionByUserIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);
        mapper
            .Setup(x => x.Map<SubscriptionDto>(entity))
            .Returns(dto);

        var result = await service.GetActiveSubscriptionByUserIdAsync(userId);

        Assert.Same(dto, result);
    }

    [Fact]
    public async Task Get_all_returns_an_empty_sequence_when_repository_returns_empty()
    {
        var (service, repository, _, _, mapper) = CreateServiceWithMapper();
        var userId = Guid.NewGuid();

        repository
            .Setup(x => x.GetSubscriptionsByUserIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Subscription>());
        mapper
            .Setup(x => x.Map<IEnumerable<SubscriptionDto>>(It.IsAny<IEnumerable<Subscription>>()))
            .Returns(Array.Empty<SubscriptionDto>());

        var result = await service.GetUserSubscriptionsAsync(userId);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Get_by_id_returns_null_when_repository_has_no_match()
    {
        var (service, repository, _, _, mapper) = CreateServiceWithMapper();
        var subscriptionId = Guid.NewGuid();

        repository
            .Setup(x => x.GetByIdAsync(subscriptionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Subscription?)null);

        var result = await service.GetSubscriptionByIdAsync(subscriptionId);

        Assert.Null(result);
        mapper.Verify(x => x.Map<SubscriptionDto>(It.IsAny<Subscription>()), Times.Never);
    }

    [Fact]
    public void Constructor_rejects_null_dependencies()
    {
        var repository = Mock.Of<ISubscriptionRepository>();
        var userRepository = Mock.Of<IUserRepository>();
        var mapper = Mock.Of<IMapper>();
        var context = Mock.Of<IAppDbContext>();
        var logger = Mock.Of<ILogger<SubscriptionService>>();

        Assert.Throws<ArgumentNullException>(() =>
            new SubscriptionService(null!, userRepository, mapper, context, logger));
        Assert.Throws<ArgumentNullException>(() =>
            new SubscriptionService(repository, null!, mapper, context, logger));
        Assert.Throws<ArgumentNullException>(() =>
            new SubscriptionService(repository, userRepository, null!, context, logger));
        Assert.Throws<ArgumentNullException>(() =>
            new SubscriptionService(repository, userRepository, mapper, null!, logger));
        Assert.Throws<ArgumentNullException>(() =>
            new SubscriptionService(repository, userRepository, mapper, context, null!));
    }

    private static (
        SubscriptionService service,
        Mock<ISubscriptionRepository> repository,
        Mock<IUserRepository> userRepository,
        Mock<IAppDbContext> context)
        CreateService()
    {
        var mapper = new Mock<IMapper>();
        var result = CreateServiceWithMapper(mapper);
        return (result.service, result.repository, result.userRepository, result.context);
    }

    private static (
        SubscriptionService service,
        Mock<ISubscriptionRepository> repository,
        Mock<IUserRepository> userRepository,
        Mock<IAppDbContext> context,
        Mock<IMapper> mapper)
        CreateServiceWithMapper(Mock<IMapper>? mapper = null)
    {
        var repository = new Mock<ISubscriptionRepository>();
        var userRepository = new Mock<IUserRepository>();
        var context = new Mock<IAppDbContext>();
        var actualMapper = mapper ?? new Mock<IMapper>();
        var logger = Mock.Of<ILogger<SubscriptionService>>();

        var service = new SubscriptionService(
            repository.Object,
            userRepository.Object,
            actualMapper.Object,
            context.Object,
            logger);

        return (service, repository, userRepository, context, actualMapper);
    }
}
