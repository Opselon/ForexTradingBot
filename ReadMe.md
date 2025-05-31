بسیار عالی! کاملاً متوجه شدم. متن README شما را با دقت بسیار بالا، قالب‌بندی Markdown استاندارد کرده و تمامی placeholderها و متن‌های اضافی را حذف کردم. همچنین، لینک‌های GitHub را با استفاده از نام کاربری `Opselon` شما به‌روزرسانی کرده‌ام.

این فایل آماده است تا مستقیماً در مخزن GitHub شما قرار گیرد:

---

# 📈 ForexSignalBot

[![Build Status](https://img.shields.io/badge/Build-Passing-brightgreen)](https://github.com/Opselon/ForexTradingBot/actions)
[![License](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/Opselon/ForexTradingBot/blob/main/LICENSE)
[![GitHub Stars](https://img.shields.io/github/stars/Opselon/ForexTradingBot?style=social)](https://github.com/Opselon/ForexTradingBot/stargazers)
[![GitHub Forks](https://img.shields.io/github/forks/Opselon/ForexTradingBot?style=social)](https://github.com/Opselon/ForexTradingBot/network/members)
[![GitHub Issues](https://img.shields.io/github/issues/Opselon/ForexTradingBot)](https://github.com/Opselon/ForexTradingBot/issues)

---

## 🚀 Project Overview (ForexSignalBot)

"ForexSignalBot" is a comprehensive and professional Telegram-based system meticulously designed to deliver accurate, up-to-date, and reliable signals for global financial markets, with a primary focus on the Forex market. Developed using modern architecture and best programming practices, this project provides a robust platform for traders, analysts, and financial market enthusiasts to make more informed trading decisions.

### Background and Necessity

The Forex market, as the world's largest financial market, generates a vast volume of financial and news data daily, making it exceedingly challenging for ordinary users to analyze and track. Both professional traders and beginners often require tools to receive timely and trustworthy signals, along with comprehensive analytical information, in the simplest possible format. "ForexSignalBot" is specifically crafted to address this need by offering signal services, economic analyses, and access to credible news directly through a Telegram bot.

### Core Objectives

*   **High-Accuracy Signals:** Provide buy/sell signals with high precision, based on real-time market analysis.
*   **Easy & Fast Access to Information:** Ensure quick and effortless access to economic news, political developments, and global events relevant to financial markets.
*   **User-Friendly & Extensible Platform:** Create a platform that is intuitive and adaptable for traders with varying levels of knowledge and experience.
*   **Advanced AI & Data Analysis:** Leverage cutting-edge technologies in Artificial Intelligence and data analytics to continuously improve signal quality.
*   **Security & Stability:** Maintain user data security and guarantee service stability using robust software engineering standards and 24/7 operations.

---

## 🏛️ Architecture and Technologies

The project is built upon a **Clean Architecture** design, ensuring logical and technology-agnostic layering. This structure facilitates easy development, high testability, and better code maintainability. It separates concerns into distinct layers, ensuring that domain logic remains independent of external frameworks or databases.

### Project Layers

*   **Domain:** 🎯 Contains the core business logic, entities (such as Users, Signals, Transactions, Settings), and value objects. This layer is independent of any other technology or framework.
*   **Application:** ⚙️ Implements the application-specific business rules and use cases. It orchestrates domain entities, acts as an interface to the domain layer, and contains application services (e.g., Signal management, User services, Analytics).
*   **Infrastructure:** 📦 Provides implementations for external concerns defined in the Application layer. This includes database access (using EF Core and PostgreSQL), integration with external APIs (like Telegram Bot API), reliable RSS feed fetching, and background job processing.
*   **WebAPI:** 🌐 Acts as the entry point for HTTP requests. It contains controllers for managing users, signals, subscriptions, and other API-driven interactions.
*   **TelegramPanel:** 🤖 Dedicated services for the Telegram bot, including receiving and processing user commands via Webhooks/Polling, managing user sessions, and sending messages. It bridges user interactions with the Application layer.
*   **BackgroundTasks:** ⏳ Handles periodic or long-running processes such as RSS feed aggregation, signal analysis, data updates, and notification dispatch, often powered by distributed task queues like Hangfire.
*   **Shared:** 🤝 A common library containing utility classes, extension methods, and reusable components utilized across the entire solution.

### Core Technologies

*   **.NET 9:** The latest version of Microsoft's versatile developer platform, enabling high-performance and cross-platform applications.
*   **Docker:** 🐳 For containerization, ensuring consistent deployment environments and easy scalability.
*   **PostgreSQL:** 🐘 A powerful, open-source relational database system used for robust data storage.
*   **Entity Framework Core:** A modern object-relational mapper (ORM) for .NET, simplifying database interactions.
*   **Telegram.Bot API:** The official .NET library for interacting with the Telegram Bot API, handling all bot communications.
*   **Hangfire:** ⏱️ For seamless integration of background jobs, recurring tasks, and distributed processing, ensuring asynchronous operations.
*   **Polly:** 🛡️ A .NET resilience and transient-fault-handling library, implementing retry, circuit breaker, timeout, and fallback policies for robust API and database interactions.
*   **HTML Agility Pack:** For parsing and manipulating HTML content from RSS feeds.
*   **AutoMapper:** For simplified object-to-object mapping between different layers (e.g., entities to DTOs).
*   **Microsoft.Extensions.Logging:** For structured and efficient logging throughout the application.
*   **Potentially AI/ML Frameworks:** (e.g., ML.NET, TensorFlow.NET) for advanced sentiment analysis and predictive modeling in signals (Future Vision).

---

## ✨ Key Features

ForexSignalBot is designed with a rich set of features to cater to the diverse needs of financial traders:

*   **Flexible Membership & Subscription System:**
    *   Offers various membership tiers (Free and Premium) with differentiated access to features.
    *   Integrated token wallet for managing user credits and transactions.
*   **Advanced News Aggregation & Analysis:**
    *   **100+ High-Quality RSS Feeds:** 📰 Fetches news from an extensive list of highly reliable and stable sources including: Google News, Investing.com, Reuters, Bloomberg, CNBC, FXStreet, DailyFX, CoinTelegraph, CoinDesk, Wall Street Journal, TechCrunch, MarketWatch, Kitco, Seeking Alpha, ZeroHedge, Al Jazeera, Associated Press, Council on Foreign Relations, and Foreign Affairs.
    *   **Categorized Feeds:** News feeds are meticulously categorized (e.g., Forex Essentials, USD/EUR/JPY/GBP/AUD/CAD/CHF Forex, Global Stocks, Commodities, Crypto, Macroeconomics, Geopolitics, General Business, Tech & FinTech) for tailored user experience.
    *   **Smart Deduplication:** Advanced logic ensures no duplicate news items are sent, even across various sources or if a source changes slightly.
    *   **New User Defaults:** New users automatically receive essential, high-priority news feeds (`IsActive=1`) by default, preventing initial spam while providing immediate value. Other feeds are `IsActive=0` for users to activate manually.
    *   **User Preferences:** Users can customize their news categories, receiving only the content most relevant to their interests.
*   **Multi-Currency Signal Support:** 📈 Provides analytical signals for major Forex currencies (USD, EUR, JPY, GBP, AUD, CAD, CHF, NZD) and other key global assets.
*   **Intelligent Analysis & Sentiment (Future/Ongoing):**
    *   Utilizes sentiment analysis algorithms to analyze news and data, aiming to enhance signal quality and market insights.
    *   Plans for more advanced AI integration for predictive modeling.
*   **Robust Telegram Bot:**
    *   **Webhook/Polling Flexibility:** Fast and responsive message reception via Webhooks, with graceful fallback to Polling for high availability.
    *   **Queued Message Processing:** Messages are processed in a queue, ensuring smooth, non-blocking user experience and preventing system overload.
    *   **Rich UI Elements:** Supports inline keyboards, MarkdownV2 formatting, and media for interactive and informative messages.
*   **Security & Data Integrity:**
    *   Focus on secure token management for user access levels.
    *   Implementation of robust exception handling and Polly policies for transient fault tolerance, guaranteeing 24/7 uptime and data consistency.
*   **Scalability & Monitoring:** Designed for production deployment with Docker and integrated logging for continuous monitoring.

---

## 🎨 UI/UX Concepts (Telegram Bot)

Since the primary user interface is the Telegram bot itself, the UI/UX focuses on intuitive interactions, clear messaging, and easy customization.

*   **Main Menu & Commands:** Users interact through a command-based interface (e.g., `/start`, `/help`, `/settings`).
    *   **Icon Suggestion:** A simple list icon or a house icon for `/start`, a question mark for `/help`, gear icon for `/settings`.
*   **News Feeds:**
    *   **Message Format:** News items are sent in a clean, readable MarkdownV2 format, with bold titles, italicized sources, truncated summaries, and a "Read Full Article" inline button.
    *   **Icon Suggestion:** 📰 (News/Newspaper emoji) at the start of news messages.
    *   **"Read More" Button:** A clear, blue button with a chain link emoji.
*   **Signal Notifications:**
    *   **Message Format:** Signals are presented with clear buy/sell indicators, asset symbols, entry/SL/TP prices, and status updates.
    *   **Icon Suggestion:** 📈 (Chart Increasing emoji) for Buy, 📉 (Chart Decreasing emoji) for Sell, 🔔 for new signals.
*   **Settings & Preferences:**
    *   Users navigate settings via inline keyboards (e.g., `⚙️ Preferences -> News Categories -> Forex -> USD [✅/❌]`).
    *   **Icon Suggestion:** ✅ for active, ❌ for inactive, ▶️ for navigation buttons.
*   **Error Handling:** User-friendly error messages are sent when unhandled issues occur, with a clear indication that the team has been notified.
    *   **Icon Suggestion:** 🤖 (Robot emoji) or ⚠️ (Warning emoji) for error messages.

---

## 🛠️ Getting Started (For Developers)

To set up the ForexSignalBot project locally:

1.  **Clone the repository:**
    ```bash
    git clone https://github.com/Opselon/ForexTradingBot.git
    cd ForexTradingBot
    ```
2.  **Database Setup:**
    *   Ensure PostgreSQL is installed and running.
    *   Update the `DefaultConnection` string in `appsettings.json` (or `appsettings.Development.json`) to point to your PostgreSQL instance.
    *   Apply Entity Framework Core migrations:
        ```bash
        dotnet ef database update --project Infrastructure --startup-project WebAPI
        ```
    *   **Populate RSS Feeds & Categories:** Run the provided `Populate_RssSources_Categories.sql` script (from the project root, or in your SQL client) against your database. This will set up the initial categories and RSS feeds.
3.  **Telegram Bot Token:**
    *   Obtain a bot token from BotFather on Telegram.
    *   Configure your bot token in `appsettings.json` under `TelegramPanelSettings:BotToken`.
4.  **Hangfire Dashboard (Optional):**
    *   Configure your Hangfire dashboard path and security as needed in `appsettings.json`.
5.  **Build and Run:**
    ```bash
    dotnet build
    dotnet run --project WebAPI # Or run from Visual Studio
    ```
6.  **Docker (Optional):**
    *   If you plan to use Docker, ensure Docker Desktop is running.
    *   Build and run Docker containers:
        ```bash
        docker-compose build
        docker-compose up
        ```

---

## 🗺️ Roadmap and Future Plans

The ForexSignalBot project is continuously evolving. Our future plans include:

*   **Advanced AI/ML Integration:** Implement more sophisticated AI models for deeper data analysis, predictive signaling, and personalized market insights.
*   **Admin & User Web Panel:** Develop a comprehensive web-based management panel for both administrators and users to manage subscriptions, preferences, and view advanced analytics.
*   **Expanded Signal Categories:** Introduce new signal categories and asset classes based on user feedback and market demand.
*   **Enhanced Personalization:** Provide more granular customization options for notification types, frequency, and content.
*   **Integration with Trading Platforms:** Explore possibilities for direct integration with popular trading platforms (e.g., MetaTrader 4/5) for automated signal execution (with user consent).
*   **Community Features:** Foster a community around the bot, potentially including shared insights and discussions.

---

## 🤝 Contributing

We welcome contributions from the community! If you're interested in contributing to ForexSignalBot, please:

1.  Fork the repository.
2.  Create a new branch for your feature or bug fix.
3.  Commit your changes following conventional commit guidelines.
4.  Push your branch and open a pull request.

Please review our [CONTRIBUTING.md](https://github.com/Opselon/ForexTradingBot/blob/main/CONTRIBUTING.md) for more details.

---

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](https://github.com/Opselon/ForexTradingBot/blob/main/LICENSE) file for details.