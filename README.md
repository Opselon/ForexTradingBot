# ForexSignalBot 🤖📈

## Your Intelligent Assistant for Forex & Crypto Trading Signals and News!

[![.NET](https://img.shields.io/badge/.NET-9.0-blueviolet)](https://dotnet.microsoft.com/download/dotnet/9.0)
[![EF Core](https://img.shields.io/badge/EF%20Core-9.0-blue)](https://docs.microsoft.com/en-us/ef/core/)
[![Telegram.Bot](https://img.shields.io/badge/Telegram.Bot-19.0-blue.svg)](https://github.com/TelegramBots/Telegram.Bot)
[![Hangfire](https://img.shields.io/badge/Hangfire-Powered-orange)](https://www.hangfire.io/)
[![Docker](https://img.shields.io/badge/Docker-Ready-blue)](https://www.docker.com/)
[![Clean Architecture](https://img.shields.io/badge/Architecture-Clean%20%26%20DDD-lightgrey)](https://blog.cleancoder.com/uncle-bob/2012/08/13/the-clean-architecture.html)

---

**ForexSignalBot** is a comprehensive and powerful Telegram bot designed to provide users with timely Forex and cryptocurrency trading signals, market news, and advanced analytical insights. Built with a robust Clean Architecture and leveraging the latest .NET 9 technologies, this bot aims to be a reliable, scalable, and feature-rich platform for traders of all levels.

## ✨ Key Features

*   **Real-time Signal Delivery**: Get instant notifications for potential buy/sell signals in Forex and Crypto markets.
*   **Aggregated News Feeds**: Stay updated with the latest financial news from various configurable RSS sources.
*   **User-Specific Preferences**:
    *   **Category Subscription**: Users can subscribe/unsubscribe to specific news or signal categories (e.g., "Forex Majors", "Gold", "BTC Signals").
    *   **Notification Settings**: Fine-grained control over what notifications users receive (general, VIP, RSS news).
    *   **Language Preferences**: Multi-language support for a global user base (English by default, easily extendable).
*   **Subscription Tiers**:
    *   **Free Plan**: Access to basic features and public signals/news.
    *   **Premium Plans (VIP)**: Unlock exclusive VIP signals, advanced analytics, and priority notifications.
*   **Cryptocurrency Payments**: Secure and seamless subscription payments using **Crypto Pay API** (@CryptoBot / @CryptoTestnetBot).
*   **AI-Powered Insights (Future)**: Planned integration for sentiment analysis of news and potentially AI-generated signal insights.
*   **Interactive Telegram UI**:
    *   User-friendly command-based interaction (`/start`, `/menu`, `/settings`, `/signals`, `/subscribe`, `/profile`, `/help`).
    *   Dynamic Inline Keyboards for easy navigation and configuration.
    *   Professionally formatted messages with MarkdownV2, emojis, and clear information.
*   **Robust Background Processing**: Utilizes **Hangfire** for:
    *   Periodic fetching of RSS feeds.
    *   Asynchronous dispatch and sending of mass notifications to users, respecting Telegram API rate limits.
    *   Other background tasks like subscription management and data cleanup.
*   **User Management**: Secure registration and profile management.
*   **Admin Panel (via API - Future)**: Endpoints for managing users, RSS sources, signals, and system settings.

##   (Architecture)

This project strictly adheres to the principles of **Clean Architecture** and incorporates elements of **Domain-Driven Design (DDD)** to ensure maintainability, testability, and scalability.


*   **`Domain`**: The core of the application, containing business entities, enums, and value objects. It has no dependencies on other layers.
*   **`Application`**: Contains the application-specific business logic (use cases, implemented as Commands/Queries with MediatR), DTOs, interfaces for services and repositories, and mappers (AutoMapper). Depends only on `Domain` (and potentially a `Shared` library).
*   **`Infrastructure`**: Implements interfaces defined in the `Application` layer. Handles data persistence (EF Core, Repositories), interaction with external services (CryptoPay API, RSS feed fetching), and background job scheduling (Hangfire). Depends on `Application` and `Domain`.
*   **`TelegramPanel`**: A presentation layer specifically for Telegram bot interactions. It includes Telegram-specific command handlers, inline keyboard logic, message formatters, and hosted services for the bot. Depends on the `Application` layer.
*   **`BackgroundTasks`**: Houses the implementation of background job handlers (e.g., `NotificationSendingService`) invoked by Hangfire. Depends on `Application` and potentially `TelegramPanel` (for sending messages).
*   **`WebAPI`**: The main entry point of the application (ASP.NET Core). Hosts the Telegram Webhook endpoint, CryptoPay Webhook endpoint, potentially an admin API, and the Hangfire Dashboard. Configures and runs the entire application. Depends on `Application`, `Infrastructure`, `TelegramPanel`, and `BackgroundTasks`.
*   **`Shared`**: (Optional but recommended) A common library for cross-cutting concerns like custom exceptions, result patterns, string extensions, and localization resources. Has no project dependencies.

## 🛠️ Technologies & Stack

*   **Backend Framework**: .NET 9, ASP.NET Core
*   **Database**: Entity Framework Core 9 (configurable for SQL Server or PostgreSQL)
*   **Telegram Integration**: `Telegram.Bot` library
*   **Background Jobs**: `Hangfire` (with MemoryStorage for dev, configurable for SQL/Postgres)
*   **Logging**: `Serilog`
*   **API Documentation**: `Swagger (OpenAPI)`
*   **Mapping**: `AutoMapper`
*   **Validation**: `FluentValidation`
*   **MediatR**: For CQRS/Mediator pattern in the Application layer
*   **HTTP Client Resilience**: `Polly` for retry policies
*   **HTML Parsing**: `HtmlAgilityPack`
*   **RSS/Atom Parsing**: `System.ServiceModel.Syndication`
*   **Cryptocurrency Payments**: Crypto Pay API
*   **Containerization (Planned)**: Docker

## 📂 Project Structure (High-Level)


## 🚀 Getting Started

**(Placeholder for setup and running instructions - to be filled in by the developer)**

1.  **Prerequisites**:
    *   .NET 9 SDK (or specified version)
    *   SQL Server / PostgreSQL instance (or use `MemoryStorage` for Hangfire/EF Core for initial dev)
    *   ngrok (for testing Webhooks locally)
2.  **Configuration**:
    *   Clone the repository.
    *   Create `appsettings.Development.json` in the `WebAPI` project.
    *   Add your **Telegram Bot Token** (from BotFather) to `TelegramPanel:BotToken`.
    *   Add your **CryptoPay API Token** (from @CryptoBot) to `CryptoPay:ApiToken`.
    *   Configure your database `ConnectionStrings:DefaultConnection`.
    *   (If using Webhook) Set up ngrok and update `TelegramPanel:WebhookAddress`.
3.  **Database Migration**:
    *   Use the provided PowerShell script (`Update-EfDatabase.ps1` or `Manage-EfMigrations.ps1`) or `dotnet ef` commands:
        ```powershell
        # From the root of the solution
        dotnet ef migrations add InitialCreate -p src/Infrastructure -s src/WebAPI
        dotnet ef database update -p src/Infrastructure -s src/WebAPI
        ```
4.  **Run the Application**:
    *   Set `WebAPI` as the startup project and run.
    *   Access the Hangfire dashboard at `/hangfire`.
    *   Interact with your bot on Telegram!

## 🔧 Key Modules & Functionality

*   **`RssReaderService` (Infrastructure)**: Fetches, parses, and stores news items from RSS feeds, handling conditional GETs and duplicate prevention.
*   **`NotificationDispatchService` (Application)**: Identifies target users for news/signals and enqueues notification jobs.
*   **`NotificationSendingService` (BackgroundTasks)**: A Hangfire job handler that sends queued notifications to users via Telegram, managing rate limits and retries.
*   **`UserService` (Application)**: Manages user registration, profile updates, and retrieval.
*   **`SubscriptionService` & `PaymentService` (Application)**: Handle subscription logic and integration with the CryptoPay API for creating payment invoices.
*   **`PaymentConfirmationService` (Application)**: Processes successful payment webhooks from CryptoPay to activate subscriptions.
*   **Telegram Command Handlers (TelegramPanel)**: Specific handlers for Telegram commands like `/start`, `/menu`, `/settings`, and callback queries from inline buttons.
*   **`TelegramBotService` (TelegramPanel)**: Manages the bot's connection to Telegram (Polling or Webhook setup).
*   **`UpdateProcessingService` (TelegramPanel)**: Core of the Telegram update handling, managing a middleware pipeline and routing updates.

## 🤝 Contributing

**(Placeholder - Add guidelines if this becomes an open project)**
Contributions are welcome! Please read the contributing guidelines (TODO) before submitting pull requests.

## 📄 License

**(Placeholder - Choose a license, e.g., MIT)**
This project is licensed under the MIT License - see the LICENSE.md (TODO) file for details.

---

*This README provides a high-level overview. Detailed documentation for each module and API can be found within the codebase comments and (eventually) dedicated documentation files.*
