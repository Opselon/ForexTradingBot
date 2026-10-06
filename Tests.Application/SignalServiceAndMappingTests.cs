using Application.Common.Mappings;
using Application.DTOs;
using Application.Services;
using AutoMapper;
using Domain.Entities;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Application.Common.Interfaces;
using Application.Interfaces;
using Microsoft.Extensions.DependencyInjection; // For ServiceCollection / BuildServiceProvider
using Microsoft.Extensions.Logging.Abstractions; // For NullLoggerFactory

namespace Tests.Application;

/// <summary>
/// Real mapping tests: verifies the AutoMapper configuration is valid,
/// every CreateMap maps correctly, and nothing breaks silently at runtime.
/// This catches mapping misconfigurations that only explode when the app runs.
/// </summary>
public class AutoMapperConfigurationTests
{
    private readonly IMapper _mapper;

    public AutoMapperConfigurationTests()
    {
        var config = new MapperConfiguration(
            mc => mc.AddMaps(typeof(MappingProfile).Assembly),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        _mapper = config.CreateMapper();
    }

    [Fact]
    public void Configuration_Is_Valid()
    {
        // Assert: every CreateMap in MappingProfile must have a valid configuration.
        // (AssertConfigurationIsValid throws on unmapped members that are not ignored,
        //  mismatched types, missing source members, etc.)
        var act = () => _mapper.ConfigurationProvider.AssertConfigurationIsValid();
        act.Should().NotThrow("AutoMapper configuration must be valid at startup");
    }

    [Fact]
    public void Signal_Maps_To_SignalDto_With_Type_As_String()
    {
        var category = new SignalCategory { Id = Guid.NewGuid(), Name = "Scalping" };
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "EURUSD",
            Type = Domain.Enums.SignalType.Buy, // enum must become string via mapping
            Category = category,
        };

        var dto = _mapper.Map<SignalDto>(signal);

        dto.Should().NotBeNull();
        dto.Id.Should().Be(signal.Id);
        dto.Symbol.Should().Be("EURUSD");
        dto.Type.Should().Be(signal.Type.ToString(),
            "MappingProfile maps Signal.Type via .ToString()");
        dto.Category.Should().NotBeNull("signal has a category");
        dto.Category!.Name.Should().Be("Scalping");
    }

    [Fact]
    public void Signal_Without_Category_Maps_To_Null_Category_Dto()
    {
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "GBPJPY",
            Type = Domain.Enums.SignalType.Sell,
            Category = null,
        };

        var dto = _mapper.Map<SignalDto>(signal);

        dto.Should().NotBeNull();
        dto.Symbol.Should().Be("GBPJPY");
        dto.Type.Should().Be("Sell");
        dto.Category.Should().BeNull("no category was attached to the entity");
    }
}

/// <summary>
/// Unit tests for SignalService — verifies business behavior against mocked
/// repositories: creation flow, not-found paths, update validation, and null guards.
/// </summary>
public class SignalServiceTests
{
    private readonly Mock<ISignalRepository> _signalRepo = new();
    private readonly Mock<ISignalCategoryRepository> _categoryRepo = new();
    private readonly Mock<IAppDbContext> _dbContext = new();
    private readonly Mock<ILogger<SignalService>> _logger = new();

    private SignalService CreateService()
    {
        var config = new MapperConfiguration(
            mc => mc.AddMaps(typeof(MappingProfile).Assembly),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var mapper = config.CreateMapper();
        return new SignalService(_signalRepo.Object, _categoryRepo.Object, mapper, _dbContext.Object, _logger.Object);
    }

    [Fact]
    public void Constructor_Throws_When_Any_Dependency_Is_Null()
    {
        var config = new MapperConfiguration(
            mc => mc.AddMaps(typeof(MappingProfile).Assembly),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var mapper = config.CreateMapper();

        var act1 = () => new SignalService(null!, _categoryRepo.Object, mapper, _dbContext.Object, _logger.Object);
        var act2 = () => new SignalService(_signalRepo.Object, null!, mapper, _dbContext.Object, _logger.Object);
        var act3 = () => new SignalService(_signalRepo.Object, _categoryRepo.Object, null!, _dbContext.Object, _logger.Object);
        var act4 = () => new SignalService(_signalRepo.Object, _categoryRepo.Object, mapper, null!, _logger.Object);
        var act5 = () => new SignalService(_signalRepo.Object, _categoryRepo.Object, mapper, _dbContext.Object, null!);

        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
        act3.Should().Throw<ArgumentNullException>();
        act4.Should().Throw<ArgumentNullException>();
        act5.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task CreateSignalAsync_Throws_When_Category_Not_Found()
    {
        _categoryRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync((SignalCategory?)null);
        var service = CreateService();
        var dto = new CreateSignalDto { Symbol = "EURUSD", CategoryId = Guid.NewGuid() };

        var act = () => service.CreateSignalAsync(dto);

        (await act.Should().ThrowAsync<Exception>())
            .WithMessage("*not found*");
        _signalRepo.Verify(r => r.AddAsync(It.IsAny<Signal>(), It.IsAny<CancellationToken>()), Times.Never,
            "no signal must be persisted when the category does not exist");
    }

    [Fact]
    public async Task CreateSignalAsync_Persists_Signal_And_Returns_Dto()
    {
        var categoryId = Guid.NewGuid();
        _categoryRepo.Setup(r => r.GetByIdAsync(categoryId, It.IsAny<CancellationToken>()))
                     .ReturnsAsync(new SignalCategory { Id = categoryId, Name = "Scalping" });

        Signal? captured = null;
        _signalRepo.Setup(r => r.AddAsync(It.IsAny<Signal>(), It.IsAny<CancellationToken>()))
                   .Callback<Signal, CancellationToken>((s, _) => captured = s)
                   .Returns(Task.CompletedTask);
        _signalRepo.Setup(r => r.GetByIdWithDetailsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync((Guid id, CancellationToken _) => new Signal
                   {
                       Id = id,
                       Symbol = "EURUSD",
                       Type = Domain.Enums.SignalType.Buy,
                       Category = new SignalCategory { Id = categoryId, Name = "Scalping" },
                   });

        var service = CreateService();
        var dto = new CreateSignalDto { Symbol = "EURUSD", CategoryId = categoryId };

        var result = await service.CreateSignalAsync(dto);

        result.Should().NotBeNull();
        result.Symbol.Should().Be("EURUSD");
        result.Type.Should().Be("Buy");
        result.Category.Should().NotBeNull();
        result.Category!.Name.Should().Be("Scalping");
        captured.Should().NotBeNull("signal must be persisted via repository");
        captured!.Id.Should().NotBe(Guid.Empty, "service assigns a new Guid");
        captured.PublishedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        _dbContext.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetSignalByIdAsync_Returns_Null_When_Not_Found()
    {
        _signalRepo.Setup(r => r.GetByIdWithDetailsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync((Signal?)null);
        var service = CreateService();

        var result = await service.GetSignalByIdAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task UpdateSignalAsync_Throws_When_Signal_Not_Found()
    {
        _signalRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync((Signal?)null);
        var service = CreateService();
        var dto = new UpdateSignalDto();

        var act = () => service.UpdateSignalAsync(Guid.NewGuid(), dto);

        (await act.Should().ThrowAsync<Exception>())
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task UpdateSignalAsync_Throws_When_New_Category_Not_Found()
    {
        var signalId = Guid.NewGuid();
        _signalRepo.Setup(r => r.GetByIdAsync(signalId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new Signal { Id = signalId, Symbol = "EURUSD" });
        var newCategoryId = Guid.NewGuid();
        _categoryRepo.Setup(r => r.GetByIdAsync(newCategoryId, It.IsAny<CancellationToken>()))
                     .ReturnsAsync((SignalCategory?)null);
        var service = CreateService();
        var dto = new UpdateSignalDto { CategoryId = newCategoryId };

        var act = () => service.UpdateSignalAsync(signalId, dto);

        (await act.Should().ThrowAsync<Exception>())
            .WithMessage("*not found*");
        _dbContext.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never,
            "nothing must be saved when the referenced category is missing");
    }
}

/// <summary>
/// Smoke tests over the DI registration: after the AutoMapper 16 migration,
/// the manual IMapper registration must produce a working mapper instance.
/// </summary>
public class DependencyInjectionTests
{
    [Fact]
    public void AddApplicationServices_Registers_IMapper_And_Resolves_It()
    {
        var services = new ServiceCollection();
        services.AddLogging(); // SignalService requires ILogger<T>

        global::Application.DependencyInjection.AddApplicationServices(services);

        var provider = services.BuildServiceProvider();
        var mapper = provider.GetService<IMapper>();
        mapper.Should().NotBeNull("IMapper must be registered by AddApplicationServices");

        // The resolved mapper must actually work: map a simple entity.
        var dto = mapper!.Map<SignalDto>(new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "XAUUSD",
            Type = Domain.Enums.SignalType.Buy,
        });
        dto.Symbol.Should().Be("XAUUSD");
        dto.Type.Should().Be("Buy");
    }
}
