# ForexSignalBot: AI-Driven Telegram Forex Signals 📈

[![Build Status](https://github.com/Opselon/ForexTradingBot/actions/workflows/build.yml/badge.svg)](https://github.com/Opselon/ForexTradingBot/actions/workflows/build.yml)
[![License](https://img.shields.io/github/license/Opselon/ForexTradingBot)](https://github.com/Opselon/ForexTradingBot/blob/main/LICENSE)
[![GitHub Stars](https://img.shields.io/github/stars/Opselon/ForexTradingBot?style=social)](https://github.com/Opselon/ForexTradingBot/stargazers)
[![GitHub Forks](https://img.shields.io/github/forks/Opselon/ForexTradingBot?style=social)](https://github.com/Opselon/ForexTradingBot/network/members)
[![GitHub Issues](https://img.shields.io/github/issues/Opselon/ForexTradingBot)](https://github.com/Opselon/ForexTradingBot/issues)

## 🚀 Project Overview: Precision Trading with ForexSignalBot

**ForexSignalBot** is a sophisticated, AI-enhanced Telegram-based system meticulously engineered to deliver hyper-accurate, real-time, and highly reliable trading signals for global financial markets, with a strategic focus on the dynamic Forex market. Built on cutting-edge **.NET 9** architecture and adhering to the highest standards of software engineering best practices, this project provides a robust, resilient, and intuitive platform for traders, analysts, and financial market enthusiasts to make exceptionally informed trading decisions.

### Background & Necessity: Navigating the Volatile Markets

The Forex market, as the world's largest and most liquid financial market, generates an overwhelming volume of financial data and news daily. This sheer scale makes comprehensive analysis and timely tracking an insurmountable challenge for both novice and seasoned traders. There's a critical demand for intelligent tools that can distill this complexity into actionable, trustworthy signals and comprehensive analytical insights, delivered in the simplest possible format. **ForexSignalBot** precisely addresses this imperative, offering premium signal services, deep economic analyses, and direct access to credible news sources—all seamlessly integrated within a user-friendly Telegram bot.

### Core Objectives: Empowering Strategic Trading

Our mission with ForexSignalBot is to redefine accessibility and accuracy in financial trading:

*   **📈 High-Precision Signals:** Deliver highly accurate buy/sell signals underpinned by real-time, sophisticated market analysis and data-driven insights.
*   **⚡ Instant & Effortless Information:** Ensure lightning-fast and effortless access to critical economic news, geopolitical developments, and global events directly impacting financial markets.
*   **🌐 User-Centric & Extensible Platform:** Cultivate an intuitive, adaptable, and highly customizable platform tailored for traders across all experience levels.
*   **🧠 Advanced AI & Data Analytics:** Continuously enhance signal quality and market predictive capabilities through state-of-the-art Artificial Intelligence and advanced data analytics.
*   **🔒 Security & Unwavering Stability:** Guarantee paramount user data security and ensure unparalleled service stability through robust software engineering principles and continuous 24/7 operational resilience.

---

## 🏛️ Architecture & Core Technologies: Engineered for Excellence

ForexSignalBot is architected on the principles of **Clean Architecture**, fostering a highly logical, technology-agnostic, and layered design. This structure guarantees exceptional testability, maintainability, and extensibility by strictly separating concerns and ensuring domain logic remains independent of external frameworks or databases.

### Project Layers: A Blueprint for Scalability

*   **Domain:** 🎯 The foundational layer, encapsulating core business logic, entities (e.g., `Users`, `Signals`, `Transactions`, `Settings`), and value objects. This layer is entirely independent and technology-agnostic.
*   **Application:** ⚙️ Orchestrates the execution of application-specific business rules and use cases. It acts as the interface to the Domain layer, containing application services responsible for signal management, user services, and analytics.
*   **Infrastructure:** 📦 Provides concrete implementations for external concerns defined in the Application layer. This includes persistent data storage (via EF Core and PostgreSQL), integration with external APIs (like Telegram Bot API), reliable RSS feed fetching, and robust background job processing.
*   **WebAPI:** 🌐 The primary entry point for HTTP requests, hosting controllers for managing users, subscriptions, signals, and other RESTful API interactions.
*   **TelegramPanel:** 🤖 Dedicated services for handling all Telegram bot interactions, including processing user commands via Webhooks/Polling, managing user sessions, and sending rich messages. It seamlessly bridges user interactions with the Application layer.
*   **BackgroundTasks:** ⏳ Manages periodic or long-running operations such as RSS feed aggregation, complex signal analysis, data synchronization, and notification dispatch, powered by distributed task queues.
*   **Shared:** 🤝 A cross-cutting library containing reusable utility classes, extension methods, and common components utilized across the entire solution.

### Core Technologies: Powering Performance & Reliability

*   **.NET 9:** The absolute latest version of Microsoft's high-performance, cross-platform developer platform, providing unparalleled speed and versatility.
*   **Docker 🐳:** For robust containerization, ensuring consistent, isolated, and scalable deployment environments, enabling seamless CI/CD pipelines.
*   **PostgreSQL 🐘:** A highly advanced, open-source relational database system renowned for its robustness, reliability, and powerful data management capabilities.
*   **Entity Framework Core (EF Core):** Microsoft's modern, high-performance Object-Relational Mapper (ORM) for .NET, simplifying complex database interactions and schema management.
*   **Telegram.Bot API:** The official, comprehensive .NET library for seamless and efficient interaction with the Telegram Bot API, handling all bot communications.
*   **Hangfire ⏱️:** A powerful, open-source library for transparent and easy background job processing, recurring tasks, and distributed processing, ensuring asynchronous operations and system responsiveness.
*   **Polly 🛡️:** A robust .NET resilience and transient-fault-handling library. It implements fluent policies such as Retry, Circuit Breaker, Timeout, Bulkhead, and Fallback, ensuring the application remains resilient against transient failures in API calls and database interactions.
*   **HTML Agility Pack:** A robust and flexible HTML parser used for extracting and manipulating content from complex RSS feeds and web pages.
*   **AutoMapper:** A convention-based object-to-object mapper, significantly simplifying data transfer between different layers (e.g., entities to DTOs) and reducing boilerplate code.
*   **Microsoft.Extensions.Logging:** For structured, high-performance logging, enabling comprehensive observability and diagnostics across the application.
*   **ML.NET / TensorFlow.NET:** (Future Vision) Integration of cutting-edge AI/ML frameworks for advanced sentiment analysis, sophisticated predictive modeling, and continuous signal quality enhancement.

---

## ✨ Key Features: Unlocking Market Intelligence

ForexSignalBot is engineered with an extensive suite of features designed to cater to the nuanced needs of modern financial traders:

### Flexible Membership & Subscription System: Tailored Access

*   Offers diverse membership tiers (Free and Premium) with tiered access to advanced features and premium content.
*   Integrates a secure token-based wallet system for managing user credits and facilitating transparent transactions.

### 📰 Advanced News Aggregation & Intelligent Analysis: Stay Ahead

*   **100+ High-Quality RSS Feeds:** Meticulously fetches news from an extensive, curated list of highly reliable and stable global financial news sources, including:
    *   `Google News`, `Investing.com`, `Reuters`, `Bloomberg`, `CNBC`, `FXStreet`, `DailyFX`
    *   `CoinTelegraph`, `CoinDesk`, `Wall Street Journal`, `TechCrunch`, `MarketWatch`, `Kitco`, `Seeking Alpha`
    *   `ZeroHedge`, `Al Jazeera`, `Associated Press`, `Council on Foreign Relations`, `Foreign Affairs`
*   **Categorized Feeds:** News feeds are intelligently categorized (e.g., `Forex Essentials`, `USD/EUR/JPY/GBP/AUD/CAD/CHF Forex`, `Global Stocks`, `Commodities`, `Crypto`, `Macroeconomics`, `Geopolitics`, `General Business`, `Tech & FinTech`) for a highly personalized user experience.
*   **Smart Deduplication:** Employs advanced algorithms to prevent duplicate news items from being sent, even if sources rephrase or re-publish content.
*   **New User Defaults:** New users automatically receive essential, high-priority news feeds (`IsActive=1`) by default, providing immediate value without initial information overload. Other feeds (`IsActive=0`) are available for manual activation based on user interest.
*   **User Preferences:** Users retain granular control to customize their news categories, ensuring they receive only the content most relevant to their trading strategies and interests.

### 📈 Multi-Currency Signal Support: Diverse Market Coverage

*   Provides analytical signals for all major Forex currency pairs (`USD`, `EUR`, `JPY`, `GBP`, `AUD`, `CAD`, `CHF`, `NZD`) and other key global assets, offering a comprehensive trading perspective.

### 🧠 Intelligent Analysis & Sentiment (Future/Ongoing): AI-Powered Edge

*   Actively integrates and refines sentiment analysis algorithms to analyze news and data, aiming to significantly enhance signal quality, identify emerging trends, and provide deeper market insights.
*   Strategic plans for more advanced AI/ML integration for sophisticated predictive modeling, pattern recognition, and adaptive signaling.

### 🤖 Robust & Responsive Telegram Bot: Seamless User Experience

*   **Webhook/Polling Flexibility:** Achieves fast and responsive message reception via Webhooks, with graceful fallback to Polling for maximum reliability and high availability.
*   **Queued Message Processing:** All incoming and outgoing messages are processed asynchronously in a robust queue, ensuring a smooth, non-blocking user experience and preventing system overload, even under high traffic.
*   **Rich UI Elements:** Fully supports Telegram's rich UI capabilities, including inline keyboards, `MarkdownV2` formatting for visually appealing messages, and media attachments for interactive and informative content delivery.

### 🔒 Security & Data Integrity: Trustworthy Operations

*   Prioritizes secure token management for robust user authentication and authorization across different access levels.
*   Implements comprehensive exception handling and leverages Polly policies for advanced transient fault tolerance, guaranteeing 24/7 uptime and unparalleled data consistency.
*   Designed for seamless production deployment with **Docker** and integrates structured logging for continuous monitoring, alerting, and performance optimization.

---

## 🎨 UI/UX Concepts (Telegram Bot): Intuitive & Engaging

As the Telegram bot serves as the primary user interface, our UI/UX strategy centers on intuitive interactions, clear communication, and effortless customization.

*   **Main Menu & Commands:** Users interact through a natural, command-based interface (e.g., `/start`, `/help`, `/settings`).
    *   **Icon Suggestion:** 🏠 for `/start` (home), ❓ for `/help`, ⚙️ for `/settings`.
*   **News Feeds:**
    *   **Message Format:** News items are presented in a clean, highly readable `MarkdownV2` format, featuring bold titles, italicized sources, intelligently truncated summaries, and a prominent "Read Full Article" inline button.
    *   **Icon Suggestion:** 📰 (Newspaper emoji) at the beginning of each news message.
    *   **"Read More" Button:** A clear, blue button with a 🔗 (chain link emoji) for direct access.
*   **Signal Notifications:**
    *   **Message Format:** Signals are conveyed with clear buy/sell indicators, asset symbols, precise entry/SL/TP prices, and real-time status updates.
    *   **Icon Suggestion:** 📈 (Chart Increasing emoji) for Buy, 📉 (Chart Decreasing emoji) for Sell, 🔔 for new signals.
*   **Settings & Preferences:**
    *   Users navigate settings via intuitive inline keyboards (e.g., `⚙️ Preferences -> News Categories -> Forex -> USD [✅/❌]`).
    *   **Icon Suggestion:** ✅ for active, ❌ for inactive, ▶️ for navigation buttons.
*   **Error Handling:** User-friendly and informative error messages are sent when unhandled issues occur, clearly indicating that the development team has been notified and is addressing the issue.
    *   **Icon Suggestion:** ⚠️ (Warning emoji) or 🤖 (Robot emoji) for error messages.

---

## 🛠️ Getting Started (For Developers): Ignite Your Bot

To set up the **ForexSignalBot** project locally and contribute:

1.  **Clone the repository:**
    ```bash
    git clone https://github.com/Opselon/ForexTradingBot.git
    cd ForexTradingBot
    ```

2.  **Database Setup (PostgreSQL):**
    *   Ensure PostgreSQL is installed and running on your system.
    *   Update the `DefaultConnection` string in `appsettings.json` (or `appsettings.Development.json` for local development) to correctly point to your PostgreSQL instance.
    *   Apply Entity Framework Core migrations to create the database schema:
        ```bash
        dotnet ef database update --project Infrastructure --startup-project WebAPI
        ```
    *   **Populate RSS Feeds & Categories:** Run the provided `Populate_RssSources_Categories.sql` script (located in the project root, or execute it directly from your SQL client) against your database. This crucial step will set up the initial categories and a comprehensive list of RSS feeds.

3.  **Telegram Bot Token:**
    *   Obtain a unique bot token from BotFather on Telegram.
    *   Configure your bot token in `appsettings.json` under `TelegramPanelSettings:BotToken`.

4.  **Hangfire Dashboard (Optional but Recommended):**
    *   Configure your Hangfire dashboard path and security settings as needed within `appsettings.json` for monitoring background jobs.

5.  **Build and Run (.NET):**
    ```bash
    dotnet build
    dotnet run --project WebAPI # Or run directly from Visual Studio/VS Code
    ```

6.  **Docker Integration (Recommended for Production & Consistency - `dotnet ducker`):**
    *   Ensure Docker Desktop is installed and running on your machine.
    *   Build and run all services using Docker Compose for a containerized environment:
        ```bash
        docker-compose build
        docker-compose up
        ```
    *   This will orchestrate your PostgreSQL database, WebAPI, and TelegramPanel services within isolated Docker containers.

---

## 🗺️ Roadmap & Future Plans: The Path to 2025 and Beyond

The **ForexSignalBot** project is on an accelerated trajectory of continuous innovation. Our ambitious future plans include:

*   **🧠 Advanced AI/ML Integration:** Implement even more sophisticated AI models for deeper, predictive data analysis, hyper-personalized market insights, and adaptive signaling algorithms.
*   **🌐 Admin & User Web Panel:** Develop a comprehensive, intuitive web-based management panel for both administrators (for system oversight, user management, content curation) and users (for subscription management, preference customization, and advanced analytics visualization).
*   **➕ Expanded Signal Categories & Asset Classes:** Introduce new signal categories and broader asset classes (e.g., indices, bonds) based on user feedback and evolving market demands.
*   **🎯 Enhanced Personalization:** Provide even more granular customization options for notification types, frequency, content filters, and preferred market alerts.
*   **🔗 Integration with Trading Platforms:** Explore possibilities for secure, direct integration with popular trading platforms (e.g., MetaTrader 4/5, cTrader) for automated signal execution (always with explicit user consent and robust risk management).
*   **🤝 Community Features:** Foster a vibrant community around the bot, potentially including shared insights, discussion forums, and collaborative learning features.

---

## 🤝 Contributing: Join Our Journey

We enthusiastically welcome contributions from the global developer community! If you're interested in contributing to **ForexSignalBot**, please follow these guidelines:

1.  **Fork** the repository.
2.  **Create a new branch** for your feature or bug fix: `git checkout -b feature/your-feature-name` or `bugfix/your-bug-fix`.
3.  **Commit your changes** following conventional commit guidelines (e.g., `feat: add new signal type`, `fix: resolve issue with RSS parsing`).
4.  **Push your branch** and **open a Pull Request**.

Please review our `CONTRIBUTING.md` file (to be created) for more detailed guidelines and coding standards.

---

## 📄 License

This project is proudly licensed under the **MIT License** - see the [LICENSE](https://github.com/Opselon/ForexTradingBot/blob/main/LICENSE) file for comprehensive details.

---